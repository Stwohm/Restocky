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
}

public sealed class MarketCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (MarketScan Scan, DateTime UpdatedUtc)> store = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public void Replace(uint itemId, MarketScan scan) => store[itemId] = (scan, Clock());

    internal (MarketScan Scan, DateTime UpdatedUtc)? GetScan(uint itemId)
        => store.TryGetValue(itemId, out var entry) ? entry : null;

    public DateTime? UpdatedUtc(uint itemId) => GetScan(itemId)?.UpdatedUtc;

    public bool HasDataSince(uint itemId, DateTime sinceUtc) => UpdatedUtc(itemId) >= sinceUtc;

    public IReadOnlyList<MarketOffer> GetOffers(uint itemId, bool isHQ, bool matchQuality)
        => store.TryGetValue(itemId, out var entry) ? entry.Scan.Competitors(isHQ, matchQuality) : Array.Empty<MarketOffer>();

    public long GetLowest(uint itemId, bool isHQ, bool matchQuality, long ourPrice = 0)
        => LowestPriceResolver.Pick(GetOffers(itemId, isHQ, matchQuality).Select(o => o.Price).ToArray(), ourPrice: ourPrice);
}
