using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Restocker.Common;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>Fork: watched items, their listed / available units, alert threshold and a Restock button.</summary>
public sealed class LowStockTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private readonly LowStockService service;
    private string search = string.Empty;
    private StockKind kindFilter;
    private StockStatus statusFilter;
    private readonly Dictionary<(uint, bool), int> restockTo = new();
    /// <summary>Price typed or picked for the next restock (session only).</summary>
    private readonly Dictionary<(uint, bool), long> restockPrice = new();
    private string? lastMessage;

    public LowStockTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog, LowStockService service)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
        this.service = service;
    }

    private static string ItemName(uint id)
        => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}";

    public void Draw()
    {
        DrawAdd();
        ImGui.Separator();
        if (!string.IsNullOrEmpty(lastMessage)) ImGui.TextColored(new Vector4(0.95f, 0.8f, 0.4f, 1f), lastMessage);
        DrawTable();
    }

    /// <summary>Search over items we list or hold (retainers, bag, saddlebag) and add them to the watch list.</summary>
    private void DrawAdd()
    {
        ImGui.TextUnformatted("Watch an item:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##lowstock-search", "Search item you list or hold", ref search, 64);
        if (search.Length < 2) return;

        var known = configuration.Snapshots.Values.SelectMany(s => s.Listings.Select(l => (l.ItemId, l.IsHQ))
                .Concat(s.Inventory.Select(i => (i.ItemId, i.IsHQ))))
            .Concat(configuration.Characters.Values.SelectMany(c => c.Bag.Concat(c.Saddlebag).Concat(c.PremiumSaddlebag)
                .Where(i => i.IsListable).Select(i => (i.ItemId, i.IsHQ))))
            .Distinct()
            .Select(k => (k.ItemId, k.IsHQ, Name: ItemName(k.ItemId)))
            .Where(k => k.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(k => !configuration.LowStock.Any(e => e.ItemId == k.ItemId && e.IsHq == k.IsHQ))
            .OrderBy(k => k.Name).Take(10).ToList();

        foreach (var k in known)
        {
            if (ImGui.SmallButton($"+ {k.Name}{(k.IsHQ ? " (HQ)" : "")}##add-{k.ItemId}-{k.IsHQ}"))
            {
                configuration.LowStock.Add(new LowStockEntry { ItemId = k.ItemId, IsHq = k.IsHQ });
                configuration.Save();
                search = string.Empty;
            }
        }
        if (known.Count == 0) ImGui.TextDisabled("no match among items on your retainers / bag / saddlebag (open the bell once to refresh)");
    }

    private void DrawTable()
    {
        if (configuration.LowStock.Count == 0)
        {
            ImGui.TextDisabled("No watched items. Alerts go to the Restocky chat channel when listed units drop to the threshold.");
            return;
        }

        DrawTagCombo("##kind-filter", Strings.StockKindLabel, ref kindFilter, Strings.StockTag, Strings.AllStockTags, 140);
        ImGui.SameLine();
        DrawTagCombo("##status-filter", Strings.StockStatusLabel, ref statusFilter, Strings.StockTag, Strings.AllStockTags, 140);

        var bellOpen = AddonHelper.IsOpen("RetainerList");
        var cid = Plugin.PlayerState.ContentId;
        var retainerNames = service.CurrentRetainerNames();
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##lowstock", 8, flags, new Vector2(0, Math.Max(120f, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.StockItem, ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn(Strings.StockCounts, ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn(Strings.StockThreshold, ImGuiTableColumnFlags.WidthFixed, 49);
        ImGui.TableSetupColumn(Strings.StockPerListing, ImGuiTableColumnFlags.WidthFixed, 63);
        ImGui.TableSetupColumn(Strings.StockPrice, ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn(Strings.StockMarket, ImGuiTableColumnFlags.WidthFixed, 111);
        ImGui.TableSetupColumn(Strings.StockTarget, ImGuiTableColumnFlags.WidthFixed, 91);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 21);
        ImGui.TableHeadersRow();

        LowStockEntry? remove = null;
        var now = DateTime.UtcNow;
        var tracker = Plugin.Instance.Tracker.Tracker;
        foreach (var e in configuration.LowStock.Where(e => e.MatchesTags(kindFilter, statusFilter)))
        {
            var id = $"{e.ItemId}-{e.IsHq}";
            var listed = service.ListedUnits(e.ItemId, e.IsHq);
            var avail = service.Sources(e.ItemId, e.IsHq).Sum(s => s.Available);
            var low = listed <= e.Threshold;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (low) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.4f, 1f));
            ImGui.TextWrapped(ItemName(e.ItemId) + (e.IsHq ? " (HQ)" : ""));
            if (low) ImGui.PopStyleColor();
            ImGui.PushID(id);
            DrawRowOptions(e, cid, retainerNames);
            ImGui.PopID();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listed:N0} / {avail:N0}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockCountsHint);

            ImGui.TableNextColumn();
            var th = e.Threshold;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##th-{id}", ref th, 0)) { e.Threshold = Math.Max(0, th); configuration.Save(); }
            var thresholdHovered = ImGui.IsItemHovered();
            var velocity = SalesVelocity.Calculate(tracker.GetSales(e.ItemId, e.IsHq), now);
            ImGui.TextDisabled($"≈{velocity.SuggestedThreshold:N0}");
            if (thresholdHovered || ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(Strings.SuggestedThreshold, velocity.SuggestedThreshold));

            ImGui.TableNextColumn();
            var per = e.PerListing;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##per-{id}", ref per, 0)) { e.PerListing = Math.Max(0, per); configuration.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"0 = same as your current listings ({service.PerListing(e)})");

            // Price for this restock: typed by the user, or picked from the market suggestion with "Use".
            ImGui.TableNextColumn();
            var key = (e.ItemId, e.IsHq);
            if (!restockPrice.TryGetValue(key, out var chosen)) chosen = service.Price(e);
            // Fork: InputScalar with null step refs crashes in Dalamud's binding; game prices fit in int.
            var price = (int)Math.Clamp(chosen, 0, int.MaxValue);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##price-{id}", ref price, 0))
                restockPrice[key] = Math.Max(0, price);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Price for the new listings. The default uses your current listing or market price and respects the floor.");

            ImGui.TableNextColumn();
            var sug = service.Suggestion(e);
            if (sug is { Price: > 0 } s)
            {
                var age = DateTime.UtcNow - s.FetchedUtc;
                if (ImGui.SmallButton($"Use {s.Price:N0}##use-{id}")) restockPrice[key] = s.Price;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Market lowest -{configuration.UndercutGil}, subject to the price floor. Fetched {(int)age.TotalMinutes} min ago.");
            }
            else
            {
                ImGui.TextDisabled(sug == null ? "not fetched" : configuration.MatchQuality.For(e.ItemId, e.IsHq)
                    ? Strings.NoCompetitor(e.IsHq) : "no competitor");
            }
            var compareFrom = service.RetainerToCompareFrom(e);
            var canCheck = bellOpen && !executor.IsRunning && compareFrom != null;
            if (!canCheck) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Check##chk-{id}") && compareFrom != null)
            {
                executor.OnFetchMarketCompleted = () =>
                {
                    if (configuration.MatchQuality.For(e.ItemId, e.IsHq) &&
                        service.Suggestion(e) is { Price: 0 } empty && empty.FetchedUtc >= executor.JobFetchStartUtc)
                        Plugin.ChatPrint($"{ItemName(e.ItemId)}: {Strings.NoCompetitor(e.IsHq)}");
                };
                executor.OnFetchJobCompleted = null;
                executor.StartFetchMarketPricesForRetainer(compareFrom);
            }
            if (!canCheck) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(compareFrom == null
                    ? "No listing of this item on your retainers to compare prices from; type a price."
                    : bellOpen ? "Fetch live market prices from the retainer that lists it" : "open the summoning bell first");

            ImGui.TableNextColumn();
            var to = restockTo.TryGetValue((e.ItemId, e.IsHq), out var t) ? t : e.Target;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##to-{id}", ref to, 0))
            {
                to = Math.Max(0, to);
                restockTo[(e.ItemId, e.IsHq)] = to;
                e.Target = to;
                configuration.Save();
            }

            var topUps = listed > 0 && avail > 0 && configuration.TopUpPartialStacksOnRestock;
            var can = bellOpen && !executor.IsRunning && (to > listed || (topUps && service.PlanRestock(e, listed, 0).Count > 0));
            var userPrices = listed == 0; // nothing listed: the game's price window decides
            if (!can) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"{(userPrices ? Strings.StockListAndPrice : Strings.StockRestock)}##rs-{id}"))
            {
                if (userPrices) RestockWithHandover(e, to);
                else Restock(e, to, restockPrice.TryGetValue(key, out var rp) ? rp : service.Price(e));
            }
            if (!can) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!bellOpen ? "open the summoning bell first"
                    : userPrices ? $"Nothing listed: opens the game's 'Put up for sale' window and waits for your price; the rest (up to {to}) follow at that price"
                    : topUps ? string.Format(Strings.TopUpRestockHint, to)
                    : $"List up to {to} units now (retainer inventory, then bag, then saddlebag) at the price on this row");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##rm-{id}")) remove = e;
        }
        ImGui.EndTable();

        if (remove != null)
        {
            configuration.LowStock.Remove(remove);
            configuration.Save();
        }
    }

    private void DrawRowOptions(LowStockEntry entry, ulong cid, IReadOnlyList<string> names)
    {
        var kind = entry.Kind;
        var status = entry.Status;
        if (kind != StockKind.None)
        {
            var color = kind switch
            {
                StockKind.Flip => new Vector4(1f, 0.75f, 0.3f, 1f),
                StockKind.Gather => new Vector4(0.5f, 0.9f, 0.6f, 1f),
                _ => new Vector4(0.6f, 0.75f, 1f, 1f),
            };
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            DrawTagButton(Strings.StockTag(kind));
            ImGui.PopStyleColor();
        }
        if (status != StockStatus.None) DrawTagButton($"[{Strings.StockTag(status)}]");
        if (kind == StockKind.None && status == StockStatus.None) DrawTagButton(Strings.StockTags);
        if (ImGui.BeginPopup("##tags"))
        {
            var changed = DrawTagCombo("##kind", Strings.StockKindLabel, ref kind, Strings.StockTag, Strings.NoStockTag, 150);
            changed |= DrawTagCombo("##status", Strings.StockStatusLabel, ref status, Strings.StockTag, Strings.NoStockTag, 150);
            if (changed)
            {
                entry.Kind = kind;
                entry.Status = status;
                configuration.Save();
            }
            ImGui.EndPopup();
        }

        var selected = entry.PreferredRetainers.GetValueOrDefault(cid);
        var seller = $"{Strings.SellOn}: {selected ?? Strings.ResetFloorToAuto}";
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(cid == 0);
        if (ImGui.BeginCombo("##seller", seller))
        {
            if (ImGui.Selectable(Strings.ResetFloorToAuto, selected == null))
            {
                entry.PreferredRetainers.Remove(cid);
                configuration.Save();
            }
            foreach (var name in names)
                if (ImGui.Selectable(name, selected == name))
                {
                    entry.PreferredRetainers[cid] = name;
                    configuration.Save();
                }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(seller);
    }

    private static void DrawTagButton(string label)
    {
        var style = ImGui.GetStyle();
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var width = ImGui.CalcTextSize(label).X + 2 * style.FramePadding.X + style.ItemSpacing.X;
        if (ImGui.GetItemRectMax().X + width <= right) ImGui.SameLine();
        if (ImGui.SmallButton(label)) ImGui.OpenPopup("##tags");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockTags);
    }

    private static bool DrawTagCombo<T>(string id, string label, ref T current, Func<T, string> text, string unselected, float width)
        where T : struct, Enum
    {
        var changed = false;
        ImGui.SetNextItemWidth(width);
        string Label(T value) => EqualityComparer<T>.Default.Equals(value, default) ? unselected : text(value);
        if (ImGui.BeginCombo(id, $"{label}: {Label(current)}"))
        {
            foreach (var value in Enum.GetValues<T>())
                if (ImGui.Selectable(Label(value), EqualityComparer<T>.Default.Equals(value, current)))
                {
                    current = value;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        return changed;
    }

    private void RestockWithHandover(LowStockEntry e, int target)
    {
        // Price 1 only so the planner produces the listing layout; the user prices the first one in the
        // game's sale window and the rest follow at that price.
        var actions = service.PlanRestock(e, target, 1, allowTopUps: false);
        if (actions.Count == 0)
        {
            var why = service.Sources(e.ItemId, e.IsHq).Count == 0 ? "none in retainer inventories / bag / saddlebag" : "no free market slot";
            lastMessage = $"{ItemName(e.ItemId)}: nothing to list: {why}";
            return;
        }
        lastMessage = $"{ItemName(e.ItemId)}: opening the game's sale window for you";
        Plugin.ChatPrint(lastMessage);
        executor.PrintPreferredRetainerFallbacks(actions);
        executor.StartRestockWithHandover(actions);
    }

    private void Restock(LowStockEntry e, int target, long price)
    {
        var actions = service.PlanRestock(e, target, price);
        if (actions.Count == 0)
        {
            var why = price <= 0 ? "no price (type one, or Check / Use the market price)"
                : service.Sources(e.ItemId, e.IsHq).Count == 0 ? "none in retainer inventories / bag / saddlebag"
                : "no free market slot";
            lastMessage = $"{ItemName(e.ItemId)}: nothing to list: {why}";
            return;
        }
        confirmDialog.Request(actions, list =>
        {
            var added = list.Where(a => a.TopUp == null).ToList();
            var topUps = list.Count - added.Count;
            var lines = new List<string>();
            if (added.Count > 0) lines.Add(string.Format(Strings.LowStockListing, ItemName(e.ItemId), added.Sum(a => a.Quantity), added.Count, price));
            if (topUps > 0) lines.Add(string.Format(Strings.TopUpCount, topUps));
            lastMessage = string.Join(" · ", lines);
            Plugin.ChatPrint(lastMessage);
            executor.StartApplyActions(list);
        });
    }
}
