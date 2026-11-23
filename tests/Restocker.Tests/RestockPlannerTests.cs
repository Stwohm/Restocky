using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Plan;

namespace Restocker.Tests;

public class RestockPlannerTests
{
    private const uint Twine = 50970;

    private static Dictionary<string, int> Free(int a = 20, int b = 20) => new() { ["A"] = a, ["B"] = b };

    [Fact]
    public void One_listed_restock_to_three_adds_two_from_retainer_bag_first()
    {
        var sources = new[]
        {
            new RestockSource("char.1.bag", null, 5),
            new RestockSource("A", "A", 4),
        };
        var p = RestockPlanner.Plan(Twine, false, 3, 1, 1, 80_000, sources, Free());
        Assert.Equal(2, p.Count);
        Assert.All(p, a => { Assert.Equal("A", a.SourceKey); Assert.Equal("A", a.RetainerKey); Assert.Equal(1, a.Quantity); Assert.Equal(80_000, a.UnitPrice); });
    }

    [Fact]
    public void Falls_back_to_bag_then_saddle_on_the_retainer_with_most_free_slots()
    {
        var sources = new[]
        {
            new RestockSource("char.1.saddle", null, 10),
            new RestockSource("char.1.bag", null, 1),
        };
        var p = RestockPlanner.Plan(Twine, false, 3, 0, 1, 1, sources, Free(a: 2, b: 5));
        Assert.Equal(new[] { "char.1.bag", "char.1.saddle", "char.1.saddle" }, p.Select(a => a.SourceKey));
        Assert.Equal("B", p[0].RetainerKey);
    }

    [Fact]
    public void Bag_items_go_to_the_retainer_that_already_sells_the_item()
    {
        var sources = new[] { new RestockSource("char.1.bag", null, 5) };
        var p = RestockPlanner.Plan(Twine, false, 2, 0, 1, 1, sources, Free(a: 3, b: 18), new[] { "A" });
        Assert.All(p, a => Assert.Equal("A", a.RetainerKey));
    }

    [Fact]
    public void Full_preferred_retainer_falls_back_to_most_free_slots()
    {
        var sources = new[] { new RestockSource("char.1.bag", null, 5) };
        var p = RestockPlanner.Plan(Twine, false, 2, 0, 1, 1, sources, Free(a: 0, b: 18), new[] { "A" });
        Assert.All(p, a => Assert.Equal("B", a.RetainerKey));
    }

    [Fact]
    public void Preferred_retainer_bag_beats_other_retainer_bag()
    {
        var sources = new[] { new RestockSource("B", "B", 5), new RestockSource("A", "A", 5) };
        var p = RestockPlanner.Plan(Twine, false, 1, 0, 1, 1, sources, Free(), new[] { "A" });
        Assert.Equal("A", Assert.Single(p).SourceKey);
    }

    [Fact]
    public void Retainer_inventory_never_lists_on_another_retainer()
    {
        var sources = new[] { new RestockSource("A", "A", 10) };
        var p = RestockPlanner.Plan(Twine, false, 5, 0, 1, 1, sources, Free(a: 2, b: 20));
        Assert.Equal(2, p.Count); // A has only 2 free slots
        Assert.All(p, a => Assert.Equal("A", a.RetainerKey));
    }

    [Fact]
    public void Stack_size_is_respected_and_last_listing_is_partial()
    {
        var sources = new[] { new RestockSource("A", "A", 99) };
        var p = RestockPlanner.Plan(1, false, 12, 0, 5, 10, sources, Free());
        Assert.Equal(new[] { 5, 5, 2 }, p.Select(a => a.Quantity));
    }

    [Fact]
    public void Nothing_when_already_at_target_or_no_price()
    {
        var sources = new[] { new RestockSource("A", "A", 9) };
        Assert.Empty(RestockPlanner.Plan(1, false, 3, 3, 1, 10, sources, Free()));
        Assert.Empty(RestockPlanner.Plan(1, false, 3, 0, 1, 0, sources, Free()));
    }

    [Fact]
    public void Saddle_staging_covers_current_and_later_bag_listings_of_the_same_quality()
    {
        static PlannedAction Listing(string source, int qty, bool hq = false) => new()
        {
            Kind = PlannedActionKind.NewListing, ItemId = Twine, SourceKey = source, Quantity = qty, IsHQ = hq,
        };

        var needs = RestockPlanner.SaddleStagingNeeds(
            [Listing("A", 9), Listing("char.1.bag", 2), Listing("char.1.saddle", 3), Listing("char.1.bag", 9, hq: true)],
            [Listing("char.1.bag", 4), Listing("char.1.saddle", 9)]);

        var need = Assert.Single(needs);
        Assert.Equal((Twine, false), need.Key);
        Assert.Equal(9, need.Value);
    }

    [Fact]
    public void Alert_fires_once_and_rearms_above_threshold()
    {
        var notified = false;
        Assert.False(RestockPlanner.ShouldNotify(3, 1, ref notified));
        Assert.True(RestockPlanner.ShouldNotify(1, 1, ref notified));
        Assert.False(RestockPlanner.ShouldNotify(0, 1, ref notified));
        Assert.False(RestockPlanner.ShouldNotify(2, 1, ref notified));
        Assert.True(RestockPlanner.ShouldNotify(1, 1, ref notified));
    }
}
