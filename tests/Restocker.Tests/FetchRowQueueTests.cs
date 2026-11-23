using System.Collections.Generic;
using Restocker.Execution;

namespace Restocker.Tests;

/// <summary>
/// Regression tests for yagi2/Restocker#1 ("InvalidOperationException: Queue empty").
/// The executor dequeued a duplicate row once when skipping it and once more after
/// returning to the sell list, so the row after every duplicate was never scanned
/// and a trailing duplicate emptied the queue before the second dequeue.
/// </summary>
public class FetchRowQueueTests
{
    /// <summary>Drives the queue the way Executor does and returns the rows it clicked.</summary>
    private static (List<int> Visited, List<uint> FetchedItems) Drive(uint[] itemByRow, int uniqueTargets)
    {
        var q = new FetchRowQueue(itemByRow.Length, uniqueTargets);
        var visited = new List<int>();
        var fetchedItems = new List<uint>();
        var guard = 0;
        while (!q.IsEmpty)
        {
            if (++guard > 100) throw new Exception("loop did not terminate");
            var row = q.CurrentRow;
            visited.Add(row);
            var decision = q.OnDialogResolved(itemByRow[row], false);
            if (decision == FetchRowDecision.Fetch) fetchedItems.Add(itemByRow[row]);
            q.CompleteCurrentRow();
        }
        return (visited, fetchedItems);
    }

    [Fact]
    public void Duplicate_row_does_not_skip_the_next_row()
    {
        // A A B: row 1 is a duplicate of row 0; row 2 must still be scanned.
        var (visited, fetched) = Drive(new uint[] { 10, 10, 20 }, uniqueTargets: 0);
        Assert.Equal(new[] { 0, 1, 2 }, visited);
        Assert.Equal(new uint[] { 10, 20 }, fetched);
    }

    [Fact]
    public void Trailing_duplicate_does_not_throw_queue_empty()
    {
        // A B B with a stale unique count of 3 (so early drain never fires):
        // the last row is a duplicate. Old code threw InvalidOperationException here.
        var ex = Record.Exception(() => Drive(new uint[] { 10, 20, 20 }, uniqueTargets: 3));
        Assert.Null(ex);
    }

    [Fact]
    public void Five_competence_stacks_scan_every_unique_item()
    {
        // Pattern from the 2026-11 log: one item listed five times in a row between others.
        var items = new uint[] { 1, 2, 7, 7, 7, 7, 7, 3, 4, 5 };
        var (visited, fetched) = Drive(items, uniqueTargets: 0);
        Assert.Equal(items.Length, visited.Count);
        Assert.Equal(new uint[] { 1, 2, 7, 3, 4, 5 }, fetched);
    }

    [Fact]
    public void Unresolved_dialog_advances_exactly_one_row()
    {
        var q = new FetchRowQueue(3, 0);
        Assert.Equal(FetchRowDecision.SkipUnresolved, q.OnDialogResolved(0, false));
        q.CompleteCurrentRow();
        Assert.Equal(1, q.CurrentRow);
    }

    [Fact]
    public void Drain_fires_once_all_unique_items_are_fetched()
    {
        // A B A A: after B the 2 unique targets are done; rows 2 and 3 are drained.
        var (visited, _) = Drive(new uint[] { 10, 20, 10, 10 }, uniqueTargets: 2);
        Assert.Equal(new[] { 0, 1 }, visited);
    }

    [Fact]
    public void Remaining_searches_exclude_duplicates_and_are_bounded_by_remaining_rows()
    {
        var q = new FetchRowQueue(3, 2);
        q.OnDialogResolved(10, false);
        q.CompleteCurrentRow();
        Assert.Equal(1, q.RemainingSearches);
        q.OnDialogResolved(10, false);
        q.CompleteCurrentRow();
        Assert.Equal(1, q.RemainingSearches);
        q.SkipCurrentRow();
        Assert.Equal(0, q.RemainingSearches);
    }

    [Fact]
    public void Hq_and_nq_of_same_item_are_distinct()
    {
        var q = new FetchRowQueue(2, 0);
        Assert.Equal(FetchRowDecision.Fetch, q.OnDialogResolved(10, false));
        q.CompleteCurrentRow();
        Assert.Equal(FetchRowDecision.Fetch, q.OnDialogResolved(10, true));
    }

    [Fact]
    public void Skip_current_row_on_timeout_advances_one()
    {
        var q = new FetchRowQueue(2, 0);
        q.SkipCurrentRow();
        Assert.Equal(1, q.CurrentRow);
        q.SkipCurrentRow();
        Assert.True(q.IsEmpty);
        q.SkipCurrentRow(); // no throw on empty
    }
}
