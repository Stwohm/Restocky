using System;
using System.Collections.Generic;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

/// <summary>
/// Turns one retainer's live listings + freshly fetched competitor prices into
/// Reprice actions (to apply) and history rows for everything not applied.
/// Pure logic; the executor supplies live data and writes the rows.
/// </summary>
public static class AutoRepricePlanner
{
    public sealed class Result
    {
        public List<PlannedAction> Actions { get; } = new();

        /// <summary>History rows for listings that will NOT be changed (applied=false).</summary>
        public List<PriceHistoryRow> NotApplied { get; } = new();

        /// <summary>Competitor listings ignored as bait, one notice per (item, hq).</summary>
        public List<BaitNotice> Baits { get; } = new();
        internal Dictionary<(int Slot, uint ItemId, bool IsHq), long> BaitPrices { get; } = new();

        public int HeldCount { get; internal set; }
    }

    /// <param name="competitors">
    /// Competitor prices (ascending, own retainers excluded) fetched in THIS cycle for (item, hq),
    /// or null when no fresh data exists for it.
    /// </param>
    public static Result Plan(
        string retainerKey,
        string retainerName,
        IReadOnlyList<LiveListing> listings,
        Func<uint, bool, IReadOnlyList<MarketOffer>?> competitors,
        Func<uint, bool, long> floorFor,
        RepriceSettings settings,
        DateTime nowUtc,
        Func<uint, bool, bool>? isHeld = null,
        Action<LiveListing, RepriceDecision, IReadOnlyList<MarketOffer>>? onDecision = null,
        Func<uint, bool, bool>? includeBaitForPlan = null,
        Func<uint, bool>? marketTimedOut = null)
    {
        var result = new Result();
        foreach (var l in listings)
        {
            var includeBait = includeBaitForPlan?.Invoke(l.ItemId, l.IsHQ) == true;
            if (isHeld?.Invoke(l.ItemId, l.IsHQ) == true)
            {
                result.HeldCount++;
                result.NotApplied.Add(new PriceHistoryRow
                {
                    UtcTime = nowUtc,
                    Retainer = retainerName,
                    Slot = l.Slot,
                    ItemId = l.ItemId,
                    Hq = l.IsHQ,
                    Qty = l.Quantity,
                    OldPrice = l.UnitPrice,
                    NewPrice = l.UnitPrice,
                    Applied = false,
                    Reason = RepriceReason.Held,
                });
                continue;
            }
            var offers = competitors(l.ItemId, l.IsHQ);
            RepriceDecision d;
            var undercutters = string.Empty;
            if (offers == null)
            {
                var reason = marketTimedOut?.Invoke(l.ItemId) == true ? RepriceReason.NoData : RepriceReason.Error;
                d = new RepriceDecision(l.UnitPrice, false, reason, 0);
            }
            else
            {
                var prices = new List<long>(offers.Count);
                foreach (var o in offers) prices.Add(o.Price);
                prices.Sort();
                d = RepriceRules.Decide(l.UnitPrice, prices, floorFor(l.ItemId, l.IsHQ), settings, includeBait);
                undercutters = MarketOfferFormat.Undercutters(offers, l.UnitPrice);
                var dropped = BaitFinder.Dropped(offers, d.LowestCompetitor);
                if (dropped.Count > 0)
                {
                    result.BaitPrices[(l.Slot, l.ItemId, l.IsHQ)] = dropped[0].Price;
                    if (!result.Baits.Exists(b => b.ItemId == l.ItemId && b.IsHq == l.IsHQ))
                        result.Baits.Add(new BaitNotice(l.ItemId, l.IsHQ, retainerName, dropped, d.LowestCompetitor));
                }
            }

            onDecision?.Invoke(l, d, offers ?? Array.Empty<MarketOffer>());

            if (d.Apply)
            {
                result.Actions.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.Reprice,
                    RetainerKey = retainerKey,
                    ItemId = l.ItemId,
                    IsHQ = l.IsHQ,
                    Quantity = l.Quantity,
                    UnitPrice = d.NewPrice,
                    ListingIndex = l.Slot,
                    OldPrice = l.UnitPrice,
                    LowestCompetitor = d.LowestCompetitor,
                    Reason = d.Reason,
                    BypassedMaxDrop = d.BypassedMaxDrop,
                    MatchedBait = d.MatchedBait,
                    Undercutters = undercutters,
                });
            }
            else
            {
                result.NotApplied.Add(new PriceHistoryRow
                {
                    UtcTime = nowUtc,
                    Retainer = retainerName,
                    Slot = l.Slot,
                    ItemId = l.ItemId,
                    Hq = l.IsHQ,
                    Qty = l.Quantity,
                    OldPrice = l.UnitPrice,
                    LowestCompetitor = d.LowestCompetitor,
                    NewPrice = d.NewPrice,
                    Applied = false,
                    Reason = d.Reason,
                    Undercutters = undercutters,
                });
            }
        }
        return result;
    }
}
