using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Execution;

public sealed record RunEtaTimings
{
    public double RowSeconds { get; init; } = 1.9;
    public double SearchSeconds { get; init; } = 0.75;
    public double RetainerSeconds { get; init; } = 6;
    public double RepriceSeconds { get; init; } = 0.6;
    public double ListingSeconds { get; init; } = 0.75;
    public double RepriceShare { get; init; } = 0.25;
}

// A search adds market response time to the cost of walking a row. A duplicate
// only costs the row navigation, or nothing when it is drained without opening.
internal readonly record struct RunEtaWork(int Retainers = 0, int Rows = 0, int Searches = 0,
    int Reprices = 0, int Listings = 0)
{
    internal static RunEtaWork ForJob(RetainerVisitJob job, RunEtaTimings timings,
        IReadOnlyList<ListingEntry> snapshot, Func<uint, bool, bool> isHeld)
    {
        var work = new RunEtaWork(Retainers: 1, Reprices: job.Actions.Count(a => a.Kind == PlannedActionKind.Reprice),
            Listings: job.Actions.Count(a => a.Kind == PlannedActionKind.NewListing));
        if (!job.FetchPrices) return work;

        var eligible = job.AutoPlan ? snapshot.Count(l => !isHeld(l.ItemId, l.IsHQ)) : 0;
        return work with
        {
            Rows = snapshot.Count,
            Searches = snapshot.Select(l => (l.ItemId, l.IsHQ)).Distinct().Count(),
            Reprices = work.Reprices + (int)Math.Round(eligible * timings.RepriceShare),
        };
    }

    internal double Seconds(RunEtaTimings timings) =>
        Retainers * timings.RetainerSeconds + Rows * timings.RowSeconds + Searches * timings.SearchSeconds
        + Reprices * timings.RepriceSeconds + Listings * timings.ListingSeconds;
}

internal sealed class RunEta(RunEtaTimings timings, Dictionary<RetainerVisitJob, RunEtaWork> work)
{
    private const double Smoothing = 0.25;
    private TimeSpan forecastAt;
    private double forecast = work.Values.Sum(w => w.Seconds(timings));
    private TimeSpan visitStartedAt;
    private TimeSpan visitActionTime;
    private TimeSpan actionStartedAt;
    private TimeSpan searchStartedAt;
    private TimeSpan searchTime;

    internal RunEtaTimings Timings { get; private set; } = timings;
    internal RunEtaWork WorkFor(RetainerVisitJob job) => work.GetValueOrDefault(job);
    internal TimeSpan Remaining(TimeSpan elapsed)
        => work.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(1, forecast - (elapsed - forecastAt).TotalSeconds));

    internal void SetWork(RetainerVisitJob job, RunEtaWork next, TimeSpan elapsed, bool discovered = false)
    {
        var previous = Remaining(elapsed).TotalSeconds;
        var added = discovered ? Math.Max(0, next.Seconds(Timings) - WorkFor(job).Seconds(Timings)) : 0;
        work[job] = next;
        UpdateForecast(previous + added, elapsed);
    }

    internal void LearnRepriceShare(int eligible, int planned)
    {
        if (eligible > 0)
            Timings = Timings with { RepriceShare = Average(Timings.RepriceShare, (double)planned / eligible) };
    }

    internal void StartAction(TimeSpan elapsed)
    {
        actionStartedAt = elapsed;
        searchTime = TimeSpan.Zero;
    }

    internal void StartSearch(TimeSpan elapsed) => searchStartedAt = elapsed;
    internal void FinishSearch(TimeSpan elapsed) => searchTime = elapsed - searchStartedAt;

    internal void FinishRow(RetainerVisitJob job, int rows, int searches, TimeSpan elapsed, TimeSpan cooldown)
    {
        var duration = elapsed - actionStartedAt + cooldown;
        visitActionTime += duration;
        Timings = Timings with
        {
            RowSeconds = AverageDuration(Timings.RowSeconds, duration - searchTime),
            SearchSeconds = searchTime > TimeSpan.Zero ? AverageDuration(Timings.SearchSeconds, searchTime) : Timings.SearchSeconds,
        };
        SetWork(job, WorkFor(job) with { Rows = rows, Searches = searches }, elapsed);
    }

    internal void FinishReprice(RetainerVisitJob job, TimeSpan elapsed, TimeSpan cooldown = default)
    {
        var duration = elapsed - actionStartedAt + cooldown;
        visitActionTime += duration;
        Timings = Timings with { RepriceSeconds = AverageDuration(Timings.RepriceSeconds, duration) };
        var current = WorkFor(job);
        SetWork(job, current with { Reprices = current.Reprices - 1 }, elapsed);
    }

    internal void FinishListing(RetainerVisitJob job, TimeSpan elapsed, TimeSpan cooldown = default)
    {
        var duration = elapsed - actionStartedAt + cooldown;
        visitActionTime += duration;
        Timings = Timings with { ListingSeconds = AverageDuration(Timings.ListingSeconds, duration) };
        var current = WorkFor(job);
        SetWork(job, current with { Listings = current.Listings - 1 }, elapsed);
    }

    internal void FinishVisit(RetainerVisitJob job, TimeSpan elapsed, bool learnOverhead)
    {
        var previous = Remaining(elapsed).TotalSeconds;
        if (learnOverhead)
            Timings = Timings with { RetainerSeconds = AverageDuration(Timings.RetainerSeconds, elapsed - visitStartedAt - visitActionTime) };
        visitStartedAt = elapsed;
        visitActionTime = TimeSpan.Zero;
        work.Remove(job);
        UpdateForecast(previous, elapsed);
    }

    private void UpdateForecast(double remaining, TimeSpan elapsed)
    {
        var target = work.Values.Sum(w => w.Seconds(Timings));
        forecast = Math.Min(remaining, Average(remaining, target));
        forecastAt = elapsed;
    }

    private static double Average(double previous, double sample) => previous + Smoothing * (sample - previous);

    // Bound the influence of a single stall on later runs.
    private static double AverageDuration(double previous, TimeSpan duration)
        => Average(previous, Math.Clamp(duration.TotalSeconds, 0.1, Math.Max(0.1, previous * 3)));
}
