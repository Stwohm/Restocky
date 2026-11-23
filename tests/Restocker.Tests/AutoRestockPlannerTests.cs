using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public class AutoRestockPlannerTests
{
    private const uint Twine = 50970;
    private const uint Other = 50971;
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Tops_up_by_units_at_the_lowest_live_price_after_repricings()
    {
        var a = Retainer("A", Listed(Twine, 1, 1_234), Listed(Twine, 1, 1_600));
        var b = Retainer("B", Listed(Twine, 7, 800, hq: true));
        a.Inventory.Add(Stock(Twine, 2));
        var tracker = Tracker(Sale(Twine, "A", gross: 2_000));
        var market = Cache(Now, nq: 900);
        var entry = new LowStockEntry { ItemId = Twine, Threshold = 2, Target = 3, Price = 3_000 };

        var result = Assert.Single(Plan([entry], [a, b], tracker: tracker, market: market));

        Assert.Equal(AutoRestockPriceKind.Listing, result.PriceKind);
        var action = Assert.Single(result.Actions);
        Assert.Equal("A", action.SourceKey);
        Assert.Equal("A", action.RetainerKey);
        Assert.Equal(1, action.Quantity);
        Assert.Equal(1_234, action.UnitPrice);
        Assert.False(action.IsHQ);
    }

    [Fact]
    public void Requires_threshold_and_target_and_respects_hold_by_quality()
    {
        var a = Retainer("A", Listed(Twine, 2, 100), Listed(Other, 1, 100));
        a.Inventory.AddRange([Stock(Twine, 5), Stock(Twine, 5, hq: true), Stock(Other, 5)]);
        var entries = new[]
        {
            new LowStockEntry { ItemId = Twine, Threshold = 1, Target = 3 },
            new LowStockEntry { ItemId = Twine, IsHq = true, Target = 2 },
            new LowStockEntry { ItemId = Other, Threshold = 1, Target = 1 },
        };

        Assert.Empty(Plan(entries, [a], market: Cache(Now, hq: 200), held: (id, hq) => id == Twine && hq));
        Assert.True(Assert.Single(Plan(entries, [a], market: Cache(Now, hq: 200))).IsHq);
    }

    [Theory]
    [InlineData(1_200, 15, 0, 1_185)]
    [InlineData(1_200, 15, 1_195, 1_195)]
    [InlineData(1, 15, 0, 1)]
    public void Fresh_market_uses_same_quality_undercut_and_floor(int competitor, int undercut, int floor, int price)
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1, hq: true));
        var tracker = Tracker(Sale(Twine, "A", gross: 1_234, hq: true));
        var market = Cache(Now.AddHours(-5.99), nq: 75, hq: competitor);

        var result = Assert.Single(Plan([Watch(Twine, hq: true)], [a], tracker: tracker, market: market,
            undercut: undercut, floor: floor));

        Assert.Equal(AutoRestockPriceKind.Market, result.PriceKind);
        Assert.Equal(price, Assert.Single(result.Actions).UnitPrice);
    }

    [Theory]
    [InlineData(6, 6, 5_000)]
    [InlineData(7, 6, 5_000)]
    [InlineData(1, 1, 5_000)]
    [InlineData(0, 0, 5_000)]
    [InlineData(-1, 6, 5_000)]
    [InlineData(1, 6, 0)]
    public void Falls_back_to_last_same_quality_sale_when_the_scan_cannot_supply_a_price(int age, int limit, int hqPrice)
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1, hq: true));
        var tracker = Tracker(
            Sale(Twine, "A", gross: 1_000, hq: true, when: Now.AddDays(-2)),
            Sale(Twine, "Former retainer", gross: 2_468, hq: true, quantity: 2, when: Now.AddDays(-1)),
            Sale(Twine, "A", gross: 900),
            Sale(Other, "A", gross: 50, hq: true));

        var result = Assert.Single(Plan([Watch(Twine, hq: true)], [a], tracker: tracker,
            market: Cache(Now.AddHours(-age), nq: 300, hq: hqPrice), scanAge: limit));

        Assert.Equal(AutoRestockPriceKind.LastSale, result.PriceKind);
        Assert.Equal(1_234, Assert.Single(result.Actions).UnitPrice);
    }

    [Theory]
    [InlineData(1_234, 0, 1_234)]
    [InlineData(0, 2_344, 1_234)]
    [InlineData(1_000, 2_344, 1_234)]
    public void Handles_sale_history_with_only_net_proceeds_and_older_records_with_only_asking_price(
        int lastPrice, int netGil, int expected)
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1));
        var sale = Sale(Twine, "A", gross: null, quantity: 2);
        sale.LastPrice = lastPrice;
        sale.SoldTotalGil = netGil;

        var result = Assert.Single(Plan([Watch(Twine)], [a], tracker: Tracker(sale)));

        Assert.Equal(AutoRestockPriceKind.LastSale, result.PriceKind);
        Assert.Equal(expected, Assert.Single(result.Actions).UnitPrice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Applies_the_floor_to_listing_and_sale_sources(bool listed)
    {
        var a = listed ? Retainer("A", Listed(Twine, 1, 100)) : Retainer("A");
        a.Inventory.Add(Stock(Twine, 1));
        var result = Assert.Single(Plan([new LowStockEntry { ItemId = Twine, Target = 2 }], [a],
            tracker: Tracker(Sale(Twine, "A", gross: 99)), floor: 105));

        Assert.Equal(listed ? AutoRestockPriceKind.Listing : AutoRestockPriceKind.LastSale, result.PriceKind);
        Assert.Equal(105, Assert.Single(result.Actions).UnitPrice);
    }

    [Fact]
    public void Reports_a_skip_without_inventing_a_price_or_using_the_floor_as_one()
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1));
        var result = Assert.Single(Plan([Watch(Twine)], [a], floor: 750));
        Assert.Empty(result.Actions);
        Assert.Equal(AutoRestockPriceKind.NoMarketOrSale, result.PriceKind);

        var invalidFloor = Assert.Single(Plan([Watch(Twine)], [a], market: Cache(Now, nq: 100), floor: (long)uint.MaxValue + 1));
        Assert.Empty(invalidFloor.Actions);
        Assert.Equal(AutoRestockPriceKind.OutOfRange, invalidFloor.PriceKind);

        a.Listings.Add(Listed(Twine, 1, 0));
        var missingListingPrice = Assert.Single(Plan([new LowStockEntry { ItemId = Twine, Target = 2 }], [a],
            market: Cache(Now, nq: 100)));
        Assert.Empty(missingListingPrice.Actions);
        Assert.Equal(AutoRestockPriceKind.UnknownListingPrice, missingListingPrice.PriceKind);
    }

    [Fact]
    public void Shares_free_slots_across_items()
    {
        var a = Retainer("A", Enumerable.Repeat(Listed(Other, 1, 100), 19).ToArray());
        var b = Retainer("B", Enumerable.Repeat(Listed(Other, 1, 100), 20).ToArray());
        var ch = new CharacterSnapshot { CharacterContentId = 1, Bag = [Stock(Twine, 2), Stock(Twine, 2, hq: true)] };

        var result = Assert.Single(Plan([Watch(Twine), Watch(Twine, hq: true)], [a, b],
            character: ch, market: Cache(Now, nq: 100, hq: 200)));

        var action = Assert.Single(result.Actions);
        Assert.Equal("A", action.RetainerKey);
        Assert.False(action.IsHQ);
    }

    [Fact]
    public void Plans_a_watched_item_only_once()
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 2));
        var result = Assert.Single(Plan([Watch(Twine), Watch(Twine)], [a], market: Cache(Now, nq: 100)));
        Assert.Equal(1, Assert.Single(result.Actions).Quantity);
    }

    [Fact]
    public void Reuses_retainer_bag_and_saddle_sources_without_combining_inventory_stacks()
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1));
        var ch = new CharacterSnapshot
        {
            CharacterContentId = 1, Bag = [Stock(Twine, 2), Stock(Twine, 1)],
            Saddlebag = [Stock(Twine, 10, max: 2)], PremiumSaddlebag = [Stock(Twine, 5)],
        };
        var entry = new LowStockEntry { ItemId = Twine, Target = 16, PerListing = 5 };
        var result = Assert.Single(Plan([entry], [a], character: ch, market: Cache(Now, nq: 100)));

        Assert.Equal(new[] { 1, 2, 1, 2, 2, 2, 2, 2, 2 }, result.Actions.Select(x => x.Quantity));
        Assert.Equal(new[] { "A", "char.1.bag", "char.1.bag" }, result.Actions.Take(3).Select(x => x.SourceKey));
        Assert.All(result.Actions.Skip(3), x => Assert.Equal("char.1.saddle", x.SourceKey));
    }

    private static List<AutoRestockPlan> Plan(LowStockEntry[] entries, RetainerSnapshot[] retainers,
        CharacterSnapshot? character = null, ListingTracker? tracker = null, MarketCache? market = null,
        int scanAge = 6, int undercut = 1, long floor = 0, Func<uint, bool, bool>? held = null)
        => AutoRestockPlanner.Plan(entries, retainers, character, tracker ?? new(), market, Now, scanAge, undercut,
            held ?? ((_, _) => false), (_, _) => floor);

    private static LowStockEntry Watch(uint id, bool hq = false) => new() { ItemId = id, IsHq = hq, Target = 1 };
    private static RetainerSnapshot Retainer(string key, params ListingEntry[] listings)
        => new() { Key = key, Listings = listings.ToList() };
    private static ListingEntry Listed(uint id, int qty, long price, bool hq = false)
        => new() { ItemId = id, IsHQ = hq, Quantity = qty, UnitPrice = price };
    private static InventoryEntry Stock(uint id, int qty, bool hq = false, int max = 99)
        => new() { ItemId = id, IsHQ = hq, Quantity = qty, MaxStackPerListing = max };
    private static ListingTracker Tracker(params ListingRecord[] sales) => new() { Records = sales.ToList() };
    private static ListingRecord Sale(uint id, string key, long? gross, bool hq = false, int quantity = 1, DateTime? when = null)
        => new()
        {
            ItemId = id, IsHq = hq, Quantity = quantity, RetainerKey = key, Outcome = ListingTracker.Sold,
            SoldGrossGil = gross, SoldRecordedUtc = when ?? Now,
        };

    private static MarketCache Cache(DateTime scannedAt, long nq = 0, long hq = 0)
    {
        var cache = new MarketCache { Clock = () => scannedAt };
        cache.Replace(Twine, new MarketScan([new MarketOffer(nq, "NQ", 1)], [new MarketOffer(hq, "HQ", 1)]));
        return cache;
    }
}
