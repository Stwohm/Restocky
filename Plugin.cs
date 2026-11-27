using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Restocker.Common;
using Restocker.Windows;
using Restocker.Localization;
using Restocker.Execution;
using Restocker.Market;

namespace Restocker;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    internal RetainerHistoryCapture SaleHistory { get; }

    private readonly Bridge.GilhoundBridge gilhoundBridge;

    private const string CommandName = "/restocky";
    private readonly Dictionary<(uint ItemId, bool IsHq), DalamudLinkPayload> baitMatchLinks = new();

    // Bait link IDs have the high bit set. Price links carry their own item and reviewed price.
    private const uint MatchNqPrice = 0x40000000;
    private const uint MatchHqPrice = MatchNqPrice + 1;
    private readonly DalamudLinkPayload priceLinkNq;
    private readonly DalamudLinkPayload priceLinkHq;

    public static Configuration Configuration { get; private set; } = null!;
    public static Plugin Instance { get; private set; } = null!;
    public RetainerWatcher RetainerWatcher { get; private set; } = null!;
    public readonly WindowSystem WindowSystem = new("Restocky");

    private MainWindow MainWindow { get; }
    private ProgressWindow ProgressWindow { get; }
    private BellWatcher BellWatcher { get; }
    private AutoRetainerDetector ArDetector { get; }
    private Executor Executor { get; }
    internal MarketWatcher MarketWatcher { get; }
    private UndercutHistoryHost Undercuts { get; }
    internal UndercutHistory UndercutHistory => Undercuts.History;
    internal ListingMarketScans ListingScans => Undercuts.ListingScans;
    // Fork: market purchase cost basis.
    private CostLedgerHost Costs { get; }
    private AutoCycleController AutoCycle { get; }
    internal ActivityLog Activity { get; }
    public TrackerHost Tracker { get; }
    public Restocker.Plan.LowStockService LowStock { get; private set; } = null!;

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Strings.SetLanguage(Configuration.ResolveLanguage(ClientState.ClientLanguage));

        Activity = new(System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "activity-log.jsonl"),
            message => Log.Warning($"[Restocky] Activity log: {message}"));
        MarketWatcher = new MarketWatcher(MarketBoard, ObjectTable, Log);
        MarketWatcher.Cache.RestorePrices(Configuration.RestockMarketPrices ?? []);
        Undercuts = new UndercutHistoryHost(MarketWatcher, Configuration, Framework, PlayerState, ObjectTable, Log,
            PluginInterface.GetPluginConfigDirectory());
        Costs = new CostLedgerHost(MarketBoard, Framework, Log, PluginInterface.GetPluginConfigDirectory());
        Tracker = new TrackerHost(Framework, ChatGui, DataManager, Log, PluginInterface.GetPluginConfigDirectory(),
            Configuration, Costs.Ledger.Consume);
        Tracker.Tracker.SaleRecorded += sale => Activity.Add("Sale",
            $"{sale.ItemName}{(sale.IsHq ? " (HQ)" : "")} [{sale.RetainerName}] ×{sale.Quantity:N0} · {sale.SoldTotalGil:N0} gil");
        SaleHistory = new RetainerHistoryCapture(Tracker.Tracker);
        MarketWatcher.CompetitorsObserved += (item, scan) =>
        {
            var now = DateTime.UtcNow;
            var retainers = Configuration.Snapshots.Values.Where(s => s.CharacterContentId == PlayerState.ContentId)
                .Select(s => s.Key).ToHashSet();
            Tracker.Tracker.ObserveCompetitors(item, false, scan.Competitors(false, Configuration.MatchQuality.For(item, false)), now, retainers);
            Tracker.Tracker.ObserveCompetitors(item, true, scan.Competitors(true, Configuration.MatchQuality.For(item, true)), now, retainers);
        };
        Executor = new Executor(Framework, Log, Configuration, Costs.Ledger, MarketWatcher.Cache);
        MarketWatcher.EmptyResultReplaced += Executor.OnEmptyResultReplaced;
        priceLinkNq = ChatGui.AddChatLinkHandler(MatchNqPrice, OnMatchPrice);
        priceLinkHq = ChatGui.AddChatLinkHandler(MatchHqPrice, OnMatchPrice);
        Executor.History = new PriceHistoryLog(
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "price-history.csv"));
        Executor.RunHistory = new RunReportHistory(
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "runs.json"), message => Log.Warning(message), Activity.Add);

        ArDetector = new AutoRetainerDetector(PluginInterface, NotificationManager, Log);
        var arIpc = new AutoRetainerIpc(PluginInterface, Log, () => ArDetector.IsAutoRetainerLoaded());
        AutoCycle = new AutoCycleController(
            Framework, Condition, ObjectTable, ChatGui, Log, Executor, Configuration,
            () => ArDetector.IsAutoRetainerLoaded(), arIpc);

        LowStock = new Restocker.Plan.LowStockService(Framework, Configuration, Tracker.Tracker, MarketWatcher.Cache, Costs.Ledger);
        MainWindow = new MainWindow(Configuration, Executor, MarketWatcher, AutoCycle, Costs.Ledger);
        WindowSystem.AddWindow(MainWindow);
        ProgressWindow = new ProgressWindow(Executor);
        WindowSystem.AddWindow(ProgressWindow);

        RetainerWatcher = new RetainerWatcher(
            Configuration,
            AddonLifecycle,
            PlayerState,
            ObjectTable,
            DataManager,
            Log
        );

        BellWatcher = new BellWatcher(
            Framework,
            GameGui,
            open => MainWindow.IsOpen = open,
            () => Configuration.AutoOpenOnBell,
            () => RetainerWatcher.CaptureCharacterSnapshot()
        );

        ArDetector.WarnIfPresent();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Restocky window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        // 設定タブは MainWindow 内にあるので OpenConfigUi も同じウィンドウへ
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        gilhoundBridge = new(PluginInterface.GetPluginConfigDirectory());
        AutoCycle.ListenForPostprocess();
    }

    public void Dispose()
    {
        AutoCycle.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        ChatGui.RemoveChatLinkHandler(MatchNqPrice);
        ChatGui.RemoveChatLinkHandler(MatchHqPrice);
        foreach (var link in baitMatchLinks.Values)
            ChatGui.RemoveChatLinkHandler(link.CommandId);

        gilhoundBridge.Dispose();
        WindowSystem.RemoveAllWindows();
        LowStock.Dispose();
        MarketWatcher.Dispose();
        try
        {
            Configuration.RestockMarketPrices = MarketWatcher.Cache.SavedPrices();
            Configuration.Save();
        }
        catch (Exception ex) { Log.Warning(ex, "[Restocker] could not save cached market prices"); }
        SaleHistory.Dispose();
        Undercuts.Dispose();
        Costs.Dispose();
        Tracker.Dispose();
        Executor.Dispose();
        BellWatcher.Dispose();
        RetainerWatcher.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    public void ToggleMainUi() => MainWindow.Toggle();

    /// <summary>Fork: every Restocky chat line goes through here, on the configured channel.</summary>
    public static void ChatPrint(string message) => PrintChatMessage($"[Restocky] {message}");

    private static void PrintChatMessage(SeString message)
    {
        try
        {
            ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
            {
                Type = (Dalamud.Game.Text.XivChatType)(Configuration?.ChatChannel ?? (int)Dalamud.Game.Text.XivChatType.RetainerSale),
                Message = message,
            });
        }
        catch (Exception ex) { Log.Warning(ex, "[Restocker] chat print failed"); }
    }

    private Payload BaitLink(uint item, bool hq)
    {
        var key = (ItemId: item, IsHq: hq);
        if (!baitMatchLinks.TryGetValue(key, out var link))
        {
            var displayName = MarketItemName(item, hq);
            link = ChatGui.AddChatLinkHandler(BaitMatchOverrides.LinkId(key.ItemId, key.IsHq), (_, _) =>
            {
                if (Configuration.IsHeld(key.ItemId, key.IsHq)) return;
                Executor.QueueBaitMatch(key.ItemId, key.IsHq);
                ChatPrint(string.Format(Strings.BaitMatchQueued, displayName));
            });
            baitMatchLinks.Add(key, link);
        }

        return link;
    }

    internal static string MarketItemName(uint item, bool hq)
    {
        var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var name = sheet.TryGetRow(item, out var row) ? row.Name.ExtractText() : $"#{item}";
        return hq ? $"{name} (HQ)" : name;
    }

    internal void PrintSummary(IEnumerable<ChatEvent> events)
    {
        foreach (var group in ChatSummary.Format(events))
        {
            var builder = new SeStringBuilder().AddText($"[Restocky] {group.Title} ({group.Lines.Count})");
            foreach (var line in group.Lines)
            {
                var e = line.Event;
                builder.Add(NewLinePayload.Payload).AddText("  ");
                if (e.Item != 0)
                {
                    if (DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().HasRow(e.Item))
                        builder.AddItemLink(e.Item, e.Hq).AddText(" ");
                    else builder.AddText($"#{e.Item} ");
                }
                if (e.Category is ChatCategory.Prices or ChatCategory.Guards)
                    builder.AddUiForeground(line.Text, (ushort)(e.Proposed < e.Old ? 17 : 45));
                else builder.AddText(line.Text);
                if (!e.Match || Configuration.IsHeld(e.Item, e.Hq)) continue;
                builder.AddText(" ");
                if (e.Category == ChatCategory.Bait) builder.Add(BaitLink(e.Item, e.Hq));
                else
                {
                    var handler = e.Hq ? priceLinkHq : priceLinkNq;
                    var encoded = new Lumina.Text.SeStringBuilder().PushDalamudLink(handler.CommandId,
                        handler.Plugin, checked((int)e.Item), e.Floor > 0 ? 1 : 0,
                        e.Proposed.ToString(CultureInfo.InvariantCulture));
                    builder.Add(new RawPayload(encoded.ToReadOnlySeString().Data.ToArray()));
                }
                builder.AddUiForeground(e.Category == ChatCategory.Bait ? $"[{Strings.MatchBait}]" : "[Match]", 45)
                    .Add(RawPayload.LinkTerminator);
            }
            PrintChatMessage(builder.Build());
        }
    }

    private void OnMatchPrice(uint command, SeString message)
    {
        if (message.Payloads.FirstOrDefault() is not DalamudLinkPayload { Extra1: > 0 } link
            || !long.TryParse(link.ExtraString, NumberStyles.None, CultureInfo.InvariantCulture, out var proposed)
            || proposed <= 0) return;
        var item = (uint)link.Extra1;
        var hq = command == MatchHqPrice;
        if (Configuration.IsHeld(item, hq)) return;
        Executor.PriceMatches.Queue(item, hq, proposed, link.Extra2 == 1);
        ChatPrint(string.Format(Strings.PriceMatchQueued, MarketItemName(item, hq), proposed));
    }

    /// <summary>Executor 用: 自動 fetch 中に「期待アイテム」フィルタを設定。</summary>
    public void SetMarketWatcherExpected(uint itemId, bool isHq) => MarketWatcher.SetExpectedItem(itemId, isHq);

    public void ClearMarketWatcherExpected() => MarketWatcher.ClearExpectedItem();
}
