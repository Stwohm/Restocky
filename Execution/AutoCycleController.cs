using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Restocker.Common;
using Restocker.Localization;

namespace Restocker.Execution;

/// <summary>
/// Fork: timed full cycle while the player stands at the summoning bell.
///
/// Off on every plugin load (never persisted). While on:
///   - runs <see cref="Executor.StartFullCycle"/> immediately, then every interval +/- jitter;
///   - turns itself off when the player leaves the bell, moves, enters combat / duty /
///     cutscene / zone change, a duty pops, the user cancels a run, or N cycles in a row fail.
/// Every stop is printed to chat with its reason.
/// </summary>
public sealed class AutoCycleController : IDisposable
{
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Executor executor;
    private readonly Configuration configuration;
    private readonly Func<bool> isAutoRetainerLoaded;
    private readonly AutoRetainerIpc ar;
    private readonly Random rng = new();

    // ---- AutoRetainer coordination ----
    private DateTime? arWaitSinceUtc;
    private DateTime arLastBusyUtc = DateTime.MinValue;
    private DateTime nextArPollUtc = DateTime.MinValue;
    private DateTime? bellReopenAtUtc;
    private DateTime cycleStartUtc;
    private static readonly TimeSpan ArMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ArMaxWait = TimeSpan.FromMinutes(15);

    /// <summary>Non-null while the loop is holding back for AutoRetainer (shown in the UI).</summary>
    public string? WaitingReason { get; private set; }

    private bool cycleRunning;
    private bool cycleIsAuto;
    private Vector3 anchor;
    private DateTime nextPollUtc = DateTime.MinValue;

    public bool Enabled { get; private set; }
    public DateTime? NextRunUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public string? LastSummary { get; private set; }
    public bool CycleRunning => cycleRunning || ar.HasPostprocess;

    /// <summary>Max distance (yalms) the player may drift from where the loop was turned on.</summary>
    private const float MoveTolerance = 1.0f;

