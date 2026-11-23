using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>Why a listing got (or did not get) a new price. Written to price-history.csv.</summary>
public enum RepriceReason
{
    /// <summary>Lowest competitor - undercut.</summary>
    Undercut,

    /// <summary>Like Undercut, but one or two bait listings at the bottom of the board were ignored.</summary>
    Outlier,

    /// <summary>Target was below the per-item floor; clamped to the floor.</summary>
    Floor,

    /// <summary>Target dropped more than the allowed percentage; not applied.</summary>
    MaxDrop,

    /// <summary>Already at the target price.</summary>
    NoChange,

    /// <summary>No competitor listing on the board (only own retainers, or none).</summary>
    NoCompetitor,

    /// <summary>Fetch failed to reach a search, slot mismatched, or a game call failed.</summary>
    Error,

    /// <summary>Applied from the manual plan-then-apply table.</summary>
    Manual,

    /// <summary>Automatic cycles are disabled for this item and quality.</summary>
    Held,
    MaxRaise,
    NoData,
}

public sealed class RepriceSettings
{
    public int UndercutGil { get; init; } = 1;

    /// <summary>Skip (do not apply) when the new price is more than this % below the current one. 0 disables.</summary>
    public double MaxDropPercent { get; init; } = 30;
    public double MaxRaisePercent { get; init; } = 30;

    public int OutlierMaxGroupSize { get; init; } = 2;
    public double OutlierGapRatio { get; init; } = 1.5;
}

public readonly record struct RepriceDecision(
    long NewPrice,
    bool Apply,
    RepriceReason Reason,
    long LowestCompetitor)
{
    // Fork: manual prefill keeps the proposed price for either guarded change.
    public bool MarkForReview => Reason is RepriceReason.MaxDrop or RepriceReason.MaxRaise;
    public bool BypassedMaxDrop { get; init; }
    internal bool MatchedBait { get; init; }
    internal long ClampedFloor { get; init; }
}

/// <summary>
/// Pure price rule for the auto / full-cycle path:
/// lowest competitor (outliers ignored) - undercut, clamped to the item floor,
/// refused when the change exceeds the configured drop or raise limit.
/// The caller selects the competitor pool using the listing's Match quality setting.
/// </summary>
public static class RepriceRules
{
    public static string ReasonCode(RepriceReason r) => r switch
    {
        RepriceReason.Undercut => "undercut",
        RepriceReason.Outlier => "outlier",
        RepriceReason.Floor => "floor",
        RepriceReason.MaxDrop => "max_drop",
        RepriceReason.MaxRaise => "max_raise",
        RepriceReason.NoChange => "no_change",
        RepriceReason.NoCompetitor => "no_competitor",
        RepriceReason.NoData => "no_data",
        RepriceReason.Error => "error",
        RepriceReason.Manual => "manual",
        RepriceReason.Held => "held",
        _ => r.ToString().ToLowerInvariant(),
    };

    /// <param name="currentPrice">Our current asking price for this listing.</param>
    /// <param name="competitorPricesAscending">Other sellers' unit prices, selected quality pool, ascending. Own retainers excluded.</param>
    /// <param name="floor">Per-item minimum price. 0 = none.</param>
    public static RepriceDecision Decide(
        long currentPrice,
        IReadOnlyList<long> competitorPricesAscending,
        long floor,
        RepriceSettings settings,
        bool includeBait = false)
    {
        if (competitorPricesAscending.Count == 0)
            return new RepriceDecision(currentPrice, false, RepriceReason.NoCompetitor, 0);

        var raw = competitorPricesAscending[0];
        var picked = LowestPriceResolver.Pick(competitorPricesAscending, settings.OutlierMaxGroupSize, settings.OutlierGapRatio, currentPrice);
        var lowest = includeBait ? competitorPricesAscending.FirstOrDefault(price => price > 0) : picked;
        var matchedBait = lowest > 0 && lowest < picked;
        if (matchedBait) picked = lowest;
        if (picked <= 0)
            return new RepriceDecision(currentPrice, false, RepriceReason.Error, 0);

        var reason = !includeBait && picked != raw ? RepriceReason.Outlier : RepriceReason.Undercut;
        var target = Math.Max(1, picked - Math.Max(0, settings.UndercutGil));

        if (floor > 0 && target < floor)
        {
            target = floor;
            reason = RepriceReason.Floor;
        }

        if (target == currentPrice)
            return new RepriceDecision(target, false, RepriceReason.NoChange, picked) { ClampedFloor = reason == RepriceReason.Floor ? floor : 0 };

        var clampedFloor = reason == RepriceReason.Floor ? floor : 0;
        if (LimitExceeded(currentPrice, target, settings.MaxDropPercent, settings.MaxRaisePercent) is { } limit)
        {
            if (matchedBait && limit == RepriceReason.MaxDrop)
                return new RepriceDecision(target, true, reason, picked) { BypassedMaxDrop = true, MatchedBait = true, ClampedFloor = clampedFloor };
            return new RepriceDecision(target, false, limit, picked) { ClampedFloor = clampedFloor };
        }

        return new RepriceDecision(target, true, reason, picked) { MatchedBait = matchedBait, ClampedFloor = clampedFloor };
    }

    // Fork: the editor and confirmation check the actual proposal, including manual edits.
    public static RepriceReason? LimitExceeded(long current, long proposed, double maxDrop, double maxRaise)
    {
        if (current <= 0 || proposed <= 0) return null;
        var change = ChangePercent(current, proposed);
        if (maxRaise > 0 && change > maxRaise) return RepriceReason.MaxRaise;
        if (maxDrop > 0 && -change > maxDrop) return RepriceReason.MaxDrop;
        return null;
    }

    public static double ChangePercent(long current, long proposed) => (proposed - current) * 100.0 / current;
}
