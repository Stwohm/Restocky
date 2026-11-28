using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class RetainerBoardTests
{
    [Fact]
    public void Undercut_stuck_and_stale_can_be_visible_on_the_same_listing()
    {
        var now = DateTime.UnixEpoch.AddDays(9);
        var listing = new ListingEntry { UnitPrice = 100 };
        var record = new ListingRecord { FirstSeenUtc = DateTime.UnixEpoch, Outcome = ListingTracker.Active };
        var scan = new ListingMarketScan(now, 100, [new(90, "Rival", 1)]);
        Assert.Equal(new BoardListingHealth(90, 9, true, true),
            BoardListingHealth.Read(listing, record, scan, true, now, 7));
        record.MissingSinceUtc = now;
        listing.UnitPrice = 89;
        Assert.Equal(new BoardListingHealth(null, 9, false, true),
            BoardListingHealth.Read(listing, record, scan, false, now, 7));
        Assert.Equal(new BoardListingHealth(null, null, false, false),
            BoardListingHealth.Read(null, null, null, false, now, 7));
    }

    [Theory]
    [InlineData(199, 20, false)]
    [InlineData(201, 21, true)]
    public void Assignment_rounds_up_per_quality_and_counts_actual_slots_independently(int target, int expected, bool over)
    {
        var a = new RetainerSnapshot { CharacterContentId = 1, Key = "A", RetainerName = "RetainerA",
            Listings = [new() { ItemId = 42 }, new() { ItemId = 42, IsHQ = true }, new() { ItemId = 43 }] };
        var nq = new LowStockEntry { ItemId = 42, Target = target, PerListing = 10, PreferredRetainers = new() { [1] = a.RetainerName } };
        var hq = new LowStockEntry { ItemId = 42, IsHq = true, Target = 500, PreferredRetainers = new() { [1] = "RetainerB" } };
        var auto = new LowStockEntry { ItemId = 43, Target = 0 };
        var summary = RetainerBoard.Summarize(a, [nq, hq, auto, nq], _ => "A", e => e.PerListing);
        Assert.Equal(expected, summary.Assigned);
        Assert.Equal(over, summary.OverAssigned);
        Assert.Equal(3, summary.Used);
        Assert.Equal(17, summary.Free);
        Assert.Equal(2, summary.Items.Count);
        Assert.All(summary.Items, i => Assert.Equal(1, i.Used));
    }
}
