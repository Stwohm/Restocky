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

    internal bool NeedsSaddleStaging => Actions.Any(a => a.Kind == PlannedActionKind.NewListing && a.SourceKey.EndsWith(".saddle"));

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
