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
        boardRecords = Plugin.Instance.Tracker.Tracker.ActiveListings.ToLookup(r => (r.RetainerKey, r.Slot, r.ItemId, r.IsHq));
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
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (27 * em)), 1, 3);
        if (!ImGui.BeginTable("##retainer-board", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
            new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
        foreach (var seller in boardRoster)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(seller.Key);
            var marketMark = DrawBoardHeader(seller, now, em);
            DrawFillButton(seller);
            DrawBoardSlots(seller, marketMark, now, em);
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (noteDirty && !ImGui.IsAnyItemActive()) { configuration.Save(); noteDirty = false; }
    }

    private string DrawBoardHeader(RetainerSnapshot seller, DateTime now, float em)
    {
        var summary = boardAssignments[seller.Key];
        var marketMark = Marker(seller.LastRefreshedUtc, now);
        var bagMark = Marker(InventorySnapshot.OldestRefresh(RetainerWatcher.RetainerPages.Select(t => (uint)t),
            seller.InventoryRefreshedUtc, DateTime.MinValue), now);
        var occupied = seller.Inventory.Count(i => RetainerWatcher.RetainerPages.Contains((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)i.ContainerId));
        ImGui.TextUnformatted(seller.RetainerName);
        ImGui.SameLine();
        ImGui.TextDisabled($"Bag {(bagMark == "?" ? "?" : $"{occupied}{bagMark}")} slots" + (seller.Gil is { } gil ? $" · {gil:N0} gil" : ""));
        ImGui.ProgressBar(summary.Used / (float)Planner.MaxListingSlots, new Vector2(-1, em),
            $"{(marketMark == "?" ? "?" : $"{summary.Used}{marketMark}")}/20 market slots");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(seller.LastRefreshedUtc == DateTime.MinValue ? "Market slots have never been read."
                : $"Last read {seller.LastRefreshedUtc.ToLocalTime():g}{(marketMark == "!" ? " (stale)" : "")}");
        if (marketMark == "?") ImGui.TextDisabled("Used ? · Reserved ? · Free ?");
        else
        {
            ImGui.TextUnformatted($"Used {summary.Used} · Reserved {summary.Reserved} · Free {summary.Free}");
            ImGui.TextUnformatted($"Missing {summary.Missing} · Unplanned {summary.Extra} · Blocking {summary.Blocking}");
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

    private void DrawBoardSlots(RetainerSnapshot seller, string marketMark, DateTime now, float em)
    {
        if (!ImGui.BeginTable("##slots", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame)) return;
        foreach (var cell in boardCells[seller.Key])
        {
            ImGui.TableNextColumn();
            ImGui.PushID(cell.Index);
            if (cell.Listing is { } listing)
                DrawBoardListing(seller, cell, listing, marketMark, now, em);
            else
            {
                ImGui.BeginGroup();
                var state = marketMark == "?" ? (cell.Plan == null ? "?" : "Planned ?")
                    : cell.Plan == null ? "Free" : "Reserved";
                var clicked = ImGui.Selectable($"{cell.Index + 1} · {state}##slot");
                if (cell.Plan is { } p)
                {
                    DrawItemIcon(p.ItemId, em);
                    ImGui.TextWrapped($"{ItemName(p.ItemId)}{(p.IsHq ? " HQ" : "")} ×{p.PerListing}");
                }
                ImGui.EndGroup();
                if (clicked || ImGui.IsItemClicked()) OpenSlot(cell);
            }
            DrawSlotPopup(seller, cell, now, em);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static void DrawItemIcon(uint id, float size)
    {
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row)
            && Plugin.Textures.GetFromGameIcon(new GameIconLookup(row.Icon)).GetWrapOrDefault() is { } icon)
            ImGui.Image(icon.Handle, new Vector2(size));
    }

    private void DrawBoardListing(RetainerSnapshot seller, BoardSlot cell, ListingEntry listing, string marketMark, DateTime now, float em)
    {
        var key = (listing.ItemId, listing.IsHQ);
        var record = boardRecords![(seller.Key, listing.ListingIndex, key.ItemId, key.IsHQ)].FirstOrDefault();
        var entry = boardSettings.GetValueOrDefault(key);
        var scan = Plugin.Instance.ListingScans.Get(seller.Key, listing.ListingIndex, key.ItemId, key.IsHQ);
        var flags = new List<string>();
        if (scan is { Offers.Count: > 0 } s && s.Offers[0].Price < listing.UnitPrice) flags.Add("Undercut");
        if (record != null && ListingTracker.IsStuck(record, now, configuration.StuckListingDays)) flags.Add("Stuck");
        if (marketMark == "!" || record?.MissingSinceUtc != null) flags.Add("Stale");
        if (configuration.IsHeld(key.ItemId, key.IsHQ)) flags.Add("Hold");
        if (entry is { Enabled: false }) flags.Add("Paused");
        if (flags.Count > 0)
        {
            var color = flags[0] switch
            {
                "Undercut" => new Vector4(.55f, .18f, .13f, .68f),
                "Hold" => new Vector4(.18f, .32f, .52f, .68f),
                "Paused" => new Vector4(.33f, .27f, .37f, .68f),
                _ => new Vector4(.48f, .32f, .13f, .68f),
            };
            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(color));
        }
        var name = ItemName(key.ItemId);
        var state = cell.Filled ? "Planned" : cell.Blocking ? $"Blocking plan for {ItemName(cell.Plan!.ItemId)}" : "Unplanned";
        var display = state + " · " + (key.IsHQ ? "HQ " : "") + name;
        ImGui.BeginGroup();
        var clicked = ImGui.Selectable($"{cell.Index + 1} · {(cell.Filled ? "Filled" : cell.Blocking ? "Blocking" : "Unplanned")}##slot");
        DrawItemIcon(key.ItemId, em);
        ImGui.TextWrapped($"{name}{(key.IsHQ ? " HQ" : "")} ×{listing.Quantity}");
        ImGui.TextDisabled($"{listing.UnitPrice:N0} g");
        ImGui.EndGroup();
        if (clicked || ImGui.IsItemClicked()) OpenSlot(cell);
        if (ImGui.IsItemHovered())
        {
            var stockItem = entry ?? new LowStockEntry { ItemId = key.ItemId, IsHq = key.IsHQ };
            var (inventory, listings) = service.StockReadings(stockItem, boardRoster, now);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Slot {cell.Index + 1} · {display}");
            if (flags.Count > 0) ImGui.TextUnformatted(string.Join(" · ", flags));
            if (!string.IsNullOrWhiteSpace(entry?.Note)) ImGui.TextWrapped(entry.Note);
            if (record != null) ImGui.TextUnformatted($"Listed since {record.FirstSeenUtc.ToLocalTime():g}");
            ImGui.TextUnformatted($"Listed {StockTotal(listings)}");
            ImGui.TextUnformatted(FormatSupply(stockItem, inventory).Detail);
            ImGui.TextUnformatted($"Floor {service.EffectiveFloor(key.ItemId, key.IsHQ):N0} gil");
            ImGui.EndTooltip();
        }
    }
}
