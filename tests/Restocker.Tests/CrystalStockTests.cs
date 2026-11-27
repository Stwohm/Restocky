using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;
using Restocker.Execution;
using Restocker.Plan;

namespace Restocker.Tests;

public class CrystalStockTests
{
    [Fact]
    public void Captured_crystal_balances_share_the_cost_of_the_remaining_lot()
    {
        var character = new CharacterSnapshot { Bag = [new() { ContainerId = 2001, ItemId = 8, Quantity = 4800 }] };
        var retainer = new RetainerSnapshot { Inventory = [new() { ContainerId = 12001, ItemId = 8, Quantity = 5100 }] };
        var held = InventorySnapshot.HeldUnits(8, false, [retainer], [character], null, 0);
        Assert.Equal((0L, 9900L), held);
        var ledger = new CostLedger();
        ledger.RecordLot(8, false, 200, 71.4m, DateTime.UtcNow);
        Assert.Equal((2L, true), BreakEvenFloor.Resolve(0, ledger.WeightedAverageCost(8, false, held.Available), 5, 0));
    }

    [Theory]
    [InlineData(2001u)]
    [InlineData(12001u)]
    public void Crystal_balance_keeps_its_own_timestamp_until_the_container_loads(uint container)
    {
        var before = DateTime.UtcNow.AddHours(-3);
        var now = before.AddHours(3);
        var timestamps = new Dictionary<uint, DateTime> { [container] = before };
        var previous = new InventoryEntry { ContainerId = container, ItemId = 8, Quantity = 4800 };
        var entries = InventorySnapshot.Refresh([previous], timestamps, new Dictionary<uint, List<InventoryEntry>> { [0] = [] }, now);
        var freshness = new StockFreshness(now, before.AddHours(-1), 2);
        Assert.Equal(4800, Assert.Single(entries).Quantity);
        Assert.Equal(StockReadState.Stale, freshness.Inventory(entries, [container], timestamps, 8, false).State);
        entries = InventorySnapshot.Refresh(entries, timestamps, new Dictionary<uint, List<InventoryEntry>> { [container] = [] }, now);
        Assert.Equal(new StockReading(0, StockReadState.Fresh, TimeSpan.Zero),
            freshness.Inventory(entries, [container], timestamps, 8, false));
    }

    [Theory]
    [InlineData(2, 9999)]
    [InlineData(8, 9999)]
    [InlineData(19, 9999)]
    [InlineData(20, 99)]
    public void Listing_cap_depends_on_item_and_inventory_stack(uint id, int expected)
    {
        Assert.Equal(expected, InventoryEntry.ListingLimit(id, 15000));
        Assert.Equal(1, InventoryEntry.ListingLimit(id, 1));
    }

    [Theory]
    [InlineData(20000, 9000, 1050)]
    [InlineData(6500, 6500, 2500)]
    public void Crystal_listings_respect_the_cap_per_listing_and_total_target(int perListing, int first, int second)
    {
        var retainer = new RetainerSnapshot { Key = "RetainerA", Inventory =
            [new() { ContainerId = 12001, ItemId = 8, Quantity = 9999, MaxStackPerListing = InventoryEntry.ListingLimit(8, 9999) }] };
        var character = new CharacterSnapshot { CharacterContentId = 1, Bag =
            [new() { ContainerId = 2001, ItemId = 8, Quantity = 9000, MaxStackPerListing = InventoryEntry.ListingLimit(8, 9999) }] };
        var sources = AutoRestockPlanner.Sources(8, false, [retainer], character);
        var actions = RestockPlanner.Plan(8, false, 10050, 0, perListing, 2, sources,
            new Dictionary<string, int> { [retainer.Key] = 20 });
        Assert.Equal(first, actions[0].Quantity);
        Assert.Equal(second, actions[1].Quantity);
        Assert.Equal(10050, actions.Sum(a => a.Quantity));
        Assert.Equal("char.1.bag", actions[0].SourceKey);
    }

