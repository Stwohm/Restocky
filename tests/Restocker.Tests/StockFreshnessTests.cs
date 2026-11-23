using System.Collections.Generic;
using Restocker.Data;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class StockFreshnessTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 20, 0, 0, DateTimeKind.Utc);
    private static readonly StockFreshness Freshness = new(Now, Now.AddHours(-4), 2);

    [Fact]
    public void A_fully_read_empty_source_is_zero_and_an_unread_source_is_unknown()
    {
        Assert.Equal(new StockReading(0, StockReadState.Fresh, TimeSpan.Zero),
            Freshness.Inventory([], [1, 2], new Dictionary<uint, DateTime> { [1] = Now, [2] = Now }, 10, false));
        Assert.Equal(new StockReading(null, StockReadState.NeverRead, default),
            Freshness.Inventory([Stock(1, 10, 12)], [1, 2], null, 10, false));
        Assert.Equal(new StockReading(null, StockReadState.Incomplete, TimeSpan.FromMinutes(5)),
            Freshness.Inventory([Stock(1, 10, 12)], [1, 2], new Dictionary<uint, DateTime> { [1] = Now.AddMinutes(-5) }, 10, false));
    }

    [Fact]
    public void Freshness_uses_the_oldest_page_and_counts_only_listable_matching_stock()
    {
        var timestamps = new Dictionary<uint, DateTime> { [1] = Now, [2] = Now.AddHours(-2) };
        var reading = Freshness.Inventory([Stock(1, 10, 12), Stock(2, 10, 2), Stock(2, 10, 3, hq: true),
            Stock(1, 11, 20), Stock(1, 10, 5, listable: false)], [1, 2], timestamps, 10, false);

        Assert.Equal(new StockReading(14, StockReadState.Fresh, TimeSpan.FromHours(2)), reading);
        timestamps[2] = Now.AddHours(-3);
        Assert.Equal(new StockReading(null, StockReadState.Stale, TimeSpan.FromHours(3)),
            Freshness.Inventory([Stock(1, 10, 12)], [1, 2], timestamps, 10, false));
        timestamps[2] = Now.AddHours(-5);
        Assert.Equal(new StockReading(null, StockReadState.PreviousSession, TimeSpan.FromHours(5)),
            Freshness.Inventory([Stock(1, 10, 12)], [1, 2], timestamps, 10, false));
    }

    [Fact]
    public void Totals_distinguish_known_zero_from_partial_or_unknown_stock()
    {
        var unknown = Freshness.Count(9, DateTime.MinValue);
        var zero = Freshness.Count(0, Now);
        Assert.Equal((null, true), StockFreshness.Total([unknown]));
        Assert.Equal((0, false), StockFreshness.Total([zero]));
        Assert.Equal((0, true), StockFreshness.Total([zero, unknown]));
        Assert.Equal((32, true), StockFreshness.Total([zero, unknown, Freshness.Count(32, Now)]));
    }

    private static InventoryEntry Stock(uint container, uint item, int qty, bool hq = false, bool listable = true)
        => new() { ContainerId = container, ItemId = item, Quantity = qty, IsHQ = hq, IsListable = listable };
}
