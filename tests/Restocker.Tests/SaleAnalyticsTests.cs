using System.IO;
using Restocker.Market;

namespace Restocker.Tests;

// Fork: verify replay, notice enrichment and persisted alert state without Dalamud.
public sealed class SaleAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("history")]
    [InlineData("allaganmarket")]
    [InlineData("allaganmarket-delayed")]
    [InlineData("game")]
    [InlineData("game-unknown-gil")]
    public void Sale_consumes_once_across_notices_history_and_reload(string source)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var ledger = new CostLedger();
            ledger.AddManualLot(100, true, 1, 32.5m, Now.AddDays(-5));
            ledger.AddManualLot(100, true, 4, 99m, Now.AddDays(3));
            ledger.AddManualLot(100, false, 2, 10m, Now.AddDays(-5));
            var calls = 0;
            var noticeTime = source == "allaganmarket-delayed" ? Now.AddDays(2) : Now.AddSeconds(10);
            (decimal, int) Consume(uint id, bool hq, int qty, DateTime utc)
            {
                calls++;
                Assert.Equal(100u, id);
                Assert.True(hq);
                Assert.Equal(2, qty);
                Assert.Equal(source == "history" ? Now : noticeTime, utc);
                return ledger.Consume(id, hq, qty, utc);
            }

            var tracker = new ListingTracker(Consume);
            if (source != "history")
                tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100)], _ => "Item", Now.AddDays(-3));
            if (source == "allaganmarket-delayed")
                tracker.ObserveRetainer("A", "RetainerA", [], _ => "Item", Now.AddDays(1), authoritative: true);
            if (source.StartsWith("allaganmarket", StringComparison.Ordinal))
                Assert.NotNull(tracker.OnAllaganSale([100], 2, source == "allaganmarket-delayed" ? 190 : 189, noticeTime));
            if (source.StartsWith("game", StringComparison.Ordinal))
                Assert.NotNull(tracker.OnSale(100, true, source == "game-unknown-gil" ? 0 : 189, noticeTime));

            var path = Path.Combine(directory.FullName, "listings.json");
            if (source != "history")
            {
                Assert.True(tracker.Save(directory.FullName));
                tracker = ListingTracker.Load(path, Consume);
            }
            var sale = new RetainerHistorySale(100, 190, (uint)new DateTimeOffset(Now).ToUnixTimeSeconds(), 2, true, false, "BuyerA");
            tracker.OnHistorySale("A", "RetainerA", sale, _ => "Item");
            tracker.OnHistorySale("A", "RetainerA", sale, _ => "Item");
            Assert.True(tracker.Save(directory.FullName));
            var ledgerPath = Path.Combine(directory.FullName, CostLedger.FileName);
            ledger.SaveIfDirty(ledgerPath);
            ledger = CostLedger.Load(ledgerPath);
            tracker = ListingTracker.Load(path, Consume);
            tracker.OnHistorySale("A", "RetainerA", sale, _ => "Item");

            var record = Assert.Single(tracker.GetSales(100, true));
            Assert.Equal(1, calls);
            Assert.Equal(32.5m, record.ConsumedCost);
            Assert.Equal(1, record.UnknownCostUnits);
            Assert.Equal(62.5m, record.RealizedProfit);
            Assert.Equal(Now, record.RecordedSaleUtc);
            Assert.Equal(4, ledger.RemainingUnits(100, true));
            Assert.Equal(2, ledger.RemainingUnits(100, false));
            Assert.EndsWith(",32.5,62.5,95", tracker.ListingsCsv().TrimEnd());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Stuck_boundary_and_notification_flag_survive_reload()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var tracker = new ListingTracker();
            tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, false, 2, 100)], _ => "Item", Now.AddDays(-3));
            var listing = Assert.Single(tracker.Records);
            tracker.OnRepriceApplied("A", 0, 100, false, 110);
            Assert.False(tracker.TryMarkStuckNotified(listing, Now.AddTicks(-1), 3));
            Assert.True(tracker.TryMarkStuckNotified(listing, Now, 3));
            Assert.False(tracker.TryMarkStuckNotified(listing, Now, 3));
            Assert.True(tracker.Save(directory.FullName));
            tracker = ListingTracker.Load(Path.Combine(directory.FullName, "listings.json"));
            listing = Assert.Single(tracker.Records);
            Assert.Equal(1, listing.RepriceCount);
            Assert.True(ListingTracker.IsStuck(listing, Now, 3));
            Assert.False(ListingTracker.IsStuck(listing, Now, 4));
            Assert.False(tracker.TryMarkStuckNotified(listing, Now.AddDays(1), 4));
            Assert.True(listing.StuckNotified);
            Assert.NotNull(tracker.OnSale(100, false, 209, Now));
            Assert.False(ListingTracker.IsStuck(listing, Now, 3));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Rolling_windows_include_both_endpoints_and_prefer_history_time()
    {
        var velocity = SalesVelocity.Calculate([
            Sold(Now, 1), Sold(Now.AddDays(-1), 2), Sold(Now.AddDays(-1).AddTicks(-1), 4),
            Sold(Now.AddDays(-3), 8), Sold(Now.AddDays(-3).AddTicks(-1), 16),
            Sold(Now.AddDays(-7), 32), Sold(Now.AddDays(-7).AddTicks(-1), 64),
            Sold(Now.AddDays(-30), 128), Sold(Now.AddDays(-30).AddTicks(-1), 256), Sold(Now.AddTicks(1), 512),
            new() { Outcome = ListingTracker.Active, SoldRecordedUtc = Now, Quantity = 1024 }
        ], Now);

        Assert.Equal((3L, 2), (velocity.Last24h.Units, velocity.Last24h.Sales));
        Assert.Equal((15L, 4), (velocity.Last3d.Units, velocity.Last3d.Sales));
        Assert.Equal((63L, 6), (velocity.Last7d.Units, velocity.Last7d.Sales));
        Assert.Equal((255L, 8), (velocity.Last30d.Units, velocity.Last30d.Sales));
    }

    [Fact]
    public void Intervals_stock_and_threshold_use_their_own_windows()
    {
        var velocity = SalesVelocity.Calculate([
            new() { Outcome = ListingTracker.Sold, SoldRecordedUtc = Now, Quantity = 5 },
            Sold(Now.AddHours(-12), 1), Sold(Now.AddDays(-2), 2), Sold(Now.AddDays(-20), 22)
        ], Now);

        Assert.Equal(24, velocity.Last7d.AverageHoursBetweenSales);
        Assert.Equal(160, velocity.Last30d.AverageHoursBetweenSales);
        Assert.Equal(12, velocity.DaysOfStock(5, 7));
        Assert.Equal(0, velocity.DaysOfStock(0, 0));
        Assert.Equal(2, velocity.SuggestedThreshold);

        var empty = SalesVelocity.Calculate([], Now);
        Assert.Null(empty.DaysOfStock(5, 7));
        Assert.Null(empty.Last30d.AverageHoursBetweenSales);
        Assert.Null(SalesVelocity.Calculate([Sold(Now, 1)], Now).Last7d.AverageHoursBetweenSales);
        Assert.Equal(0, empty.SuggestedThreshold);
    }

    private static ListingRecord Sold(DateTime at, int qty)
        => new() { Outcome = ListingTracker.Sold, SaleTimeUtc = at, SoldRecordedUtc = Now, Quantity = qty };
}
