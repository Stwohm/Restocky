using System.IO;
using System.Linq;
using Restocker.Execution;
using Restocker.Market;

namespace Restocker.Tests;

public class RunReportHistoryTests
{
    [Fact]
    public void A_guarded_prefill_is_a_proposal_to_review()
    {
        var row = new PriceHistoryRow { Reason = RepriceReason.MaxDrop, OldPrice = 100, NewPrice = 25 };
        var entry = RunEntry.FromPriceHistory(row, "Item", prefill: true);
        var report = new RunReport(DateTime.UnixEpoch, DateTime.UnixEpoch, RunMode.FullPrefill, "Done", false, [entry]);
        Assert.Contains("review (proposed 25)", entry.Detail);
        Assert.Contains("1 proposed", report.Counts());
        Assert.True(entry.NeedsAttention);
    }

    [Fact]
    public void Malformed_entries_do_not_hide_valid_runs_or_break_the_report_tab()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, """[null,{},{"Entries":[null]},{"Entries":[],"StopReason":"Done"}]""");
        var valid = Assert.Single(new RunReportHistory(temp.Path).Reports);
        Assert.False(valid.NeedsAttention);
        Assert.Contains("Done", valid.Summary());
    }

    [Fact]
    public void Persists_twenty_latest_completed_or_stopped_runs_in_order()
    {
        using var temp = new TempFile();
        var history = new RunReportHistory(temp.Path);
        var start = DateTime.UnixEpoch;
        for (var i = 0; i < 22; i++)
        {
            history.Start(i == 21 ? RunMode.AutoRetainer : RunMode.Auto, start.AddMinutes(i));
            history.Add(new RunEntry("RetainerB", RunEntryKind.TopUp, "Darksteel 48 → 80"));
            history.Finish(start.AddMinutes(i + 1), $"End {i}", stopped: i == 21);
        }
        history.Finish(start.AddMinutes(100), "Duplicate finish");

        var reloaded = new RunReportHistory(temp.Path).Reports;
        Assert.Equal(20, reloaded.Count);
        Assert.Equal(start.AddMinutes(2), reloaded[^1].StartedUtc);
        Assert.Equal(start.AddMinutes(22), reloaded[0].EndedUtc);
        Assert.Equal(RunMode.AutoRetainer, reloaded[0].Mode);
        Assert.Equal("End 21", reloaded[0].StopReason);
        Assert.True(reloaded[0].NeedsAttention);
        Assert.Equal("Darksteel 48 → 80", Assert.Single(reloaded[0].Entries).Detail);
    }

    [Fact]
    public void Copied_report_groups_by_retainer_and_lists_problems_first()
    {
        var kept = RunEntry.FromPriceHistory(new PriceHistoryRow { Retainer = "RetainerB", Reason = RepriceReason.NoCompetitor }, "Twine");
        var restocked = new RunEntry("RetainerA", RunEntryKind.Restocked, "Restocked sand 4× @ 200 (last sale)");
        var guard = RunEntry.FromPriceHistory(new PriceHistoryRow
        {
            Retainer = "RetainerB", Reason = RepriceReason.MaxDrop, OldPrice = 80_000, NewPrice = 20_000, ClampedFloor = 20_000,
        }, "Polish");
        var report = new RunReport(DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(1), RunMode.Full, "Completed", false,
            [restocked, kept, guard]);

        Assert.True(report.NeedsAttention);
        Assert.Equal(RunEntryKind.Guarded, guard.Kind);
        Assert.Equal(new[] { "RetainerB", "RetainerA" }, report.ByRetainer().Select(g => g.Key));
        Assert.Equal(new[] { guard, kept }, report.ByRetainer().First());
        var summary = report.Summary();
        Assert.Contains("max_drop blocked", summary);
        Assert.Contains("Floor hit", summary);
        Assert.Contains("no competitor", summary);
        Assert.Contains("last sale", summary);
        Assert.Contains("Completed", summary);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();
        public void Dispose() => File.Delete(Path);
    }
}
