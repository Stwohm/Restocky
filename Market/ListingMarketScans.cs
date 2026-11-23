using System;
using System.Collections.Generic;

namespace Restocker.Market;

internal readonly record struct ListingMarketScan(DateTime ScannedUtc, long UnitPrice, IReadOnlyList<MarketOffer> Offers)
{
    // MarketScan sorts offers by unit price.
    public bool WasUndercut => Offers.Count > 0 && Offers[0].Price < UnitPrice;
}

internal sealed class ListingMarketScans
{
    private readonly Dictionary<(string RetainerKey, int Slot, uint ItemId, bool IsHq), ListingMarketScan> scans = new();

    public void Record(string retainerKey, int slot, uint itemId, bool isHq, ListingMarketScan scan)
    {
        var key = (retainerKey, slot, itemId, isHq);
        if (!scans.TryGetValue(key, out var previous) || scan.ScannedUtc >= previous.ScannedUtc)
            scans[key] = scan;
    }

    public ListingMarketScan? Get(string retainerKey, int slot, uint itemId, bool isHq)
        => scans.TryGetValue((retainerKey, slot, itemId, isHq), out var scan) ? scan : null;
}
