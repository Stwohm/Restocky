using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Execution;

/// <summary>
/// Executor が 1 リテイナーぶん処理する単位。
/// 名前で呼び出し、SellList を開き、Actions を順に実行し、終了する。
/// </summary>
public sealed class RetainerVisitJob
{
    /// <summary>RetainerList addon に表示される名前。完全一致でクリック対象を特定する。</summary>
    public required string RetainerName { get; init; }

    /// <summary>このリテイナーで処理するアクション列。Refresh モードでは空。</summary>
    public required List<PlannedAction> Actions { get; init; }

    /// <summary>Fork: walk the sell list rows and fetch market prices before running Actions.</summary>
    public bool FetchPrices { get; init; }

    /// <summary>Fork: after fetching, plan Reprice actions with the price rules and run them.</summary>
    public bool AutoPlan { get; init; }

    internal bool HasTopUpFor(PlannedAction action) =>
        Actions.Any(a => a.TopUp != null && a.ItemId == action.ItemId && a.IsHQ == action.IsHQ);

    internal bool NeedsSaddleStaging => Actions.Any(UsesSaddle);

    internal static bool UsesSaddle(PlannedAction action) => action.TopUp is { SaddleUnits: > 0 }
        || (action.Kind == PlannedActionKind.NewListing && action.SourceKey.EndsWith(".saddle"));

    internal static List<List<PlannedAction>> GroupApplyActions(IEnumerable<PlannedAction> actions)
    {
        var plan = actions.ToList();
        var visits = new List<List<PlannedAction>>();
        // Top up the largest stacks first, before any new listing can use the remaining stock.
        foreach (var action in plan.Where(a => a.TopUp != null).OrderByDescending(a => a.TopUp!.OriginalQuantity))
        {
            if (visits.Count == 0 || visits[^1][0].RetainerKey != action.RetainerKey)
                visits.Add(new List<PlannedAction>());
            visits[^1].Add(action);
        }

        var lastTopUpVisit = visits.LastOrDefault();
        foreach (var group in plan.Where(a => a.TopUp == null).GroupBy(a => a.RetainerKey))
        {
            var ordered = group.OrderBy(a => a.Kind).ThenBy(a => a.ListingIndex);
            if (lastTopUpVisit != null && lastTopUpVisit[0].RetainerKey == group.Key)
                lastTopUpVisit.AddRange(ordered);
            else
                visits.Add(ordered.ToList());
        }
        return visits;
    }

    internal static bool StartAtActive(List<RetainerVisitJob> jobs, string? activeName)
    {
        var index = jobs.FindIndex(j => j.RetainerName == activeName);
        if (index < 0) return false;
        var earlier = jobs.GetRange(0, index);
        jobs.RemoveRange(0, index);
        jobs.AddRange(earlier);
        return true;
    }
}
