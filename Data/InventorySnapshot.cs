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

    internal static DateTime OldestRefresh(IEnumerable<uint> containers, IReadOnlyDictionary<uint, DateTime>? refreshedUtc,
        DateTime legacyUtc)
        => refreshedUtc == null ? legacyUtc : containers.Select(id => refreshedUtc.GetValueOrDefault(id))
            .DefaultIfEmpty().Min();
}
