using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

internal readonly record struct MarketSale(uint ItemId, bool IsHq, int Quantity, long UnitPrice, DateTime SoldUtc, string? BuyerName = null);

internal sealed record MarketSaleScan(DateTime ScannedUtc, IReadOnlyList<MarketSale> Sales);

internal sealed record DailyMarketSales(int Total, int Matched, int RecordedOurs, IReadOnlyList<MarketSale> Lost)
{
    private static readonly TimeSpan MatchTolerance = TimeSpan.FromMinutes(5);

    internal static bool IsOnDay(DateTime utc, DateTime day, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date == day.Date;

    internal static DailyMarketSales Calculate(IEnumerable<MarketSale> market, IEnumerable<ListingRecord> own,
        DateTime day, TimeZoneInfo zone)
    {
        var sales = market.ToArray();
        var total = sales.Count(s => IsOnDay(s.SoldUtc, day, zone));
        var ownSales = own.Where(s => s.RecordedSaleUtc.HasValue).ToArray();
        var recorded = ownSales.Count(s => IsOnDay(s.RecordedSaleUtc!.Value, day, zone));
        if (total == 0) return new DailyMarketSales(0, 0, recorded, []);

        var candidates = ownSales.ToLookup(s => (s.ItemId, s.IsHq, s.Quantity));
        var lost = new List<MarketSale>();

        foreach (var item in sales.GroupBy(s => (s.ItemId, s.IsHq, s.Quantity)))
        {
            // One tracker record can claim only one market transaction. Reassign a prior match
            // when it has another candidate so nearby identical sales do not reduce the count.
            var matched = new Dictionary<ListingRecord, int>();
            var rows = item.ToArray();
            var eligible = rows.Select(s => candidates[item.Key]
                .Where(r => (r.RecordedSaleUtc!.Value - s.SoldUtc).Duration() <= MatchTolerance
                    && (string.IsNullOrEmpty(r.BuyerName) || string.IsNullOrEmpty(s.BuyerName) || r.BuyerName == s.BuyerName)
                    && SamePrice(s, r))
                .OrderBy(r => (r.RecordedSaleUtc!.Value - s.SoldUtc).Duration()).ToArray()).ToArray();
            var closestFirst = Enumerable.Range(0, rows.Length).OrderBy(i => eligible[i].Length == 0
                ? TimeSpan.MaxValue : (eligible[i][0].RecordedSaleUtc!.Value - rows[i].SoldUtc).Duration());
            foreach (var i in closestFirst)
                if (!TryMatch(i, eligible, matched, new HashSet<ListingRecord>())
                    && IsOnDay(rows[i].SoldUtc, day, zone))
                    lost.Add(rows[i]);
        }

        return new DailyMarketSales(total, total - lost.Count, recorded,
            lost.OrderByDescending(s => s.SoldUtc).ToArray());
    }

    private static bool TryMatch(int row, ListingRecord[][] eligible,
        Dictionary<ListingRecord, int> matched, HashSet<ListingRecord> visited)
    {
        foreach (var sale in eligible[row])
        {
            if (!visited.Add(sale)) continue;
            if (matched.TryGetValue(sale, out var previous) && !TryMatch(previous, eligible, matched, visited)) continue;
            matched[sale] = row;
            return true;
        }
        return false;
    }

    private static bool SamePrice(MarketSale market, ListingRecord own)
    {
        var gross = market.UnitPrice * market.Quantity;
        if (own.SoldGrossGil is > 0) return gross == own.SoldGrossGil;

        // Prefer the tracked unit price when the proceeds support it. Retainer history can
        // also supply net proceeds without a tracked listing or gross price.
        if (own.LastPrice > 0 &&
            (own.SoldTotalGil == 0 || ListingTracker.NetFromGross(own.LastPrice * own.Quantity) == own.SoldTotalGil))
            return market.UnitPrice == own.LastPrice;

        return (own.SoldTotalGil > 0 || own.HistoryKey != null)
            && ListingTracker.NetFromGross(gross) == own.SoldTotalGil;
    }
}
