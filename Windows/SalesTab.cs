using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Restocker.Market;
using Restocker.Localization;

namespace Restocker.Windows;

/// <summary>Fork: listing lifecycles from <see cref="ListingTracker"/> (active first, then most recently ended).</summary>
public sealed class SalesTab
{
    private string filter = string.Empty;
    private bool showEnded = true;
    private int period;
    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                             | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;

    public void Draw()
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return;
        var host = plugin.Tracker;
        DailyMarketSalesDisplay.Draw(plugin.MarketWatcher.HomeWorldId);
        if (!ImGui.BeginTabBar("##sales-sections")) return;
        var now = DateTime.UtcNow;
        if (ImGui.BeginTabItem(Strings.SalesListings + "###sales-listings"))
        {
            DrawListings(host.Tracker, now);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Strings.SalesVelocity + "###sales-velocity"))
        {
            DrawVelocity(host.Tracker, now);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Strings.StuckListings + "###sales-stuck"))
        {
            DrawStuck(host.Tracker, now);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawListings(ListingTracker tracker, DateTime now)
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##sales-filter", "Filter item / retainer / competitor", ref filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Show sold / gone", ref showEnded);
        ImGui.SameLine();
        if (ImGui.SmallButton("Open CSV folder"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Plugin.PluginInterface.GetPluginConfigDirectory(),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Restocker] open folder failed"); }
        }

        // Fork: the visible period filters ended rows and the realized profit total together.
        string[] periods = [Strings.SalesAllTime, Strings.Velocity24h, Strings.Velocity3d, Strings.Velocity7d, Strings.Velocity30d];
        ImGui.SetNextItemWidth(145);
        ImGui.Combo(Strings.SalesPeriod + "###sales-period", ref period, periods, periods.Length);
        int[] periodDays = [0, 1, 3, 7, 30];
        var since = now.AddDays(-periodDays[period]);

        var rows = tracker.Records
            .Where(r => showEnded || r.Outcome == ListingTracker.Active)
            .Where(r => period == 0 || r.Outcome == ListingTracker.Active
                || (r.RecordedSaleUtc ?? r.LastSeenUtc) >= since && (r.RecordedSaleUtc ?? r.LastSeenUtc) <= now)
            .Where(r => filter.Length == 0
                        || r.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.RetainerName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Competitors.Keys.Any(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Outcome == ListingTracker.Active ? 0 : 1)
            .ThenByDescending(r => r.EndedUtc ?? r.LastSeenUtc)
            .ToList();

        var sold = rows.Where(r => r.Outcome == ListingTracker.Sold).ToList();
        if (sold.Count > 0)
        {
            ImGui.TextDisabled($"Sold: {sold.Count} listings, {sold.Sum(r => r.SoldTotalGil):N0} gil, avg {sold.Average(r => r.DaysListed):0.0} days, avg {sold.Average(r => r.RepriceCount):0.0} reprices");
            var returns = sold.Select(SaleReturn.Realized).ToList();
            ImGui.TextUnformatted(string.Format(Strings.ProfitEarningTotals,
                returns.Sum(r => r.Profit).GetValueOrDefault().ToString("N2"),
                returns.Sum(r => r.Earning).GetValueOrDefault().ToString("N2")));
            var incomplete = returns.Count(r => r.Profit == null && r.Earning == null);
            if (incomplete > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(string.Format(Strings.SalesProfitIncomplete, incomplete));
            }
        }

        if (!ImGui.BeginTable("##sales", 13, TableFlags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 0.1f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("First", ImGuiTableColumnFlags.WidthStretch, 0.09f);
        ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.WidthStretch, 0.09f);
        ImGui.TableSetupColumn("Reprices", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Undercut by", ImGuiTableColumnFlags.WidthStretch, 0.22f);
        ImGui.TableSetupColumn("Days", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Sold for", ImGuiTableColumnFlags.WidthStretch, 0.09f);
        ImGui.TableSetupColumn(Strings.Buyer);
        ImGui.TableSetupColumn(Strings.SaleTime);
        ImGui.TableSetupColumn(Strings.SalesRealizedProfit);
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.ItemName + (r.IsHq ? " (HQ)" : ""));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.RetainerName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Quantity.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.HistoryOnly ? "" : r.FirstPrice.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.HistoryOnly ? "" : r.LastPrice.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{r.RepriceCount} ({r.UndercutCount})");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("price changes (times a competitor was below us)");
            ImGui.TableNextColumn();
            var by = string.Join(", ", r.Competitors.Values.Where(c => c.TimesBelowUs > 0)
                .OrderByDescending(c => c.TimesBelowUs).Select(c => $"{c.Seller} {c.MaxQuantity}x ({c.TimesBelowUs})"));
            ImGui.TextUnformatted(by);
            if (ImGui.IsItemHovered() && r.Competitors.Count > 0)
                ImGui.SetTooltip(string.Join("\n", r.Competitors.Values.OrderBy(c => c.LowestPrice)
                    .Select(c => $"{c.Seller}: up to {c.MaxQuantity}x, lowest {c.LowestPrice:N0}, seen {c.TimesSeen}, below us {c.TimesBelowUs}")));
            ImGui.TableNextColumn();
            var days = r.DaysListed ?? (now - r.FirstSeenUtc).TotalDays;
            ImGui.TextUnformatted(r.HistoryOnly ? "" : days.ToString("0.0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Outcome);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.SoldTotalGil > 0 ? r.SoldTotalGil.ToString("N0") : "");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.BuyerName ?? "");
            ImGui.TableNextColumn(); ImGui.TextUnformatted((r.SaleTimeUtc ?? r.SoldRecordedUtc)?.ToLocalTime().ToString("G") ?? "");
            ImGui.TableNextColumn();
            if (r.Outcome == ListingTracker.Sold) DrawRealizedProfit(r);
        }
        ImGui.EndTable();
    }

    private static void DrawRealizedProfit(ListingRecord sale)
    {
        var returns = SaleReturn.Realized(sale);
        if (returns.Profit is { } profit)
        {
            ImGui.TextUnformatted(profit.ToString("N2") + (returns.Earning.HasValue ? "*" : ""));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(Strings.SalesProfitDetails, sale.ConsumedCost, sale.UnknownCostUnits));
        }
        if (returns.Earning is { } earning)
        {
            if (returns.Profit.HasValue) ImGui.SameLine();
            SaleReturnDisplay.DrawEarning(earning, "N2");
        }
        if (returns.Profit == null && returns.Earning == null)
            ImGui.TextDisabled("—");
    }

    private static void DrawVelocity(ListingTracker tracker, DateTime now)
    {
        var stock = Plugin.Instance.LowStock.GetTrackedStock();
        var rows = tracker.GetSales().GroupBy(s => (s.ItemId, s.IsHq))
            .Select(g => (g.Key, Name: g.First().ItemName, Velocity: SalesVelocity.Calculate(g, now)));

        ImGui.TextDisabled(Strings.VelocityTableLegend);
        if (!ImGui.BeginTable("##velocity-table", 8, TableFlags | ImGuiTableFlags.Sortable)) return;
        const ImGuiTableColumnFlags noSort = ImGuiTableColumnFlags.NoSort;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColItem, noSort | ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn(Strings.Velocity24h, noSort);
        ImGui.TableSetupColumn(Strings.Velocity3d, noSort);
        ImGui.TableSetupColumn(Strings.Velocity7d, ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Strings.Velocity30d, noSort);
        ImGui.TableSetupColumn(Strings.VelocityHours7dShort, noSort);
        ImGui.TableSetupColumn(Strings.VelocityHours30dShort, noSort);
        ImGui.TableSetupColumn(Strings.VelocityStockDays, noSort);
        ImGui.TableHeadersRow();

        var sort = ImGui.TableGetSortSpecs();
        var ascending = sort.SpecsCount > 0 && sort.Specs.SortDirection == ImGuiSortDirection.Ascending;
        var sorted = ascending ? rows.OrderBy(r => r.Velocity.Last7d.Units) : rows.OrderByDescending(r => r.Velocity.Last7d.Units);
        foreach (var row in sorted.ThenBy(r => r.Name).ThenBy(r => r.Key.IsHq))
        {
            var velocity = row.Velocity;
            var supply = stock.GetValueOrDefault(row.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Name + (row.Key.IsHq ? " (HQ)" : ""));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(SalesVelocityDisplay.CompactCounts(velocity.Last24h));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(SalesVelocityDisplay.CompactCounts(velocity.Last3d));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(SalesVelocityDisplay.CompactCounts(velocity.Last7d));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(SalesVelocityDisplay.CompactCounts(velocity.Last30d));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(SalesVelocityDisplay.Number(velocity.Last7d.AverageHoursBetweenSales));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(SalesVelocityDisplay.Number(velocity.Last30d.AverageHoursBetweenSales));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SalesVelocityDisplay.Number(velocity.DaysOfStock(supply.Listed, supply.Available)) + (supply.Provisional ? " ?" : ""));
            if (supply.Provisional && ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockProvisional);
        }
        ImGui.EndTable();
    }

    private static void DrawStuck(ListingTracker tracker, DateTime now)
    {
        var rows = tracker.Records.Where(r => ListingTracker.IsStuck(r, now, Plugin.Configuration.StuckListingDays))
            .OrderBy(r => r.FirstSeenUtc).ToList();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(Strings.StuckEmpty);
            return;
        }
        if (!ImGui.BeginTable("##stuck-table", 6, TableFlags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColItem);
        ImGui.TableSetupColumn(Strings.StuckRetainer);
        ImGui.TableSetupColumn(Strings.ColTotalQty);
        ImGui.TableSetupColumn(Strings.StuckDays);
        ImGui.TableSetupColumn(Strings.StuckReprices);
        ImGui.TableSetupColumn(Strings.ColCurrentPrice);
        ImGui.TableHeadersRow();
        foreach (var listing in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.ItemName + (listing.IsHq ? " (HQ)" : ""));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.RetainerName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.Quantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted((now - listing.FirstSeenUtc).TotalDays.ToString("N1"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.RepriceCount.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.LastPrice > 0 ? listing.LastPrice.ToString("N0") : Strings.PriceUnknown);
        }
        ImGui.EndTable();
    }
}
