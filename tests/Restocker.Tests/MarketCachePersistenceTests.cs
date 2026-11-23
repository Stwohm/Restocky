using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Restocker.Market;

namespace Restocker.Tests;

public sealed class MarketCachePersistenceTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Restoring_a_full_cache_after_a_live_scan_keeps_the_limit_and_the_new_scan()
    {
        var cache = new MarketCache { Clock = () => Now };
        cache.Replace(1000, new MarketScan([new(200, "New", 1)], []) { WorldId = 5 });
        var saved = Enumerable.Range(1, MarketCache.SavedPriceLimit)
            .Select(i => new CachedMarketPrice(5, (uint)i, Now.AddSeconds(-i), 100, 0, 100));
        cache.RestorePrices(saved.Prepend(null!));
        Assert.Equal(MarketCache.SavedPriceLimit, cache.SavedPrices().Count);
        Assert.Contains(cache.SavedPrices(), p => p.ItemId == 1000);
        Assert.Null(cache.CachedLowest(5, MarketCache.SavedPriceLimit, false, true));
    }

    [Fact]
    public void A_saved_scan_keeps_low_offers_corroborated_by_our_current_price()
    {
        var cache = new MarketCache { Clock = () => Now };
        cache.Replace(42, new MarketScan([new(100, "A", 1), new(120, "B", 1), new(500, "C", 1)], []) { WorldId = 5 });
        var saved = new MarketCache();
        saved.RestorePrices(JsonSerializer.Deserialize<List<CachedMarketPrice>>(JsonSerializer.Serialize(cache.SavedPrices()))!);
        foreach (var quality in new[] { false, true })
        {
            Assert.Equal((100L, Now), saved.CachedLowest(5, 42, false, quality, 180));
            Assert.Equal((500L, Now), saved.CachedLowest(5, 42, false, quality, 181));
        }
    }

    [Fact]
    public void Restores_lowest_by_world_and_quality_with_the_original_timestamp()
    {
        const uint item = 5121;
        var cache = new MarketCache { Clock = () => Now };
        cache.Replace(item, new MarketScan([new(10, "Bait", 1), new(770, "NQ", 48)], [new(500, "HQ", 1)]) { WorldId = 5 });
        cache.Replace(item, new MarketScan([new(300, "NQ", 1)], []) { WorldId = 6 });
        var saved = JsonSerializer.Serialize(cache.SavedPrices());
        var restored = new MarketCache();
        restored.RestorePrices(JsonSerializer.Deserialize<List<CachedMarketPrice>>(saved)!);

        Assert.Equal((770L, Now), restored.CachedLowest(5, item, false, true));
        Assert.Equal((500L, Now), restored.CachedLowest(5, item, true, true));
        Assert.Equal((500L, Now), restored.CachedLowest(5, item, false, false));
        Assert.Equal((300L, Now), restored.CachedLowest(6, item, false, true));
        Assert.Equal((0L, Now), restored.CachedLowest(6, item, true, true));
        Assert.Null(restored.CachedLowest(7, item, false, true));
        Assert.False(restored.HasDataSince(item, Now));
    }

    [Fact]
    public void Keeps_only_the_newest_prices_and_replaces_old_offers_when_a_board_empties()
    {
        var updated = Now;
        var cache = new MarketCache { Clock = () => updated };
        for (uint item = 1; item <= MarketCache.SavedPriceLimit + 1; item++)
        {
            updated = updated.AddSeconds(1);
            cache.Replace(item, new MarketScan([new(770, "NQ", 48)], []) { WorldId = 5 });
        }
        Assert.Equal(MarketCache.SavedPriceLimit, cache.SavedPrices().Count);
        Assert.DoesNotContain(cache.SavedPrices(), price => price.ItemId == 1);
        Assert.Equal((770L, Now.AddSeconds(1)), cache.CachedLowest(5, 1, false, true));

        updated = updated.AddSeconds(1);
        cache.Replace(2, new MarketScan([], []) { WorldId = 5 });
        Assert.Equal((0L, updated), cache.CachedLowest(5, 2, false, true));
        Assert.Equal(0, cache.SavedPrices().Single(price => price.ItemId == 2).Nq);
    }
}
