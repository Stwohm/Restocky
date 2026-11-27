using System.Linq;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class MatchClickPlannerTests
{
    private static ListingEntry Row(int slot, uint item = 42, bool hq = true, long price = 50)
        => new() { ListingIndex = slot, ItemId = item, IsHQ = hq, Quantity = 2, UnitPrice = price };

    [Fact]
    public void Plans_only_changed_matching_slots_across_retainers_once()
    {
        var a = new RetainerSnapshot { Key = "RetainerA", Listings = [Row(0), Row(1, hq: false), Row(2, 99), Row(3)] };
        var b = new RetainerSnapshot { Key = "RetainerB", Listings = [Row(0), Row(1, price: 100)] };
        var result = MatchClickPlanner.Plan([a, b, a], 42, true, 100, false, false, 20, 30);
        Assert.Equal(new[] { ("RetainerA", 0), ("RetainerA", 3), ("RetainerB", 0) },
            result.Actions.Select(x => (x.RetainerKey, x.ListingIndex)));
        Assert.All(result.Actions, x =>
        {
            Assert.Equal(PlannedActionKind.Reprice, x.Kind);
            Assert.Equal(100, x.UnitPrice);
            Assert.Equal(2, x.Quantity);
            Assert.True(x.MatchedPrice);
        });
        Assert.False(MatchClickPlanner.Plan([a, b], 13, true, 100, false, false, 0, 30).FoundListing);
    }

    [Theory]
    [InlineData(true, 0, true, false)]
    [InlineData(false, 101, true, false)]
    [InlineData(false, 100, false, false)]
    [InlineData(false, 100, true, true)]
    public void Hold_and_current_floor_guard_each_listing(bool held, long floor, bool reviewed, bool applies)
    {
        var a = new RetainerSnapshot { Key = "RetainerA", Listings = [Row(0)] };
        var result = MatchClickPlanner.Plan([a], 42, true, 100, reviewed, held, floor, 30);
        Assert.True(result.FoundListing);
        Assert.Equal(applies, result.Actions.Count == 1);
    }

    [Fact]
    public void Duplicate_reviews_are_pending_until_the_job_finishes()
    {
        var matches = new PriceMatchOverrides();
        matches.Queue(42, true, 100);
        Assert.True(matches.IsPending(42, true, 100, false, false));
        matches.BeginCycle();
        matches.Consume(42, true);
        Assert.True(matches.IsPending(42, true, 100, false, true));
        Assert.False(matches.IsPending(42, true, 100, false, false));
        matches.BeginApply();
        matches.Started(42, false, 100, true);
        Assert.True(matches.IsPending(42, false, 100, true, true));
        Assert.False(matches.IsPending(42, true, 100, true, true));
        Assert.False(matches.IsPending(42, false, 100, true, false));
    }
}
