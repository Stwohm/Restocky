using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Plan;

internal enum StockReadState { Fresh, NeverRead, PreviousSession, Stale, Incomplete }

internal readonly record struct StockReading(int? Units, StockReadState State, TimeSpan Age);

internal readonly record struct StockFreshness(DateTime NowUtc, DateTime LoginUtc, int MaxAgeHours)
{
    internal StockReading Inventory(IEnumerable<InventoryEntry> entries, IEnumerable<uint> containers,
        IReadOnlyDictionary<uint, DateTime>? timestamps, uint itemId, bool hq)
    {
        var oldest = InventorySnapshot.OldestRefresh(containers, timestamps, DateTime.MinValue);
        if (oldest == DateTime.MinValue && timestamps != null)
        {
            var newest = containers.Select(id => timestamps.GetValueOrDefault(id)).DefaultIfEmpty().Max();
            if (newest != DateTime.MinValue)
                return new(null, StockReadState.Incomplete, NowUtc - newest);
        }
        var reading = Count(0, oldest);
        return reading.State == StockReadState.Fresh
            ? reading with { Units = entries.Where(e => e.IsListable && e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity) }
            : reading;
    }

    internal StockReading Count(int units, DateTime readUtc)
    {
        if (readUtc == DateTime.MinValue) return new(null, StockReadState.NeverRead, default);
        var age = NowUtc - readUtc;
        if (readUtc < LoginUtc) return new(null, StockReadState.PreviousSession, age);
        if (age.TotalHours > MaxAgeHours) return new(null, StockReadState.Stale, age);
        return new(units, StockReadState.Fresh, age);
    }

    internal static (int? Units, bool Incomplete) Total(IEnumerable<StockReading> readings)
    {
        int? total = null;
        var incomplete = false;
        foreach (var reading in readings)
        {
            if (reading.Units is { } units) total = (total ?? 0) + units;
            else incomplete = true;
        }
        return (total, incomplete || total == null);
    }
}
