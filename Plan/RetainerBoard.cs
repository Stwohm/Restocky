using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

internal readonly record struct BoardListingHealth(long? UndercuttingPrice, int? DaysListed, bool Stuck, bool Stale)
{
    internal static BoardListingHealth Read(ListingEntry? listing, ListingRecord? record, ListingMarketScan? scan,
        bool staleSnapshot, DateTime now, int threshold)
    {
        long? cheaper = listing != null && scan is { Offers.Count: > 0 } s && s.Offers[0].Price < listing.UnitPrice
            ? s.Offers[0].Price : null;
        return new(cheaper, record == null ? null : Math.Max(0, (now - record.FirstSeenUtc).Days),
            record != null && ListingTracker.IsStuck(record, now, threshold), staleSnapshot || record?.MissingSinceUtc != null);
    }
}

internal sealed record SlotAssignment(LowStockEntry Item, long Needed, int Used);
internal sealed record RetainerSlotSummary(List<SlotAssignment> Items, int Used)
{
    internal long Assigned => Items.Sum(i => i.Needed);
    internal int Free => Planner.MaxListingSlots - Used;
    internal bool OverAssigned => Assigned > Planner.MaxListingSlots;
}

internal static class RetainerBoard
{
    internal static RetainerSlotSummary Summarize(RetainerSnapshot seller, IEnumerable<LowStockEntry> entries,
        Func<LowStockEntry, string?> firstPreferredKey, Func<LowStockEntry, int> perListing)
    {
        var assignments = new List<SlotAssignment>();
        foreach (var item in entries.DistinctBy(e => (e.ItemId, e.IsHq)))
        {
            var preferred = item.PreferredRetainers.GetValueOrDefault(seller.CharacterContentId);
            if (preferred != null ? preferred != seller.RetainerName : firstPreferredKey(item) != seller.Key) continue;
            var size = Math.Max(1, perListing(item));
            var needed = (Math.Max(0L, item.Target) + size - 1) / size;
            var used = seller.Listings.Count(l => l.ItemId == item.ItemId && l.IsHQ == item.IsHq);
            assignments.Add(new(item, needed, used));
        }
        return new(assignments, seller.Listings.Count);
    }
}
