using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Restocker.Common;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Execution;

/// <summary>
/// 自前ステートマシンによるリテイナー巡回・操作の実行器（Y モード）。
///
/// 0.1.x で実装済みのフロー:
///   - <see cref="ExecutionMode.RefreshAll"/>: 各リテイナーを呼び出し → SellList を一瞬開いて
///     RetainerWatcher が snapshot を取り直すのを待ち → 閉じる → 終了 → 次。
///
/// Reprice / NewListing は AwaitingSellDialog / ConfirmingSellDialog 経由で
/// 個別アクションを処理するパスがあるが、価格セットや qty セットの addon 操作は
/// addon 構造体の AskingPrice / Quantity を直接叩く方式で書いている (Marketbuddy 流)。
/// </summary>
public sealed unsafe class Executor : IDisposable
{
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly Restocker.Market.MarketCache? marketCache;
    private readonly CostLedger costLedger;
    /// <summary>FetchMarketPrice 完了後に発火するコールバック。RepriceTab が -1ギル を当てる用。</summary>
    public System.Action? OnFetchMarketCompleted;

    /// <summary>Fork: fired after each retainer's fetch phase in a non-auto fetch job, with that retainer's snapshot key.</summary>
    public System.Action<string>? OnFetchJobCompleted;

    public ExecutionState State { get; private set; } = ExecutionState.Idle;
    public ExecutionMode Mode { get; private set; }
    public string? StatusMessage { get; private set; }
    public int CompletedJobs { get; private set; }
    public int TotalJobs => jobs.Count;
    internal int CompletedRetainers => jobs.Take(CompletedJobs).Select(j => j.RetainerName).Distinct().Count();
    public int CompletedActions { get; private set; }
    public int TotalActions { get; private set; }
    public bool IsRunning => State != ExecutionState.Idle && State != ExecutionState.Done && State != ExecutionState.Stopped;
    public string CurrentStateLabel => State.ToString();
    internal TimeSpan? EstimatedRemaining => runEta?.Remaining(runClock.Elapsed);

    private readonly Stopwatch runClock = new();
    private RunEta? runEta;
    private bool partialFirstVisit;
    private RetainerVisitJob CurrentJob => jobs[jobCursor];
    private readonly List<RetainerVisitJob> jobs = new();
    private int jobCursor;
    private readonly HashSet<string> historyRequested = new();
    private string? historyRetainerKey;
    private long historyReceipt;
    private DateTime historyDeadline;
    private readonly Queue<PlannedAction> currentJobActions = new();
    private bool collectAutoRestock;
    private string? autoRetainerKey;
    private bool postprocessOpenedSellList;
    private bool autoRestockPass;
    private readonly Dictionary<string, RetainerSnapshot> autoRestockSnapshots = new();
    private readonly Dictionary<(uint ItemId, bool IsHq), (AutoRestockPriceKind Kind, long Price, int Quantity)> autoRestockResults = new();
    private readonly Queue<int> slotsToReadPrice = new();
    private string? readingSnapshotKey;
    private bool cancelRequested;
    /// <summary>このリテイナージョブ内で MoveToRetainerMarket に使った market slot。
    /// サーバ反映ラグで同じスロットを再ターゲットして "swap" 動作になることを防ぐ。</summary>
    private readonly HashSet<int> usedMarketSlots = new();

    /// <summary>AwaitingNewListing 用: 直前の MoveToRetainerMarket の対象 market slot。</summary>
    private int pendingListingSlot = -1;
    private uint pendingListingItemId;
    private bool pendingListingIsHQ;
    private int pendingListingQuantity;
    private string? listedRetainerKey;
    private PlannedAction? pendingReprice;

    /// <summary>PreStagingSaddle 用: 最後の saddle slot 移動を発火した時刻。
    /// 最終 staging からの settle 時間を保証するため。</summary>
    private DateTime? lastStageAt;
    private ((uint ItemId, bool IsHq) Item, int InBag)? pendingStage;
    private readonly HashSet<(uint ItemId, bool IsHq)> failedStages = new();

    /// <summary>FetchAwaitingSellDialog で listing slot click を再送した記録。</summary>
    private bool fetchSlotClickRetried;

    // ---- Fork: fetch phase (see FetchRowQueue for the row-advance contract) ----

    /// <summary>Non-null while the current job is walking sell-list rows for market prices.</summary>
    private FetchRowQueue? fetchRows;
    private readonly ContextMenuRetryPolicy fetchContextMenuRetry = new();

    /// <summary>Set on entering a FetchPrices job; rows are built from the LIVE market
    /// container on the first PerformingAction tick (after the snapshot wait).</summary>
    private bool fetchRowsPending;

    /// <summary>(item, hq) resolved from the currently open RetainerSell dialog.</summary>
    private uint fetchItemId;
    private bool fetchIsHq;

    /// <summary>When ComparePrices was clicked; cache entries older than this are stale.</summary>
    private DateTime fetchClickedAtUtc;
    private bool searchResultWasClosed;
    private readonly HashSet<uint> timedOutMarketItems = new();
    internal bool MarketFetchTimedOut(uint itemId) => timedOutMarketItems.Contains(itemId);

    /// <summary>Start of the current job's fetch phase; the planner only trusts data newer than this.</summary>
    internal DateTime JobFetchStartUtc { get; private set; }

    /// <summary>Bait notices already shown this session (key = BaitFinder.Key).</summary>
    private readonly HashSet<string> reportedBaits = new();
    private readonly BaitMatchOverrides baitMatches = new();
    private readonly Dictionary<string, Dictionary<(int Slot, uint ItemId, bool IsHq), long>> lastBaitPrices = new();

    internal void QueueBaitMatch(uint itemId, bool isHq) => baitMatches.Queue(itemId, isHq);

    internal Dictionary<(int Slot, uint ItemId, bool IsHq), long> LastBaitPricesFor(string retainerKey)
    {
        if (!lastBaitPrices.TryGetValue(retainerKey, out var prices))
            lastBaitPrices[retainerKey] = prices = new();
        return prices;
    }

    /// <summary>Fork: append-only price-history.csv. Null = disabled.</summary>
    public PriceHistoryLog? History { get; set; }

    /// <summary>Fork: per-run counters for the full / auto cycle summary.</summary>
    public int RepricesApplied { get; private set; }
    public int RepricesSkipped { get; private set; }
    public int ListingsHeld { get; private set; }
    // Fork: retain guarded proposals until the end-of-cycle chat summary.
    private readonly List<PriceHistoryRow> skippedSummaryRows = new();

    /// <summary>Fork: every price actually written in this run, for the chat summary.</summary>
    public List<(string Retainer, uint ItemId, bool IsHq, long OldPrice, long NewPrice)> AppliedReprices { get; } = new();
    private static readonly TimeSpan SaddleSettleAfterStage = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FetchRowCooldown = TimeSpan.FromMilliseconds(800);

    private DateTime nextStepNoEarlierThan = DateTime.MinValue;
    private static readonly TimeSpan StepThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromMilliseconds(700);
    /// <summary>テキスト一致の SelectString エントリが見つからなかった時に諦めるタイムアウト。</summary>
    private static readonly TimeSpan SelectStringTimeout = TimeSpan.FromSeconds(4);
    private DateTime? waitingSince;

    public Executor(IFramework framework, IPluginLog log, Configuration configuration, CostLedger costLedger, Restocker.Market.MarketCache? marketCache = null)
    {
        this.framework = framework;
        this.log = log;
        this.configuration = configuration;
        this.costLedger = costLedger;
        this.marketCache = marketCache;
        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    public void StartRefreshAll()
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        var retainers = ActiveRetainersInDisplayOrder();
        jobs.Clear();
        foreach (var r in retainers)
        {
            jobs.Add(new RetainerVisitJob { RetainerName = r, Actions = new List<PlannedAction>() });
        }
        Begin(ExecutionMode.RefreshAll);
    }

