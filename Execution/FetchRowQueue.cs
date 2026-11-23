using System;
using System.Collections.Generic;

namespace Restocker.Execution;

/// <summary>What the executor should do with the row whose RetainerSell dialog just opened.</summary>
public enum FetchRowDecision
{
    /// <summary>First time this (item, hq) is seen in this job: fire ComparePrices.</summary>
    Fetch,

    /// <summary>Same (item, hq) already fetched in this job: close the dialog, no market request.</summary>
    SkipDuplicate,

    /// <summary>Dialog item name could not be resolved to an item id: close the dialog.</summary>
    SkipUnresolved,
}

/// <summary>
/// Pure bookkeeping for the "walk display rows 0..N-1 and fetch market prices" phase.
/// Extracted from <see cref="Executor"/> so the row-advance rules can be unit tested.
///
/// Contract with the executor:
///   1. Read <see cref="CurrentRow"/>, click it.
///   2. When the dialog opens, call <see cref="OnDialogResolved"/>. It never advances the queue.
///   3. When the dialogs are closed again and the sell list is back, call
///      <see cref="CompleteCurrentRow"/>. This is the only place a dialog-opened row advances.
///   4. If the dialog never opened (timeout), call <see cref="SkipCurrentRow"/> instead of 2 and 3.
/// </summary>
public sealed class FetchRowQueue
{
    private readonly Queue<int> rows = new();
    private readonly HashSet<(uint ItemId, bool IsHq)> fetched = new();
    private readonly int uniqueTargets;

    /// <param name="rowCount">Number of display rows in the sell list.</param>
    /// <param name="uniqueTargets">Distinct (item, hq) pairs on the sell list, or 0 to disable early drain.</param>
    public FetchRowQueue(int rowCount, int uniqueTargets)
    {
        for (var r = 0; r < rowCount; r++) rows.Enqueue(r);
        this.uniqueTargets = uniqueTargets;
    }

    public bool IsEmpty => rows.Count == 0;
    public int Remaining => rows.Count;
    internal int RemainingSearches => Math.Clamp(uniqueTargets - fetched.Count, 0, Remaining);
    public int CurrentRow => rows.Count > 0 ? rows.Peek() : -1;
    public int FetchedCount => fetched.Count;
    public IReadOnlyCollection<(uint ItemId, bool IsHq)> Fetched => fetched;

    public FetchRowDecision OnDialogResolved(uint itemId, bool isHq)
    {
        if (itemId == 0) return FetchRowDecision.SkipUnresolved;
        if (!fetched.Add((itemId, isHq))) return FetchRowDecision.SkipDuplicate;
        return FetchRowDecision.Fetch;
    }

    /// <summary>Advance past the current row. Returns how many trailing rows were drained.</summary>
    public int CompleteCurrentRow()
    {
        if (rows.Count == 0) return 0;
        rows.Dequeue();
        if (uniqueTargets > 0 && fetched.Count >= uniqueTargets && rows.Count > 0)
        {
            var drained = rows.Count;
            rows.Clear();
            return drained;
        }
        return 0;
    }

    /// <summary>Row's dialog never opened; drop it without touching the fetched set.</summary>
    public void SkipCurrentRow()
    {
        if (rows.Count > 0) rows.Dequeue();
    }
}
