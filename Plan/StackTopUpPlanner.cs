using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Plan;

internal sealed record StackTopUp(int OriginalQuantity, int InventoryStackSize, int BagUnits, int SaddleUnits, int RetainerUnits)
{
    internal int AddedUnits => BagUnits + SaddleUnits + RetainerUnits;
}

internal static class StackTopUpPlanner
{
    internal static List<PlannedAction> Plan(uint itemId, bool hq, int maxPerListing, int inventoryStackSize,
        IEnumerable<(string RetainerKey, ListingEntry Listing)> listings, List<RestockSource> remaining, int perListing = 0, int additionalUnits = int.MaxValue)
    {
        var limit = perListing > 0 ? Math.Min(perListing, maxPerListing) : maxPerListing;
        var actions = new List<PlannedAction>();
        foreach (var (retainerKey, listing) in listings
            .Where(x => x.Listing.ItemId == itemId && x.Listing.IsHQ == hq
                && x.Listing.Quantity > 0 && x.Listing.Quantity < limit
                && x.Listing.UnitPrice is > 0 and <= uint.MaxValue)
            .OrderByDescending(x => x.Listing.Quantity))
        {
            var need = Math.Min(limit - listing.Quantity, additionalUnits);
            if (need <= 0) break;
            StackTopUp topUp;
            if (InventoryEntry.IsCrystal(itemId))
            {
                // Withdraw to the same crystal balance that supplies the addition.
                var source = remaining.Where(s => s.Available > 0 && s.Available <= inventoryStackSize - listing.Quantity
                    && (s.FixedTargetKey == retainerKey || s.SourceKey.EndsWith(".bag")))
                    .OrderBy(s => s.FixedTargetKey != null).FirstOrDefault();
                if (source == null) continue;
                var units = Take(s => s == source, ref need);
                topUp = new(listing.Quantity, inventoryStackSize, source.FixedTargetKey == null ? units : 0,
                    0, source.FixedTargetKey != null ? units : 0);
            }
            else
            {
                var retainer = Take(s => s.FixedTargetKey == retainerKey, ref need);
                var bag = Take(s => s.FixedTargetKey == null && s.SourceKey.EndsWith(".bag"), ref need);
                var saddle = Take(s => s.FixedTargetKey == null && s.SourceKey.EndsWith(".saddle"), ref need);
                topUp = new(listing.Quantity, inventoryStackSize, bag, saddle, retainer);
            }
            additionalUnits -= topUp.AddedUnits;
            if (topUp.AddedUnits == 0) continue;
            actions.Add(new PlannedAction
            {
                Kind = PlannedActionKind.TopUp,
                RetainerKey = retainerKey,
                ListingIndex = listing.ListingIndex,
                ItemId = itemId,
                IsHQ = hq,
                Quantity = listing.Quantity + topUp.AddedUnits,
                UnitPrice = listing.UnitPrice,
                TopUp = topUp,
            });
        }
        return actions;

        int Take(Func<RestockSource, bool> matches, ref int need)
        {
            var taken = 0;
            for (var i = 0; i < remaining.Count && need > 0; i++)
            {
                var source = remaining[i];
                if (!matches(source) || source.Available <= 0) continue;
                var qty = Math.Min(need, source.Available);
                remaining[i] = source with { Available = source.Available - qty };
                need -= qty;
                taken += qty;
            }
            return taken;
        }
    }
}