    /// <summary>
    /// 指定リテイナーの SellList を開いて、画面表示 row 0..N を順番に click → ComparePrices
    /// → MarketCache 更新 を走らせる。snapshot の slot index は画面 row と一致しないので
    /// (FFXIV 側のソートで揺れる) 行順に走らせて、dialog の itemName から item id を
    /// 逆引きして expected を確定する。完了後 <see cref="OnFetchMarketCompleted"/> 発火。
    /// </summary>
    public void StartFetchMarketPricesForRetainer(string retainerKey)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        if (!configuration.Snapshots.TryGetValue(retainerKey, out var snap))
        {
            log.Warning($"[Restocker] StartFetchMarketPricesForRetainer: unknown key {retainerKey}");
            return;
        }
        // Fork: row count and unique items are read from the LIVE market container once the
        // sell list is open (see BuildFetchRowsFromLive), not from the possibly stale snapshot.
        jobs.Clear();
        jobs.Add(new RetainerVisitJob { RetainerName = snap.RetainerName, Actions = new List<PlannedAction>(), FetchPrices = true });
        Begin(ExecutionMode.ApplyActions);
    }

    /// <summary>
    /// Fork: like <see cref="StartFullCycle"/> but only fetches prices for every retainer; nothing is
    /// applied. <see cref="OnFetchJobCompleted"/> fires per retainer so the UI can prefill prices.
    /// </summary>
    public void StartFetchAll()
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        jobs.Clear();
        foreach (var r in ActiveRetainersInDisplayOrder())
            jobs.Add(new RetainerVisitJob { RetainerName = r, Actions = new List<PlannedAction>(), FetchPrices = true });
        Begin(ExecutionMode.ApplyActions);
    }

    /// <summary>
    /// Fork: one pass over every retainer at the bell. For each: open the sell list, fetch
    /// live market prices, plan with <see cref="RepriceRules"/>, apply, close, next.
    /// </summary>
    public void StartFullCycle()
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        jobs.Clear();
        foreach (var r in ActiveRetainersInDisplayOrder())
            jobs.Add(FullCycleJob(r));
        Begin(ExecutionMode.FullCycle);
    }

    private static RetainerVisitJob FullCycleJob(string name) => new()
    {
        RetainerName = name,
        Actions = new List<PlannedAction>(),
        FetchPrices = true,
        AutoPlan = true,
    };

    internal bool StartAutoRetainerCycle(string retainerName)
    {
        if (IsRunning || !Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) return false;
        var key = RetainerHistoryCapture.ActiveKey(out var activeName);
        if (key == null || activeName != retainerName) return false;
        jobs.Clear();
        jobs.Add(FullCycleJob(retainerName));
        Begin(ExecutionMode.FullCycle, key);
        return true;
    }

    internal void EndAutoRetainerCycle(string reason)
    {
        if (autoRetainerKey == null) return;
        try
        {
            if (IsRunning) Stop(reason);
            if (State == ExecutionState.Done || autoRetainerKey != ActiveRetainerKey()) return;

            // Return the menus we opened to AR's quit task, without dismissing its retainer.
            if (postprocessOpenedSellList)
            {
                ContextMenuHelper.Close();
                foreach (var name in new[] { "ItemSearchResult", "RetainerSell", "RetainerSellList" })
                {
                    var addon = AddonHelper.GetVisible(name);
                    if (addon != null) Callback.Fire(addon, true, -1);
                }
            }
            if (historyRetainerKey != null)
            {
                var history = AddonHelper.GetVisible("RetainerHistory");
                if (history != null) history->Close(true);
            }
        }
        finally { autoRetainerKey = null; }
    }

    internal void InterruptAutoRetainerCycle()
    {
        if (autoRetainerKey == null) return;
        autoRetainerKey = null;
        if (IsRunning) Stop(Strings.AutoRetainerCycleStopped);
    }

    // ---------------- Fork: restock with price handover ----------------

    private sealed class Handover
    {
        public required string RetainerName { get; init; }
        public required uint ItemId { get; init; }
        public required bool IsHq { get; init; }
        public required int Quantity { get; init; }
        public required List<PlannedAction> SameRetainerFollowUps { get; init; }
        public required List<PlannedAction> AllFollowUps { get; init; }
        public HashSet<int> SlotsBefore { get; } = new();
        public bool Staged;
        public bool Done;
    }

    private Handover? handover;

    /// <summary>True while the game's sale window is waiting for the user.</summary>
    public bool WaitingForUserPrice => State == ExecutionState.HandoverWaitUser;

    /// <summary>
    /// Fork: open the game's own "Put up for sale" window for the first planned listing (quantity
    /// pre-set, price left to the user) and wait. When the user confirms, the new listing's price is
    /// read back and used for the remaining listings. Nothing is listed by Restocky until then.
    /// </summary>
    public void StartRestockWithHandover(List<PlannedAction> actions)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        if (actions.Count == 0) return;
        var first = actions[0];
        if (!configuration.Snapshots.TryGetValue(first.RetainerKey, out var snap)) return;
        var rest = actions.Skip(1).ToList();
        foreach (var a in rest) a.UnitPrice = 0; // set once the user has priced the first one
        handover = new Handover
        {
            RetainerName = snap.RetainerName,
            ItemId = first.ItemId,
            IsHq = first.IsHQ,
            Quantity = first.Quantity,
            SameRetainerFollowUps = rest.Where(a => a.RetainerKey == first.RetainerKey).ToList(),
            AllFollowUps = rest,
        };

        jobs.Clear();
        jobs.Add(new RetainerVisitJob { RetainerName = snap.RetainerName, Actions = new List<PlannedAction>() });
        foreach (var grp in rest.Where(a => a.RetainerKey != first.RetainerKey).GroupBy(a => a.RetainerKey))
        {
            if (!configuration.Snapshots.TryGetValue(grp.Key, out var s2)) continue;
            jobs.Add(new RetainerVisitJob { RetainerName = s2.RetainerName, Actions = grp.ToList() });
        }
        Begin(ExecutionMode.ApplyActions);
        if (!IsRunning) handover = null;
    }

    private bool HandoverDueForCurrentJob()
        => handover is { Done: false } h && jobs.Count > jobCursor && jobs[jobCursor].RetainerName == h.RetainerName;

    /// <summary>Find the item in the bag (or this retainer's inventory) and right-click it via the agent.</summary>
    private void TickHandoverOpenContext()
    {
        var h = handover!;
        var im = InventoryManager.Instance();
        if (im == null) { FailHandover("inventory not available"); return; }

        h.SlotsBefore.Clear();
        foreach (var l in ReadLiveListings()) h.SlotsBefore.Add(l.Slot);

        InventoryType[] order =
        {
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
        };
        foreach (var t in order)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var it = c->GetInventorySlot(i);
                if (it == null || it->ItemId != h.ItemId) continue;
                if (((it->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != h.IsHq) continue;
                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
                if (agent == null) { FailHandover("inventory context not available"); return; }
                // Owner addon id 0, as MarketDumper does (upstream passed RetainerSellList's id and
                // found the "Put up for sale" entry unreliable).
                agent->OpenForItemSlot(t, i, 0, 0);
                log.Information($"[Restocker] handover: context menu for item={h.ItemId} at {t}#{i}");
                State = ExecutionState.HandoverAwaitContext;
                waitingSince = DateTime.UtcNow;
                Throttle();
                return;
            }
        }

        // Only in the saddlebag: move one stack to the bag first (saddlebag items have no sale entry).
        if (!h.Staged && StageOneSaddleSlot(h.ItemId, h.IsHq))
        {
            h.Staged = true;
            Wait(SaddleSettleAfterStage);
            return;
        }
        FailHandover("item not found in bag, this retainer's inventory or saddlebag");
    }

    private void TickHandoverAwaitContext()
    {
        if (AddonHelper.IsOpen("ContextMenu") && ContextMenuHelper.ClickEntry(AddonText.IsPutUpForSaleEntry))
        {
            State = ExecutionState.HandoverAwaitDialog;
            waitingSince = DateTime.UtcNow;
            Throttle();
            return;
        }
        waitingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(3))
        {
            var entries = AddonHelper.IsOpen("ContextMenu") ? string.Join(" | ", ContextMenuHelper.EnumerateEntries()) : "(no menu)";
            if (AddonHelper.IsOpen("ContextMenu")) ContextMenuHelper.Close();
            FailHandover($"'Put up for sale' entry not found ({entries})");
        }
    }

    private void TickHandoverAwaitDialog()
    {
        var h = handover!;
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null)
        {
            waitingSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(6)) FailHandover("sale window did not open");
            return;
        }
        // Quantity only; the price is the user's (event 3 = quantity, as upstream's sell dialog path).
        Callback.Fire(addon, true, 3, h.Quantity);
        StatusMessage = "set the price in the game's sale window and confirm";
        Plugin.ChatPrint($"set the price in the sale window and confirm; Restocky lists the rest ({h.AllFollowUps.Count}) at that price.");
        State = ExecutionState.HandoverWaitUser;
        waitingSince = null;
    }

    /// <summary>The window is the user's. When it closes, look for the new listing and read its price.</summary>
    private void TickHandoverWaitUser()
    {
        if (AddonHelper.IsOpen("RetainerSell") || AddonHelper.IsOpen("ItemSearchResult")) return;
        if (!AddonHelper.IsOpen("RetainerSellList")) { AwaitRetainerMenu("RetainerSellList"); return; }
        var h = handover!;
        var added = ReadLiveListings().FirstOrDefault(l => !h.SlotsBefore.Contains(l.Slot) && l.ItemId == h.ItemId && l.IsHQ == h.IsHq);
        if (added.ItemId == 0)
        {
            // The server may need a moment to show the new listing.
            waitingSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - waitingSince.Value < TimeSpan.FromSeconds(3)) return;
            Plugin.ChatPrint("sale window closed without a new listing; nothing else listed.");
            FinishHandover(price: 0);
            return;
        }
        Plugin.ChatPrint($"price {added.UnitPrice:N0} picked up; listing {h.AllFollowUps.Count} more.");
        FinishHandover(added.UnitPrice);
    }

    private void FailHandover(string why)
    {
        log.Warning($"[Restocker] handover failed: {why}");
        Plugin.ChatPrint($"List & price: {why}. Nothing was listed.");
        FinishHandover(price: 0);
    }

    private void FinishHandover(long price)
    {
        var h = handover!;
        h.Done = true;
        StatusMessage = null;
        waitingSince = null;
        foreach (var a in h.AllFollowUps) a.UnitPrice = price; // 0 => ExecuteDirectListing skips them
        if (price > 0)
        {
            listedRetainerKey = ActiveRetainerKey();
            foreach (var a in h.SameRetainerFollowUps) currentJobActions.Enqueue(a);
            TotalActions += h.SameRetainerFollowUps.Count;
            var work = runEta!.WorkFor(CurrentJob);
            runEta.SetWork(CurrentJob, work with { Listings = work.Listings + h.SameRetainerFollowUps.Count },
                runClock.Elapsed, discovered: true);
        }
        State = ExecutionState.PerformingAction;
        Wait(TimeSpan.FromMilliseconds(500));
    }

    public void StartApplyActions(IEnumerable<PlannedAction> actions)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        // RetainerKey ごとにグループ化、各リテイナー名を解決
        jobs.Clear();
        foreach (var grp in actions.GroupBy(a => a.RetainerKey))
        {
            if (!configuration.Snapshots.TryGetValue(grp.Key, out var snap))
            {
                log.Warning($"[Restocker] unknown retainer key in plan: {grp.Key}");
                continue;
            }
            jobs.Add(new RetainerVisitJob
            {
                RetainerName = snap.RetainerName,
                Actions = grp.OrderBy(a => a.Kind).ThenBy(a => a.ListingIndex).ToList(),
            });
        }
        Begin(ExecutionMode.ApplyActions);
    }

    private void Begin(ExecutionMode mode, string? postprocessKey = null)
    {
        Mode = mode;
        if (handover is { Done: true }) handover = null;
        autoRetainerKey = postprocessKey;
        postprocessOpenedSellList = false;
        if (mode == ExecutionMode.FullCycle) baitMatches.BeginCycle();
        collectAutoRestock = mode == ExecutionMode.FullCycle && postprocessKey == null
            && configuration.AutoRestockDuringCycle && configuration.LowStock.Count > 0;
        autoRestockPass = false;
        pendingReprice = null;
        autoRestockSnapshots.Clear();
        autoRestockResults.Clear();
        historyRequested.Clear();
        historyRetainerKey = null;
        jobCursor = 0;
        CompletedJobs = 0;
        CompletedActions = 0;
        partialFirstVisit = postprocessKey != null;
        InitializeEta();
        runClock.Restart();
        cancelRequested = false;
        StatusMessage = null;
        waitingSince = null;
        usedMarketSlots.Clear();
        fetchRows = null;
        fetchRowsPending = false;
        fetchContextMenuRetry.Reset();
        RepricesApplied = 0;
        AppliedReprices.Clear();
        RepricesSkipped = 0;
        skippedSummaryRows.Clear();
        ListingsHeld = 0;
        if (jobs.Count == 0) { State = ExecutionState.Done; return; }

        if (postprocessKey != null)
        {
            State = ExecutionState.AwaitingSelectString;
            Throttle();
            return;
        }

        // smart-start: 既に対象リテイナーの SellList を開いている場合は
        // ベル巡回をスキップして直接 PerformingAction から走る
        if (AddonHelper.IsOpen("RetainerSellList"))
        {
            var rm = RetainerManager.Instance();
            var active = rm != null ? rm->GetActiveRetainer() : null;
            var activeName = active != null ? active->NameString : null;
            if (handover is { Done: false } h && activeName != h.RetainerName)
            {
                Stop("open the planned pricing retainer or return to the bell list");
                return;
            }
            if (RetainerVisitJob.StartAtActive(jobs, activeName))
            {
                partialFirstVisit = true;
                EnterJob();
                State = jobs[0].NeedsSaddleStaging ? ExecutionState.PreStagingSaddle : ExecutionState.PerformingAction;
                log.Info($"[Restocker] Executor start (smart): mode={mode}, jobs={jobs.Count}, actions={TotalActions}, starting at active retainer '{activeName}'");
                return;
            }
        }

        State = ExecutionState.SelectingRetainer;
        log.Info($"[Restocker] Executor start: mode={mode}, jobs={jobs.Count}, actions={TotalActions}");
    }

    private void InitializeEta()
    {
        var characterId = Plugin.PlayerState.ContentId;
        var work = new Dictionary<RetainerVisitJob, RunEtaWork>();
        var timings = configuration.RunEtaTimings;
        TotalActions = 0;
        foreach (var job in jobs)
        {
            var snapshot = configuration.Snapshots.Values.FirstOrDefault(s =>
                s.CharacterContentId == characterId && s.RetainerName == job.RetainerName);
            var estimate = RunEtaWork.ForJob(job, timings, snapshot?.Listings ?? [], configuration.IsHeld);
            work.Add(job, estimate);
            TotalActions += estimate.Rows + estimate.Reprices + job.Actions.Count(a => a.Kind != PlannedActionKind.Reprice);
        }
        runEta = new RunEta(timings, work);
    }

    private void FinishVisit()
    {
        var learnOverhead = !partialFirstVisit && handover?.RetainerName != CurrentJob.RetainerName;
        runEta!.FinishVisit(CurrentJob, runClock.Elapsed, learnOverhead);
        partialFirstVisit = false;
        CompletedJobs++;
    }

    private void SaveEtaTimings()
    {
        configuration.RunEtaTimings = runEta!.Timings;
        configuration.Save();
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        cancelRequested = true;
        log.Info("[Restocker] Executor cancel requested");
    }

    private void Tick(IFramework _)
    {
        if (!IsRunning) return;
        if (cancelRequested) { Stop("cancelled"); return; }
        if (DateTime.UtcNow < nextStepNoEarlierThan) return;

        try
        {
            if (autoRetainerKey != null && !configuration.RunFullCycleAfterAutoRetainer)
            {
                Stop(Strings.AutoRetainerCycleStopped);
                return;
            }
            if (autoRetainerKey != null && (!Plugin.Condition[ConditionFlag.OccupiedSummoningBell]
                || autoRetainerKey != ActiveRetainerKey()))
            {
                Stop(Strings.AutoRetainerRetainerUnavailable);
                return;
            }

            // Fork: retainer greeting / farewell lines ("Talk" box) block every menu until clicked.
            if (TalkHelper.ClickIfVisible())
            {
                log.Debug("[Restocker] advanced Talk dialog");
                waitingSince = null; // the wait timers should not count time spent in dialogue
                Throttle();
                return;
            }

            switch (State)
            {
                case ExecutionState.SelectingRetainer: TickSelectingRetainer(); break;
                case ExecutionState.AwaitingSelectString: TickAwaitingSelectString(); break;
                case ExecutionState.OpeningSellList: TickOpeningSellList(); break;
                case ExecutionState.AwaitingSellList: TickAwaitingSellList(); break;
                case ExecutionState.PerformingAction: TickPerformingAction(); break;
                case ExecutionState.ReadingPrices: TickReadingPrices(); break;
                case ExecutionState.AwaitingSellDialogForReading: TickAwaitingSellDialogForReading(); break;
                case ExecutionState.AwaitingSellListAfterReading: TickAwaitingSellListAfterReading(); break;
                case ExecutionState.AwaitingContextMenu: TickAwaitingContextMenu(); break;
                case ExecutionState.ClickingPutUpForSale: TickClickingPutUpForSale(); break;
                case ExecutionState.FetchAwaitingContextMenuClose: TickFetchAwaitingContextMenuClose(); break;
                case ExecutionState.AwaitingSellDialog: TickAwaitingSellDialog(); break;
                case ExecutionState.ConfirmingSellDialog: TickConfirmingSellDialog(); break;
                case ExecutionState.AwaitingSaddleMove: TickAwaitingSaddleMove(); break;
                case ExecutionState.PreStagingSaddle: TickPreStagingSaddle(); break;
                case ExecutionState.AwaitingNewListing: TickAwaitingNewListing(); break;
                case ExecutionState.FetchAwaitingSellDialog: TickFetchAwaitingSellDialog(); break;
                case ExecutionState.FetchAwaitingMarketData: TickFetchAwaitingMarketData(); break;
                case ExecutionState.FetchAwaitingSellListAfter: TickFetchAwaitingSellListAfter(); break;
                case ExecutionState.ClosingSellList: TickClosingSellList(); break;
                case ExecutionState.AwaitingSelectStringAfterSell: TickAwaitingSelectStringAfterSell(); break;
                case ExecutionState.AwaitingSaleHistory: TickSaleHistory(); break;
                case ExecutionState.DismissingRetainer: TickDismissingRetainer(); break;
                case ExecutionState.AwaitingDismissed: TickAwaitingDismissed(); break;
                case ExecutionState.HandoverOpenContext: TickHandoverOpenContext(); break;
                case ExecutionState.HandoverAwaitContext: TickHandoverAwaitContext(); break;
                case ExecutionState.HandoverAwaitDialog: TickHandoverAwaitDialog(); break;
                case ExecutionState.HandoverWaitUser: TickHandoverWaitUser(); break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] Executor tick exception");
            Stop($"tick exception: {ex.GetType().Name}");
        }
    }

    private void Throttle() => nextStepNoEarlierThan = DateTime.UtcNow + StepThrottle;
    private void Wait(TimeSpan span) => nextStepNoEarlierThan = DateTime.UtcNow + span;
    private void AwaitRetainerMenu(string name)
    {
        waitingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value >= TimeSpan.FromSeconds(15))
            Stop($"timed out waiting for {name} ({State})");
    }
    private void Stop(string reason)
    {
        State = ExecutionState.Stopped;
        StatusMessage = reason;
        handover = null;
        fetchRows = null;
        fetchRowsPending = false;
        fetchContextMenuRetry.Reset();
        Plugin.Instance?.ClearMarketWatcherExpected();
        if (pendingReprice is { } action)
        {
            pendingReprice = null;
            TickPendingReprice(action);
        }
        CaptureListingsAfterActions();
        PrintAutoRestockResults();
        log.Warning($"[Restocker] Executor stopped: {reason}");
    }

    // ---------------- 状態ごと ----------------

    private void TickSelectingRetainer()
    {
        if (jobCursor >= jobs.Count) { State = ExecutionState.Done; return; }

        var addon = AddonHelper.GetVisible("RetainerList");
        if (addon == null) { Stop("RetainerList not visible"); return; }

        var job = jobs[jobCursor];
        var ordered = ActiveRetainersInDisplayOrder();
        var idx = ordered.IndexOf(job.RetainerName);
        if (idx < 0) { Stop($"retainer '{job.RetainerName}' not in bell list"); return; }

        Callback.Fire(addon, true, 2, (uint)idx, Callback.ZeroAtkValue, Callback.ZeroAtkValue);
        log.Debug($"[Restocker] click retainer #{idx} = {job.RetainerName}");
        State = ExecutionState.AwaitingSelectString;
        Throttle();
    }

    private void TickAwaitingSelectString()
    {
        if (!AddonHelper.IsOpen("SelectString")) { AwaitRetainerMenu("SelectString"); return; }
        if (waitingSince == null) waitingSince = DateTime.UtcNow;

        var idx = SelectStringHelper.FindEntryIndex(AddonText.IsHaveRetainerSellItemsEntry);
        if (idx >= 0)
        {
            var entries = SelectStringHelper.EnumerateEntries();
            var matched = idx < entries.Count ? entries[idx] : "?";
            log.Information($"[Restocker] SelectString matched [{idx}] = '{matched}' (all: {string.Join(" | ", entries)})");
            waitingSince = null;
            State = ExecutionState.OpeningSellList;
            Throttle();
            return;
        }

        if (DateTime.UtcNow - waitingSince.Value > SelectStringTimeout)
        {
            var entries = SelectStringHelper.EnumerateEntries();
            log.Warning($"[Restocker] sell-items entry not found. Entries seen: {string.Join(" | ", entries)}");
            Stop("sell-items entry not found in retainer menu (see /xllog for entries)");
        }
    }

    private void TickOpeningSellList()
    {
        if (!SelectStringHelper.ClickEntry(AddonText.IsHaveRetainerSellItemsEntry)) { AwaitRetainerMenu("sell-items entry"); return; }
        waitingSince = null;
        if (autoRetainerKey != null) postprocessOpenedSellList = true;
        State = ExecutionState.AwaitingSellList;
        Throttle();
    }

    private void TickAwaitingSellList()
    {
        if (!AddonHelper.IsOpen("RetainerSellList")) { AwaitRetainerMenu("RetainerSellList"); return; }
        waitingSince = null;

        // 新しいリテイナーに入ったので市場スロットの占有メモはリセット
        usedMarketSlots.Clear();

        // PostRequestedUpdate で snapshot が拾われるまで一拍待つ
        Wait(SnapshotWait);

        // Refresh / Apply 共通: RetainerWatcher が PostRequestedUpdate で
        // GetRetainerMarketPrice 経由で各 listing の現在価格を埋めてくれるので、
        // ここでは PerformingAction に進めば良い (Refresh は Actions 空 → 即 close)。
        EnterJob();

        // saddle source の NewListing が混じっていれば、必要量を bag に pre-staging
        // してから出品 phase に入る。1 件ずつ stage→listing をやると server で
        // staging のトランザクションを完了する前に出品コマンドが届いて拒否される
        // ことが観測されているため、staging を一括で先に終わらせる方式にした。
        if (jobs[jobCursor].NeedsSaddleStaging)
        {
            log.Information("[Restocker] saddle source detected, entering PreStagingSaddle");
            State = ExecutionState.PreStagingSaddle;
            waitingSince = null;
            return;
        }

        State = ExecutionState.PerformingAction;
    }

    /// <summary>
    /// 現在ジョブの全 NewListing(saddle source) 必要量を集計し、
    /// 既に bag にある分との差分だけ saddle → bag に staging する。
    /// 1 tick = 1 saddle slot 移動 + Wait。すべて満たされたら PerformingAction へ。
    /// </summary>
    private void TickPreStagingSaddle()
    {
        if (pendingStage is { } pending && lastStageAt is { } stagedAt)
        {
            if (TotalInCharBag(pending.Item.ItemId, pending.Item.IsHq) <= pending.InBag)
            {
                // A rejected move can leave the same stack available. Wait for it to settle,
                // then skip that item's staging so an unattended cycle can continue.
                var remaining = SaddleSettleAfterStage - (DateTime.UtcNow - stagedAt);
                if (remaining > TimeSpan.Zero) { Wait(remaining); return; }
                failedStages.Add(pending.Item);
                log.Warning($"[Restocker] saddle move not confirmed: item={pending.Item.ItemId} hq={pending.Item.IsHq}");
            }
            pendingStage = null;
        }

        var canStage = !autoRestockPass || (AddonHelper.IsOpen("RetainerSellList") && currentJobActions.Peek().RetainerKey == ActiveRetainerKey());
        var actions = autoRestockPass ? currentJobActions.Where(a => canStage && CanAutoRestock(a)) : currentJobActions;
        var laterActions = autoRestockPass ? jobs.Skip(jobCursor + 1).SelectMany(j => j.Actions).Where(CanAutoRestock) : null;
        var needs = RestockPlanner.SaddleStagingNeeds(actions, laterActions);

        var im = InventoryManager.Instance();
        if (im == null) { Stop("InventoryManager null"); return; }

        // 1 種類でも不足していれば 1 saddle slot を bag に運んで return
        foreach (var (k, need) in needs)
        {
            if (failedStages.Contains(k)) continue;
            var inBag = TotalInCharBag(k.ItemId, k.IsHq);
            if (inBag >= need) continue;

            log.Information($"[Restocker] pre-stage need item={k.ItemId} hq={k.IsHq} need={need} inBag={inBag}, moving 1 saddle slot");
            if (!StageOneSaddleSlot(k.ItemId, k.IsHq))
            {
                log.Warning($"[Restocker] pre-stage cannot stage item={k.ItemId} hq={k.IsHq} (no saddle slot or no free bag slot). Continuing with what we have.");
                continue;
            }
            lastStageAt = DateTime.UtcNow;
            pendingStage = (k, inBag);
            // server 側のトランザクションを待つ
            Wait(TimeSpan.FromMilliseconds(1000));
            return;
        }

        // 全てのアイテム需要が満たされた → 最後の staging から settle 時間を確保
        if (lastStageAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - lastStageAt.Value;
            if (elapsed < SaddleSettleAfterStage)
            {
                var remaining = SaddleSettleAfterStage - elapsed;
                log.Information($"[Restocker] pre-staging satisfied, settling {remaining.TotalMilliseconds:F0}ms before listings");
                Wait(remaining);
                return;
            }
        }

        log.Information("[Restocker] pre-staging complete, starting listings");
        lastStageAt = null;
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    /// <summary>キャラバッグ内の対象アイテムの合計数量。</summary>
    private int TotalInCharBag(uint itemId, bool isHQ)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var total = 0;
        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                total += (int)item->Quantity;
            }
        }
        return total;
    }

    /// <summary>該当アイテムを持つ saddle slot を 1 つ char bag の空き slot に丸ごと移動。</summary>
    private bool StageOneSaddleSlot(uint itemId, bool isHQ)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;

        foreach (var st in SaddleContainers)
        {
            var sc = im->GetInventoryContainer(st);
            if (sc == null) continue;
            for (var si = 0; si < sc->Size; si++)
            {
                var sit = sc->GetInventorySlot(si);
                if (sit == null || sit->ItemId != itemId) continue;
                var hq = (sit->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                if (sit->Quantity == 0) continue;

                foreach (var bt in CharBagContainers)
                {
                    var bc = im->GetInventoryContainer(bt);
                    if (bc == null) continue;
                    for (var bi = 0; bi < bc->Size; bi++)
                    {
                        var bit = bc->GetInventorySlot(bi);
                        if (bit != null && bit->ItemId == 0)
                        {
                            var ret = im->MoveItemSlot(st, (ushort)si, bt, (ushort)bi, true);
                            log.Information($"[Restocker] pre-stage {st}#{si}({sit->Quantity}) → {bt}#{bi} ret={ret}");
                            return true;
                        }
                    }
                }
                log.Warning("[Restocker] pre-stage: no free char bag slot");
                return false;
            }
        }
        return false;
    }

    private void TickReadingPrices()
    {
        if (slotsToReadPrice.Count == 0)
        {
            // 全 listing 読み終わり → snapshot 保存して closing へ
            if (!string.IsNullOrEmpty(readingSnapshotKey)) configuration.Save();
            readingSnapshotKey = null;
            State = ExecutionState.ClosingSellList;
            Throttle();
            return;
        }

        var slot = slotsToReadPrice.Peek();
        ClickListingSlot(slot);
        State = ExecutionState.AwaitingSellDialogForReading;
        Throttle();
    }

    private void TickAwaitingSellDialogForReading()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) return;
        var sell = (AddonRetainerSell*)addon;

        var price = ReadAskingPrice(sell);
        var slot = slotsToReadPrice.Dequeue();
        if (price > 0 && !string.IsNullOrEmpty(readingSnapshotKey)
            && configuration.Snapshots.TryGetValue(readingSnapshotKey, out var snap))
        {
            var listing = snap.Listings.FirstOrDefault(l => l.ListingIndex == slot);
            if (listing != null) listing.UnitPrice = price;
            log.Debug($"[Restocker] read price slot={slot} value={price}");
        }
        else
        {
            log.Warning($"[Restocker] could not read AskingPrice for slot={slot}");
        }

        // RetainerSell ダイアログを Cancel で閉じる（変更を保存しない）
        Callback.Fire(addon, true, -1);
        State = ExecutionState.AwaitingSellListAfterReading;
        Throttle();
    }

    private void TickAwaitingSellListAfterReading()
    {
        if (AddonHelper.IsOpen("RetainerSell")) return;
        if (!AddonHelper.IsOpen("RetainerSellList")) return;
        State = ExecutionState.ReadingPrices;
        Throttle();
    }

    private static long ReadAskingPrice(AddonRetainerSell* sell)
    {
        if (sell == null || sell->AskingPrice == null) return 0;
        var node = sell->AskingPrice->AtkTextNode;
        if (node == null) return 0;
        var text = node->NodeText.ToString() ?? string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text) if (c >= '0' && c <= '9') sb.Append(c);
        return long.TryParse(sb.ToString(), out var v) ? v : 0;
    }

    // ---------------- Fork: job entry / fetch phase / live reads ----------------

    private bool jobObserved;

    /// <summary>"Ascendant Twine [RetainerA]: 84,999 → 84,998 ↓" for every price written this run.</summary>
    public void PrintRepriceLines()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        foreach (var r in AppliedReprices)
        {
            var name = sheet.TryGetRow(r.ItemId, out var row) ? row.Name.ExtractText() : $"#{r.ItemId}";
            var arrow = r.NewPrice < r.OldPrice ? "↓" : "↑";
            Plugin.ChatPrint($"  {name}{(r.IsHq ? " (HQ)" : "")} [{r.Retainer}]: {r.OldPrice:N0} → {r.NewPrice:N0} {arrow}");
        }
        foreach (var r in skippedSummaryRows)
        {
            var name = sheet.TryGetRow(r.ItemId, out var row) ? row.Name.ExtractText() : $"#{r.ItemId}";
            if (r.Reason is RepriceReason.NoCompetitor or RepriceReason.NoData)
            {
                var reason = r.Reason == RepriceReason.NoData ? Strings.NoMarketData
                    : configuration.MatchQuality.For(r.ItemId, r.Hq) ? Strings.NoCompetitor(r.Hq) : Strings.NoMarketCompetitor;
                Plugin.ChatPrint($"{name} [{r.Retainer} #{r.Slot}] {string.Format(Strings.KeptPrice, r.OldPrice, reason)}");
                continue;
            }
            var skipped = string.Format(Localization.Strings.SkippedPrice, RepriceRules.ReasonCode(r.Reason));
            var percent = RepriceRules.ChangePercent(r.OldPrice, r.NewPrice);
            Plugin.ChatPrint($"  {name}{(r.Hq ? " (HQ)" : "")} [{r.Retainer} #{r.Slot}] {skipped}: {r.OldPrice:N0} → {r.NewPrice:N0} ({percent:+0.##;-0.##;0}%)");
        }
    }

    private void ObserveListingsNow()
    {
        try
        {
            var im = InventoryManager.Instance();
            var market = im != null ? im->GetInventoryContainer(InventoryType.RetainerMarket) : null;
            if (market == null || !market->IsLoaded) return;
            var key = ActiveRetainerKey();
            if (string.IsNullOrEmpty(key)) return;
            var items = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            Plugin.Instance?.Tracker.Tracker.ObserveRetainer(
                key, jobs[jobCursor].RetainerName, ReadLiveListings(),
                id => items.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}",
                DateTime.UtcNow, authoritative: true);
        }
        catch (Exception ex) { log.Warning(ex, "[Restocker] listing observation failed"); }
    }

    private void EnterJob()
    {
        jobObserved = false;
        listedRetainerKey = null;
        lastStageAt = null;
        pendingStage = null;
        failedStages.Clear();
        currentJobActions.Clear();
        foreach (var a in jobs[jobCursor].Actions) currentJobActions.Enqueue(a);
        fetchRows = null;
        fetchRowsPending = jobs[jobCursor].FetchPrices;
    }

    /// <summary>Occupied RetainerMarket slots of the active retainer, read from the game now.</summary>
    private List<LiveListing> ReadLiveListings()
    {
        var result = new List<LiveListing>();
        var im = InventoryManager.Instance();
        if (im == null) return result;
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null || !market->IsLoaded) return result;
        for (var i = 0; i < market->Size; i++)
        {
            var it = market->GetInventorySlot(i);
            if (it == null || it->ItemId == 0) continue;
            result.Add(new LiveListing(
                i,
                it->ItemId,
                (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                (int)it->Quantity,
                (long)im->GetRetainerMarketPrice((short)i)));
        }
        return result;
    }

    private void BuildFetchRowsFromLive()
    {
        fetchRowsPending = false;
        var live = ReadLiveListings();
        var unique = new HashSet<(uint, bool)>();
        foreach (var l in live) unique.Add((l.ItemId, l.IsHQ));
        fetchRows = new FetchRowQueue(live.Count, unique.Count);
        timedOutMarketItems.Clear();
        JobFetchStartUtc = DateTime.UtcNow;
        var work = runEta!.WorkFor(CurrentJob);
        TotalActions += live.Count - work.Rows;
        runEta.SetWork(CurrentJob, work with { Rows = live.Count, Searches = unique.Count }, runClock.Elapsed, discovered: true);
        log.Information($"[Restocker] fetch phase: {jobs[jobCursor].RetainerName} rows={live.Count} unique={unique.Count}");
    }

    /// <summary>All rows walked. Manual fetch: notify the UI. Full cycle: plan + enqueue reprices.</summary>
    private void OnFetchPhaseDone()
    {
        fetchRows = null;
        Plugin.Instance?.ClearMarketWatcherExpected();
        var job = jobs[jobCursor];
        if (!job.AutoPlan)
        {
            try { OnFetchMarketCompleted?.Invoke(); }
            catch (Exception ex) { log.Error(ex, "[Restocker] OnFetchMarketCompleted threw"); }
            try { OnFetchJobCompleted?.Invoke(ActiveRetainerKey()); }
            catch (Exception ex) { log.Error(ex, "[Restocker] OnFetchJobCompleted threw"); }
            return;
        }

        var live = ReadLiveListings();
        var key = ActiveRetainerKey();
        var since = JobFetchStartUtc;
        var plan = AutoRepricePlanner.Plan(
            key,
            job.RetainerName,
            live,
            (item, hq) => marketCache != null && marketCache.HasDataSince(item, since)
                ? marketCache.GetOffers(item, hq, configuration.MatchQuality.For(item, hq))
                : null,
            (itemId, hq) => configuration.EffectiveFloorFor(itemId, hq, costLedger).Price,
            configuration.ToRepriceSettings(),
            DateTime.UtcNow,
            configuration.IsHeld,
            (listing, decision, offers) =>
            {
                var matchQuality = configuration.MatchQuality.For(listing.ItemId, listing.IsHQ);
                var competitor = MarketOfferFormat.RepriceCompetitor(offers, matchQuality, decision.LowestCompetitor);
                var reason = RepriceRules.ReasonCode(decision.Reason);
                var details = $"{job.RetainerName} slot={listing.Slot} item={listing.ItemId} hq={listing.IsHQ} old={listing.UnitPrice:N0} {competitor} target={decision.NewPrice:N0}";
                if (decision.Reason is RepriceReason.MaxDrop or RepriceReason.MaxRaise or RepriceReason.Error)
                    log.Warning($"[Restocker] not applied ({reason}): {details}");
                else
                    log.Information($"[Restocker] reprice decision ({reason}): {details}");
            },
            baitMatches.IncludeBaitForPlan,
            MarketFetchTimedOut);

        lastBaitPrices[key] = plan.BaitPrices;
        ListingsHeld += plan.HeldCount;

        foreach (var row in plan.NotApplied)
        {
            RepricesSkipped++;
            History?.Append(row);
            if (row.Reason is RepriceReason.MaxDrop or RepriceReason.MaxRaise or RepriceReason.NoCompetitor or RepriceReason.NoData)
                skippedSummaryRows.Add(row);
        }
        foreach (var b in plan.Baits)
        {
            if (!reportedBaits.Add(BaitFinder.Key(b))) continue;
            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(b.ItemId, out var row)
                ? row.Name.ExtractText() : $"#{b.ItemId}";
            var msg = BaitFinder.Format(name, b);
            log.Information($"[Restocker] {msg}");
            Plugin.Instance?.PrintBaitMatchLink(msg, name, b);
        }
        foreach (var a in plan.Actions) currentJobActions.Enqueue(a);
        var work = runEta!.WorkFor(job);
        var reprices = currentJobActions.Count(a => a.Kind == PlannedActionKind.Reprice);
        TotalActions += reprices - work.Reprices;
        runEta.SetWork(job, work with { Reprices = reprices }, runClock.Elapsed, discovered: true);
        runEta.LearnRepriceShare(live.Count(l => !configuration.IsHeld(l.ItemId, l.IsHQ)), plan.Actions.Count);
        log.Information($"[Restocker] full cycle plan: {job.RetainerName} listings={live.Count} reprice={plan.Actions.Count} not-applied={plan.NotApplied.Count}");
    }

    private string ActiveRetainerKey()
    {
        var rm = RetainerManager.Instance();
        var active = rm != null ? rm->GetActiveRetainer() : null;
        if (active == null) return string.Empty;
        return RetainerSnapshot.MakeKey(Plugin.PlayerState.ContentId, active->RetainerId);
    }

    private void WriteHistory(PlannedAction a, long newPrice, bool applied, RepriceReason reason)
    {
        History?.Append(new PriceHistoryRow
        {
            UtcTime = DateTime.UtcNow,
            Retainer = jobs.Count > jobCursor ? jobs[jobCursor].RetainerName : string.Empty,
            Slot = a.ListingIndex,
            ItemId = a.ItemId,
            Hq = a.IsHQ,
            Qty = a.Quantity,
            OldPrice = a.OldPrice,
            LowestCompetitor = a.LowestCompetitor,
            NewPrice = newPrice,
            Applied = applied,
            Reason = reason,
            Undercutters = a.Undercutters,
        });
    }

    private void TickPerformingAction()
    {
        if (pendingReprice is { } reprice)
        {
            TickPendingReprice(reprice);
            return;
        }

        // Read the live listings once at the start of the visit. Jobs that add listings
        // capture them again before closing the sell list.
        if (!jobObserved)
        {
            jobObserved = true;
            ObserveListingsNow();
        }

        // Fork: fetch phase runs before the job's actions.
        if (fetchRowsPending) BuildFetchRowsFromLive();
        if (fetchRows != null)
        {
            if (fetchRows.IsEmpty)
            {
                OnFetchPhaseDone();
            }
            else
            {
                // Expected item 0 = ignore every OfferingsReceived until the dialog tells us
                // which item this row really is.
                Plugin.Instance?.SetMarketWatcherExpected(0, false);
                runEta!.StartAction(runClock.Elapsed);
                ClickFetchListingSlot(fetchRows.CurrentRow);
                return;
            }
        }

        // Fork: restock handover after the placeholder listing is confirmed on this retainer.
        if (currentJobActions.Count == 0 && HandoverDueForCurrentJob())
        {
            State = ExecutionState.HandoverOpenContext;
            Wait(TimeSpan.FromMilliseconds(500));
            return;
        }

        // Refresh モードまたは Apply モードでアクション完了 → SellList を閉じる
        if (currentJobActions.Count == 0)
        {
            State = ExecutionState.ClosingSellList;
            Throttle();
            return;
        }

        var action = currentJobActions.Peek();
        runEta!.StartAction(runClock.Elapsed);
        switch (action.Kind)
        {
            case PlannedActionKind.FetchMarketPrice:
                // Fork: fetch rows live in FetchRowQueue now; a queued FetchMarketPrice is a no-op.
                currentJobActions.Dequeue();
                CompletedActions++;
                return;
            case PlannedActionKind.Reprice:
                // InventoryManager.SetRetainerMarketPrice で直接価格を書き換え。
                // RetainerSell ダイアログを開かないので桁違いに速い。
                {
                    currentJobActions.Dequeue();
                    CompletedActions++;
                    if (Mode == ExecutionMode.FullCycle && configuration.IsHeld(action.ItemId, action.IsHQ))
                    {
                        ListingsHeld++;
                        RepricesSkipped++;
                        WriteHistory(action, action.OldPrice, false, RepriceReason.Held);
                        runEta.FinishReprice(CurrentJob, runClock.Elapsed, StepThrottle);
                        Throttle();
                        return;
                    }
                    if (!ValidWritePrice(action))
                    {
                        log.Warning($"[Restocker] reprice skipped: price {action.UnitPrice:N0} is below the floor or outside the market API range");
                        RepricesSkipped++;
                        WriteHistory(action, action.UnitPrice, false, RepriceReason.Error);
                        runEta.FinishReprice(CurrentJob, runClock.Elapsed, StepThrottle);
                        Throttle();
                        return;
                    }
                    var im = InventoryManager.Instance();
                    // Fork: the slot must still hold the item the plan was made for. A stale
                    // snapshot used to reprice whatever item had moved into that slot.
                    if (!TryReadRepriceSlot(im, action, out var livePrice)
                        || (Mode == ExecutionMode.FullCycle && livePrice != action.OldPrice))
                    {
                        log.Warning($"[Restocker] reprice skipped: could not verify {action.RetainerKey} slot={action.ListingIndex} item={action.ItemId} hq={action.IsHQ}");
                        RepricesSkipped++;
                        WriteHistory(action, action.UnitPrice, false, RepriceReason.Error);
                        runEta.FinishReprice(CurrentJob, runClock.Elapsed, StepThrottle);
                        Throttle();
                        return;
                    }
                    if (action.OldPrice == 0) action.OldPrice = livePrice;

                    im->SetRetainerMarketPrice((short)action.ListingIndex, (uint)action.UnitPrice);
                    log.Information($"[Restocker] SetRetainerMarketPrice slot={action.ListingIndex} item={action.ItemId} hq={action.IsHQ} {action.OldPrice:N0} -> {action.UnitPrice:N0} ({RepriceRules.ReasonCode(action.Reason)})");
                    pendingReprice = action;
                    waitingSince = DateTime.UtcNow;
                    Throttle();
                    return;
                }
            case PlannedActionKind.NewListing:
                // 新規出品は MoveToRetainerMarket の **直接 API** で 1 ショットで処理する。
                // ContextMenu 経由は「マーケットに出品」エントリが現在の FFXIV 環境で
                // 出ない条件があり信頼できない (確認: 7.x で表示されないケースあり)。
                // 直接 API なら RetainerSellList が開いた状態 + リテイナー召喚中で
                // server 側でアイテム移動と出品が同時にコミットされる。
                {
                    var sellList = AddonHelper.GetVisible("RetainerSellList");
                    var retLarge = AddonHelper.GetVisible("InventoryRetainerLarge");
                    log.Information($"[Restocker] NewListing: RetainerSellList visible={sellList != null}, InventoryRetainerLarge visible={retLarge != null}, item={action.ItemId} hq={action.IsHQ} qty={action.Quantity} price={action.UnitPrice} src={action.SourceKey}");
                    if (sellList == null || (autoRestockPass && (!CanAutoRestock(action) || ActiveRetainerKey() != action.RetainerKey)))
                    {
                        log.Warning("[Restocker] NewListing: sell list, target retainer, or auto-restock settings no longer permit listing");
                        currentJobActions.Dequeue();
                        CompletedActions++;
                        runEta.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
                        Throttle();
                        return;
                    }

                    // saddle source は AwaitingSellList → PreStagingSaddle で
                    // 必要量を bag に運んでからこの phase に入っている前提なので、
                    // 個別 staging はもう不要。bag に該当アイテムが無ければ skip。
                    if (!ExecuteDirectListing(action, suppressWarning: false, dstSlotOut: out var dstSlot))
                    {
                        log.Warning($"[Restocker] NewListing skip (direct listing failed): item={action.ItemId} hq={action.IsHQ}");
                        currentJobActions.Dequeue();
                        CompletedActions++;
                        runEta.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
                        Throttle();
                        return;
                    }
                    // server 反映を確認するまで次に進まない (race condition で動作不良になるため)
                    listedRetainerKey = action.RetainerKey;
                    pendingListingSlot = dstSlot;
                    pendingListingItemId = action.ItemId;
                    pendingListingIsHQ = action.IsHQ;
                    pendingListingQuantity = action.Quantity;
                    State = ExecutionState.AwaitingNewListing;
                    waitingSince = DateTime.UtcNow;
                    Throttle();
                    return;
                }
        }
        Stop($"unknown action kind {action.Kind}");
    }

    private bool TryReadRepriceSlot(InventoryManager* im, PlannedAction action, out long price)
    {
        price = 0;
        if (im == null || ActiveRetainerKey() != action.RetainerKey) return false;
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null || !market->IsLoaded || action.ListingIndex < 0 || action.ListingIndex >= market->Size)
            return false;
        var slot = market->GetInventorySlot(action.ListingIndex);
        if (slot == null || slot->ItemId != action.ItemId) return false;
        var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        if (isHq != action.IsHQ) return false;
        price = (long)im->GetRetainerMarketPrice((short)action.ListingIndex);
        return true;
    }

    private void TickPendingReprice(PlannedAction action)
    {
        var sameSlot = TryReadRepriceSlot(InventoryManager.Instance(), action, out var price);
        waitingSince ??= DateTime.UtcNow;
        var readback = RepriceReadbackCheck.Check(action.UnitPrice, sameSlot ? price : null, DateTime.UtcNow - waitingSince.Value, IsRunning);
        if (readback == RepriceReadback.Waiting) return;
        if (IsRunning) runEta!.FinishReprice(CurrentJob, runClock.Elapsed);
        pendingReprice = null;
        waitingSince = null;
        if (readback == RepriceReadback.Unconfirmed)
        {
            log.Warning($"[Restocker] reprice not confirmed for {action.RetainerKey} slot={action.ListingIndex} item={action.ItemId} hq={action.IsHQ}");
            RepricesSkipped++;
            WriteHistory(action, action.UnitPrice, false, RepriceReason.Error);
            return;
        }

        if (action.MatchedBait) baitMatches.Consume(action.ItemId, action.IsHQ);
        if (action.BypassedMaxDrop)
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var name = sheet.TryGetRow(action.ItemId, out var row) ? row.Name.ExtractText() : $"#{action.ItemId}";
            if (action.IsHQ) name += " (HQ)";
            var drop = -RepriceRules.ChangePercent(action.OldPrice, price);
            Plugin.ChatPrint(string.Format(Localization.Strings.BaitMatchMaxDrop, name, drop));
        }

        if (configuration.Snapshots.TryGetValue(action.RetainerKey, out var snapshot)
            && snapshot.TryUpdateListingPrice(action.ListingIndex, action.ItemId, action.IsHQ, price))
            configuration.Save();
        else
            Plugin.Instance?.RetainerWatcher.CaptureActiveRetainerSnapshot();

        RepricesApplied++;
        AppliedReprices.Add((jobs.Count > jobCursor ? jobs[jobCursor].RetainerName : "?",
            action.ItemId, action.IsHQ, action.OldPrice, price));
        WriteHistory(action, price, true, action.Reason);
        Plugin.Instance?.Tracker.Tracker.OnRepriceApplied(
            action.RetainerKey, action.ListingIndex, action.ItemId, action.IsHQ, price);
    }

    /// <summary>
    /// RetainerSell ダイアログの ItemName ノードのテキストから item id を逆引き。
    /// HQ アイコン付きなら HQ 判定、戻り値の itemId が 0 ならマッチ無し。
    /// 起動時に Lumina の Item シートを name → id でメモ化する。
    /// </summary>
    private static Dictionary<string, uint>? itemNameToIdCache;
    private static List<KeyValuePair<string, uint>>? itemNameByLengthDesc;

    private static (uint itemId, bool isHq) ResolveItemFromDialogName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (0, false);
        var isHq = DialogItemName.IsHq(raw);
        var stripped = DialogItemName.Clean(raw);

        if (itemNameToIdCache == null)
        {
            itemNameToIdCache = new Dictionary<string, uint>();
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                foreach (var row in sheet)
                {
                    var name = row.Name.ExtractText();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!itemNameToIdCache.ContainsKey(name)) itemNameToIdCache[name] = row.RowId;
                }
                itemNameByLengthDesc = itemNameToIdCache.OrderByDescending(p => p.Key.Length).ToList();
            }
            catch { }
        }
        return (DialogItemName.Match(stripped, itemNameToIdCache, itemNameByLengthDesc ?? new()), isHq);
    }

    /// <summary>
    /// Fork: decode the node as a SeString so macro payloads (colour / glow) disappear instead of
    /// leaking printable bytes like "H%I&...IH" into the name. Falls back to the raw text.
    /// </summary>
    private static string ReadNodeText(AtkTextNode* node)
    {
        if (node == null) return string.Empty;
        try
        {
            return Dalamud.Game.Text.SeStringHandling.SeString.Parse(node->NodeText.AsSpan()).TextValue;
        }
        catch
        {
            return node->NodeText.ToString() ?? string.Empty;
        }
    }

    private void ClickListingSlot(int slot)
    {
        var addon = AddonHelper.GetVisible("RetainerSellList");
        if (addon == null) { Stop("RetainerSellList not visible at click slot"); return; }
        // RetainerSellList の listing 行 click は 3 引数 (case, slot, 1)。
        // 第 3 引数のフラグが無いと addon 側で row click として解釈されず無視される。
        Callback.Fire(addon, true, 0, slot, 1);
        log.Information($"[Restocker] click listing slot {slot} on RetainerSellList");
    }

    private void TickAwaitingSellDialog()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) return;
        var sell = (AddonRetainerSell*)addon;

        var action = currentJobActions.Peek();
        // ECommons 流: AskingPrice 設定は Callback.Fire(addon, true, 2, value)
        Callback.Fire(addon, true, 2, (int)Math.Min(action.UnitPrice, int.MaxValue));
        if (action.Kind == PlannedActionKind.NewListing)
        {
            // Quantity は event 3 で設定
            Callback.Fire(addon, true, 3, action.Quantity);
        }
        log.Information($"[Restocker] RetainerSell set price={action.UnitPrice} qty={action.Quantity}");
        State = ExecutionState.ConfirmingSellDialog;
        Throttle();
    }

    private void TickAwaitingContextMenu()
    {
        if (!AddonHelper.IsOpen("ContextMenu"))
        {
            if (waitingSince == null) waitingSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(3))
            {
                // Fork: a retry that fails to reopen the menu still spends a retry for this row.
                if (fetchRows != null && fetchContextMenuRetry.RetryCount > 0)
                {
                    HandleMissingAdjustPriceEntry(fetchRows.CurrentRow, "menu did not open");
                    return;
                }

                // 1 件詰まっても残り全部を巻き込みたくないので、stop ではなく skip。
                log.Warning("[Restocker] ContextMenu did not open, skipping this action");
                fetchContextMenuRetry.Reset();
                SkipContextMenuAction();
            }
            return;
        }
        waitingSince = null;
        State = ExecutionState.ClickingPutUpForSale;
        Throttle();
    }

    private void TickClickingPutUpForSale()
    {
        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        var nextKind = fetchRows != null
            ? PlannedActionKind.FetchMarketPrice
            : currentJobActions.Count > 0 ? currentJobActions.Peek().Kind : PlannedActionKind.NewListing;

        // FetchMarketPrice (= 既存出品の listing 行クリック後) は「価格を変更する」を選ぶ。
        // それ以外 (NewListing 系) は「マーケットに出品する」を選ぶ。
        Predicate<string> predicate = nextKind == PlannedActionKind.FetchMarketPrice
            ? AddonText.IsAdjustPriceEntry
            : AddonText.IsPutUpForSaleEntry;
        var label = nextKind == PlannedActionKind.FetchMarketPrice ? "adjust-price" : "put-up-for-sale";

        if (ContextMenuHelper.ClickEntry(predicate))
        {
            if (nextKind == PlannedActionKind.FetchMarketPrice) fetchContextMenuRetry.Reset();
            waitingSince = null;
            State = nextKind switch
            {
                PlannedActionKind.FetchMarketPrice => ExecutionState.FetchAwaitingSellDialog,
                _ => ExecutionState.AwaitingSellDialog,
            };
            Throttle();
            return;
        }
        if (DateTime.UtcNow - waitingSince!.Value > TimeSpan.FromSeconds(3))
        {
            var entries = string.Join(" | ", ContextMenuHelper.EnumerateEntries());
            if (nextKind == PlannedActionKind.FetchMarketPrice && fetchRows != null)
            {
                HandleMissingAdjustPriceEntry(fetchRows.CurrentRow, entries.Length == 0 ? "empty" : entries);
                return;
            }

            log.Warning($"[Restocker] {label} entry not found in ContextMenu. Entries: {entries}");
            ContextMenuHelper.Close();
            Stop($"{label} entry not found in ContextMenu (see /xllog)");
        }
    }

    // Fork: first clicks and retries use the same listing callback and throttle.
    private void ClickFetchListingSlot(int slot)
    {
        State = ExecutionState.AwaitingContextMenu;
        waitingSince = null;
        ClickListingSlot(slot);
        Throttle();
    }

    private void HandleMissingAdjustPriceEntry(int slot, string entries)
    {
        var decision = fetchContextMenuRetry.OnEntryMissing();
        if (decision == ContextMenuRetryDecision.Retry)
            log.Warning($"[Restocker] adjust-price entry not found (entries: {entries}), retry {fetchContextMenuRetry.RetryCount}/{ContextMenuRetryPolicy.MaxRetries} for slot {slot}");
        else
            log.Warning($"[Restocker] adjust-price entry not found (entries: {entries}), skipping slot {slot}");

        ContextMenuHelper.Close();
        waitingSince = null;
        State = ExecutionState.FetchAwaitingContextMenuClose;
        Throttle();
    }

    private void TickFetchAwaitingContextMenuClose()
    {
        if (AddonHelper.IsOpen("ContextMenu"))
        {
            ContextMenuHelper.Close();
            waitingSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - waitingSince.Value >= TimeSpan.FromSeconds(5))
            {
                fetchContextMenuRetry.Reset();
                SkipContextMenuAction();
                return;
            }
            Throttle();
            return;
        }

        // Fork: exhausting retries resets the row's retry count and leaves it queued until the menu closes.
        if (fetchContextMenuRetry.RetryCount > 0)
            ClickFetchListingSlot(fetchRows!.CurrentRow);
        else
            SkipContextMenuAction();
    }

    private void SkipContextMenuAction()
    {
        if (fetchRows != null)
        {
            fetchRows.SkipCurrentRow();
            FinishEtaRow(StepThrottle);
        }
        else if (currentJobActions.Count > 0) currentJobActions.Dequeue();
        CompletedActions++;
        Plugin.Instance?.ClearMarketWatcherExpected();
        waitingSince = null;
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    /// <summary>
    /// MoveToRetainerMarket で 1 件出品する。
    /// SourceKey は「ヒント」として候補 container 順序に使うだけで、
    /// 結局は実機の InventoryManager 全体から item を見つけたら即発火する。
    /// (UI のセクション操作ミスや snapshot stale を吸収するため。)
    /// </summary>
    private bool ExecuteDirectListing(PlannedAction action) => ExecuteDirectListing(action, suppressWarning: false, dstSlotOut: out _);

    private bool ExecuteDirectListing(PlannedAction action, bool suppressWarning, out int dstSlotOut)
    {
        dstSlotOut = -1;
        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (im == null) return false;

        if (action.Quantity <= 0)
        {
            log.Warning($"[Restocker] ExecuteDirectListing: quantity={action.Quantity} invalid, skipping");
            return false;
        }
        if (!ValidWritePrice(action))
        {
            log.Warning($"[Restocker] ExecuteDirectListing: unitPrice={action.UnitPrice} below the floor or outside the market API range, skipping");
            return false;
        }

        // 出品先の RetainerMarket で空きスロットを探す。
        // usedMarketSlots に入っているスロットは「ついさっき使った」のでサーバ反映前
        // でもターゲットしないことで swap 動作を防ぐ。
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null || (autoRestockPass && !market->IsLoaded)) return false;
        var dstSlot = -1;
        for (var i = 0; i < Math.Min(market->Size, Planner.MaxListingSlots); i++)
        {
            if (usedMarketSlots.Contains(i)) continue;
            var s = market->GetInventorySlot(i);
            if (s == null) continue;
            if (s->ItemId == 0) { dstSlot = i; break; }
        }
        if (dstSlot < 0) { log.Warning("[Restocker] no free RetainerMarket slot"); return false; }

        var sourceKey = string.IsNullOrEmpty(action.SourceKey) ? action.RetainerKey : action.SourceKey;
        var candidates = autoRestockPass ? SourceContainersFor(sourceKey) : ResolveSearchOrder(sourceKey);

        foreach (var t in candidates)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || (autoRestockPass && !c->IsLoaded)) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;

                var price = (uint)action.UnitPrice;
                var srcQtyBefore = item->Quantity;
                im->MoveToRetainerMarket(
                    t, (ushort)i,
                    InventoryType.RetainerMarket, (ushort)dstSlot,
                    (uint)action.Quantity,
                    price);
                usedMarketSlots.Add(dstSlot);
                dstSlotOut = dstSlot;
                // 直後に client 側の市場 slot を確認: ここで反映されないなら API が no-op
                var srcAfter = c->GetInventorySlot(i);
                var marketAfter = market->GetInventorySlot(dstSlot);
                log.Information(
                    $"[Restocker] listed src={t}#{i}(qty before={srcQtyBefore} after={srcAfter->Quantity}) -> market#{dstSlot} (item={marketAfter->ItemId} qty={marketAfter->Quantity}) requested qty={action.Quantity} price={price}");
                return true;
            }
        }

        if (suppressWarning) return false;

        // 失敗時は、各 container に該当 item があれば（qty 不足でも）情報を残す
        log.Warning($"[Restocker] no source slot with itemId={action.ItemId} hq={action.IsHQ} qty>={action.Quantity} (src hint={sourceKey}, searched={string.Join(",", candidates)})");
        foreach (var t in candidates)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var it = c->GetInventorySlot(i);
                if (it == null || it->ItemId == 0) continue;
                if (it->ItemId == action.ItemId)
                {
                    var hq = (it->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                    log.Information($"  found {t}#{i} item={it->ItemId} hq={hq} qty={it->Quantity} (need hq={action.IsHQ} qty>={action.Quantity})");
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 検索順序: source ヒントに沿って優先するが、見つからなければ反対側も walk する。
    /// .saddle の場合は char bag (staging 後) のみ。
    /// </summary>
    private static InventoryType[] ResolveSearchOrder(string sourceKey)
    {
        if (sourceKey.EndsWith(".saddle"))
        {
            // 直接サドルから動かないので staging 後の char bag のみ見る
            return CharBagContainers;
        }
        if (sourceKey.StartsWith("char."))
        {
            // ヒントは char bag、ただし retainer bag にあれば fallback で使う
            return CombineUnique(CharBagContainers, RetainerInventoryContainersStatic);
        }
        // retainer 由来 → retainer bag を優先、見つからなければ char bag も
        return CombineUnique(RetainerInventoryContainersStatic, CharBagContainers);
    }

    private static InventoryType[] CombineUnique(InventoryType[] a, InventoryType[] b)
    {
        var seen = new HashSet<InventoryType>();
        var list = new List<InventoryType>(a.Length + b.Length);
        foreach (var x in a) if (seen.Add(x)) list.Add(x);
        foreach (var x in b) if (seen.Add(x)) list.Add(x);
        return list.ToArray();
    }

    private static readonly InventoryType[] RetainerInventoryContainersStatic =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    };

    private static InventoryType[] SourceContainersFor(string sourceKey)
    {
        if (sourceKey.StartsWith("char."))
        {
            // saddle / bag どちらの場合も MoveToRetainerMarket は char bag からしか動かない。
            // saddle source は staging で char bag に運んでから出品するため、
            // 検索対象も char bag のみ (見つからなければ caller が staging に走る)。
            return new[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
            };
        }
        // retainer source -> retainer's own pages
        return new[]
        {
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
        };
    }

    private static readonly InventoryType[] SaddleContainers =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    private static readonly InventoryType[] CharBagContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    /// <summary>キャラバッグに該当アイテムが minQuantity 以上入った slot があるか。</summary>
    private bool HasItemInCharBag(uint itemId, bool isHQ, int minQuantity)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                if (item->Quantity >= minQuantity) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// サドル → キャラバッグへ **1 action ぶんだけ** スレッディングする。
    /// 連続発射するとサーバ反映が間に合わず IsLoaded フリッピングして
    /// 同じ saddle slot を再ターゲットしてしまうため、per-action にする。
    /// 1 saddle slot を 1 char bag 空きスロットへ MoveItemSlot するだけ。
    /// </summary>
    private bool TryStageSaddleToCharBag(PlannedAction action)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;

        // 1) 該当アイテムを持つ saddle slot を 1 つ探す (qty>0、HQ 一致)
        var saddleType = (InventoryType)0;
        var saddleSlot = -1;
        var saddleQty = 0;
        foreach (var t in SaddleContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity == 0) continue;
                saddleType = t;
                saddleSlot = i;
                saddleQty = (int)item->Quantity;
                break;
            }
            if (saddleSlot >= 0) break;
        }
        if (saddleSlot < 0)
        {
            log.Warning($"[Restocker] saddle stage: no saddle slot has item={action.ItemId} hq={action.IsHQ}");
            return false;
        }

        // 2) 空きキャラバッグスロットを探す
        var bagType = (InventoryType)0;
        var bagSlot = -1;
        foreach (var bt in CharBagContainers)
        {
            var bc = im->GetInventoryContainer(bt);
            if (bc == null) continue;
            for (var bi = 0; bi < bc->Size; bi++)
            {
                var bit = bc->GetInventorySlot(bi);
                if (bit != null && bit->ItemId == 0)
                {
                    bagType = bt;
                    bagSlot = bi;
                    break;
                }
            }
            if (bagSlot >= 0) break;
        }
        if (bagSlot < 0)
        {
            log.Warning("[Restocker] saddle stage: no free char bag slot");
            return false;
        }

        // 3) 1 saddle slot 丸ごと → char bag 空きスロットへ
        var ret = im->MoveItemSlot(saddleType, (ushort)saddleSlot,
            bagType, (ushort)bagSlot, true);
        log.Information($"[Restocker] stage saddle {saddleType}#{saddleSlot}({saddleQty}) → char {bagType}#{bagSlot} ret={ret}");
        waitingSince = null;
        return true;
    }

    // ---------------- FetchMarketPrice flow ----------------

    private void TickFetchAwaitingSellDialog()
    {
        if (fetchRows == null || fetchRows.IsEmpty) { State = ExecutionState.PerformingAction; return; }
        var row = fetchRows.CurrentRow;

        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null)
        {
            if (waitingSince == null)
            {
                waitingSince = DateTime.UtcNow;
                fetchSlotClickRetried = false;
            }
            var elapsed = DateTime.UtcNow - waitingSince.Value;
            // 2.5s 経って開いていなければ 1 回だけ click 再送
            if (!fetchSlotClickRetried && elapsed > TimeSpan.FromSeconds(2.5))
            {
                log.Warning($"[Restocker] RetainerSell did not open within 2.5s; retrying click on row {row}");
                ClickListingSlot(row);
                fetchSlotClickRetried = true;
            }
            if (elapsed > TimeSpan.FromSeconds(6))
            {
                log.Warning($"[Restocker] RetainerSell never opened for row {row}, skipping. (RetainerSellList open={AddonHelper.IsOpen("RetainerSellList")})");
                fetchRows.SkipCurrentRow();
                FinishEtaRow(StepThrottle);
                CompletedActions++;
                waitingSince = null;
                fetchSlotClickRetried = false;
                State = ExecutionState.PerformingAction;
                Throttle();
            }
            return;
        }
        waitingSince = null;
        fetchSlotClickRetried = false;

        // dialog のアイテム名から item id を逆引き。snapshot の slot index は画面 row と
        // 一致しないため、行を click した結果開いた dialog の表示アイテムを真として扱う。
        var sell = (AddonRetainerSell*)addon;
        var dialogName = ReadNodeText(sell->ItemName);
        var (resolvedItemId, resolvedIsHq) = ResolveItemFromDialogName(dialogName);

        // Fork (yagi2/Restocker#1): skip paths only close the dialog. The single row advance
        // happens in TickFetchAwaitingSellListAfter via FetchRowQueue.CompleteCurrentRow.
        var decision = fetchRows.OnDialogResolved(resolvedItemId, resolvedIsHq);
        if (decision != FetchRowDecision.Fetch)
        {
            if (decision == FetchRowDecision.SkipUnresolved)
                log.Warning($"[Restocker] could not resolve item id from dialog name '{dialogName}' (row {row}); skipping");
            else
                log.Information($"[Restocker] skip duplicate fetch for item={resolvedItemId} hq={resolvedIsHq} (row {row})");
            Callback.Fire(addon, true, -1);
            Plugin.Instance?.ClearMarketWatcherExpected();
            Plugin.Instance?.SetMarketWatcherExpected(0, false);
            State = ExecutionState.FetchAwaitingSellListAfter;
            Throttle();
            return;
        }

        fetchItemId = resolvedItemId;
        fetchIsHq = resolvedIsHq;
        Plugin.Instance?.SetMarketWatcherExpected(resolvedItemId, resolvedIsHq);
        searchResultWasClosed = !AddonHelper.IsOpen("ItemSearchResult");
        fetchClickedAtUtc = DateTime.UtcNow;
        runEta!.StartSearch(runClock.Elapsed);

        // ComparePrices を click。AddonRetainerSell の event id 4
        Callback.Fire(addon, true, 4);
        log.Information($"[Restocker] clicked ComparePrices for item={resolvedItemId} hq={resolvedIsHq} (row={row}) dialog='{dialogName}'");
        State = ExecutionState.FetchAwaitingMarketData;
        waitingSince = DateTime.UtcNow;
        Throttle();
    }

    private void TickFetchAwaitingMarketData()
    {
        // Fork: only data written after this ComparePrices click counts. Upstream accepted any
        // cached entry, so a second fetch of the same item returned the previous result instantly.
        var scan = marketCache?.GetScan(fetchItemId);
        var ready = scan?.UpdatedUtc >= fetchClickedAtUtc;
        var sr = AddonHelper.GetVisible("ItemSearchResult");
        if (!ready && searchResultWasClosed && IsEmptyMarketResult((AddonItemSearchResult*)sr, fetchItemId)
            && Plugin.Instance?.MarketWatcher.RecordEmptySearch(fetchItemId) == true)
        {
            scan = marketCache?.GetScan(fetchItemId);
            ready = scan?.UpdatedUtc >= fetchClickedAtUtc;
            log.Information($"[Restocker] ItemSearchResult confirmed an empty board for item={fetchItemId}");
        }
        var timeout = DateTime.UtcNow - fetchClickedAtUtc > TimeSpan.FromSeconds(6);
        if (!ready && !timeout) return;
        runEta!.FinishSearch(runClock.Elapsed);

        if (!ready)
        {
            timedOutMarketItems.Add(fetchItemId);
            log.Warning($"[Restocker] market fetch timeout for item={fetchItemId} hq={fetchIsHq} (ItemSearchResult open={sr != null}); keeping price with no market data");
        }
        else if (scan is { } fresh)
        {
            var key = ActiveRetainerKey();
            var listingScans = Plugin.Instance?.ListingScans;
            foreach (var listing in ReadLiveListings())
            {
                if (listing.ItemId != fetchItemId) continue;
                var offers = fresh.Scan.Competitors(listing.IsHQ, configuration.MatchQuality.For(fetchItemId, listing.IsHQ));
                listingScans?.Record(key, listing.Slot, listing.ItemId, listing.IsHQ,
                    new ListingMarketScan(fresh.UpdatedUtc, listing.UnitPrice, offers));
            }
            log.Information($"[Restocker] market data ready for item={fetchItemId} hq={fetchIsHq}, competitors={fresh.Scan.Competitors(fetchIsHq, true).Count}, lowest={marketCache!.GetLowest(fetchItemId, fetchIsHq, true)}");
        }

        // 1 件分の fetch 完了。expected は 0 (= 全部無視) に戻して次の listing に進む。
        Plugin.Instance?.SetMarketWatcherExpected(0, false);

        // ItemSearchResult を閉じる → RetainerSell に戻る
        if (sr != null) Callback.Fire(sr, true, -1);
        // RetainerSell も Cancel で閉じる (価格変更は別フェーズで SetRetainerMarketPrice 直で行うので)
        var sell = AddonHelper.GetVisible("RetainerSell");
        if (sell != null) Callback.Fire(sell, true, -1);

        State = ExecutionState.FetchAwaitingSellListAfter;
        waitingSince = null;
        Throttle();
    }

    private static bool IsEmptyMarketResult(AddonItemSearchResult* result, uint itemId)
    {
        if (result == null || !result->IsReady || result->Results == null || result->Results->ListLength != 0)
            return false;
        var agent = AgentItemSearch.Instance();
        if (agent == null || agent->ResultItemId != itemId) return false;
        var search = agent->InfoProxyItemSearch;
        if (search == null || search->SearchItemId != itemId || search->WaitingForListings || search->ListingCount != 0)
            return false;

        // The empty list also exists while loading. Require the game's localized terminal message.
        var emptyText = AddonText.NoMarketResults.Trim();
        return emptyText.Length > 0 && (NodeShows(result->ErrorMessage, emptyText) || NodeShows(result->HitsMessage, emptyText));
    }

    private static bool NodeShows(AtkTextNode* node, string text)
        => node != null && node->IsVisible() && ReadNodeText(node).Trim() == text;

    private void TickFetchAwaitingSellListAfter()
    {
        // server が前リクエストの後続バッチを遅延配送してきて ItemSearchResult が
        // 再 open するケースが起きる。ただ visible 待ちにすると永久に止まるので、
        // 見えてたら Cancel を再送し、5 秒経っても閉じなければ強制続行する。
        var sr = AddonHelper.GetVisible("ItemSearchResult");
        var sell = AddonHelper.GetVisible("RetainerSell");
        if (sr != null || sell != null)
        {
            if (waitingSince == null) waitingSince = DateTime.UtcNow;
            var elapsed = DateTime.UtcNow - waitingSince.Value;
            if (elapsed > TimeSpan.FromSeconds(5))
            {
                log.Warning($"[Restocker] post-fetch dialogs still open after 5s (ItemSearchResult={sr != null}, RetainerSell={sell != null}); forcing continue");
                // fall through and advance the queue
            }
            else
            {
                if (sr != null) Callback.Fire(sr, true, -1);
                if (sell != null) Callback.Fire(sell, true, -1);
                return;
            }
        }

        if (!AddonHelper.IsOpen("RetainerSellList")) { AwaitRetainerMenu("RetainerSellList"); return; }

        waitingSince = null;
        if (fetchRows != null)
        {
            // The ONLY row advance for a row whose dialog opened (fetch, duplicate or unresolved).
            var drained = fetchRows.CompleteCurrentRow();
            CompletedActions += 1 + drained;
            FinishEtaRow(FetchRowCooldown);
            if (drained > 0)
                log.Information($"[Restocker] all unique items fetched; draining {drained} duplicate row(s)");
        }

        State = ExecutionState.PerformingAction;
        // 次の listing click までの cooldown。server 側の前リクエストの応答遅延が
        // 次の検索結果に紛れ込むのを抑えるため。
        Wait(FetchRowCooldown);
    }

    private void FinishEtaRow(TimeSpan cooldown)
        => runEta!.FinishRow(CurrentJob, fetchRows!.Remaining, fetchRows.RemainingSearches, runClock.Elapsed, cooldown);

    private void TickAwaitingNewListing()
    {
        var im = InventoryManager.Instance();
        if (im == null) { Stop("InventoryManager null"); return; }
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null) { Stop("RetainerMarket container null"); return; }

        // 直前に出品した market slot に該当アイテムが入って qty が一致したら confirm
        var slot = market->IsLoaded && pendingListingSlot >= 0 && pendingListingSlot < market->Size
            ? market->GetInventorySlot(pendingListingSlot)
            : null;
        if (slot != null && slot->ItemId == pendingListingItemId && slot->Quantity == pendingListingQuantity)
        {
            var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var action = currentJobActions.Peek();
            if (hq == pendingListingIsHQ && (!autoRestockPass || (ActiveRetainerKey() == action.RetainerKey
                && im->GetRetainerMarketPrice((short)pendingListingSlot) == (ulong)action.UnitPrice)))
            {
                listedRetainerKey = action.RetainerKey;
                if (autoRestockPass)
                {
                    var key = (action.ItemId, action.IsHQ);
                    var result = autoRestockResults[key];
                    autoRestockResults[key] = (result.Kind, result.Price, result.Quantity + action.Quantity);
                }
                CompletedActions++;
                currentJobActions.Dequeue();
                runEta!.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
                pendingListingSlot = -1;
                waitingSince = null;
                State = ExecutionState.PerformingAction;
                Throttle();
                return;
            }
        }

        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(8))
        {
            log.Warning($"[Restocker] new listing not confirmed in market#{pendingListingSlot} for item={pendingListingItemId} hq={pendingListingIsHQ} qty={pendingListingQuantity} (server lag or rejected). Skipping.");
            currentJobActions.Dequeue();
            CompletedActions++;
            runEta!.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
            pendingListingSlot = -1;
            waitingSince = null;
            State = ExecutionState.PerformingAction;
            Throttle();
        }
    }

    private void TickAwaitingSaddleMove()
    {
        if (currentJobActions.Count == 0)
        {
            State = ExecutionState.PerformingAction;
            return;
        }
        var action = currentJobActions.Peek();
        var im = InventoryManager.Instance();
        if (im == null) { Stop("InventoryManager null"); return; }

        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;
                waitingSince = null;
                State = ExecutionState.PerformingAction;
                // staging 直後に MoveToRetainerMarket を投げると server がトランザクション
                // を完了する前で出品が暗黙 reject されるケースを観測した
                // (10 件 OK の後 staging 経由になるとすべて confirmation 失敗)。
                // 追加で 1 秒 wait して server 側のコミットを待つ。
                Wait(TimeSpan.FromMilliseconds(1000));
                return;
            }
        }

        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(5))
        {
            Stop("saddle staging timed out (server roundtrip too slow / move failed)");
        }
    }

    private void TickConfirmingSellDialog()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) { Stop("RetainerSell not visible at confirm"); return; }
        var sell = (AddonRetainerSell*)addon;
        // Confirm ボタンを ECommons 流の ReceiveEvent で click (FireCallback だと
        // server 側に届かないことがある)
        var ok = ButtonClick.Click(sell->Confirm, addon);
        if (!ok)
        {
            log.Warning("[Restocker] Confirm click failed");
            Stop("Confirm click failed");
            return;
        }
        log.Information("[Restocker] confirmed RetainerSell dialog");

        var dequeued = currentJobActions.Dequeue();
        CompletedActions++;
        runEta!.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
        // 新規出品の場合、市場スロットは server が次に空いてるところを使うはずなので
        // 我々の usedMarketSlots 追跡は厳密には不要だが、Reprice 用に Reprice 経路は別
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    private void TickClosingSellList()
    {
        CaptureListingsAfterActions();
        var addon = AddonHelper.GetVisible("RetainerSellList");
        if (addon != null && collectAutoRestock && !autoRestockPass && configuration.AutoRestockDuringCycle)
        {
            try
            {
                var snapshot = Plugin.Instance.RetainerWatcher.CaptureActiveRetainerSnapshot(authoritative: true);
                if (snapshot != null && snapshot.RetainerName == jobs[jobCursor].RetainerName)
                    autoRestockSnapshots[snapshot.Key] = snapshot;
            }
            catch (Exception ex) { log.Warning(ex, "[Restocker] auto-restock snapshot capture failed"); }
        }
        if (addon == null)
        {
            // 既に閉じている = OK
            State = ExecutionState.AwaitingSelectStringAfterSell;
            Throttle();
            return;
        }
        Callback.Fire(addon, true, -1);
        log.Debug("[Restocker] closed RetainerSellList");
        State = ExecutionState.AwaitingSelectStringAfterSell;
        Throttle();
    }

    private void CaptureListingsAfterActions()
    {
        var key = listedRetainerKey;
        listedRetainerKey = null;
        if (string.IsNullOrEmpty(key) || key != ActiveRetainerKey() || !AddonHelper.IsOpen("RetainerSellList")) return;
        try
        {
            Plugin.Instance?.RetainerWatcher.CaptureActiveRetainerSnapshot(authoritative: true);
            Plugin.Instance?.RetainerWatcher.CaptureCharacterSnapshot();
        }
        catch (Exception ex) { log.Warning(ex, "[Restocker] snapshot refresh after listing failed"); }
    }

    private void TickAwaitingSelectStringAfterSell()
    {
        if (!AddonHelper.IsOpen("SelectString")) { AwaitRetainerMenu("SelectString"); return; }
        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        if (SelectStringHelper.HasEntry(AddonText.IsQuitEntry))
        {
            waitingSince = null;
            State = ExecutionState.DismissingRetainer;
            TryOpenSaleHistory();
            Throttle();
            return;
        }
        if (DateTime.UtcNow - waitingSince.Value > SelectStringTimeout)
        {
            var entries = SelectStringHelper.EnumerateEntries();
            log.Warning($"[Restocker] quit entry not found. Entries seen: {string.Join(" | ", entries)}");
            Stop("quit entry not found in retainer menu");
        }
    }

    // Fork: by this point the authoritative sell-list read has separated live stacks
    // from missing ones. The next state is dismissal even if this optional read fails.
    private void TryOpenSaleHistory()
    {
        historyRetainerKey = null;
        var expected = jobs[jobCursor].RetainerName;
        var reason = SaleHistoryPolicy.SkipReason(Mode, configuration.FetchSaleHistory, historyRequested.Contains(expected));
        if (reason != null)
        {
            log.Information($"[Restocky] history: TryOpenSaleHistory skipped {reason}");
            return;
        }
        try
        {
            var capture = Plugin.Instance.SaleHistory;
            if (!capture.Available)
            {
                log.Information("[Restocky] history: TryOpenSaleHistory skipped capture unavailable");
                return;
            }
            var key = RetainerHistoryCapture.ActiveKey(out var name);
            if (key == null)
            {
                log.Information("[Restocky] history: TryOpenSaleHistory skipped key null");
                return;
            }
            if (name != expected)
            {
                log.Information($"[Restocky] history: TryOpenSaleHistory skipped name mismatch active='{name}' job='{expected}'");
                return;
            }
            historyReceipt = capture.Receipt(key);
            if (!SelectStringHelper.ClickEntry(AddonText.IsSaleHistoryEntry))
            {
                log.Information("[Restocky] history: TryOpenSaleHistory skipped entry not found");
                return;
            }
            historyRequested.Add(name);
            historyRetainerKey = key;
            historyDeadline = DateTime.UtcNow.AddSeconds(5);
            State = ExecutionState.AwaitingSaleHistory;
            log.Information($"[Restocky] history: TryOpenSaleHistory clicked retainer='{name}'");
        }
        catch (Exception ex) { log.Warning(ex, "[Restocky] history: sale history request failed"); }
    }

    private void TickSaleHistory()
    {
        try
        {
            var arrived = Plugin.Instance.SaleHistory.Receipt(historyRetainerKey!) != historyReceipt;
            var window = AddonHelper.GetVisible("RetainerHistory");
            if (DateTime.UtcNow < historyDeadline && (!arrived || window == null)) return;
            log.Information($"[Restocky] history: TickSaleHistory {(arrived ? "arrived" : "timed out")} window={(window == null ? "absent" : "visible")}");
            if (window != null) window->Close(true);
        }
        catch (Exception ex) { log.Warning(ex, "[Restocky] history: sale history wait failed"); }
        State = ExecutionState.DismissingRetainer;
        Throttle();
    }

    private void TickDismissingRetainer()
    {
        // A slow reply can open the window after the optional wait has expired.
        if (historyRetainerKey != null)
        {
            try
            {
                var window = AddonHelper.GetVisible("RetainerHistory");
                if (window != null)
                {
                    window->Close(true);
                    AwaitRetainerMenu("RetainerHistory to close");
                    Throttle();
                    return;
                }
            }
            catch (Exception ex) { log.Warning(ex, "[Restocky] Closing sale history failed"); }
        }
        if (autoRetainerKey != null)
        {
            if (AddonHelper.IsOpen("RetainerSellList") || AddonHelper.IsOpen("RetainerSell")
                || AddonHelper.IsOpen("ItemSearchResult") || !SelectStringHelper.HasEntry(AddonText.IsQuitEntry))
            { AwaitRetainerMenu("retainer quit menu"); return; }
            historyRetainerKey = null;
            FinishVisit();
            State = ExecutionState.Done;
            SaveEtaTimings();
            return;
        }
        if (!SelectStringHelper.ClickEntry(AddonText.IsQuitEntry)) { AwaitRetainerMenu("quit entry"); return; }
        waitingSince = null;
        historyRetainerKey = null;
        State = ExecutionState.AwaitingDismissed;
        Throttle();
    }

    private void TickAwaitingDismissed()
    {
        if (AddonHelper.IsOpen("SelectString") || AddonHelper.IsOpen("RetainerSell") || AddonHelper.IsOpen("RetainerSellList")
            || !AddonHelper.IsOpen("RetainerList")) { AwaitRetainerMenu("RetainerList"); return; }
        waitingSince = null;

        FinishVisit();
        jobCursor++;
        if (jobCursor >= jobs.Count)
        {
            if (TryQueueAutoRestock())
            {
                State = ExecutionState.SelectingRetainer;
                Throttle();
                return;
            }
            PrintAutoRestockResults();
            State = ExecutionState.Done;
            SaveEtaTimings();
            log.Info($"[Restocker] Executor done: {CompletedJobs} retainers, {CompletedActions} actions");
            // Full cycles print their own summary + lines (AutoCycleController); everything else here.
            if (Mode != ExecutionMode.FullCycle && AppliedReprices.Count > 0)
            {
                Plugin.ChatPrint($"done: {AppliedReprices.Count} price(s) changed");
                PrintRepriceLines();
            }
            return;
        }
        State = ExecutionState.SelectingRetainer;
        Throttle();
    }

    private bool TryQueueAutoRestock()
    {
        if (!collectAutoRestock || autoRestockPass || !configuration.AutoRestockDuringCycle) return false;
        if (autoRestockSnapshots.Count != jobs.Count)
        {
            Plugin.ChatPrint(Strings.AutoRestockIncompleteSnapshots);
            return false;
        }

        Plugin.Instance.RetainerWatcher.CaptureCharacterSnapshot();
        configuration.Characters.TryGetValue(CharacterSnapshot.MakeKey(Plugin.PlayerState.ContentId), out var character);
        var plans = AutoRestockPlanner.Plan(configuration.LowStock, autoRestockSnapshots.Values, character,
            Plugin.Instance.Tracker.Tracker, marketCache, DateTime.UtcNow, configuration.AutoRestockMaxScanAgeHours,
            configuration.UndercutGil, configuration.IsHeld, AutoRestockFloor, configuration.Snapshots);
        foreach (var plan in plans)
        {
            if (plan.Actions.Count == 0)
                Plugin.ChatPrint(string.Format(Strings.AutoRestockSkipped, AutoRestockItemName(plan.ItemId, plan.IsHq),
                    Strings.AutoRestockPriceLabel(plan.PriceKind)));
            else
                autoRestockResults[(plan.ItemId, plan.IsHq)] = (plan.PriceKind, plan.Actions[0].UnitPrice, 0);
        }

        foreach (var group in plans.SelectMany(p => p.Actions).GroupBy(a => a.RetainerKey))
        {
            var actions = group.ToList();
            var job = new RetainerVisitJob { RetainerName = autoRestockSnapshots[group.Key].RetainerName, Actions = actions };
            jobs.Add(job);
            runEta!.SetWork(job, RunEtaWork.ForJob(job, runEta.Timings, [], configuration.IsHeld),
                runClock.Elapsed, discovered: true);
            TotalActions += actions.Count;
        }
        autoRestockPass = jobCursor < jobs.Count;
        return autoRestockPass;
    }

    private long AutoRestockFloor(uint itemId, bool hq) => configuration.EffectiveFloorFor(itemId, hq, costLedger).Price;

    private bool CanAutoRestock(PlannedAction action) => configuration.AutoRestockDuringCycle
        && configuration.LowStock.Any(e => e.ItemId == action.ItemId && e.IsHq == action.IsHQ)
        && !configuration.IsHeld(action.ItemId, action.IsHQ)
        && ValidWritePrice(action);

    private bool ValidWritePrice(PlannedAction action) =>
        AutoRestockPlanner.IsValidPrice(action.UnitPrice, AutoRestockFloor(action.ItemId, action.IsHQ));

    private static string AutoRestockItemName(uint itemId, bool hq)
    {
        var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var row)
            ? row.Name.ExtractText() : $"#{itemId}";
        return hq ? $"{name} ({Strings.QualityHq})" : name;
    }

    private void PrintAutoRestockResults()
    {
        foreach (var (key, result) in autoRestockResults)
            if (result.Quantity > 0)
                Plugin.ChatPrint(string.Format(Strings.AutoRestocked, AutoRestockItemName(key.ItemId, key.IsHq),
                    result.Quantity, result.Price, Strings.AutoRestockPriceLabel(result.Kind)));
        autoRestockResults.Clear();
    }

    /// <summary>RetainerManager から DisplayOrder 順に並べた現役リテイナー名リスト。</summary>
    public static List<string> ActiveRetainersInDisplayOrder()
    {
        var result = new List<(string Name, int DisplayOrder)>();
        var m = RetainerManager.Instance();
        if (m == null || !m->IsReady) return new List<string>();
        for (var i = 0; i < m->Retainers.Length; i++)
        {
            var r = m->Retainers[i];
            if (r.RetainerId == 0) continue;
            var name = r.NameString;
            if (string.IsNullOrEmpty(name)) continue;
            var displayOrder = m->DisplayOrder.IndexOf((byte)i);
            result.Add((name, displayOrder));
        }
        return result.OrderBy(x => x.DisplayOrder).Select(x => x.Name).ToList();
    }
}
