using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
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
    private Dictionary<string, RetainerSlotSummary> boardAssignments = [];
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
        var preferred = boardSettings.Values.Where(e => !e.PreferredRetainers.ContainsKey(cid))
            .ToDictionary(e => e, e => service.PreferredRetainers(e).FirstOrDefault());
        boardAssignments = boardRoster.ToDictionary(s => s.Key, s => RetainerBoard.Summarize(s, boardSettings.Values,
            e => preferred.GetValueOrDefault(e), service.PerListing));
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
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (27 * em)), 1, 3);
        if (!ImGui.BeginTable("##retainer-board", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
            new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
        foreach (var seller in boardRoster)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(seller.Key);
            var marketMark = DrawBoardHeader(seller, now, em);
            DrawBoardSlots(seller, marketMark, now, em);
            ImGui.PopID();
        }
        ImGui.EndTable();
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
        var usage = marketMark == "?" ? "using ?, free ?" : $"using {summary.Used}{marketMark}, free {summary.Free}{marketMark}";
        var label = $"Assigned {summary.Assigned} slots, {usage}";
        if (summary.OverAssigned) ImGui.TextColored(new Vector4(1, .4f, .37f, 1), label);
        else ImGui.TextUnformatted(label);
        if (summary.Items.Count > 0 && ImGui.TreeNode("Assigned items"))
        {
            foreach (var assignment in summary.Items)
            {
                var item = assignment.Item;
                ImGui.TextWrapped($"{ItemName(item.ItemId)}{(item.IsHq ? " HQ" : "")}{(item.Enabled ? "" : " (paused)")} · {assignment.Needed} planned / {assignment.Used} used");
            }
            ImGui.TreePop();
        }
        return marketMark;
    }

    private string Marker(DateTime stamp, DateTime now) => service.Freshness(stamp, now).State switch
    {
        StockReadState.Fresh => "",
        StockReadState.NeverRead => "?",
        _ => "!",
    };

    private void DrawBoardSlots(RetainerSnapshot seller, string marketMark, DateTime now, float em)
    {
        if (ImGui.BeginTable("##slots", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Slot / item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 2.8f * em);
            ImGui.TableSetupColumn("Gil/unit", ImGuiTableColumnFlags.WidthFixed, 4.6f * em);
            ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, 2.5f * em);
            ImGui.TableHeadersRow();
            for (var slot = 0; slot < Planner.MaxListingSlots; slot++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var listing = seller.Listings.Find(l => l.ListingIndex == slot);
                if (listing == null)
                {
                    ImGui.TextDisabled($"{slot + 1,2}  {(marketMark == "?" ? "?" : "Empty")}");
                    continue;
                }
                DrawBoardListing(seller, slot, listing, marketMark, now, em);
            }
            ImGui.EndTable();
        }
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
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(color));
        }
        var name = ItemName(key.ItemId);
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(key.ItemId, out var row)
            && Plugin.Textures.GetFromGameIcon(new GameIconLookup(row.Icon)).GetWrapOrDefault() is { } icon)
        {
            ImGui.Image(icon.Handle, new Vector2(em));
            ImGui.SameLine(0, 2);
        }
        var display = (key.IsHQ ? "HQ " : "") + name;
        if (ImGui.Selectable($"{slot + 1,2}  {display}##slot-{slot}", false, ImGuiSelectableFlags.SpanAllColumns))
        {
            focusedItem = key;
            search = name;
            kindFilter = StockKind.None;
            statusFilter = StockStatus.None;
            categoryFilter = null;
            onlyLow = false;
            configuration.ShowRetainerBoard = false;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            var stockItem = entry ?? new LowStockEntry { ItemId = key.ItemId, IsHq = key.IsHQ };
            var (inventory, listings) = service.StockReadings(stockItem, boardRoster, now);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Slot {slot + 1} · {display}");
            if (flags.Count > 0) ImGui.TextUnformatted(string.Join(" · ", flags));
            if (!string.IsNullOrWhiteSpace(entry?.Note)) ImGui.TextWrapped(entry.Note);
            ImGui.TextUnformatted($"Listed {StockTotal(listings)}");
            ImGui.TextUnformatted(FormatSupply(stockItem, inventory).Detail);
            ImGui.TextUnformatted($"Floor {service.EffectiveFloor(key.ItemId, key.IsHQ):N0} gil");
            ImGui.EndTooltip();
        }
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{listing.Quantity:N0}");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{listing.UnitPrice:N0}");
        ImGui.TableNextColumn();
        var age = now - record?.FirstSeenUtc;
        ImGui.TextUnformatted(age is not { } a ? "?" : a.TotalDays >= 1 ? $"{(int)a.TotalDays}d" : $"{(int)a.TotalHours}h");
    }
}
