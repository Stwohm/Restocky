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
    internal int Reserved { get; init; }
}

/// <summary>A low-stock watch entry (persisted in Configuration).</summary>
[Serializable]
public sealed class LowStockEntry
{
    public uint ItemId { get; set; }
    public bool IsHq { get; set; }
    public Guid CategoryId { get; set; }
    public Dictionary<ulong, string> PreferredRetainers { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = string.Empty;

    internal bool IsLow(int listed) => Enabled && listed <= Threshold;
    internal bool AllowsAutoRestock => Enabled && EffectiveRestockMode == StockRestockMode.Auto;

    public StockKind Kind { get; set; }
    public StockStatus Status { get; set; }
    public StockRestockMode RestockMode { get; set; }

    // Tags do not change restocking: only an explicit Alert keeps an item out (user decision 2026-11-25).
    internal StockRestockMode EffectiveRestockMode => RestockMode == StockRestockMode.Alert
        ? StockRestockMode.Alert : StockRestockMode.Auto;

    internal bool MatchesTags(StockKind kind, StockStatus status)
        => (kind == StockKind.None || Kind == kind) && (status == StockStatus.None || Status == status);

    /// <summary>Notify when listed units are at or below this.</summary>
    public int Threshold { get; set; } = 1;

    /// <summary>Default "restock to" value in the UI.</summary>
    public int Target { get; set; } = 3;

    /// <summary>Units per new listing; 0 = same as the existing listings (fallback 1).</summary>
    public int PerListing { get; set; }

    /// <summary>Price for new listings; 0 = our current listing price, else market lowest - undercut.</summary>
    public long Price { get; set; }
}

public enum StockKind { None, Flip, Gather, Craft }
public enum StockStatus { None, Regular, Trial }
public enum StockRestockMode { Default, Auto, Alert }

/// <summary>
/// Fork: tops up the units listed for one item to a target, pulling from retainer inventories
/// first (listed on that same retainer), then the character bag, then the saddlebag (listed on the
/// retainer with the most free market slots). Pure logic, tested.
/// </summary>
public static class RestockPlanner
{
    internal static (List<PlannedAction> Actions, List<LowStockEntry> Skipped) PlanBatch(
        IEnumerable<LowStockEntry> entries, IReadOnlyDictionary<string, int> freeSlots,
        Func<LowStockEntry, IReadOnlyDictionary<string, int>, List<PlannedAction>> planRow)
    {
        var free = new Dictionary<string, int>(freeSlots);
        var actions = new List<PlannedAction>();
        var skipped = new List<LowStockEntry>();
        // Each quality has its own source pool. Plan it once, including its top-up reservations.
        foreach (var entry in entries.Where(e => e.Enabled).DistinctBy(e => (e.ItemId, e.IsHq)))
        {
            var row = planRow(entry, free);
            if (row.Count == 0) skipped.Add(entry);
            foreach (var action in row.Where(a => a.Kind == PlannedActionKind.NewListing))
                free[action.RetainerKey]--;
            actions.AddRange(row);
        }
        return (actions, skipped);
    }

    public static List<PlannedAction> Plan(
        uint itemId,
        bool isHq,
        int targetUnits,
        int listedUnits,
        int perListing,
        long unitPrice,
        IReadOnlyList<RestockSource> sources,
        IReadOnlyDictionary<string, int> freeSlotsByRetainer,
        IReadOnlyList<string>? preferredRetainers = null,
        string? sellOn = null,
        IReadOnlyCollection<RetainerSnapshot>? retainers = null, SlotPlans? plans = null,
        IReadOnlyList<PlannedAction>? earlier = null, bool fillOnly = false, string? fillRetainer = null)
    {
        var preferred = preferredRetainers ?? Array.Empty<string>();
        var result = new List<PlannedAction>();
        var need = fillOnly ? int.MaxValue : targetUnits - listedUnits;
        if (need <= 0 || unitPrice <= 0) return result;
        perListing = Math.Max(1, perListing);
        var free = freeSlotsByRetainer.ToDictionary(p => p.Key, p => p.Value);
        var preferredKey = sellOn == null ? null : retainers?.FirstOrDefault(r => r.RetainerName == sellOn)?.Key;
        var firstChoice = sellOn == null ? preferred.FirstOrDefault() : preferredKey;
        var preferredSnapshot = retainers?.FirstOrDefault(r => r.Key == firstChoice);
        var fullSnapshot = preferredSnapshot?.Listings.Count >= Planner.MaxListingSlots ? preferredSnapshot : null;
        var remaining = sources.Select(s => s.Available).ToArray();

        // Preferred retainers (the ones that already sell this item) list first:
        //   0 their own inventory, 1 bag -> them, 2 saddlebag -> them,
        //   3 other retainers' own inventory, 4 bag, 5 saddlebag -> retainer with most free slots.
        int Rank(RestockSource s)
        {
            if (s.FixedTargetKey != null) return preferred.Contains(s.FixedTargetKey) ? 0 : 3;
            var saddle = s.SourceKey.EndsWith(".saddle") ? 1 : 0;
            return (preferred.Any(HasRoom) ? 1 : 4) + saddle;
        }

        bool HasRoom(string key) => free.TryGetValue(key, out var n) && n > 0;

        void AddListings(int index, string? forcedTarget)
        {
            var src = sources[index];
            while (need > 0 && remaining[index] > 0)
            {
                var target = forcedTarget ?? src.FixedTargetKey
                    ?? preferred.FirstOrDefault(HasRoom)
                    ?? free.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
                if (target == null || !HasRoom(target)) break;

                var qty = Math.Min(Math.Min(perListing, src.MaxPerListing), Math.Min(need, remaining[index]));
                result.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.NewListing,
                    RetainerKey = target,
                    SourceKey = src.SourceKey,
                    ItemId = itemId,
                    IsHQ = isHq,
                    Quantity = qty,
                    UnitPrice = unitPrice,
                    PreferredRetainerFallback = target == firstChoice ? null : sellOn ?? fullSnapshot?.RetainerName,
                    PreferredRetainerFullAtUtc = target != fullSnapshot?.Key ? fullSnapshot?.LastRefreshedUtc : null,
                    StockRestock = true,
                });
                free[target]--;
                need -= qty;
                remaining[index] -= qty;
            }
        }

