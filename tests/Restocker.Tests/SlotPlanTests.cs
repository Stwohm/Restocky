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

    [Fact]
    public void Clicked_slots_share_quantity_and_last_removal_restores_stock_defaults()
    {
        var seller = Seller(1, "RetainerA");
        var entry = new LowStockEntry { ItemId = 8, Target = 300, PerListing = 50, PreferredRetainers = { [1] = "RetainerB" } };
        var config = new StockTrackingConfiguration { LowStock = [entry] };
        var plans = config.SlotPlans;
        var crystal = new SlotPlanRow(8, false, 1, 2000);
        Assert.True(plans.TryEditSlot(seller, 17, crystal, out _));
        Assert.True(plans.TryEditSlot(seller, 4, crystal, out _));
        Assert.Equal(2, Assert.Single(plans.Rows(seller.Key)).Slots);
        Assert.Equal(4000, plans.Target(1, 8, false));
        Assert.True(plans.TryEditSlot(seller, 17, crystal with { PerListing = 9999 }, out _));
        var row = Assert.Single(plans.Rows(seller.Key));
        Assert.Equal(9999, row.PerListing);
        Assert.Equal(new[] { 4, 17 }, row.Positions);
        Assert.False(plans.TryEditSlot(seller, 4, crystal with { PerListing = 10000 }, out _));
        Assert.True(plans.TryEditSlot(seller, 4, null, out _));
        Assert.Equal(9999, plans.Target(1, 8, false));
        Assert.True(plans.TryEditSlot(seller, 17, null, out _));
        Assert.Empty(plans);
        Assert.Equal(0, plans.Target(1, 8, false));
        Assert.Equal((300, 50, "RetainerB"), (entry.Target, entry.PerListing, entry.PreferredRetainers[1]));
    }

    [Fact]
    public void Full_board_allows_replacement_and_refuses_a_twenty_first_slot()
    {
        var seller = Seller(1, "RetainerA", 20);
        var plans = new SlotPlans();
        var row = new SlotPlanRow(Item, false, 1, 2);
        for (var i = 0; i < 20; i++) Assert.True(plans.TryEditSlot(seller, i, row, out _));
        Assert.False(plans.TryEditSlot(seller, 20, row, out var reason));
        Assert.Contains("20", reason);
        var blocked = plans.Layout(seller).DisplaySlots()[12];
        Assert.True(blocked.Blocking);
        var actual = blocked.Listing!;
        Assert.True(plans.TryEditSlot(seller, 12, new(actual.ItemId, actual.IsHQ, 1, actual.Quantity), out _));
        Assert.Equal(19, plans.Rows(seller.Key)[0].Slots);
        Assert.True(plans.Layout(seller).DisplaySlots()[12].Filled);
    }

    [Fact]
    public void Legacy_positions_are_pinned_on_edit_and_survive_reload_and_market_reordering()
    {
        var seller = Seller(1, "RetainerA", 3);
        seller.Listings[1].ItemId = Item;
        var plans = new SlotPlans { [seller.Key] = [new(Item, false, 2, 3)] };
        var legacy = plans.Layout(seller).DisplaySlots();
        Assert.True(legacy[1].Filled);
        Assert.NotNull(legacy[3].Plan);
        Assert.True(plans.TryEditSlot(seller, 18, new(Other, true, 1, 5), out _));
        plans = JsonSerializer.Deserialize<SlotPlans>(JsonSerializer.Serialize(plans))!;
        seller.Listings[1].ListingIndex = 9;
        seller.Listings[0].ItemId = Other;
        seller.Listings[0].IsHQ = true;
        var layout = plans.Layout(seller);
        var display = layout.DisplaySlots();
        Assert.Equal(new[] { 1, 3 }, display.Where(s => s.Plan?.ItemId == Item).Select(s => s.Index));
        Assert.Equal(9, Assert.Single(display, s => s.Filled && s.Plan!.ItemId == Item).Listing!.ListingIndex);
        Assert.True(display[18].Filled);
        Assert.Equal(0, display[18].Listing!.ListingIndex);
        Assert.Equal(3, display.Count(s => s.Listing != null));
        Assert.Null(layout.Slots[layout.Destination(Item, false)].Listing);
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
