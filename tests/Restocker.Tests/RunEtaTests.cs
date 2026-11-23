using System.Collections.Generic;
using Restocker.Data;
using Restocker.Execution;

namespace Restocker.Tests;

public sealed class RunEtaTests
{
    [Fact]
    public void Starting_at_an_open_sell_list_keeps_earlier_jobs_and_saddle_staging()
    {
        var a = new RetainerVisitJob { RetainerName = "A", Actions = [] };
        var b = new RetainerVisitJob { RetainerName = "B", Actions =
            [new() { Kind = Restocker.Data.PlannedActionKind.NewListing, SourceKey = "char.1.saddle" }] };
        var jobs = new System.Collections.Generic.List<RetainerVisitJob> { a, b };
        Assert.True(RetainerVisitJob.StartAtActive(jobs, "B"));
        Assert.Equal(new[] { b, a }, jobs);
        Assert.True(jobs[0].NeedsSaddleStaging);
    }

    [Fact]
    public void BudgetsSnapshotRowsUniqueSearchesAndExpectedReprices()
    {
        var job = new RetainerVisitJob { RetainerName = "A", FetchPrices = true, AutoPlan = true, Actions = [] };
        ListingEntry[] snapshot = [
            new() { ItemId = 1 }, new() { ItemId = 1 },
            new() { ItemId = 1, IsHQ = true }, new() { ItemId = 2 },
        ];
        var timings = new RunEtaTimings { RepriceShare = 0.5 };
        var work = RunEtaWork.ForJob(job, timings, snapshot, (item, hq) => item == 1 && hq);
        Assert.Equal(new RunEtaWork(Retainers: 1, Rows: 4, Searches: 3, Reprices: 2), work);
    }

    [Fact]
    public void LateSecondRetainerRowsOnlyAddWorkBeyondTheSnapshot()
    {
        var a = new RetainerVisitJob { RetainerName = "A", Actions = [] };
        var b = new RetainerVisitJob { RetainerName = "B", Actions = [] };
        var second = new RunEtaWork(Retainers: 1, Rows: 12, Searches: 8, Reprices: 2);
        var timings = new RunEtaTimings { RowSeconds = 1.75, SearchSeconds = 0.75 };
        var eta = new RunEta(timings, new Dictionary<RetainerVisitJob, RunEtaWork>
        {
            [a] = new(Retainers: 1, Rows: 4, Searches: 3),
            [b] = second,
        });
        Assert.Equal(TimeSpan.FromSeconds(49.45), eta.Remaining(TimeSpan.Zero));

        for (var i = 0; i < 3; i++)
        {
            var start = TimeSpan.FromSeconds(3 + 2.5 * i);
            eta.StartAction(start);
            eta.StartSearch(start + TimeSpan.FromSeconds(0.75));
            eta.FinishSearch(start + TimeSpan.FromSeconds(1.5));
            // The last row's duplicate is drained with it.
            eta.FinishRow(a, i == 2 ? 0 : 3 - i, 2 - i, start + TimeSpan.FromSeconds(1.7), TimeSpan.FromSeconds(0.8));
        }
        eta.FinishVisit(a, TimeSpan.FromSeconds(13.5), learnOverhead: true);

        var secondOpened = TimeSpan.FromSeconds(17);
        var before = eta.Remaining(secondOpened);
        eta.SetWork(b, second, secondOpened, discovered: true);
        Assert.True(eta.Remaining(secondOpened) <= before);

        before = eta.Remaining(secondOpened);
        eta.SetWork(b, second with { Rows = 13, Searches = 9 }, secondOpened, discovered: true);
        Assert.InRange(eta.Remaining(secondOpened) - before, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
        Assert.True(eta.Remaining(secondOpened + TimeSpan.FromSeconds(1)) < eta.Remaining(secondOpened));
    }

    [Fact]
    public void LearnsRowAndMarketWaitSeparatelyWithoutIncreasingEtaOnASlowSearch()
    {
        var job = new RetainerVisitJob { RetainerName = "A", Actions = [] };
        var timings = new RunEtaTimings { RowSeconds = 2, SearchSeconds = 1 };
        var eta = new RunEta(timings, new() { [job] = new(Retainers: 1, Rows: 10, Searches: 10) });
        eta.StartAction(TimeSpan.Zero);
        eta.StartSearch(TimeSpan.FromSeconds(1));
        eta.FinishSearch(TimeSpan.FromSeconds(4));
        var elapsed = TimeSpan.FromSeconds(4.2);
        var before = eta.Remaining(elapsed);
        eta.FinishRow(job, 9, 9, elapsed, TimeSpan.FromSeconds(0.8));
        Assert.True(eta.Remaining(elapsed) <= before);
        Assert.Equal(timings.RowSeconds, eta.Timings.RowSeconds);
        Assert.Equal(1.5, eta.Timings.SearchSeconds);
    }

    [Fact]
    public void ApplyRunsLearnRepriceListingAndRetainerDurationsSeparately()
    {
        var job = new RetainerVisitJob { RetainerName = "A", Actions = [] };
        var timings = new RunEtaTimings { RepriceSeconds = 1, ListingSeconds = 1, RetainerSeconds = 4 };
        var eta = new RunEta(timings, new() { [job] = new(Retainers: 1, Reprices: 1, Listings: 1) });
        eta.StartAction(TimeSpan.FromSeconds(2));
        eta.FinishReprice(job, TimeSpan.FromSeconds(2.5));
        eta.StartAction(TimeSpan.FromSeconds(2.5));
        eta.FinishListing(job, TimeSpan.FromSeconds(4.5));
        eta.FinishVisit(job, TimeSpan.FromSeconds(7.5), learnOverhead: true);
        Assert.Equal(0.875, eta.Timings.RepriceSeconds);
        Assert.Equal(1.25, eta.Timings.ListingSeconds);
        Assert.Equal(4.25, eta.Timings.RetainerSeconds);
        Assert.Equal(TimeSpan.Zero, eta.Remaining(TimeSpan.FromSeconds(7.5)));
    }
}
