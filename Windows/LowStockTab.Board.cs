using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Restocker.Common;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Windows;

public sealed partial class LowStockTab
{
    private (uint Id, bool Hq)? focusedItem;
    private DateTime nextBoardRefresh;
    private ulong boardCharacter;
    private RetainerSnapshot[] boardRoster = [];
    private Dictionary<string, SlotLayout> boardAssignments = [];
    private Dictionary<(uint, bool), LowStockEntry> boardSettings = [];
    private ILookup<(string, int, uint, bool), ListingRecord>? boardRecords;

    private Dictionary<string, BoardSlot[]> boardCells = [];
    private readonly Dictionary<(uint, bool), string> pickerStock = [];

    private void RefreshBoard(DateTime now)
    {
        var cid = Plugin.PlayerState.ContentId;
        if (boardCharacter == cid && now < nextBoardRefresh) return;
        boardCharacter = cid;
        nextBoardRefresh = now.AddSeconds(1);
        boardRoster = service.CurrentRetainers();
        boardRecords = Plugin.Instance.Tracker.Tracker.ActiveListings.OrderBy(r => r.MissingSinceUtc != null)
            .ToLookup(r => (r.RetainerKey, r.Slot, r.ItemId, r.IsHq));
        boardSettings = configuration.LowStock.DistinctBy(e => (e.ItemId, e.IsHq)).ToDictionary(e => (e.ItemId, e.IsHq));
        pickerStock.Clear();
        boardAssignments = boardRoster.ToDictionary(s => s.Key, s => configuration.SlotPlans.Layout(s));
        boardCells = boardAssignments.ToDictionary(pair => pair.Key, pair => pair.Value.DisplaySlots());
    }

