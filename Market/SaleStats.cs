using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

internal sealed record SaleTotals(long Units, decimal Net, decimal Profit, decimal Earning, bool HasFlip, bool HasGather, int Incomplete)
{
    public string Kind => (HasFlip, HasGather) switch
    {
        (true, true) => "Flip + Gather", (true, false) => "Flip", (false, true) => "Gather", _ => "Unknown"
    };

    internal static SaleTotals Sum(IEnumerable<ListingRecord> rows)
    {
        long units = 0;
        decimal net = 0, profit = 0, earning = 0;
        bool flip = false, gather = false;
        int incomplete = 0;
        foreach (var sale in rows)
        {
            var realized = SaleReturn.Realized(sale);
            units += sale.Quantity;
            net += sale.SoldTotalGil;
            profit += realized.Profit ?? 0;
            earning += realized.Earning ?? 0;
            flip |= realized.Profit.HasValue;
            gather |= realized.Earning.HasValue;
            if (realized == default) incomplete++;
        }
        return new(units, net, profit, earning, flip, gather, incomplete);
    }
}

internal sealed record StatsRow(string Name, SaleTotals Total);
internal sealed record SaleStats(SaleTotals Total, List<StatsRow> Items, List<StatsRow> Retainers)
{
    internal static SaleStats Aggregate(IEnumerable<ListingRecord> records, DateTime? fromUtc, DateTime? untilUtc)
    {
        var sold = records.Where(r => r.Outcome == ListingTracker.Sold && InRange(r.RecordedSaleUtc ?? r.LastSeenUtc)).ToList();
        bool InRange(DateTime t) => (!fromUtc.HasValue || t >= fromUtc) && (!untilUtc.HasValue || t < untilUtc);
        var items = sold.GroupBy(r => (r.ItemId, r.IsHq)).Select(g =>
            new StatsRow(g.First().ItemName + (g.Key.IsHq ? " (HQ)" : ""), SaleTotals.Sum(g))).ToList();
        var retainers = sold.GroupBy(r => string.IsNullOrEmpty(r.RetainerKey) ? r.RetainerName : r.RetainerKey)
            .Select(g => new StatsRow(g.Last().RetainerName, SaleTotals.Sum(g))).OrderBy(r => r.Name).ToList();
        return new(SaleTotals.Sum(sold), items, retainers);
    }
}
