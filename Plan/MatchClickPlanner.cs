using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

internal static class MatchClickPlanner
{
    internal static (bool FoundListing, List<PlannedAction> Actions) Plan(IEnumerable<RetainerSnapshot> retainers,
        uint item, bool hq, long proposed, bool floorReviewed, bool held, long floor, double maxRaise)
    {
        var actions = new List<PlannedAction>();
        var slots = new HashSet<(string, int)>();
        foreach (var retainer in retainers)
        foreach (var listing in retainer.Listings.Where(l => l.ItemId == item && l.IsHQ == hq))
        {
            if (!slots.Add((retainer.Key, listing.ListingIndex)) || held || listing.UnitPrice == proposed
                || !AllowsPrice(listing.UnitPrice, proposed, floor, floorReviewed, maxRaise)) continue;

            actions.Add(new PlannedAction
            {
                Kind = PlannedActionKind.Reprice, RetainerKey = retainer.Key, ListingIndex = listing.ListingIndex,
                ItemId = item, IsHQ = hq, Quantity = listing.Quantity, OldPrice = listing.UnitPrice,
                UnitPrice = proposed, MatchedPrice = true, ClickReviewedFloor = floorReviewed, Reason = RepriceReason.Manual,
            });
        }
        return (slots.Count > 0, actions);
    }

    internal static bool AllowsPrice(long current, long proposed, long floor, bool reviewedFloor, double maxRaise)
        => RestockPricing.IsValidPrice(proposed, floor) && (proposed != floor || reviewedFloor
            || RepriceRules.LimitExceeded(current, proposed, 0, maxRaise) != RepriceReason.MaxRaise);
}
