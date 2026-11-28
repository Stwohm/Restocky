using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

internal enum RestockSkipReason { None, NoStock, NoSlot, PreferredRetainerFull, SourceRetainerFull, Unavailable, SourceUnavailable, WriteFailed, TimedOut, PriceUnavailable, Unconfirmed, CrystalWithdrawalPending, CrystalWithdrawalFailed }

internal sealed record AutoRestockPlan(uint ItemId, bool IsHq, AutoRestockPriceKind PriceKind, List<PlannedAction> Actions,
    RestockSkipReason SkipReason = RestockSkipReason.None, string? RetainerName = null);

/// <summary>Plans one pass from the current character's post-reprice snapshots.</summary>
internal static class AutoRestockPlanner
{
    internal static List<AutoRestockPlan> Plan(
        IEnumerable<LowStockEntry> entries, IReadOnlyCollection<RetainerSnapshot> retainers, CharacterSnapshot? character,
        ListingTracker tracker, Func<uint, bool, bool> isHeld, Func<uint, bool, long> floorFor,
        Func<uint, bool, int>? keepFor = null, SlotPlans? slotPlans = null)
    {
        var plans = new List<AutoRestockPlan>();
        var earlier = new List<PlannedAction>();
        var keys = retainers.Select(r => r.Key).ToHashSet();
        var owner = retainers.FirstOrDefault();
        if (owner == null) return plans;
        var free = retainers.ToDictionary(r => r.Key, r => Math.Max(0, Planner.MaxListingSlots - r.Listings.Count));
        var listings = retainers.SelectMany(r => r.Listings).ToLookup(l => (l.ItemId, l.IsHQ));
        foreach (var entry in entries.DistinctBy(e => (e.ItemId, e.IsHq)))
        {
            var itemId = entry.ItemId;
            var hq = entry.IsHq;
            if (!entry.AllowsAutoRestock || isHeld(itemId, hq)) continue;

            var existing = listings[(itemId, hq)];
            var listedUnits = existing.Sum(l => l.Quantity);
            var plannedTarget = slotPlans?.Target(owner.CharacterContentId, itemId, hq) ?? 0;
            var target = plannedTarget > 0 ? plannedTarget : entry.Target;
            if (listedUnits > entry.Threshold || listedUnits >= target) continue;

            var sellOn = plannedTarget > 0 ? null : entry.PreferredRetainers.GetValueOrDefault(owner.CharacterContentId);
            var preferences = plannedTarget > 0 ? [] : tracker.PreferredRetainers(itemId, hq, keys);
            var preferred = retainers.FirstOrDefault(r => sellOn != null ? r.RetainerName == sellOn : r.Key == preferences.FirstOrDefault());
            var floor = floorFor(itemId, hq);
            var (price, kind, _) = existing.Any() ? RestockPricing.FromListings(existing, floor)
                : floor > uint.MaxValue ? new RestockPrice(0, AutoRestockPriceKind.OutOfRange)
                : new RestockPrice(1, AutoRestockPriceKind.Pending); // layout only, priced at execution
            if (price == 0)
            {
                plans.Add(new AutoRestockPlan(itemId, hq, kind, new(), RetainerName: preferred?.RetainerName));
                continue;
            }

            var sources = Sources(itemId, hq, retainers, character, keepFor?.Invoke(itemId, hq) ?? 0);
            var actions = RestockPlanner.Plan(itemId, hq, target, listedUnits,
                RestockPlanner.PerListing(entry, existing.Select(l => l.Quantity)), price, sources, free,
                preferences, sellOn, retainers, slotPlans, earlier);
            if (actions.Count == 0)
            {
                var reason = sources.Count == 0 ? RestockSkipReason.NoStock
                    : sellOn != null && preferred != null && free[preferred.Key] == 0 ? RestockSkipReason.PreferredRetainerFull
                    : free.Values.All(n => n == 0) ? RestockSkipReason.NoSlot : RestockSkipReason.SourceRetainerFull;
                var sourceKeys = sources.Select(s => s.FixedTargetKey).Distinct().Take(2).ToList();
                var retainer = preferred ?? (sourceKeys.Count == 1 ? retainers.FirstOrDefault(r => r.Key == sourceKeys[0]) : null);
                plans.Add(new AutoRestockPlan(itemId, hq, kind, actions, reason, retainer?.RetainerName));
                continue;
            }
            if (kind == AutoRestockPriceKind.Pending)
                foreach (var action in actions)
                {
                    action.ResolveRestockPrice = true;
                    action.UnitPrice = 0;
                }
            earlier.AddRange(actions);
            foreach (var action in actions) free[action.RetainerKey]--;
            plans.Add(new AutoRestockPlan(itemId, hq, kind, actions));
        }
        return plans;
    }

    internal static bool CompleteVisitRoster(RetainerSnapshot? active, IReadOnlyCollection<RetainerSnapshot> roster,
        int expectedCount) => active != null && roster.Count == expectedCount && roster.Contains(active)
        && roster.All(r => r.CharacterContentId == active.CharacterContentId && r.LastRefreshedUtc != DateTime.MinValue);

    internal static List<AutoRestockPlan> ForVisit(IEnumerable<AutoRestockPlan> plans,
        RetainerSnapshot active, RetainerSnapshot first)
    {
        var result = new List<AutoRestockPlan>();
        foreach (var plan in plans)
        {
            if (plan.Actions.Count == 0)
            {
                // Assign character-wide skips to one stable visit.
                if ((plan.RetainerName ?? first.RetainerName) == active.RetainerName) result.Add(plan);
                continue;
            }
            var actions = plan.Actions.FindAll(a => a.RetainerKey == active.Key);
            if (actions.RemoveAll(a => a.NeedsCrystalWithdrawal) > 0)
                result.Add(plan with { Actions = [], SkipReason = RestockSkipReason.CrystalWithdrawalPending, RetainerName = active.RetainerName });
            if (actions.Count > 0) result.Add(plan with { Actions = actions });
        }
        return result;
    }

    internal static List<RestockSource> Sources(uint itemId, bool hq, IEnumerable<RetainerSnapshot> retainers,
        CharacterSnapshot? character, int keep = 0)
    {
        var sources = new List<RestockSource>();
        // Keep can remove the entire bag stack from the sellable sources. Its protected
        // units still occupy physical space during a cross-retainer withdrawal.
        var protectedCrystals = InventoryEntry.IsCrystal(itemId) && character != null
            ? Math.Min(keep, character.Bag.Where(i => i.ItemId == itemId && i.IsHQ == hq).Sum(i => i.Quantity)) : 0;
        foreach (var retainer in retainers)
            Add(retainer.Inventory, retainer.Key, retainer.Key);
        if (character != null)
        {
            var prefix = CharacterSnapshot.MakeKey(character.CharacterContentId);
            Add(character.Bag, prefix + ".bag", null);
            Add(character.Saddlebag.Concat(character.PremiumSaddlebag), prefix + ".saddle", null);
        }
        InventoryReserve.Apply(sources, keep);
        return sources;

        void Add(IEnumerable<InventoryEntry> inventory, string sourceKey, string? fixedTarget)
        {
            foreach (var stack in inventory)
                if (stack.IsListable && stack.ItemId == itemId && stack.IsHQ == hq && stack.Quantity > 0)
                    sources.Add(new RestockSource(sourceKey, fixedTarget, stack.Quantity)
                    {
                        MaxPerListing = Math.Max(1, stack.MaxStackPerListing),
                        CrystalWithdrawalRoom = InventoryEntry.CrystalCapacity - protectedCrystals,
                    });
        }
    }
}
