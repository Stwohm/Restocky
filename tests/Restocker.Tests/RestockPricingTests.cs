using System.Collections.Generic;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class RestockPricingTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Applies_floor_to_lowest_positive_current_listing()
    {
        ListingEntry[] listings = [new() { UnitPrice = 0 }, new() { UnitPrice = 500 }, new() { UnitPrice = 650 }];
        Assert.Equal(new RestockPrice(550, AutoRestockPriceKind.Listing), RestockPricing.FromListings(listings, 550));
    }

    [Theory]
    [InlineData(true, 0, 769)]
    [InlineData(true, 790, 790)]
    [InlineData(false, 0, 499)]
    public void Live_market_beats_cache_and_last_sale_with_quality_bait_filter_undercut_and_floor(bool matchQuality, int floor, int expected)
    {
        var scan = new MarketScan([new(10, "Bait", 1), new(770, "NQ", 48)], [new(500, "HQ", 1)]);
        var price = RestockPricing.Resolve(scan.Lowest(false, matchQuality), (650, Now.AddHours(-2)), Now, 6, 1, floor,
            () => new ListingRecord { SoldGrossGil = 39_534, Quantity = 66 });

        Assert.Equal(new RestockPrice(expected, AutoRestockPriceKind.LiveMarket), price);
    }

    [Fact]
    public void Failed_lookup_uses_fresh_cache_and_reports_its_age()
    {
        var price = RestockPricing.Resolve(null, (770, Now.AddHours(-2)), Now, 6, 1, 0, () => null);
        Assert.Equal(new RestockPrice(769, AutoRestockPriceKind.Cache, TimeSpan.FromHours(2)), price);
    }

    [Theory]
    [InlineData(6, 6, 5000)]
    [InlineData(7, 6, 5000)]
    [InlineData(0, 0, 5000)]
    [InlineData(-1, 6, 5000)]
    [InlineData(1, 6, 0)]
    public void Failed_lookup_uses_last_sale_when_cached_price_is_unusable(int age, int maxAge, int cachedLowest)
    {
        var price = RestockPricing.Resolve(null, (cachedLowest, Now.AddHours(-age)), Now, maxAge, 1, 0,
            () => new ListingRecord { SoldGrossGil = 39_534, Quantity = 66 });
        Assert.Equal(new RestockPrice(599, AutoRestockPriceKind.LastSale), price);
    }

    [Fact]
    public void Confirmed_empty_board_bypasses_a_fresh_cache_and_applies_floor_to_sale()
    {
        var cache = (770L, Now.AddHours(-2));
        var price = RestockPricing.Resolve(0, cache, Now, 6, 1, 600,
            () => new ListingRecord { SoldGrossGil = 39_534, Quantity = 66 });
        Assert.Equal(new RestockPrice(600, AutoRestockPriceKind.LastSale), price);
        Assert.Equal(new RestockPrice(0, AutoRestockPriceKind.NoMarketOrSale),
            RestockPricing.Resolve(0, cache, Now, 6, 1, 600, () => null));
        Assert.Equal(new RestockPrice(0, AutoRestockPriceKind.NoMarketOrSale),
            RestockPricing.Resolve(null, null, Now, 6, 1, 600, () => null));
    }

    [Theory]
    [InlineData(1234, 0, 1234)]
    [InlineData(0, 2345, 1234)]
    [InlineData(1000, 2345, 1234)]
    public void Accepts_legacy_asking_price_and_net_only_sale_history(int lastPrice, int netGil, int expected)
    {
        var price = RestockPricing.Resolve(null, null, Now, 6, 1, 0,
            () => new ListingRecord { LastPrice = lastPrice, SoldTotalGil = netGil, Quantity = 2 });
        Assert.Equal(new RestockPrice(expected, AutoRestockPriceKind.LastSale), price);
    }

    [Fact]
    public void Skips_a_net_sale_that_cannot_represent_a_whole_gil_unit_price()
    {
        var price = RestockPricing.Resolve(null, null, Now, 6, 1, 0,
            () => new ListingRecord { SoldTotalGil = 101, Quantity = 3 });
        Assert.Equal(new RestockPrice(0, AutoRestockPriceKind.NoMarketOrSale), price);
    }

    [Fact]
    public void Uses_the_latest_same_quality_sale_from_this_character_or_home_world()
    {
        var own = Sale("1.F", Now.AddDays(-2));
        var sameWorld = Sale("2.B", Now.AddDays(-1));
        var otherWorld = Sale("3.C", Now);
        var wrongQuality = Sale("1.F", Now, hq: true);
        var tracker = new ListingTracker { Records = [own, wrongQuality, sameWorld, otherWorld] };
        var snapshots = new Dictionary<string, RetainerSnapshot>
        {
            ["2.B"] = new() { CharacterContentId = 2, WorldId = 7 },
            ["3.C"] = new() { CharacterContentId = 3, WorldId = 8 },
        };

        Assert.Same(sameWorld, RestockPricing.LastSale(tracker, 5121, false, 1, 7, snapshots));
        Assert.Same(own, RestockPricing.LastSale(tracker, 5121, false, 1, 0, snapshots));

        static ListingRecord Sale(string retainer, DateTime when, bool hq = false) => new()
        {
            RetainerKey = retainer, ItemId = 5121, IsHq = hq, Outcome = ListingTracker.Sold, SoldRecordedUtc = when,
        };
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-1, 0, false)]
    [InlineData(99, 100, false)]
    [InlineData(100, 100, true)]
    [InlineData(4294967295L, 0, true)]
    [InlineData(4294967296L, 0, false)]
    public void Validates_price_against_floor_and_api_range(long price, long floor, bool valid)
        => Assert.Equal(valid, RestockPricing.IsValidPrice(price, floor));

    [Theory]
    [InlineData(348)] // tracked listing
    [InlineData(0)]   // history only
    public void Last_sale_recovers_a_stack_price_whose_tax_is_not_a_whole_gil(long lastPrice)
    {
        var price = RestockPricing.Resolve(0, null, DateTime.UtcNow, 6, 1, 0,
            () => new ListingRecord { LastPrice = lastPrice, SoldTotalGil = 32_730, Quantity = 99 });
        Assert.Equal(new RestockPrice(348, AutoRestockPriceKind.LastSale), price);
    }
}
