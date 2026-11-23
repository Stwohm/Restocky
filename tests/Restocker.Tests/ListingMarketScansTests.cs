using Restocker.Data;
using Restocker.Market;

namespace Restocker.Tests;

public class ListingMarketScansTests
{
    [Fact]
    public void A_fresh_plan_replaces_the_reason_and_a_newer_scan_invalidates_it()
    {
        var scans = new ListingMarketScans();
        var now = DateTime.UnixEpoch.AddHours(1);
        MarketOffer[] bait = [new(12, "Rival", 4)];
        var floor = new RepriceDecision(90, false, RepriceReason.MaxDrop, 85) { ClampedFloor = 90 };
        var timedOut = new RepriceDecision(120, false, RepriceReason.NoData, 0);
        scans.Record("A", 2, 42, true, new(now, 120, bait));
        scans.RecordDecision("A", 2, 42, true, now, floor, true, bait);

        var result = Assert.IsType<ListingPriceDecision>(scans.GetDecision("A", 2, 42, true));
        Assert.Equal(90, result.Decision.ClampedFloor);
        Assert.Equal("Rival", Assert.Single(result.Bait).Seller);
        Assert.True(result.MatchQuality);
        Assert.Null(scans.GetDecision("B", 2, 42, true));
        Assert.Null(scans.GetDecision("A", 1, 42, true));
        Assert.Null(scans.GetDecision("A", 2, 41, true));
        Assert.Null(scans.GetDecision("A", 2, 42, false));

        scans.RecordDecision("A", 2, 42, true, now.AddMinutes(1), timedOut, false, []);
        Assert.Equal(timedOut, scans.GetDecision("A", 2, 42, true)?.Decision);
        Assert.Equal(now, scans.Get("A", 2, 42, true)?.ScannedUtc);
        scans.Record("A", 2, 42, true, new(now.AddMinutes(2), 120, []));
        scans.RecordDecision("A", 2, 42, true, now, floor, true, bait);
        Assert.Null(scans.GetDecision("A", 2, 42, true));
    }

    [Fact]
    public void Reprice_keeps_the_previous_scan_until_a_new_scan_replaces_it()
    {
        var scans = new ListingMarketScans();
        var listing = new ListingEntry { ListingIndex = 2, ItemId = 42, IsHQ = true, UnitPrice = 100 };
        var snapshot = new RetainerSnapshot { Key = "1.2", Listings = [listing] };
        var scannedUtc = DateTime.UnixEpoch;
        var offers = new[] { new MarketOffer(90, "Rival", 3) };
        var previous = new ListingMarketScan(scannedUtc, listing.UnitPrice, offers);
        scans.Record(snapshot.Key, listing.ListingIndex, listing.ItemId, listing.IsHQ, previous);

        Assert.True(snapshot.TryUpdateListingPrice(2, 42, true, 89));
        Assert.Equal(89L, listing.UnitPrice);
        var result = Assert.IsType<ListingMarketScan>(scans.Get(snapshot.Key, 2, 42, true));
        Assert.True(result.WasUndercut);
        Assert.Equal(100L, result.UnitPrice);
        Assert.Equal(1, UndercutSummary.Create(result.Offers, result.UnitPrice).ListingCount);

        var next = new ListingMarketScan(scannedUtc.AddMinutes(1), listing.UnitPrice, offers);
        scans.Record(snapshot.Key, 2, 42, true, next);
        scans.Record(snapshot.Key, 2, 42, true, previous);
        Assert.Equal(next, scans.Get(snapshot.Key, 2, 42, true));
        Assert.False(next.WasUndercut);

        var empty = new ListingMarketScan(scannedUtc.AddMinutes(2), listing.UnitPrice, []);
        scans.Record(snapshot.Key, 2, 42, true, empty);
        Assert.Equal(empty, scans.Get(snapshot.Key, 2, 42, true));
        Assert.False(empty.WasUndercut);
    }

    [Fact]
    public void Scans_are_specific_to_retainer_slot_item_and_quality()
    {
        var scans = new ListingMarketScans();
        var scan = new ListingMarketScan(DateTime.UnixEpoch, 100, []);
        scans.Record("1.2", 2, 42, true, scan);
        scans.Record("1.3", 2, 42, true, scan with { UnitPrice = 200 });

        Assert.Equal(100L, scans.Get("1.2", 2, 42, true)?.UnitPrice);
        Assert.Equal(200L, scans.Get("1.3", 2, 42, true)?.UnitPrice);
        Assert.Null(scans.Get("1.2", 3, 42, true));
        Assert.Null(scans.Get("1.2", 2, 43, true));
        Assert.Null(scans.Get("1.2", 2, 42, false));
    }
}
