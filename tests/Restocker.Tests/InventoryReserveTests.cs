using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class InventoryReserveTests
{
    private const uint Ore = 42;
    private static InventoryEntry Stock(int qty, uint id = Ore) => new() { ItemId = id, Quantity = qty };
    private static RetainerSnapshot Seller(string name) => new() { CharacterContentId = 1, Key = name, RetainerName = name };

    [Theory]
    [InlineData(100, 30, 70)]
    [InlineData(100, 0, 100)]
    [InlineData(30, 100, 0)]
    public void Character_allowance(int held, int keep, int expected)
        => Assert.Equal(expected, InventoryReserve.Available(held, keep));

    [Fact]
    public void Old_settings_default_to_list_view_and_zero_and_round_trip_quality()
    {
        var config = JsonSerializer.Deserialize<StockTrackingConfiguration>("{}")!;
        Assert.False(config.ShowRetainerBoard);
        Assert.Equal(0, config.KeepFor(Ore, true));
        config.SetKeep(Ore, true, 100);
        config.ShowRetainerBoard = true;
        config = JsonSerializer.Deserialize<StockTrackingConfiguration>(JsonSerializer.Serialize(config))!;
        Assert.True(config.ShowRetainerBoard);
        Assert.Equal(0, config.KeepFor(Ore, false));
        Assert.Equal(100, config.KeepFor(Ore, true));
        config.SetKeep(Ore, true, -1);
        Assert.Empty(config.InventoryReserves);
        Assert.Empty(config.LowStock);
    }

    [Fact]
    public void Selected_manual_top_up_and_new_listings_share_one_allowance()
    {
        var seller = Seller("RetainerA");
        seller.Inventory.Add(Stock(2));
        var character = new CharacterSnapshot { Bag = [Stock(12)], PremiumSaddlebag = [Stock(4)] };
        var entry = new LowStockEntry { ItemId = Ore, Target = 30, PerListing = 6 };
        var slots = new Dictionary<string, int> { [seller.Key] = 19 };
        var batch = RestockPlanner.PlanBatch([entry, entry], slots, (e, free) =>
        {
            var sources = AutoRestockPlanner.Sources(Ore, false, [seller], character, 10);
            var top = StackTopUpPlanner.Plan(Ore, false, 99, 99,
                [(seller.Key, new ListingEntry { ItemId = Ore, Quantity = 1, UnitPrice = 10 })], sources, 6);
            var added = top.Single().TopUp!;
            Assert.Equal(2, added.RetainerUnits);
            Assert.Equal(3, added.BagUnits);
            top.AddRange(RestockPlanner.Plan(Ore, false, e.Target, 6, e.PerListing, 10, sources, free));
            return top;
        });
        Assert.Equal(3, Assert.Single(batch.Actions, a => a.TopUp == null).Quantity);
    }

    [Fact]
    public void Bulk_rows_share_character_budget_across_sources_and_repeated_rows()
    {
        var seller = Seller("RetainerA");
        var ch = new CharacterSnapshot { CharacterContentId = 1, Bag = [Stock(12)], Saddlebag = [Stock(10)] };
        var actions = new List<PlannedAction>();
        void Add(string source) => actions.AddRange(Planner.PlanCharacterSource(ch, "char.1." + source,
            seller, Ore, false, 10, 99, 1, 8, 10, actions));
        Add("bag");
        Add("saddle");
        Add("bag");
        Assert.Equal(new[] { 8, 4 }, actions.Select(a => a.Quantity));
        Assert.Empty(Planner.PlanCharacterSource(ch, "char.1.bag", seller, Ore, false, 10, 99, 1, 15, 0, []));
    }

    [Fact]
    public void Crystal_top_up_checks_physical_capacity_including_reserved_units()
    {
        var seller = Seller("RetainerA");
        var ch = new CharacterSnapshot { Bag = [Stock(9990, 2)] };
        var sources = AutoRestockPlanner.Sources(2, false, [seller], ch, 9900);
        var listing = new ListingEntry { ItemId = 2, Quantity = 10, UnitPrice = 25 };
        Assert.Empty(StackTopUpPlanner.Plan(2, false, 9999, 9999, [(seller.Key, listing)], sources));
        listing.Quantity = 9;
        var action = Assert.Single(StackTopUpPlanner.Plan(2, false, 9999, 9999, [(seller.Key, listing)], sources));
        Assert.Equal(90, action.TopUp!.BagUnits);
    }

    [Fact]
    public void New_crystal_listing_uses_the_character_sell_menu_and_retainer_stock_is_independent()
    {
        var seller = Seller("RetainerA");
        seller.Inventory.Add(Stock(90, 2));
        var ch = new CharacterSnapshot { CharacterContentId = 1, Bag = [Stock(1030, 2)] };
        var bag = Assert.Single(Planner.PlanCharacterSource(ch, "char.1.bag", seller, 2, false, 10, 9999, 1, 80, 1000, []));
        Assert.Equal(30, bag.Quantity);
        Assert.True(bag.ListsCharacterCrystals);
        var own = Assert.Single(Planner.PlanNewListings([seller], 2, false, 10, 9999));
        Assert.Equal(90, own.Quantity);
        Assert.False(own.ListsCharacterCrystals);
    }

    [Fact]
    public void Full_cycle_and_visit_reserve_character_crystals_and_allow_retainer_sources()
    {
        const uint shard = 2;
        var a = Seller("RetainerA");
        var b = Seller("RetainerB");
        a.Inventory.Add(Stock(25, shard));
        b.Inventory.Add(Stock(50, shard));
        var ch = new CharacterSnapshot { CharacterContentId = 1, Bag = [Stock(1020, shard)] };
        var entry = new LowStockEntry { ItemId = shard, Target = 100, PerListing = 100, PreferredRetainers = new() { [1] = a.Key } };
        var plan = AutoRestockPlanner.Plan([entry], [a, b], ch, new ListingTracker(),
            (_, _) => false, (_, _) => 0, (_, _) => 1000);
        var actions = Assert.Single(plan).Actions;
        Assert.Equal(20, actions.Single(x => x.SourceKey.StartsWith("char.")).Quantity);
        Assert.Equal(25, actions.Single(x => x.SourceKey == a.Key).Quantity);
        Assert.Equal(50, actions.Single(x => x.NeedsCrystalWithdrawal).Quantity);
        Assert.Equal(45, AutoRestockPlanner.ForVisit(plan, a, a).SelectMany(p => p.Actions).Sum(x => x.Quantity));
        Assert.Equal(50, InventoryReserve.AfterTransfer(1050, 1000, 1000));
        Assert.Equal(50, InventoryReserve.AfterTransfer(150, 1000, 100));
        Assert.Equal(30, InventoryReserve.AfterTransfer(1030, 1000, 1000));
    }
}
