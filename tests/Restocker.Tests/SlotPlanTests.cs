using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class SlotPlanTests
{
    private const uint Item = 100;
    private const uint Other = 101;
    private static RetainerSnapshot Seller(int id, string name, int used = 0) => new()
    {
        Key = RetainerSnapshot.MakeKey(1, (ulong)id), RetainerName = name, CharacterContentId = 1,
        LastRefreshedUtc = DateTime.UtcNow,
        Listings = Enumerable.Range(0, used).Select(i => new ListingEntry { ListingIndex = i, ItemId = 999, Quantity = 1 }).ToList()
    };

    [Fact]
    public void Plans_round_trip_by_stable_key_and_validate_before_replacing()
    {
        var config = JsonSerializer.Deserialize<StockTrackingConfiguration>("{}")!;
        Assert.Empty(config.SlotPlans);
        var a = Seller(1, "RetainerA");
        var b = Seller(2, "RetainerB");
        Assert.True(config.SlotPlans.TrySave(a.Key, [new(Item, false, 18, 4), new(Item, true, 2, 1)], out _));
        Assert.False(config.SlotPlans.TrySave(a.Key, [new(Item, false, 21, 1)], out var reason));
        Assert.Contains("20", reason);
        Assert.True(config.SlotPlans.TrySave(b.Key, [new(Item, false, 1, 7)], out _));
        config = JsonSerializer.Deserialize<StockTrackingConfiguration>(JsonSerializer.Serialize(config))!;
        a.RetainerName = "RetainerB";
        Assert.Equal(18, config.SlotPlans.Rows(a.Key)[0].Slots);
        Assert.Equal(79, config.SlotPlans.Target(1, Item, false));
        Assert.Equal(0, config.SlotPlans.Target(2, Item, false));
    }

    [Fact]
    public void Board_matches_quality_independent_of_server_slot_and_marks_only_actual_blockers()
    {
        var a = Seller(1, "RetainerA", 19);
        a.Listings[15].ItemId = Item;
        var plans = new SlotPlans { [a.Key] = [new(Item, false, 2, 3), new(Item, true, 1, 1)] };
        var layout = plans.Layout(a);
        Assert.Equal((19, 1, 0, 2, 18, 1), (layout.Used, layout.Reserved, layout.Free, layout.Missing, layout.Extra, layout.Blocking));
        Assert.True(layout.Slots[15].Filled);
        Assert.Equal(19, layout.Destination(Item, false));
        Assert.Equal(-1, layout.Destination(Other, false));
        Assert.Equal(-1, layout.Destination(Item, true));
        var match = MatchClickPlanner.Plan([a], Item, false, 5, true, false, 0, 0);
        Assert.Equal(PlannedActionKind.Reprice, Assert.Single(match.Actions).Kind);
        Assert.Equal(15, match.Actions[0].ListingIndex);
    }

    [Theory]
    [InlineData(100u, false)]
    [InlineData(8u, true)]
    public void Restock_and_fill_use_planned_lots_before_overflow(uint id, bool crystal)
    {
        var a = Seller(1, "RetainerA", 19);
        var b = Seller(2, "RetainerB", 17);
        var plans = new SlotPlans { [a.Key] = [new(id, false, 2, 4)], [b.Key] = [new(Other, false, 2, 1)] };
        List<RestockSource> sources = [crystal ? new(b.Key, b.Key, 40) : new("char.1.bag", null, 40)];
        var free = new Dictionary<string, int> { [a.Key] = 1, [b.Key] = 3 };
        var actions = RestockPlanner.Plan(id, false, 8, 0, 1, 100, sources, free,
            sellOn: b.RetainerName, retainers: [b, a], plans: plans);
        Assert.Equal(new[] { a.Key, b.Key }, actions.Select(x => x.RetainerKey));
        Assert.All(actions, x => Assert.Equal(4, x.Quantity));
        Assert.Equal(crystal, actions[0].NeedsCrystalWithdrawal);
        var fill = RestockPlanner.Plan(id, false, 8, 80, 1, 100, sources, free,
            retainers: [a,b], plans: plans, fillOnly: true, fillRetainer: a.Key);
        Assert.Equal(a.Key, Assert.Single(fill).RetainerKey);
        a.LastRefreshedUtc = DateTime.MinValue;
        var overflow = RestockPlanner.Plan(id, false, 8, 0, 1, 100, sources, free, retainers: [a,b], plans: plans);
        Assert.Equal(b.Key, Assert.Single(overflow).RetainerKey);
    }

    [Fact]
    public void Bulk_crystals_use_the_withdrawal_path_and_batch_limits()
    {
        var a = Seller(1, "RetainerA", 18);
        var b = Seller(2, "RetainerB", 19);
        var plans = new SlotPlans { [a.Key] = [new(8, false, 2, 10)], [b.Key] = [new(Other, false, 1, 1)] };
        b.Inventory.Add(new() { ItemId = 8, Quantity = 50 });
        var actions = Planner.PlanBulk(plans, [a, b], b, null, b.Key, 8, false, 10, 9999, 5, 10, 0, []);
        Assert.Equal(2, actions.Count);
        Assert.All(actions, x => { Assert.Equal(a.Key, x.RetainerKey); Assert.True(x.NeedsCrystalWithdrawal); });
        Assert.Empty(Planner.PlanBulk(plans, [a,b], b, null, b.Key, 8, false, 10, 9999, 5, 10, 0, actions));
    }

    [Fact]
    public void Auto_batch_preserves_other_reservations_and_uses_target_and_keep()
    {
        var a = Seller(1, "RetainerA", 18);
        var b = Seller(2, "RetainerB", 18);
        var plans = new SlotPlans { [a.Key] = [new(Item, false, 1, 5)], [b.Key] = [new(Item, false, 2, 2)] };
        var bag = new CharacterSnapshot { CharacterContentId = 1, Bag =
            [new() { ItemId = Item, Quantity = 10 }, new() { ItemId = Other, Quantity = 20 }] };
        var batch = AutoRestockPlanner.Plan([new() { ItemId = Other, Target = 20, PerListing = 1 },
            new() { ItemId = Item, Target = 1 }], [a,b], bag, new ListingTracker(), (_,_) => false, (_,_) => 0,
            (id,_) => id == Item ? 2 : 0, plans);
        Assert.Equal(a.Key, Assert.Single(batch[0].Actions).RetainerKey);
        Assert.Equal(new[] { 5, 2, 1 }, batch[1].Actions.Select(x => x.Quantity));
        Assert.Equal(new[] { a.Key, b.Key, b.Key }, batch[1].Actions.Select(x => x.RetainerKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bulk_accounts_for_prior_rows_and_fixed_inventory(bool fixedSource)
    {
        var a = Seller(1, "RetainerA", 18);
        var b = Seller(2, "RetainerB", 17);
        var plans = new SlotPlans { [a.Key] = [new(Item, false, 1, 1)], [b.Key] = [new(Other, false, 2, 1)] };
        var stock = new InventoryEntry { ItemId = Item, Quantity = 10 };
        b.Inventory.Add(stock);
        var bag = new CharacterSnapshot { CharacterContentId = 1, Bag = [stock] };
        List<PlannedAction> Run(SlotPlans p, List<PlannedAction> earlier) => Planner.PlanBulk(p, [b,a], b, bag,
            fixedSource ? b.Key : "char.1.bag", Item, false, 100, 99, 4, 1, 0, earlier);
        Assert.All(Run(new(), []), x => Assert.Equal(b.Key, x.RetainerKey));
        var result = Run(plans, []);
        if (fixedSource) Assert.Equal(b.Key, Assert.Single(result).RetainerKey);
        else
        {
            Assert.Equal(2, result.Count);
            Assert.Equal(new[] { a.Key, b.Key }, result.Select(x => x.RetainerKey));
            Assert.Empty(Run(plans, result));
        }
    }
}