        var indices = Enumerable.Range(0, sources.Count);
        if (retainers?.FirstOrDefault() is { } owner && plans != null && plans.ForCharacter(owner.CharacterContentId).Any())
        {
            var overflow = new Dictionary<string, int>();
            var assignment = plans.ForCharacter(owner.CharacterContentId).FirstOrDefault(r => r.Matches(itemId, isHq));
            if (assignment != null)
            {
                preferred = [];
                preferredKey = null;
                sellOn = null;
                firstChoice = null;
                fullSnapshot = null;
            }
            foreach (var seller in retainers)
            {
                var layout = plans.Layout(seller, earlier);
                var readable = seller.LastRefreshedUtc != DateTime.MinValue;
                var capacity = free.GetValueOrDefault(seller.Key);
                overflow[seller.Key] = readable ? Math.Min(capacity, layout.OverflowFor(itemId, isHq)) : 0;
                var row = plans.Rows(seller.Key).FirstOrDefault(r => r.Matches(itemId, isHq));
                if (row == null || fillRetainer != null && seller.Key != fillRetainer) continue;
                free[seller.Key] = readable ? Math.Min(capacity, layout.ReservedFor(itemId, isHq)) : 0;
                perListing = row.PerListing;
                var crystal = InventoryEntry.IsCrystal(itemId);
                int SourceOrder(int index)
                {
                    var source = sources[index];
                    if (crystal && source.SourceKey.EndsWith(".bag")) return 0;
                    if (source.FixedTargetKey == seller.Key) return 1;
                    if (source.SourceKey.EndsWith(".bag")) return 2;
                    return 3;
                }
                foreach (var i in indices.Where(i => sources[i].FixedTargetKey == seller.Key
                    || sources[i].FixedTargetKey == null || crystal).OrderBy(SourceOrder))
                    AddListings(i, seller.Key);
            }
            free = overflow;
            if (assignment != null) perListing = assignment.PerListing;
        }
        if (fillOnly) return result;
        if (InventoryEntry.IsCrystal(itemId))
        {
            perListing = Math.Min(perListing, InventoryEntry.CrystalCapacity);
            var targets = new[] { preferredKey }.OfType<string>().Concat(preferred)
                .Concat(free.Keys.OrderByDescending(k => sources.Any(s => s.FixedTargetKey == k && s.Available > 0))
                    .ThenByDescending(k => free[k])).Distinct().Where(HasRoom).ToArray();

            foreach (var target in targets)
            {
                foreach (var i in indices.Where(i => sources[i].SourceKey.EndsWith(".bag")))
                    AddListings(i, target);
                foreach (var i in indices.Where(i => sources[i].FixedTargetKey == target))
                    AddListings(i, target);
                foreach (var i in indices.Where(i => sources[i].FixedTargetKey != null && sources[i].FixedTargetKey != target))
                    AddListings(i, target);
            }
            return result;
        }
        if (preferredKey != null && HasRoom(preferredKey))
        {
            foreach (var i in indices.Where(i => sources[i].FixedTargetKey == null || sources[i].FixedTargetKey == preferredKey)
                .OrderBy(i => sources[i].FixedTargetKey != null ? 0 : sources[i].SourceKey.EndsWith(".saddle") ? 2 : 1))
                AddListings(i, preferredKey);
        }

        foreach (var i in indices.OrderBy(i => Rank(sources[i])))
        {
            AddListings(i, null);
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
        var needs = actions.Where(a => a.TopUp != null || (a.Kind == PlannedActionKind.NewListing && a.SourceKey.StartsWith("char.")))
            .GroupBy(a => (a.ItemId, a.IsHQ))
            .Where(g => g.Any(a => a.TopUp is { SaddleUnits: > 0 } || a.SourceKey.EndsWith(".saddle")))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.TopUp is { } topUp ? topUp.BagUnits + topUp.SaddleUnits : a.Quantity));
        foreach (var action in laterActions.Where(a => a.TopUp != null || (a.Kind == PlannedActionKind.NewListing && a.SourceKey.EndsWith(".bag"))))
        {
            var key = (action.ItemId, action.IsHQ);
            if (needs.TryGetValue(key, out var need)) needs[key] = need + (action.TopUp?.BagUnits ?? action.Quantity);
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
