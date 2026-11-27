using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public class MixedStockFloorTests
{
    [Theory]
    [InlineData(3000, 100, 6)]
    [InlineData(200, 200, 76)]
    [InlineData(null, 3000, 6)]
    [InlineData(null, 10, 76)]
    public void FireCrystalFloorUsesHeldUnitsOrListedLowerBound(int? held, int listed, long expected)
    {
        var ledger = new CostLedger();
        ledger.RecordLot(8, false, 200, 71.4m, DateTime.UtcNow);
        ledger.RecordLot(8, true, 10, 900m, DateTime.UtcNow);
        var character = new CharacterSnapshot { Bag = [new() { ItemId = 8, Quantity = held.GetValueOrDefault(listed) - listed }] };
        var stock = InventorySnapshot.HeldUnits(8, false, [], held.HasValue ? [character] : [], "key", listed);
        var unitCost = ledger.WeightedAverageCost(8, false, stock.Listed + stock.Available);
        var floor = BreakEvenFloor.Resolve(0, unitCost, 5, 0).Price;
        Assert.Equal(expected, floor);
        if (expected == 6)
        {
            var decision = RepriceRules.Decide(58, [57], floor, new());
            Assert.True(decision.Apply);
            Assert.Equal(56, decision.NewPrice);
        }
    }

    [Fact]
    public void LiveListingsReplaceOnlyTheirRetainerAndAllKnownInventoriesCount()
    {
        InventoryEntry Stack(int qty) => new() { ItemId = 8, Quantity = qty };
        var current = new RetainerSnapshot { Key = "a", Listings = [new() { ItemId = 8, Quantity = 9999 }],
            Inventory = [Stack(10)] };
        var other = new RetainerSnapshot { Key = "b", Listings = [new() { ItemId = 8, Quantity = 100 },
            new() { ItemId = 8, IsHQ = true, Quantity = 9999 }], Inventory = [Stack(20)] };
        var character = new CharacterSnapshot { Bag = [Stack(30)], Saddlebag = [Stack(40)],
            PremiumSaddlebag = [Stack(50), new() { ItemId = 9, Quantity = 9999 }] };
        Assert.Equal((2850L, 150L), InventorySnapshot.HeldUnits(8, false, [current, other], [character], "a", 2750));
    }

    [Theory]
    [InlineData(56)]
    [InlineData(58)]
    public void ReviewedFloorRaiseStillGoesToGuardedHistory(long oldPrice)
    {
        var plan = AutoRepricePlanner.Plan("key", "RetainerA", [new(4, 8, false, 3000, oldPrice)],
            (_, _) => [new(57, "Rival", 1)], (_, _) => 76, new(), DateTime.UtcNow,
            reviewedPrice: (_, _) => 76);
        Assert.Empty(plan.Actions);
        var row = Assert.Single(plan.NotApplied);
        Assert.False(row.Applied);
        Assert.Equal(RepriceReason.MaxRaise, row.Reason);
        Assert.Equal(76, row.ClampedFloor);
    }
}
