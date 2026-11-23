using Restocker.Execution;

namespace Restocker.Tests;

public sealed class RunEtaTests
{
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
