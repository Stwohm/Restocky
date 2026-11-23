using System;

namespace Restocker.Execution;

internal static class RunEta
{
    internal static TimeSpan? Estimate(TimeSpan elapsed, int completedActions, int knownActions,
        int completedJobs, int totalJobs, int observedFetchJobs, int pendingFetchJobs, TimeSpan? initialDuration)
    {
        if (completedActions > 0 && knownActions > 0)
        {
            // Future fetch jobs discover their rows on opening the sell list. Project their
            // work from the live counts already seen, including reprices planned so far.
            var remaining = Math.Max(0, knownActions - completedActions);
            if (observedFetchJobs > 0)
                remaining += (int)Math.Ceiling((double)knownActions / observedFetchJobs * pendingFetchJobs);
            return elapsed / completedActions * remaining;
        }

        if (knownActions == 0 && completedJobs > 0)
            return elapsed / completedJobs * Math.Max(0, totalJobs - completedJobs);

        return initialDuration is { } duration
            ? TimeSpan.FromTicks(Math.Max(0, (duration - elapsed).Ticks))
            : null;
    }
}
