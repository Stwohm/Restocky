using System;
using System.Collections.Generic;

namespace Restocker.Market;

internal readonly record struct ListingMarketScan(DateTime ScannedUtc, long UnitPrice, IReadOnlyList<MarketOffer> Offers)
{
    // MarketScan sorts offers by unit price.
    public bool WasUndercut => Offers.Count > 0 && Offers[0].Price < UnitPrice;
}

internal readonly record struct ListingPriceDecision(DateTime PlannedUtc, RepriceDecision Decision,
    bool MatchQuality, IReadOnlyList<MarketOffer> Bait);

internal sealed class ListingMarketScans
{
    private readonly Dictionary<(string RetainerKey, int Slot, uint ItemId, bool IsHq), ListingMarketScan> scans = new();
    private readonly Dictionary<(string RetainerKey, int Slot, uint ItemId, bool IsHq), ListingPriceDecision> decisions = new();

    public void Record(string retainerKey, int slot, uint itemId, bool isHq, ListingMarketScan scan)
    {
        var key = (retainerKey, slot, itemId, isHq);
        if (!scans.TryGetValue(key, out var previous) || scan.ScannedUtc >= previous.ScannedUtc)
        {
            scans[key] = scan;
            if (decisions.TryGetValue(key, out var decision) && scan.ScannedUtc > decision.PlannedUtc)
                decisions.Remove(key);
        }
    }

    public ListingMarketScan? Get(string retainerKey, int slot, uint itemId, bool isHq)
        => scans.TryGetValue((retainerKey, slot, itemId, isHq), out var scan) ? scan : null;

    public void RecordDecision(string retainerKey, int slot, uint itemId, bool isHq, DateTime nowUtc,
        RepriceDecision decision, bool matchQuality, IReadOnlyList<MarketOffer> bait)
    {
        var key = (retainerKey, slot, itemId, isHq);
        if ((!scans.TryGetValue(key, out var scan) || nowUtc >= scan.ScannedUtc)
            && (!decisions.TryGetValue(key, out var previous) || nowUtc >= previous.PlannedUtc))
            decisions[key] = new ListingPriceDecision(nowUtc, decision, matchQuality, bait);
    }

    public ListingPriceDecision? GetDecision(string retainerKey, int slot, uint itemId, bool isHq)
        => decisions.TryGetValue((retainerKey, slot, itemId, isHq), out var decision) ? decision : null;

    public void Forget(string retainerKey, int slot, uint itemId, bool isHq)
    {
        var key = (retainerKey, slot, itemId, isHq);
        scans.Remove(key);
        decisions.Remove(key);
    }
}
