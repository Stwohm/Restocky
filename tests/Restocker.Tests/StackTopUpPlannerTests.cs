using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Plan;

namespace Restocker.Tests;

public class StackTopUpPlannerTests
{
    private const uint Sand = 1;

    [Theory]
    [InlineData(74, 0)]
    [InlineData(99, 0)]
    [InlineData(103, 4)]
    [InlineData(106, 7)]
    public void Keeps_original_price_and_reserves_only_the_units_needed_for_the_top_up(int target, int extra)
    {
        List<RestockSource> stock = [new("char.1.bag", null, 32)];
        var topUp = Assert.Single(StackTopUpPlanner.Plan(Sand, false, 99, 999, [("A", Listed(74, 5))], stock));

        Assert.Equal(PlannedActionKind.TopUp, topUp.Kind);
        Assert.Equal("A", topUp.RetainerKey);
        Assert.Equal(5, topUp.ListingIndex);
        Assert.Equal(1_234, topUp.UnitPrice);
        Assert.Equal(99, topUp.Quantity);
        Assert.Equal(new StackTopUp(74, 999, 25, 0, 0), topUp.TopUp);
        Assert.Equal(7, Assert.Single(stock).Available);

        var additions = RestockPlanner.Plan(Sand, false, target, topUp.Quantity, 99, 1_000, stock,
            new Dictionary<string, int> { ["A"] = 1 });
        Assert.Equal(extra, additions.Sum(a => a.Quantity));
        Assert.All(additions, a => Assert.Equal(1_000, a.UnitPrice));
    }

    [Fact]
    public void Tops_up_the_largest_matching_stack_first_and_then_uses_the_remaining_units()
    {
        List<RestockSource> stock = [new("char.1.bag", null, 34)];
        var listings = new[]
        {
            ("A", Listed(50, 1)), ("B", Listed(74, 2)), ("A", Listed(99, 3)),
            ("A", Listed(98, 4, hq: true)), ("A", Listed(97, 5, itemId: 2)),
            ("B", Listed(95, 6, price: 0)),
        };

        var actions = StackTopUpPlanner.Plan(Sand, false, 99, 999, listings, stock);

        Assert.Equal(new[] { 2, 1 }, actions.Select(a => a.ListingIndex));
        Assert.Equal(new[] { 99, 59 }, actions.Select(a => a.Quantity));
        Assert.Equal(0, Assert.Single(stock).Available);
    }

    [Fact]
    public void Combines_retainer_bag_and_saddle_stock_without_borrowing_from_another_retainer()
    {
        List<RestockSource> stock =
        [
            new("A", "A", 7), new("B", "B", 40), new("char.1.bag", null, 10), new("char.1.saddle", null, 12),
        ];

        var action = Assert.Single(StackTopUpPlanner.Plan(Sand, true, 99, 999, [("A", Listed(74, 5, hq: true))], stock));
        Assert.True(action.IsHQ);
        Assert.Equal(new StackTopUp(74, 999, 10, 8, 7), action.TopUp);
        Assert.Equal(new[] { 0, 40, 0, 4 }, stock.Select(s => s.Available));
        Assert.Equal(18, Assert.Single(RestockPlanner.SaddleStagingNeeds([action], [])).Value);
    }

    [Fact]
    public void Skips_stacks_when_no_eligible_inventory_can_make_them_bigger()
    {
        List<RestockSource> stock = [new("B", "B", 2), new("char.1.bag", null, 0)];
        Assert.Empty(StackTopUpPlanner.Plan(Sand, false, 99, 999, [("A", Listed(74, 5))], stock));
    }

    [Fact]
    public void Apply_visits_preserve_stack_priority_across_retainers_before_new_listings()
    {
        List<RestockSource> stock = [new("char.1.bag", null, 99)];
        var actions = StackTopUpPlanner.Plan(Sand, false, 99, 999,
            [("A", Listed(73, 1)), ("B", Listed(97, 2)), ("A", Listed(98, 3))], stock);
        var newOnA = new PlannedAction { RetainerKey = "A", Kind = PlannedActionKind.NewListing };
        var newOnB = new PlannedAction { RetainerKey = "B", Kind = PlannedActionKind.NewListing };

        var visits = RetainerVisitJob.GroupApplyActions([newOnB, newOnA, .. actions]);

        Assert.Equal(4, visits.Count);
        Assert.Same(actions[0], Assert.Single(visits[0]));
        Assert.Same(actions[1], Assert.Single(visits[1]));
        Assert.Equal(new[] { actions[2], newOnA }, visits[2]);
        Assert.Same(newOnB, Assert.Single(visits[3]));
    }

    private static ListingEntry Listed(int quantity, int slot, bool hq = false, uint itemId = Sand, long price = 1_234)
        => new() { ItemId = itemId, IsHQ = hq, Quantity = quantity, UnitPrice = price, ListingIndex = slot };
}
