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

    private void RefreshBoard(DateTime now)
    {
        var cid = Plugin.PlayerState.ContentId;
        if (boardCharacter == cid && now < nextBoardRefresh) return;
        boardCharacter = cid;
        nextBoardRefresh = now.AddSeconds(1);
        boardRoster = service.CurrentRetainers();
        boardRecords = Plugin.Instance.Tracker.Tracker.ActiveListings.ToLookup(r => (r.RetainerKey, r.Slot, r.ItemId, r.IsHq));
        boardSettings = configuration.LowStock.DistinctBy(e => (e.ItemId, e.IsHq)).ToDictionary(e => (e.ItemId, e.IsHq));
        boardAssignments = boardRoster.ToDictionary(s => s.Key, s => configuration.SlotPlans.Layout(s));
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
            DrawPlanEditor(seller);
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
    private int editingRow;

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

    private void DrawPlanEditor(RetainerSnapshot seller)
    {
        var rows = configuration.SlotPlans.Rows(seller.Key).ToList();
        void Save()
        {
            if (!configuration.SlotPlans.TrySave(seller.Key, rows, out planError)) return;
            configuration.Save();
            nextBoardRefresh = DateTime.MinValue;
        }
        void Open(int index)
        {
            editingRow = index;
            draft = index < 0 ? new(0, false, 1, 1) : rows[index];
            planSearch = "";
            planError = null;
            ImGui.OpenPopup("Edit plan");
        }

        if (ImGui.SmallButton("Add item")) Open(-1);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ImGui.PushID(i);
            ImGui.TextWrapped($"{ItemName(row.ItemId)}{(row.IsHq ? " HQ" : "")} · {row.Slots} × {row.PerListing}");
            var edit = ImGui.SmallButton("Edit");
            ImGui.SameLine();
            if (ImGui.SmallButton("Settings")) ImGui.OpenPopup("stock");
            if (ImGui.BeginPopup("stock"))
            {
                DrawBoardStockSettings(row.ItemId, row.IsHq);
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            var up = ImGui.SmallButton("Up") && i > 0;
            ImGui.SameLine();
            var down = ImGui.SmallButton("Down") && i + 1 < rows.Count;
            ImGui.SameLine();
            var remove = ImGui.SmallButton("Remove");
            ImGui.PopID();
            if (edit) Open(i);
            if (up || down)
            {
                var other = up ? i - 1 : i + 1;
                (rows[i], rows[other]) = (rows[other], rows[i]);
                Save();
                break;
            }
            if (remove) { rows.RemoveAt(i); Save(); break; }
        }
        if (!ImGui.BeginPopup("Edit plan")) return;
        if (editingRow < 0)
        {
            ImGui.InputTextWithHint("##find", "Search stock or inventory", ref planSearch, 80);
            var character = configuration.Characters.GetValueOrDefault(CharacterSnapshot.MakeKey(boardCharacter));
            var inventory = boardRoster.SelectMany(r => r.Inventory)
                .Concat(character?.Bag ?? []).Concat(character?.Saddlebag ?? []).Concat(character?.PremiumSaddlebag ?? []);
            var choices = boardSettings.Keys.Concat(inventory.Where(i => i.IsListable).Select(i => (i.ItemId, i.IsHQ))).Distinct();
            if (ImGui.BeginListBox("##items"))
            {
                foreach (var (id, hq) in choices)
                {
                    var name = ItemName(id) + (hq ? " HQ" : "");
                    if (!name.Contains(planSearch, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImGui.Selectable($"{name}##{id}-{hq}", draft.Matches(id, hq))) draft = draft with { ItemId = id, IsHq = hq };
                }
                ImGui.EndListBox();
            }
        }
        if (draft.ItemId != 0) ImGui.TextUnformatted(ItemName(draft.ItemId) + (draft.IsHq ? " HQ" : ""));
        var slots = draft.Slots;
        var qty = draft.PerListing;
        ImGui.InputInt("Slots", ref slots);
        ImGui.InputInt("Units per listing", ref qty);
        draft = draft with { Slots = slots, PerListing = qty };
        var total = rows.Where((_, i) => i != editingRow).Sum(r => (long)r.Slots) + slots;
        if (total > 20) ImGui.TextColored(new Vector4(1, .4f, .37f, 1), $"{total}/20 slots. Reduce slots to save.");
        if (ImGui.Button("Save") && draft.ItemId != 0)
        {
            if (editingRow < 0) rows.Add(draft);
            else rows[editingRow] = draft;
            if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(draft.ItemId, out var item)
                && qty > RetainerWatcher.ResolveMaxListingStack(item)) planError = "Quantity exceeds this item's listing limit.";
            else if (configuration.SlotPlans.TrySave(seller.Key, rows, out planError))
            {
                if (!configuration.LowStock.Any(e => draft.Matches(e.ItemId, e.IsHq)))
                    configuration.LowStock.Add(new() { ItemId = draft.ItemId, IsHq = draft.IsHq });
                configuration.Save();
                nextBoardRefresh = DateTime.MinValue;
                ImGui.CloseCurrentPopup();
            }
        }
        if (planError != null) ImGui.TextColored(new Vector4(1, .4f, .37f, 1), planError);
        ImGui.EndPopup();
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
        foreach (var cell in boardAssignments[seller.Key].Slots)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(cell.Index);
            if (cell.Listing is { } listing)
                DrawBoardListing(seller, cell.Index, listing, marketMark, now, em);
            else if (cell.Plan is { } p)
            {
                ImGui.TextDisabled($"{cell.Index + 1} · {(marketMark == "?" ? "Planned ?" : "Reserved")}");
                ImGui.BeginDisabled();
                DrawItemIcon(p.ItemId, em);
                ImGui.TextWrapped($"{ItemName(p.ItemId)}{(p.IsHq ? " HQ" : "")} ×{p.PerListing}");
                ImGui.EndDisabled();
            }
            else ImGui.TextDisabled($"{cell.Index + 1} · {(marketMark == "?" ? "?" : "Free")}");
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

    private void DrawBoardListing(RetainerSnapshot seller, int slot, ListingEntry listing, string marketMark, DateTime now, float em)
    {
        var key = (listing.ItemId, listing.IsHQ);
        var record = boardRecords![(seller.Key, slot, key.ItemId, key.IsHQ)].FirstOrDefault();
        var entry = boardSettings.GetValueOrDefault(key);
        var scan = Plugin.Instance.ListingScans.Get(seller.Key, slot, key.ItemId, key.IsHQ);
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
        var cell = boardAssignments[seller.Key].Slots[slot];
        var state = cell.Filled ? "Planned" : cell.Blocking ? $"Blocking plan for {ItemName(cell.Plan!.ItemId)}" : "Unplanned";
        var display = state + " · " + (key.IsHQ ? "HQ " : "") + name;
        if (ImGui.Selectable($"{slot + 1} · {(cell.Filled ? "Filled" : cell.Blocking ? "Blocking" : "Unplanned")}##slot"))
            ImGui.OpenPopup("listing");
        if (ImGui.IsItemHovered())
        {
            var stockItem = entry ?? new LowStockEntry { ItemId = key.ItemId, IsHq = key.IsHQ };
            var (inventory, listings) = service.StockReadings(stockItem, boardRoster, now);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Slot {slot + 1} · {display}");
            if (flags.Count > 0) ImGui.TextUnformatted(string.Join(" · ", flags));
            if (!string.IsNullOrWhiteSpace(entry?.Note)) ImGui.TextWrapped(entry.Note);
            if (record != null) ImGui.TextUnformatted($"Listed since {record.FirstSeenUtc.ToLocalTime():g}");
            ImGui.TextUnformatted($"Listed {StockTotal(listings)}");
            ImGui.TextUnformatted(FormatSupply(stockItem, inventory).Detail);
            ImGui.TextUnformatted($"Floor {service.EffectiveFloor(key.ItemId, key.IsHQ):N0} gil");
            ImGui.EndTooltip();
        }
        DrawItemIcon(key.ItemId, em);
        ImGui.TextWrapped($"{name}{(key.IsHQ ? " HQ" : "")} ×{listing.Quantity}");
        ImGui.TextDisabled($"{listing.UnitPrice:N0} g");
        if (ImGui.BeginPopup("listing"))
        {
            DrawBoardStockSettings(key.ItemId, key.IsHQ);
            if (ImGui.SmallButton("Show in Stock list"))
            {
                focusedItem = key;
                search = name;
                kindFilter = StockKind.None;
                statusFilter = StockStatus.None;
                categoryFilter = null;
                onlyLow = false;
                configuration.ShowRetainerBoard = false;
                configuration.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }
}