    private void DrawBoard()
    {
        var now = DateTime.UtcNow;
        RefreshBoard(now);
        if (boardRoster.Length == 0)
        {
            ImGui.TextDisabled("Open the summoning bell to read your retainers.");
            return;
        }
        var em = ImGui.GetFontSize();
        var known = boardRoster.Where(r => r.LastRefreshedUtc != DateTime.MinValue).Select(r => boardAssignments[r.Key]).ToList();
        ImGui.TextWrapped($"Missing {known.Sum(s => s.Missing)} · Unplanned {known.Sum(s => s.Extra)} · Blocking {known.Sum(s => s.Blocking)}"
            + (known.Count < boardRoster.Length ? " (read retainers only)" : ""));
        DrawFillButton(null);
        DrawBoardLegend();
        var cardWidth = 39 * em;
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / cardWidth), 1, 3);
        if (!ImGui.BeginTable("##retainer-board", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX,
            new Vector2(0, ImGui.GetContentRegionAvail().Y), Math.Max(cardWidth, ImGui.GetContentRegionAvail().X))) return;
        foreach (var seller in boardRoster)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(seller.Key);
            var marketMark = DrawBoardHeader(seller, now);
            DrawFillButton(seller);
            DrawBoardSlots(seller, marketMark, now, em);
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (noteDirty && !ImGui.IsAnyItemActive()) { configuration.Save(); noteDirty = false; }
    }

    private string DrawBoardHeader(RetainerSnapshot seller, DateTime now)
    {
        var summary = boardAssignments[seller.Key];
        var marketMark = Marker(seller.LastRefreshedUtc, now);
        var bagMark = Marker(InventorySnapshot.OldestRefresh(RetainerWatcher.RetainerPages.Select(t => (uint)t),
            seller.InventoryRefreshedUtc, DateTime.MinValue), now);
        var occupied = seller.Inventory.Count(i => RetainerWatcher.RetainerPages.Contains((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)i.ContainerId));
        ImGui.TextUnformatted(seller.RetainerName);
        ImGui.SameLine();
        ImGui.TextDisabled($"Bag {(bagMark == "?" ? "?" : $"{occupied}{bagMark}")} slots" + (seller.Gil is { } gil ? $" · {gil:N0} gil" : ""));
        var known = marketMark != "?";
        ImGui.TextUnformatted($"Used {(known ? summary.Used.ToString() : "?")}{(marketMark == "!" ? " !" : "")}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(known
            ? $"Market last read {seller.LastRefreshedUtc.ToLocalTime():g}{(marketMark == "!" ? " (stale)" : "")}"
            : "Market slots have never been read.");
        ImGui.SameLine();
        ImGui.TextColored(Muted, $"Reserved {(known ? summary.Reserved.ToString() : "?")}");
        ImGui.SameLine();
        ImGui.TextColored(Muted, $"Free {(known ? summary.Free.ToString() : "?")}");
        if (known && summary.Missing > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Missing {summary.Missing}");
        }
        if (known && summary.Blocking > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(BlockingColor, $"Blocking {summary.Blocking}");
        }
        return marketMark;
    }

    private string planSearch = "";
    private string? planError;
    private SlotPlanRow draft = new(0, false, 1, 1);


    private void DrawFillButton(RetainerSnapshot? seller)
    {
        var rows = (seller == null ? boardRoster.AsEnumerable() : [seller])
            .SelectMany(r => configuration.SlotPlans.Rows(r.Key)).ToList();
        ImGui.BeginDisabled(rows.Count == 0 || executor.IsRunning || !AddonHelper.IsOpen("RetainerList"));
        if (ImGui.SmallButton(seller == null ? "Fill to plan (all)" : "Fill to plan"))
            SubmitRestock(configuration.LowStock.Where(e => rows.Any(r => r.Matches(e.ItemId, e.IsHq))
                && !configuration.IsHeld(e.ItemId, e.IsHq)), boardCharacter, true, seller?.Key);
        ImGui.EndDisabled();
    }

    private void DrawBoardStockSettings(uint id, bool hq)
    {
        var key = (id, hq);
        var entry = boardSettings.GetValueOrDefault(key);
        if (entry == null)
        {
            if (ImGui.SmallButton("Track item"))
            {
                entry = new() { ItemId = id, IsHq = hq };
                boardSettings[key] = entry;
                configuration.LowStock.Add(entry);
                configuration.Save();
            }
            return;
        }

        var bell = AddonHelper.IsOpen("RetainerList");
        ImGui.BeginDisabled(!bell || executor.IsRunning || !entry.Enabled);
        if (ImGui.SmallButton("Restock item")) SubmitRestock([entry], boardCharacter);
        ImGui.EndDisabled();
        var editing = false;
        if (DrawSettings(entry, ItemName(id), boardCharacter, boardRoster.Select(r => r.RetainerName).ToArray(),
            service.ListedUnits(id, hq) == 0, bell, ref editing))
        {
            configuration.LowStock.Remove(entry);
            selected.Remove(key);
            restockPrice.Remove(key);
            nextBoardRefresh = DateTime.MinValue;
            configuration.Save();
            ImGui.CloseCurrentPopup();
        }
    }

    private void OpenSlot(BoardSlot cell)
    {
        draft = cell.Plan ?? new(0, false, 1, 1);
        planSearch = "";
        planError = null;
        ImGui.OpenPopup("Edit slot");
    }

    private void SaveSlot(RetainerSnapshot seller, int index, SlotPlanRow? choice, bool close = true)
    {
        if (choice != null && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(choice.ItemId, out var item)
            && choice.PerListing > RetainerWatcher.ResolveMaxListingStack(item))
        {
            planError = "Quantity exceeds this item's listing limit.";
            return;
        }
        if (!configuration.SlotPlans.TryEditSlot(seller, index, choice, out planError)) return;
        if (choice != null && !boardSettings.ContainsKey((choice.ItemId, choice.IsHq)))
            configuration.LowStock.Add(new() { ItemId = choice.ItemId, IsHq = choice.IsHq });
        configuration.Save();
        nextBoardRefresh = DateTime.MinValue;
        if (close) ImGui.CloseCurrentPopup();
    }

    private void DrawSlotPopup(RetainerSnapshot seller, BoardSlot cell, DateTime now, float em)
    {
        if (!ImGui.BeginPopup("Edit slot")) return;
        ImGui.TextUnformatted($"Slot {cell.Index + 1}");
        if (!cell.Filled && cell.Listing is { } actual &&
            ImGui.Button($"Plan this slot for {ItemName(actual.ItemId)}{(actual.IsHQ ? " HQ" : "")} ×{actual.Quantity}"))
            SaveSlot(seller, cell.Index, new(actual.ItemId, actual.IsHQ, 1, actual.Quantity));

        ImGui.InputTextWithHint("##find", "Search Stock items", ref planSearch, 80);
        if (ImGui.BeginListBox("##items", new Vector2(25 * em, 9 * em)))
        {
            var groups = boardSettings.Values.ToLookup(e => e.CategoryId);
            foreach (var id in CategoryIds())
            {
                var header = false;
                foreach (var entry in groups[id])
                {
                    var name = ItemName(entry.ItemId) + (entry.IsHq ? " HQ" : "");
                    if (!name.Contains(planSearch, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!header) ImGui.TextDisabled(CategoryLabel(id));
                    header = true;
                    var key = (entry.ItemId, entry.IsHq);
                    if (!pickerStock.TryGetValue(key, out var stock))
                    {
                        var (inventory, _) = service.StockReadings(entry, boardRoster, now);
                        pickerStock[key] = stock = FormatSupply(entry, inventory).Available;
                    }
                    DrawItemIcon(entry.ItemId, em);
                    ImGui.SameLine();
                    if (ImGui.Selectable($"{name} · Available {stock}##{entry.ItemId}-{entry.IsHq}",
                        draft.Matches(entry.ItemId, entry.IsHq), ImGuiSelectableFlags.DontClosePopups))
                    {
                        draft = configuration.SlotPlans.Rows(seller.Key).FirstOrDefault(r => r.Matches(entry.ItemId, entry.IsHq))
                            ?? new(entry.ItemId, entry.IsHq, 1, service.PerListing(entry));
                        SaveSlot(seller, cell.Index, draft, close: false);
                    }
                }
            }
            ImGui.EndListBox();
        }
        if (draft.ItemId != 0)
        {
            ImGui.TextUnformatted(ItemName(draft.ItemId) + (draft.IsHq ? " HQ" : ""));
            var qty = draft.PerListing;
            if (ImGui.InputInt("Units per listing", ref qty)) draft = draft with { PerListing = qty };
            ImGui.TextDisabled("Applies to all slots for this item on this retainer.");
            if (ImGui.Button("Save quantity")) SaveSlot(seller, cell.Index, draft);
        }
        if (cell.Plan != null && ImGui.SmallButton("Remove from this slot")) SaveSlot(seller, cell.Index, null);
        if (planError != null) ImGui.TextColored(new Vector4(1, .4f, .37f, 1), planError);
        if (cell.Listing is { } listing) DrawSlotSettings(listing.ItemId, listing.IsHQ);
        if (!cell.Filled && cell.Plan is { } plan) DrawSlotSettings(plan.ItemId, plan.IsHq);
        ImGui.EndPopup();
    }

    private void DrawSlotSettings(uint id, bool hq)
    {
        var name = ItemName(id);
        if (ImGui.BeginMenu($"Stock settings · {name}{(hq ? " HQ" : "")}##{id}"))
        {
            DrawBoardStockSettings(id, hq);
            if (ImGui.SmallButton("Show in Stock list"))
            {
                focusedItem = (id, hq);
                search = name;
                kindFilter = StockKind.None;
                statusFilter = StockStatus.None;
                categoryFilter = null;
                onlyLow = false;
                configuration.ShowRetainerBoard = false;
                configuration.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndMenu();
        }
    }

    private string Marker(DateTime stamp, DateTime now) => service.Freshness(stamp, now).State switch
    {
        StockReadState.Fresh => "",
        StockReadState.NeverRead => "?",
        _ => "!",
    };

    private static readonly Vector4 PlannedColor = new(.55f, .85f, .65f, 1);
    private static readonly Vector4 BlockingColor = new(1, .48f, .45f, 1);
    private static readonly Vector4 UndercutColor = new(1, .60f, .43f, 1);
    private static readonly Vector4 StuckColor = new(.99f, .78f, .37f, 1);
    private static readonly Vector4 HoldColor = new(.55f, .75f, 1, 1);
    private static readonly Vector4 Muted = new(.68f, .69f, .72f, 1);

    private static void DrawBoardLegend()
    {
        var first = true;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        void Key(Vector4 color, string label)
        {
            var text = "■ " + label;
            if (!first && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(text).X <= right)
                ImGui.SameLine();
            ImGui.TextColored(color, text);
            first = false;
        }
        Key(PlannedColor, "Filled");
        Key(Muted, "Reserved / — free");
        Key(ImGui.GetStyle().Colors[(int)ImGuiCol.Text], "Unplanned");
        Key(BlockingColor, "Blocking");
        Key(UndercutColor, "Undercut price");
        Key(StuckColor, "Stuck age");
        Key(Muted, "! stale / paused");
        Key(HoldColor, "Hold");
    }

    private void DrawBoardSlots(RetainerSnapshot seller, string marketMark, DateTime now, float em)
    {
        if (!ImGui.BeginTable("##slots", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings)) return;
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 1.5f * em);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 3 * em);
        ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 6.4f * em);
        ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, 3 * em);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 8 * em);
        ImGui.TableHeadersRow();
        foreach (var cell in boardCells[seller.Key])
        {
            ImGui.PushID(cell.Index);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{cell.Index + 1}##slot", false, ImGuiSelectableFlags.SpanAllColumns))
                OpenSlot(cell);
            var hovered = ImGui.IsItemHovered();
            DrawSlotPopup(seller, cell, now, em);
            DrawBoardRow(seller, cell, marketMark, now, em, hovered);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static void DrawItemIcon(uint id, float size)
    {
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row)
            && Plugin.Textures.GetFromGameIcon(new GameIconLookup(row.Icon)).GetWrapOrDefault() is { } icon)
            ImGui.Image(icon.Handle, new Vector2(size));
        else ImGui.Dummy(new Vector2(size));
    }

    private void DrawBoardRow(RetainerSnapshot seller, BoardSlot cell, string marketMark, DateTime now, float em, bool hovered)
    {
        var listing = cell.Listing;
        var plan = cell.Plan;
        var id = listing?.ItemId ?? plan?.ItemId ?? 0;
        var hq = listing?.IsHQ ?? plan?.IsHq ?? false;
        var entry = boardSettings.GetValueOrDefault((id, hq));
        var record = listing == null ? null : boardRecords![(seller.Key, listing.ListingIndex, id, hq)].FirstOrDefault();
        var scan = listing == null ? null : Plugin.Instance.ListingScans.Get(seller.Key, listing.ListingIndex, id, hq);
        var health = BoardListingHealth.Read(listing, record, scan, marketMark == "!", now, configuration.StuckListingDays);
        var held = id != 0 && configuration.IsHeld(id, hq);
        var paused = entry is { Enabled: false };
        var state = cell.Filled ? "Filled" : cell.Blocking ? "Blocking" : listing != null ? "Unplanned"
            : marketMark == "?" ? (plan == null ? "?" : "Planned ?") : plan == null ? "—" : "Reserved";
        var normal = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var stateColor = cell.Filled ? PlannedColor : cell.Blocking ? BlockingColor : listing == null ? Muted : normal;
        var name = id == 0 ? "—" : (hq ? "HQ " : "") + ItemName(id);
        ImGui.TableNextColumn();
        if (id != 0)
        {
            DrawItemIcon(id, em);
            ImGui.SameLine(0, em * .25f);
        }
        ImGui.TextColored(paused || listing == null ? Muted : normal, name);
        ImGui.TableNextColumn();
        if ((listing?.Quantity ?? plan?.PerListing) is { } quantity) RightAligned($"{quantity:N0}");
        ImGui.TableNextColumn();
        if (listing != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, health.UndercuttingPrice.HasValue ? UndercutColor : normal);
            RightAligned($"{listing.UnitPrice:N0}");
            ImGui.PopStyleColor();
        }
        ImGui.TableNextColumn();
        if (listing != null) ImGui.TextColored(health.Stuck ? StuckColor : normal, health.DaysListed is { } days ? $"{days}d" : "?");
        ImGui.TableNextColumn();
        ImGui.TextColored(stateColor, state);
        if (health.Stale) { ImGui.SameLine(0, 2); ImGui.TextColored(Muted, "!"); }
        if (held) { ImGui.SameLine(0, 3); ImGui.TextColored(HoldColor, "Hold"); }
        if (!hovered) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Slot {cell.Index + 1} · {name}");
        ImGui.TextColored(stateColor, cell.Filled ? "Filled as planned" : cell.Blocking
            ? $"Blocking plan for {(plan!.IsHq ? "HQ " : "")}{ItemName(plan.ItemId)} × {plan.PerListing:N0}"
            : listing != null ? "Unplanned listing" : marketMark == "?" ? "Market slots have never been read."
            : plan == null ? "Free slot" : $"Reserved for {plan.PerListing:N0} units");
        if (health.UndercuttingPrice is { } price) ImGui.TextColored(UndercutColor, $"Undercut: lowest scanned price {price:N0} gil");
        if (health.Stuck) ImGui.TextColored(StuckColor, $"Stuck: listed {health.DaysListed} days, threshold {Math.Max(1, configuration.StuckListingDays)}");
        if (health.Stale) ImGui.TextColored(Muted, record?.MissingSinceUtc != null ? "Stale: listing missing on the last read."
            : $"Stale: market last read {seller.LastRefreshedUtc.ToLocalTime():g}");
        if (held) ImGui.TextColored(HoldColor, "Hold: skipped by automatic cycles and Fill to plan.");
        if (paused) ImGui.TextColored(Muted, "Paused: Stock tracking is disabled for this item.");
        if (id != 0)
        {
            var stock = entry ?? new LowStockEntry { ItemId = id, IsHq = hq };
            var (inventory, listings) = service.StockReadings(stock, boardRoster, now);
            if (!string.IsNullOrWhiteSpace(entry?.Note)) ImGui.TextWrapped(entry.Note);
            if (record != null) ImGui.TextUnformatted($"Listed since {record.FirstSeenUtc.ToLocalTime():g}");
            ImGui.TextUnformatted($"Listed {StockTotal(listings)}");
            ImGui.TextUnformatted(FormatSupply(stock, inventory).Detail);
            ImGui.TextUnformatted($"Floor {service.EffectiveFloor(id, hq):N0} gil");
        }
        ImGui.EndTooltip();
    }
}
