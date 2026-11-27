using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Execution;

internal enum RunMode { Full, Auto, AutoRetainer, Restock, FullPrefill, Apply }
internal enum RunEntryKind { Repriced, Kept, Held, Guarded, NoData, Restocked, RestockSkipped, TopUp, Error, Planned }

internal sealed record RunEntry(string Retainer, RunEntryKind Kind, string Detail, bool HitFloor = false)
{
    [JsonIgnore]
    public bool NeedsAttention => HitFloor || Kind is RunEntryKind.Guarded or RunEntryKind.NoData or RunEntryKind.RestockSkipped or RunEntryKind.Error;

    public static RunEntry FromPriceHistory(PriceHistoryRow row, string itemName, bool prefill = false)
    {
        var kind = row.Applied ? RunEntryKind.Repriced : row.Reason switch
        {
            RepriceReason.Held => RunEntryKind.Held,
            RepriceReason.MaxDrop or RepriceReason.MaxRaise => RunEntryKind.Guarded,
            RepriceReason.NoData => RunEntryKind.NoData,
            RepriceReason.Error => RunEntryKind.Error,
            RepriceReason.Undercut or RepriceReason.Outlier or RepriceReason.Floor when prefill => RunEntryKind.Planned,
            _ => RunEntryKind.Kept,
        };
        var decision = new RepriceDecision(row.NewPrice, row.Applied || prefill, row.Reason, row.LowestCompetitor);
        var reason = row.Reason == RepriceReason.Error
            ? row.ErrorDetail ?? Strings.RunRepriceError : Strings.PriceDecisionLabel(decision);
        if (row.ClampedFloor > 0 && row.Reason != RepriceReason.Floor)
            reason += " · " + string.Format(Strings.DecisionFloor, row.ClampedFloor);
        var detail = row.Applied
            ? string.Format(Strings.RunRepricedItem, itemName, row.OldPrice, row.NewPrice, reason)
            : string.Format(Strings.RunListingDecision, itemName, row.Slot, reason);
        return new RunEntry(row.Retainer, kind, detail, row.ClampedFloor > 0 || row.Reason == RepriceReason.Floor);
    }
}

internal sealed record RunReport(DateTime StartedUtc, DateTime EndedUtc, RunMode Mode, string StopReason, bool Stopped,
    List<RunEntry> Entries)
{
    [JsonIgnore]
    public bool NeedsAttention => Stopped || Entries.Exists(e => e.NeedsAttention);

    public IEnumerable<IGrouping<string, RunEntry>> ByRetainer()
        => Entries.OrderByDescending(e => e.NeedsAttention).GroupBy(e => e.Retainer);

    public string Counts() => string.Format(Strings.RunCounts,
        Entries.Count(e => e.Kind == RunEntryKind.Repriced),
        Entries.Count(e => e.Kind == RunEntryKind.Kept),
        Entries.Count(e => e.Kind == RunEntryKind.Held),
        Entries.Count(e => e.Kind == RunEntryKind.Restocked),
        Entries.Count(e => e.Kind == RunEntryKind.TopUp),
        Entries.Count(e => e.Kind == RunEntryKind.Planned || (Mode == RunMode.FullPrefill && e.Kind == RunEntryKind.Guarded)),
        Entries.Count(e => e.NeedsAttention));

    public string Summary()
    {
        var lines = new List<string>
        {
            string.Format(Strings.RunTimeRange, Strings.RunModeLabel(Mode), StartedUtc.ToLocalTime(), EndedUtc.ToLocalTime()),
            Counts(),
            string.Format(Strings.RunStopReason, StopReason),
        };
        foreach (var group in ByRetainer())
        {
            lines.Add(string.Empty);
            lines.Add(string.IsNullOrEmpty(group.Key) ? Strings.RunGeneral : group.Key);
            lines.AddRange(group.Select(e => $"- {e.Detail}"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class RunReportHistory
{
    private const int Capacity = 20;
    private readonly Action<string, string>? activity;
    private readonly string path;
    private readonly Action<string>? warn;
    private readonly List<RunReport> reports = new();
    private (RunMode Mode, DateTime StartedUtc, List<RunEntry> Entries)? active;
    private bool dirty;

    public RunReportHistory(string path, Action<string>? warn = null, Action<string, string>? activity = null)
    {
        this.activity = activity;
        this.path = path;
        this.warn = warn;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<RunReport>>(File.ReadAllText(path)) is { } loaded)
                reports.AddRange(loaded.Where(r => r?.Entries != null && r.Entries.TrueForAll(e => e?.Detail != null)).Take(Capacity));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warn?.Invoke($"[Restocker] Could not read run history: {ex.Message}");
        }
    }

    public IReadOnlyList<RunReport> Reports => reports;
    public bool IsRecording => active != null;

    public void Start(RunMode mode, DateTime nowUtc)
    {
        active = (mode, nowUtc, []);
        activity?.Invoke("Cycle", $"{Strings.RunModeLabel(mode)} started");
    }

    public void Add(RunEntry entry)
    {
        activity?.Invoke(entry.Kind.ToString(), string.IsNullOrEmpty(entry.Retainer) ? entry.Detail : $"[{entry.Retainer}] {entry.Detail}");
        if (active is { } run) run.Entries.Add(entry);
    }

    public void Finish(DateTime nowUtc, string reason, bool stopped = false)
    {
        if (active is not { } run) return;
        active = null;
        activity?.Invoke(stopped ? "Stopped" : "Cycle", $"{Strings.RunModeLabel(run.Mode)} ended after {nowUtc - run.StartedUtc:g} · {reason}");
        reports.Insert(0, new RunReport(run.StartedUtc, nowUtc, run.Mode, reason, stopped, run.Entries));
        if (reports.Count > Capacity) reports.RemoveAt(Capacity);
        dirty = true;
        Save();
    }

    public void Save()
    {
        if (!dirty) return;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(reports));
            File.Move(temporary, path, overwrite: true);
            dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"[Restocker] Could not save run history: {ex.Message}");
        }
    }
}