    public AutoCycleController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        IChatGui chat,
        IPluginLog log,
        Executor executor,
        Configuration configuration,
        Func<bool> isAutoRetainerLoaded,
        AutoRetainerIpc ar)
    {
        this.ar = ar;
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.chat = chat;
        this.log = log;
        this.executor = executor;
        this.configuration = configuration;
        this.isAutoRetainerLoaded = isAutoRetainerLoaded;
        framework.Update += Tick;
    }

    internal void ListenForPostprocess() => ar.ListenForPostprocess(
        () => configuration.RunFullCycleAfterAutoRetainer && !CycleRunning && !executor.IsRunning, StartPostprocess);

    public void Dispose()
    {
        framework.Update -= Tick;
        try
        {
            framework.RunOnFrameworkThread(() =>
            {
                try { executor.EndAutoRetainerCycle(Strings.AutoRetainerCycleStopped); }
                finally { ar.StopListeningForPostprocess(); }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Restocker] AutoRetainer postprocess cleanup failed during dispose");
            ar.StopListeningForPostprocess();
        }
        finally { ar.Release(); }
    }

    private ulong ContentId() => Plugin.PlayerState.ContentId;

    private TimeSpan ExpectedCycle() => AutoRetainerGate.ExpectedCycle(configuration.RecentCycleSeconds);

    public static bool IsBellOpen() => AddonHelper.IsOpen("RetainerList");

    /// <summary>One full cycle now (the manual "one click" button). Does not turn the loop on.</summary>
    public bool RunOnce()
    {
        if (CycleRunning || executor.IsRunning) return false;
        if (!IsBellOpen()) { Print("open the summoning bell first."); return false; }
        if (ar.Read(ContentId()) is { Busy: true })
        {
            Print("AutoRetainer is working at the bell; try again when it is done.");
            return false;
        }
        Start(auto: false);
        return executor.IsRunning;
    }

    public void Enable()
    {
        if (Enabled) return;
        if (!IsBellOpen() || !condition[ConditionFlag.OccupiedSummoningBell])
        {
            Print("auto cycle needs the summoning bell open.");
            return;
        }
        var pos = PlayerPosition();
        if (pos == null) return;
        anchor = pos.Value;
        Enabled = true;
        ConsecutiveFailures = 0;
        // Countdown starts from the full interval (no jitter on the first wait), so turning it on
        // at 30 shows 30:00. "Full cycle (once)" is the way to run immediately.
        NextRunUtc = DateTime.UtcNow + Interval();
        Print($"auto cycle ON, every {Interval().TotalMinutes:0} min (+/- {Math.Max(0, configuration.AutoCycleJitterSeconds)} s).");
        if (ar.IsLoaded)
            Print("AutoRetainer detected: each cycle waits while it works or a venture is due, and pauses it while Restocky runs.");
        log.Information("[Restocker] auto cycle enabled");
    }

    public void Disable(string reason)
    {
        if (!Enabled) return;
        Enabled = false;
        NextRunUtc = null;
        WaitingReason = null;
        arWaitSinceUtc = null;
        bellReopenAtUtc = null;
        if (cycleRunning && cycleIsAuto && executor.IsRunning) executor.Cancel();
        if (!cycleRunning) ar.Release();
        Print($"auto cycle OFF: {reason}");
        log.Information($"[Restocker] auto cycle disabled: {reason}");
    }

    /// <summary>Interval changed in the UI: count down the new interval from now.</summary>
    public void RestartCountdown()
    {
        if (!Enabled || CycleRunning) return;
        NextRunUtc = DateTime.UtcNow + Interval();
    }

    private void Start(bool auto)
    {
        ar.Suppress();
        try { executor.StartFullCycle(); }
        catch
        {
            ar.Release();
            throw;
        }
        if (!executor.IsRunning)
        {
            ar.Release();
            LastSummary = "no retainers in the bell list";
            return;
        }
        cycleRunning = true;
        cycleIsAuto = auto;
        cycleStartUtc = DateTime.UtcNow;
    }

    private TimeSpan Interval() => TimeSpan.FromMinutes(Math.Max(1, configuration.AutoCycleIntervalMinutes));

    private void ScheduleNext()
    {
        var jitter = Math.Max(0, configuration.AutoCycleJitterSeconds);
        var offset = jitter == 0 ? 0 : rng.Next(-jitter, jitter + 1);
        NextRunUtc = DateTime.UtcNow + Interval() + TimeSpan.FromSeconds(offset);
    }

    private bool StartPostprocess(string retainerName)
    {
        if (!configuration.RunFullCycleAfterAutoRetainer || cycleRunning || executor.IsRunning) return false;
        if (UnsafeReason() != null || !executor.StartAutoRetainerCycle(retainerName))
        {
            log.Information($"[Restocker] AutoRetainer postprocess skipped for '{retainerName}'");
            return false;
        }
        nextArPollUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        return true;
    }

    private void FinishPostprocess(string? reason = null)
    {
        try { executor.EndAutoRetainerCycle(reason ?? Strings.AutoRetainerCycleStopped); }
        catch (Exception ex) { log.Warning(ex, "[Restocker] AutoRetainer postprocess menu cleanup failed"); }
        finally { ar.FinishPostprocess(); }

        arLastBusyUtc = DateTime.UtcNow;
        arWaitSinceUtc = null;
        bellReopenAtUtc = null;
        WaitingReason = null;
        // Count the handoff as the timed cycle. Per-retainer durations aren't samples for the all-retainer gate.
        RestartCountdown();

        if (executor.State == ExecutionState.Done && reason == null)
        {
            ConsecutiveFailures = 0;
            LastSummary = string.Format(Strings.AutoRetainerCycleDone, executor.RepricesApplied, executor.RepricesSkipped);
            Print(LastSummary);
            executor.PrintRepriceLines();
        }
        else
        {
            LastSummary = reason ?? Strings.AutoRetainerCycleStopped;
            Print(LastSummary);
            if (executor.StatusMessage == "cancelled") Disable("run cancelled by user");
        }
    }

    private void Tick(IFramework _)
    {
        var now = DateTime.UtcNow;
        try
        {
            if (ar.HasPostprocess)
            {
                if (!executor.IsRunning) FinishPostprocess();
                else if (ar.PostprocessTimedOut) FinishPostprocess(Strings.AutoRetainerCycleTimeout);
                else if (!configuration.RunFullCycleAfterAutoRetainer || UnsafeReason() != null)
                    FinishPostprocess(Strings.AutoRetainerCycleStopped);
                else if (now >= nextArPollUtc)
                {
                    nextArPollUtc = now + TimeSpan.FromSeconds(1);
                    if (ar.PostprocessInterrupted()) FinishPostprocess(Strings.AutoRetainerCycleStopped);
                }
                return;
            }

            if (now < nextPollUtc) return;
            nextPollUtc = now + TimeSpan.FromMilliseconds(250);
            if (cycleRunning && !executor.IsRunning) FinishCycle();
            if (!Enabled) return;

            if (now >= nextArPollUtc && ar.IsLoaded)
            {
                nextArPollUtc = now + TimeSpan.FromSeconds(1);
                if (ar.Read(ContentId()) is { Busy: true }) arLastBusyUtc = now;
            }

            var stop = StopReason();
            if (stop != null) { Disable(stop); return; }

            if (!executor.IsRunning && NextRunUtc is { } next && now >= next) TryStartDue(now);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] auto cycle tick exception");
            if (ar.HasPostprocess) FinishPostprocess(Strings.AutoRetainerCycleStopped);
            Disable($"internal error ({ex.GetType().Name})");
        }
    }

    /// <summary>Cycle is due: wait for AutoRetainer if needed, reopen the bell if it closed, then start.</summary>
    private void TryStartDue(DateTime now)
    {
        var st = ar.Read(ContentId());
        var waited = arWaitSinceUtc is { } since ? now - since : TimeSpan.Zero;
        var gate = AutoRetainerGate.Decide(st, ExpectedCycle(), ArMargin, waited, ArMaxWait);
        if (!gate.Proceed)
        {
            if (arWaitSinceUtc == null) log.Information($"[Restocker] auto cycle waiting: {gate.Reason}");
            arWaitSinceUtc ??= now;
            WaitingReason = gate.Reason;
            return;
        }

        // AutoRetainer closes the bell when it is done. Reopen it once, give it a few seconds.
        if (!IsBellOpen())
        {
            if (bellReopenAtUtc == null)
            {
                bellReopenAtUtc = now;
                WaitingReason = "reopening the summoning bell";
                if (!BellHelper.TryOpenNearestBell()) { Disable("bell closed and no summoning bell within reach"); }
                return;
            }
            if (now - bellReopenAtUtc.Value > TimeSpan.FromSeconds(8)) Disable("could not reopen the summoning bell");
            return;
        }

        // Only from the plain bell list: never while someone is inside a retainer's menus.
        if (AddonHelper.IsOpen("SelectString") || AddonHelper.IsOpen("RetainerSellList") || AddonHelper.IsOpen("RetainerSell"))
            return;

        if (arWaitSinceUtc != null) log.Information($"[Restocker] auto cycle: AutoRetainer clear after {(now - arWaitSinceUtc.Value).TotalSeconds:0}s ({gate.Reason})");
        arWaitSinceUtc = null;
        bellReopenAtUtc = null;
        WaitingReason = null;
        log.Information("[Restocker] auto cycle: starting full cycle");
        Start(auto: true);
        if (!cycleRunning) ScheduleNext();
    }

    /// <summary>AutoRetainer may legitimately have the bell (or have closed it) right now.</summary>
    private bool AutoRetainerOwnsBell(DateTime now)
        => ar.IsLoaded && (arWaitSinceUtc != null || bellReopenAtUtc != null || now - arLastBusyUtc < TimeSpan.FromSeconds(60));

    private void FinishCycle()
    {
        cycleRunning = false;
        ar.Release();
        var ok = executor.State == ExecutionState.Done;
        var stamp = DateTime.Now.ToString("HH:mm");
        LastSummary = ok
            ? $"{stamp} done: {executor.CompletedJobs} retainer(s), {executor.RepricesApplied} repriced, {executor.RepricesSkipped} unchanged/skipped"
                + (executor.ListingsHeld > 0 ? $", {executor.ListingsHeld} held" : string.Empty)
            : $"{stamp} stopped: {executor.StatusMessage}";
        log.Information($"[Restocker] cycle finished: {LastSummary}");

        // Summary + one line per changed price, for manual and auto runs alike.
        Print(LastSummary);
        if (ok) executor.PrintRepriceLines();
        if (!cycleIsAuto) return;
        if (!Enabled) return;

        if (ok)
        {
            ConsecutiveFailures = 0;
            // Learn how long a cycle takes; the AutoRetainer gate uses the average of the last 5.
            configuration.RecentCycleSeconds.Add((DateTime.UtcNow - cycleStartUtc).TotalSeconds);
            while (configuration.RecentCycleSeconds.Count > 5) configuration.RecentCycleSeconds.RemoveAt(0);
            configuration.Save();
        }
        else if (executor.StatusMessage == "cancelled")
        {
            Disable("run cancelled by user");
            return;
        }
        else
        {
            ConsecutiveFailures++;
            Print($"cycle failed ({ConsecutiveFailures}/{configuration.AutoCycleMaxFailures}): {executor.StatusMessage}");
            if (ConsecutiveFailures >= Math.Max(1, configuration.AutoCycleMaxFailures))
            {
                Disable($"{ConsecutiveFailures} failed cycles in a row");
                return;
            }
        }
        ScheduleNext();
    }

    /// <summary>"Ascendant Twine [RetainerA]: 84,999 → 84,998" for every price written this run.</summary>
    private void PrintRepriceLines()
    {
        if (executor.AppliedReprices.Count == 0) return;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        foreach (var r in executor.AppliedReprices)
        {
            var name = sheet.TryGetRow(r.ItemId, out var row) ? row.Name.ExtractText() : $"#{r.ItemId}";
            var arrow = r.NewPrice < r.OldPrice ? "↓" : "↑";
            Print($"  {name}{(r.IsHq ? " (HQ)" : "")} [{r.Retainer}]: {r.OldPrice:N0} → {r.NewPrice:N0} {arrow}");
        }
    }

    private string? UnsafeReason()
    {
        if (condition[ConditionFlag.InCombat]) return "combat";
        if (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95])
            return "in a duty";
        if (condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] || condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "cutscene";
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return "zone change";
        if (AddonHelper.IsOpen("ContentsFinderConfirm")) return "duty pop";
        return null;
    }

    /// <summary>Null when it is safe to keep the loop on, otherwise why it must stop.</summary>
    private string? StopReason()
    {
        var unsafeReason = UnsafeReason();
        if (unsafeReason != null) return unsafeReason;
        var pos = PlayerPosition();
        if (pos == null) return "no local player";
        if (Vector3.Distance(pos.Value, anchor) > MoveTolerance) return "player moved";

        // During a run the executor walks retainer menus; it stops by itself if the bell
        // UI disappears. Between runs the player must still be at the bell.
        if (!executor.IsRunning && !condition[ConditionFlag.OccupiedSummoningBell] && !AutoRetainerOwnsBell(DateTime.UtcNow))
            return "left the summoning bell";
        return null;
    }

    private Vector3? PlayerPosition() => objectTable.LocalPlayer?.Position;

    private void Print(string msg) => Plugin.ChatPrint(msg);
}
