using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Data;

internal static class InventorySnapshot
{
    // A loaded empty container replaces its old contents. An absent read leaves them alone.
    internal static List<InventoryEntry> Refresh(IReadOnlyList<InventoryEntry> previous,
        Dictionary<uint, DateTime> refreshedUtc,
        IReadOnlyDictionary<uint, List<InventoryEntry>> loaded, DateTime nowUtc)
    {
        var result = new List<InventoryEntry>();
        foreach (var entry in previous)
        {
            if (loaded.ContainsKey(entry.ContainerId)) continue;
            result.Add(entry);
        }
        foreach (var (container, entries) in loaded)
        {
            result.AddRange(entries);
            refreshedUtc[container] = nowUtc;
        }
        return result;
    }

    internal static (long Listed, long Available) HeldUnits(uint itemId, bool hq,
        IEnumerable<RetainerSnapshot> retainers, IEnumerable<CharacterSnapshot> characters,
        string? liveRetainer, long liveListed)
    {
        long Units(IEnumerable<InventoryEntry> entries) =>
            entries.Where(i => i.ItemId == itemId && i.IsHQ == hq).Sum(i => (long)i.Quantity);

        long listed = liveListed;
        long available = 0;
        foreach (var snapshot in retainers)
        {
            if (snapshot.Key != liveRetainer)
                listed += snapshot.Listings.Where(l => l.ItemId == itemId && l.IsHQ == hq).Sum(l => (long)l.Quantity);
            available += Units(snapshot.Inventory);
        }
        foreach (var character in characters)
            available += Units(character.Bag) + Units(character.Saddlebag) + Units(character.PremiumSaddlebag);
        return (listed, available);
    }

    internal static DateTime OldestRefresh(IEnumerable<uint> containers, IReadOnlyDictionary<uint, DateTime>? refreshedUtc,
        DateTime legacyUtc)
        => refreshedUtc == null ? legacyUtc : containers.Select(id => refreshedUtc.GetValueOrDefault(id))
            .DefaultIfEmpty().Min();
}
