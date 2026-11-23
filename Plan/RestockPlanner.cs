using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Plan;

/// <summary>Where units for a restock can come from.</summary>
/// <param name="SourceKey">Retainer snapshot key, or "char.{cid:X}.bag" / "char.{cid:X}.saddle".</param>
/// <param name="FixedTargetKey">For a retainer's own inventory: that retainer (items cannot cross retainers). Null = any retainer.</param>
/// <param name="Available">Units of the item in that source.</param>
public sealed record RestockSource(string SourceKey, string? FixedTargetKey, int Available)
{
    internal int MaxPerListing { get; init; } = int.MaxValue;
}

/// <summary>A low-stock watch entry (persisted in Configuration).</summary>
[Serializable]
public sealed class LowStockEntry
{
    public uint ItemId { get; set; }
    public bool IsHq { get; set; }

    /// <summary>Notify when listed units are at or below this.</summary>
    public int Threshold { get; set; } = 1;

    /// <summary>Default "restock to" value in the UI.</summary>
    public int Target { get; set; } = 3;

    /// <summary>Units per new listing; 0 = same as the existing listings (fallback 1).</summary>
    public int PerListing { get; set; }

    /// <summary>Price for new listings; 0 = our current listing price, else market lowest - undercut.</summary>
    public long Price { get; set; }
}

/// <summary>
/// Fork: tops up the units listed for one item to a target, pulling from retainer inventories
/// first (listed on that same retainer), then the character bag, then the saddlebag (listed on the
/// retainer with the most free market slots). Pure logic, tested.
/// </summary>
public static class RestockPlanner
{
    public static List<PlannedAction> Plan(
        uint itemId,
        bool isHq,
        int targetUnits,
        int listedUnits,
        int perListing,
        long unitPrice,
        IReadOnlyList<RestockSource> sources,
        IReadOnlyDictionary<string, int> freeSlotsByRetainer,
        IReadOnlyList<string>? preferredRetainers = null)
    {
        var preferred = preferredRetainers ?? Array.Empty<string>();
        var result = new List<PlannedAction>();
        var need = targetUnits - listedUnits;
        if (need <= 0 || unitPrice <= 0) return result;
        perListing = Math.Max(1, perListing);
        var free = freeSlotsByRetainer.ToDictionary(p => p.Key, p => p.Value);

        // Preferred retainers (the ones that already sell this item) list first:
        //   0 their own inventory, 1 bag -> them, 2 saddlebag -> them,
        //   3 other retainers' own inventory, 4 bag, 5 saddlebag -> retainer with most free slots.
        var preferredHasRoom = preferred.Any(p => free.TryGetValue(p, out var n) && n > 0);
        int Rank(RestockSource s)
        {
            if (s.FixedTargetKey != null) return preferred.Contains(s.FixedTargetKey) ? 0 : 3;
            var saddle = s.SourceKey.EndsWith(".saddle") ? 1 : 0;
            return (preferredHasRoom ? 1 : 4) + saddle;
        }
        var ordered = sources.OrderBy(Rank).ToList();

        foreach (var src in ordered)
        {
            var avail = src.Available;
            while (need > 0 && avail > 0)
            {
                var target = src.FixedTargetKey
                    ?? preferred.FirstOrDefault(p => free.TryGetValue(p, out var n) && n > 0)
                    ?? free.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
                if (target == null || !free.TryGetValue(target, out var slots) || slots <= 0) break;

                var qty = Math.Min(Math.Min(perListing, src.MaxPerListing), Math.Min(need, avail));
                result.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.NewListing,
                    RetainerKey = target,
                    SourceKey = src.SourceKey,
                    ItemId = itemId,
                    IsHQ = isHq,
                    Quantity = qty,
                    UnitPrice = unitPrice,
                });
                free[target] = slots - 1;
                need -= qty;
                avail -= qty;
            }
            if (need <= 0) break;
        }
        return result;
    }

    internal static int PerListing(LowStockEntry entry, IEnumerable<int> listedQuantities)
    {
        if (entry.PerListing > 0) return entry.PerListing;
        var qty = listedQuantities.GroupBy(q => q).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return qty > 0 ? qty : 1;
    }

    internal static Dictionary<(uint ItemId, bool IsHq), int> SaddleStagingNeeds(
        IEnumerable<PlannedAction> actions, IEnumerable<PlannedAction>? laterActions = null)
    {
        // Manual plans historically stage only the quantity assigned to the saddlebag.
        if (laterActions == null)
            return actions.Where(a => a.Kind == PlannedActionKind.NewListing && a.SourceKey.EndsWith(".saddle"))
                .GroupBy(a => (a.ItemId, a.IsHQ)).ToDictionary(g => g.Key, g => g.Sum(a => a.Quantity));

        // When an item uses the saddlebag, reserve enough in the bag for both sources.
        var needs = actions.Where(a => a.Kind == PlannedActionKind.NewListing && a.SourceKey.StartsWith("char."))
            .GroupBy(a => (a.ItemId, a.IsHQ))
            .Where(g => g.Any(a => a.SourceKey.EndsWith(".saddle")))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Quantity));
        foreach (var action in laterActions.Where(a => a.Kind == PlannedActionKind.NewListing && a.SourceKey.EndsWith(".bag")))
        {
            var key = (action.ItemId, action.IsHQ);
            if (needs.TryGetValue(key, out var need)) needs[key] = need + action.Quantity;
        }
        return needs;
    }

    /// <summary>Transitions for the alert: true exactly once when listed units drop to/below the threshold.</summary>
    public static bool ShouldNotify(int listedUnits, int threshold, ref bool alreadyNotified)
    {
        if (listedUnits > threshold)
        {
            alreadyNotified = false;
            return false;
        }
        if (alreadyNotified) return false;
        alreadyNotified = true;
        return true;
    }
}
