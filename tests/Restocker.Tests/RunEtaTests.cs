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
    public void UsesCycleAverageUntilAUnitFinishes()
    {
        var elapsed = TimeSpan.FromSeconds(20);
        Assert.Null(RunEta.Estimate(elapsed, 0, 8, 0, 2, 1, 1, null));
        Assert.Equal(TimeSpan.FromSeconds(180), RunEta.Estimate(elapsed, 0, 8, 0, 2, 1, 1, TimeSpan.FromSeconds(200)));
        Assert.Equal(TimeSpan.Zero, RunEta.Estimate(elapsed, 0, 8, 0, 2, 1, 1, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ProjectsUnvisitedFetchJobsFromDiscoveredActions()
    {
        var eta = RunEta.Estimate(TimeSpan.FromSeconds(40), 4, 8, 0, 3, 1, 2, TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(200), eta);
    }

    [Fact]
    public void UsesCompletedJobsForRefreshWithoutActions()
    {
        var eta = RunEta.Estimate(TimeSpan.FromSeconds(30), 0, 0, 2, 5, 0, 0, null);
        Assert.Equal(TimeSpan.FromSeconds(45), eta);
    }

    [Fact]
    public void UsesActionsForApplyRuns()
    {
        var eta = RunEta.Estimate(TimeSpan.FromSeconds(30), 3, 5, 1, 2, 0, 0, null);
        Assert.Equal(TimeSpan.FromSeconds(20), eta);
    }
}
