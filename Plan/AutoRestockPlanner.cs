using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

internal enum AutoRestockPriceKind { Listing, Market, LastSale, UnknownListingPrice, NoMarketOrSale, OutOfRange }

internal sealed record AutoRestockPlan(uint ItemId, bool IsHq, AutoRestockPriceKind PriceKind, List<PlannedAction> Actions);

/// <summary>Plans one pass from the current character's post-reprice snapshots.</summary>
internal static class AutoRestockPlanner
{
    internal static List<AutoRestockPlan> Plan(
        IEnumerable<LowStockEntry> entries, IReadOnlyCollection<RetainerSnapshot> retainers, CharacterSnapshot? character,
        ListingTracker tracker, MarketCache? marketCache, DateTime nowUtc, int maxScanAgeHours, int undercutGil,
        Func<uint, bool, bool> isHeld, Func<uint, bool, long> floorFor,
        IReadOnlyDictionary<string, RetainerSnapshot>? knownRetainers = null)
    {
        var plans = new List<AutoRestockPlan>();
        var keys = retainers.Select(r => r.Key).ToHashSet();
        var owner = retainers.FirstOrDefault();
        if (owner == null) return plans;
        var characterPrefix = $"{owner.CharacterContentId:X}.";
        var free = retainers.ToDictionary(r => r.Key, r => Math.Max(0, Planner.MaxListingSlots - r.Listings.Count));
        var listings = retainers.SelectMany(r => r.Listings).ToLookup(l => (l.ItemId, l.IsHQ));
        foreach (var entry in entries.DistinctBy(e => (e.ItemId, e.IsHq)))
        {
            var itemId = entry.ItemId;
            var hq = entry.IsHq;
            if (isHeld(itemId, hq)) continue;

            var existing = listings[(itemId, hq)];
            var listedUnits = existing.Sum(l => l.Quantity);
            if (listedUnits > entry.Threshold || listedUnits >= entry.Target) continue;

            var floor = floorFor(itemId, hq);
            var scan = marketCache?.GetScan(itemId);
            var (price, kind) = ResolvePrice(existing, scan?.Scan.WorldId == owner.WorldId ? scan : null,
                nowUtc, maxScanAgeHours, undercutGil, hq, floor, () => tracker.GetSales(itemId, hq).FirstOrDefault(s =>
                    keys.Contains(s.RetainerKey) || s.RetainerKey.StartsWith(characterPrefix, StringComparison.Ordinal)
                    || (knownRetainers != null && knownRetainers.TryGetValue(s.RetainerKey, out var seller)
                        && seller.WorldId != 0 && seller.WorldId == owner.WorldId)));
            if (price == 0)
            {
                plans.Add(new AutoRestockPlan(itemId, hq, kind, new()));
                continue;
            }

            var sources = new List<RestockSource>();
            foreach (var retainer in retainers)
                AddSources(sources, entry, retainer.Inventory, retainer.Key, retainer.Key);
            if (character != null)
            {
                var prefix = CharacterSnapshot.MakeKey(character.CharacterContentId);
                AddSources(sources, entry, character.Bag, prefix + ".bag", null);
                AddSources(sources, entry, character.Saddlebag.Concat(character.PremiumSaddlebag), prefix + ".saddle", null);
            }
            var actions = RestockPlanner.Plan(itemId, hq, entry.Target, listedUnits,
                RestockPlanner.PerListing(entry, existing.Select(l => l.Quantity)), price, sources, free,
                tracker.PreferredRetainers(itemId, hq, keys));
            if (actions.Count == 0) continue;
            foreach (var action in actions) free[action.RetainerKey]--;
            plans.Add(new AutoRestockPlan(itemId, hq, kind, actions));
        }
        return plans;
    }

    private static void AddSources(List<RestockSource> sources, LowStockEntry entry, IEnumerable<InventoryEntry> inventory,
        string sourceKey, string? fixedTarget)
    {
        foreach (var stack in inventory)
            if (stack.IsListable && stack.ItemId == entry.ItemId && stack.IsHQ == entry.IsHq && stack.Quantity > 0)
                sources.Add(new RestockSource(sourceKey, fixedTarget, stack.Quantity)
                {
                    MaxPerListing = Math.Max(1, stack.MaxStackPerListing),
                });
    }

    private static (long Price, AutoRestockPriceKind Kind) ResolvePrice(IEnumerable<ListingEntry> existing,
        (MarketScan Scan, DateTime UpdatedUtc)? scan, DateTime nowUtc, int maxScanAgeHours, int undercutGil,
        bool hq, long floor, Func<ListingRecord?> lastSale)
    {
        if (existing.Any())
        {
            var ours = existing.Where(l => l.UnitPrice > 0).Select(l => l.UnitPrice).DefaultIfEmpty().Min();
            return ours > 0 ? WithFloor(ours, floor, AutoRestockPriceKind.Listing) : (0, AutoRestockPriceKind.UnknownListingPrice);
        }

        if (scan is { } market && market.UpdatedUtc <= nowUtc && (nowUtc - market.UpdatedUtc).TotalHours < maxScanAgeHours)
        {
            var lowest = LowestPriceResolver.Pick(market.Scan.Competitors(hq, matchQuality: true).Select(o => o.Price).ToArray());
            if (lowest > 0)
                return WithFloor(Math.Max(1, lowest - Math.Max(0, undercutGil)), floor, AutoRestockPriceKind.Market);
        }

        var sale = lastSale();
        var salePrice = sale == null ? 0 : SaleUnitPrice(sale);
        return salePrice > 0 ? WithFloor(salePrice, floor, AutoRestockPriceKind.LastSale) : (0, AutoRestockPriceKind.NoMarketOrSale);
    }

    private static long SaleUnitPrice(ListingRecord sale)
    {
        if (sale.Quantity <= 0) return 0;
        if (sale.SoldGrossGil is > 0) return sale.SoldGrossGil.Value / sale.Quantity;
        if (sale.LastPrice is > 0 and <= uint.MaxValue && (sale.SoldTotalGil <= 0
            || ListingTracker.NetFromGross(sale.LastPrice * sale.Quantity) == sale.SoldTotalGil))
            return sale.LastPrice;
        if (sale.SoldTotalGil <= 0) return 0;

        // History-only records have net proceeds. Use the smallest gross unit price
        // consistent with those proceeds at the tracker's sale tax rate.
        var unitPrice = decimal.Ceiling(sale.SoldTotalGil / (ListingTracker.SaleNetRate * sale.Quantity));
        if (unitPrice > uint.MaxValue) return (long)uint.MaxValue + 1;
        var price = (long)unitPrice;
        return ListingTracker.NetFromGross(price * sale.Quantity) == sale.SoldTotalGil ? price : 0;
    }

    internal static bool IsValidPrice(long price, long floor) => price is > 0 and <= uint.MaxValue && price >= floor;

    private static (long, AutoRestockPriceKind) WithFloor(long price, long floor, AutoRestockPriceKind kind)
    {
        var target = Math.Max(price, floor);
        return IsValidPrice(target, floor) ? (target, kind) : (0, AutoRestockPriceKind.OutOfRange);
    }
}
