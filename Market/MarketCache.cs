using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>One competitor listing from a market board answer.</summary>
public readonly record struct MarketOffer(long Price, string Seller, int Quantity)
{
    public bool IsHQ { get; init; }
}

public static class MarketOfferFormat
{
    internal static string RepriceCompetitor(IReadOnlyList<MarketOffer> offers, bool matchQuality, long lowest)
    {
        // The first offer at the selected price is the winner when prices tie.
        var chosen = offers.FirstOrDefault(o => o.Price == lowest);
        var hq = chosen.Price <= 0 ? "none" : chosen.IsHQ ? "true" : "false";
        return $"quality_pool={(matchQuality ? "same" : "both")} lowest_hq={hq} lowest={lowest}";
    }

    /// <summary>
    /// Sellers priced below <paramref name="ourPrice"/> with the quantity they have up,
    /// e.g. "Rival 4x;Bob 1x" (several listings of one seller are summed), cheapest first.
    /// </summary>
    public static string Undercutters(IEnumerable<MarketOffer> offers, long ourPrice)
    {
        var order = new List<string>();
        var qty = new Dictionary<string, int>();
        foreach (var o in offers.Where(o => o.Price < ourPrice).OrderBy(o => o.Price))
        {
            var name = string.IsNullOrEmpty(o.Seller) ? "?" : o.Seller;
            if (!qty.ContainsKey(name)) { qty[name] = 0; order.Add(name); }
            qty[name] += System.Math.Max(1, o.Quantity);
        }
        return string.Join(";", order.Select(n => $"{n} {qty[n]}x"));
    }
}

/// <summary>One home-world response. Every pricing view uses offers from the same scan.</summary>
public sealed class MarketScan
{
    internal uint WorldId { get; init; }
    private readonly MarketOffer[] nq;
    private readonly MarketOffer[] hq;
    private readonly MarketOffer[] all;

    public MarketScan(IEnumerable<MarketOffer> nqOffers, IEnumerable<MarketOffer> hqOffers)
    {
        nq = Sorted(nqOffers.Select(o => o with { IsHQ = false }));
        hq = Sorted(hqOffers.Select(o => o with { IsHQ = true }));
        all = nq.Length == 0 ? hq : hq.Length == 0 ? nq : Sorted(nq.Concat(hq));
    }

    private static MarketOffer[] Sorted(IEnumerable<MarketOffer> offers)
        => offers.Where(o => o.Price > 0).OrderBy(o => o.Price).ToArray();

    public IReadOnlyList<MarketOffer> Competitors(bool isHQ, bool matchQuality)
        => matchQuality ? (isHQ ? hq : nq) : all;

    internal long Lowest(bool isHq, bool matchQuality, long ourPrice = 0)
        => LowestPriceResolver.Pick(Competitors(isHq, matchQuality).Select(o => o.Price).ToArray(), ourPrice: ourPrice);
}

public sealed record CachedMarketPrice(uint WorldId, uint ItemId, DateTime UpdatedUtc, long Nq, long Hq, long Combined)
{
    // Only the first three offers can affect the default two-offer bait filter.
    public long[]? NqLowOffers { get; init; }
    public long[]? HqLowOffers { get; init; }
    public long[]? CombinedLowOffers { get; init; }
}

public sealed class MarketCache
{
    internal const int SavedPriceLimit = 512;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (MarketScan Scan, DateTime UpdatedUtc)> store = new();
    private readonly Dictionary<(uint WorldId, uint ItemId), CachedMarketPrice> prices = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public void Replace(uint itemId, MarketScan scan)
    {
        lock (prices)
        {
            var now = Clock();
            store[itemId] = (scan, now);
            if (scan.WorldId == 0 || itemId == 0) return;
            var price = new CachedMarketPrice(scan.WorldId, itemId, now,
                scan.Lowest(false, true), scan.Lowest(true, true), scan.Lowest(false, false));
            prices[(scan.WorldId, itemId)] = price with
            {
                NqLowOffers = LowOffers(scan.Competitors(false, true), price.Nq),
                HqLowOffers = LowOffers(scan.Competitors(true, true), price.Hq),
                CombinedLowOffers = LowOffers(scan.Competitors(false, false), price.Combined),
            };
            if (prices.Count > SavedPriceLimit)
                prices.Remove(prices.MinBy(p => p.Value.UpdatedUtc).Key);
        }
    }

    private static long[]? LowOffers(IReadOnlyList<MarketOffer> offers, long picked)
        => offers.Count > 0 && offers[0].Price < picked ? offers.Take(3).Select(o => o.Price).ToArray() : null;

    private (long Lowest, DateTime UpdatedUtc)? SavedLowest(uint worldId, uint itemId, bool isHq, bool matchQuality, long ourPrice)
    {
        lock (prices)
        {
            if (!prices.TryGetValue((worldId, itemId), out var price)) return null;
            var lowest = matchQuality ? (isHq ? price.Hq : price.Nq) : price.Combined;
            var offers = matchQuality ? (isHq ? price.HqLowOffers : price.NqLowOffers) : price.CombinedLowOffers;
            if (ourPrice > 0 && offers is { Length: > 0 and <= 3 })
                lowest = LowestPriceResolver.Pick(offers, ourPrice: ourPrice);
            return (lowest, price.UpdatedUtc);
        }
    }

    internal (long Lowest, DateTime UpdatedUtc)? CachedLowest(uint worldId, uint itemId, bool isHq, bool matchQuality,
        long ourPrice = 0)
        => GetScan(itemId) is { } current && current.Scan.WorldId == worldId
            ? (current.Scan.Lowest(isHq, matchQuality, ourPrice), current.UpdatedUtc)
            : SavedLowest(worldId, itemId, isHq, matchQuality, ourPrice);

    internal void RestorePrices(IEnumerable<CachedMarketPrice> saved)
    {
        lock (prices)
        {
            foreach (var price in saved.Where(p => p != null && p.WorldId != 0 && p.ItemId != 0)
                .OrderByDescending(p => p.UpdatedUtc).DistinctBy(p => (p.WorldId, p.ItemId)).Take(SavedPriceLimit))
                prices.TryAdd((price.WorldId, price.ItemId), price);
            if (prices.Count > SavedPriceLimit)
                foreach (var key in prices.OrderByDescending(p => p.Value.UpdatedUtc).Skip(SavedPriceLimit).Select(p => p.Key).ToArray())
                    prices.Remove(key);
        }
    }

    internal List<CachedMarketPrice> SavedPrices()
    {
        lock (prices) return prices.Values.ToList();
    }

    internal (MarketScan Scan, DateTime UpdatedUtc)? GetScan(uint itemId)
        => store.TryGetValue(itemId, out var entry) ? entry : null;

    public DateTime? UpdatedUtc(uint itemId) => GetScan(itemId)?.UpdatedUtc;

    public bool HasDataSince(uint itemId, DateTime sinceUtc) => UpdatedUtc(itemId) >= sinceUtc;

    public IReadOnlyList<MarketOffer> GetOffers(uint itemId, bool isHQ, bool matchQuality)
        => store.TryGetValue(itemId, out var entry) ? entry.Scan.Competitors(isHQ, matchQuality) : Array.Empty<MarketOffer>();

    public long GetLowest(uint itemId, bool isHQ, bool matchQuality, long ourPrice = 0)
        => GetScan(itemId)?.Scan.Lowest(isHQ, matchQuality, ourPrice) ?? 0;
}