    [Theory]
    [InlineData(100, 500, 90, "char.1.bag", 90)]
    [InlineData(0, 500, 90, "RetainerB", 90)]
    [InlineData(0, 0, 90, "RetainerA", 90)]
    [InlineData(0, 0, 15000, "RetainerA", 9999)]
    public void Crystal_source_for_listing_retainer(int bag, int own, int need, string source, int qty)
    {
        List<RestockSource> sources = [new("RetainerA", "RetainerA", 9999),
            new("RetainerB", "RetainerB", own), new("char.1.bag", null, bag)];
        var actions = RestockPlanner.Plan(8, false, need, 0, 15000, 1, sources,
            new Dictionary<string, int> { ["RetainerA"] = 0, ["RetainerB"] = 20 }, ["RetainerB"]);
        var action = Assert.Single(actions);
        Assert.Equal("RetainerB", action.RetainerKey);
        Assert.Equal(source, action.SourceKey);
        Assert.Equal(qty, action.Quantity);
        if (source == "RetainerA")
        {
            var target = new RetainerSnapshot { Key = "RetainerB", RetainerName = "RetainerB" };
            var visit = Assert.Single(AutoRestockPlanner.ForVisit(
                [new(8, false, AutoRestockPriceKind.Pending, actions)], target, target));
            var visits = RetainerVisitJob.ListingVisits(actions, target.RetainerName, k => k).ToArray();
            Assert.Equal(new[] { "RetainerA", "RetainerB" }, visits.Select(v => v.RetainerName));
            Assert.Same(action, visits[0].Withdrawal);
            Assert.Same(action, Assert.Single(visits[1].Actions));
            Assert.Empty(visit.Actions);
            Assert.Contains("Pending", visit.SkipReason.ToString());
        }
    }

    [Theory]
    [InlineData(9999, 0, 9999, true)]
    [InlineData(100, 9998, 1, true)]
    [InlineData(100, 9998, 2, false)]
    [InlineData(1, 0, 2, false)]
    public void Withdrawal_requires_the_exact_planned_amount_to_fit(int source, int bag, int quantity, bool fits)
        => Assert.Equal(fits, InventoryEntry.CanWithdrawCrystals(source, bag, quantity));

    [Fact]
    public void With_no_preferred_seller_a_retainer_with_room_can_list_its_own_stock()
    {
        var free = new Dictionary<string, int> { ["RetainerB"] = 20, ["RetainerA"] = 19 };
        var action = Assert.Single(RestockPlanner.Plan(8, false, 20, 0, 20, 1,
            [new("RetainerA", "RetainerA", 100)], free));
        Assert.Equal("RetainerA", action.RetainerKey);
        Assert.False(action.NeedsCrystalWithdrawal);
    }

    [Theory]
    [InlineData(false, (int)RestockSkipReason.NoStock)]
    [InlineData(true, (int)RestockSkipReason.NoSlot)]
    public void Impossible_crystal_listing_reports_reason(bool hasStock, int reason)
    {
        var retainer = new RetainerSnapshot { Key = "RetainerA", RetainerName = "RetainerA",
            Listings = Enumerable.Range(0, Planner.MaxListingSlots).Select(i => new ListingEntry { ItemId = 100, ListingIndex = i }).ToList() };
        if (hasStock) retainer.Inventory.Add(new() { ContainerId = 12001, ItemId = 8, Quantity = 50 });
        var plan = Assert.Single(AutoRestockPlanner.Plan([new() { ItemId = 8, Target = 5 }], [retainer],
            null, new ListingTracker(), (_, _) => false, (_, _) => 0));
        Assert.Empty(plan.Actions);
        Assert.Equal((RestockSkipReason)reason, plan.SkipReason);
    }

    [Fact]
    public void Crystal_top_up_uses_retainer_stock_and_stops_at_target()
    {
        List<RestockSource> sources = [new("RetainerA", "RetainerA", 2000)];
        var listing = new ListingEntry { ItemId = 8, Quantity = 400, UnitPrice = 10 };
        var action = Assert.Single(StackTopUpPlanner.Plan(8, false, 9999, 9999, [("RetainerA", listing)],
            sources, perListing: 1000, additionalUnits: 150));
        Assert.Equal(550, action.Quantity);
        Assert.Equal(150, action.TopUp!.RetainerUnits);
        Assert.Equal(1850, sources[0].Available);
    }

    [Fact]
    public void Top_up_returns_crystals_to_the_balance_with_room_for_the_withdrawal()
    {
        List<RestockSource> sources = [new("RetainerA", "RetainerA", 9900), new("char.1.bag", null, 900)];
        var listing = new ListingEntry { ItemId = 8, Quantity = 500, UnitPrice = 10 };
        var action = Assert.Single(StackTopUpPlanner.Plan(8, false, 9999, 9999, [("RetainerA", listing)], sources, 1000));
        Assert.Equal(500, action.TopUp!.BagUnits);
        Assert.Equal(0, action.TopUp.RetainerUnits);
        Assert.Equal(9900, sources[0].Available);
    }
}
