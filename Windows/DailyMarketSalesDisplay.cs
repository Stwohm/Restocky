using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

internal static class DailyMarketSalesDisplay
{
    internal static void Draw(uint worldId, uint? itemId = null)
    {
        int[] periods = [1, 3, 7];
        var config = Plugin.Configuration;
        var index = Math.Max(0, Array.IndexOf(periods, config.MarketSalesPeriodDays));
        if (!itemId.HasValue)
        {
            string[] names = [Strings.Today, Strings.MarketThreeDays, Strings.MarketSevenDays];
            ImGui.SetNextItemWidth(120);
            if (ImGui.Combo(Strings.SalesPeriod + "###market-sales-period", ref index, names, names.Length))
            {
                config.MarketSalesPeriodDays = periods[index];
                config.Save();
            }
        }
        var days = periods[index];
        var today = DateTime.Today;
        ImGui.TextDisabled(days == 1 ? today.ToString("d") : $"{today.AddDays(1 - days):d} – {today:d}");
        if (itemId.HasValue) ImGui.TextWrapped(Strings.UnmatchedMarketSalesHint);
        else if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.UnmatchedMarketSalesHint);
        var zone = TimeZoneInfo.Local;
        var worldRetainers = Plugin.Configuration.Snapshots.Values.Where(s => s.WorldId == worldId)
            .Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var ownCharacter = $"{Plugin.PlayerState.ContentId:X}.";
        var market = Plugin.Instance.MarketWatcher;
        var homeWorldId = market.HomeWorldId;
        var own = Plugin.Instance.Tracker.Tracker.GetSales().Where(s =>
            (!itemId.HasValue || s.ItemId == itemId) &&
            (worldId == 0 || worldRetainers.Contains(s.RetainerKey) ||
             (worldId == homeWorldId && s.RetainerKey.StartsWith(ownCharacter, StringComparison.Ordinal))));
        var scans = market.SaleHistory.Where(s => s.Key.WorldId == worldId
            && (!itemId.HasValue || s.Key.ItemId == itemId)
            && DailyMarketSales.InPeriod(s.Value.ScannedUtc, today, zone, days)).Select(s => s.Value).ToArray();
        var daily = DailyMarketSales.Calculate(scans.SelectMany(s => s.Sales), own, today, zone, days);
        if (scans.Length == 0)
        {
            ImGui.TextDisabled(string.Format(Strings.TodayOwnSales, daily.RecordedOurs));
            return;
        }

        ImGui.TextUnformatted(string.Format(Strings.TodayMarketSales, daily.Total, daily.Matched, daily.Lost.Count));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.UnmatchedMarketSalesHint);
        if (!itemId.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(Strings.TodayMarketScope, scans.Length));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(Strings.TodayRecordedOurs, daily.RecordedOurs));
            return;
        }

        ImGui.TextDisabled(string.Format(Strings.ItemMarketScope, scans[0].ScannedUtc.ToLocalTime(), daily.RecordedOurs));
        if (daily.Lost.Count > 0)
        {
            ImGui.TextUnformatted(Strings.UnmatchedMarketSales);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.UnmatchedMarketSalesHint);
        }
        foreach (var sale in daily.Lost.Take(5))
            ImGui.TextUnformatted(string.Format(Strings.LostSaleRow,
                sale.SoldUtc.ToLocalTime(), sale.Quantity, sale.UnitPrice, sale.IsHq ? Strings.QualityHq : Strings.QualityNq));
    }
}
