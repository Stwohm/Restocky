using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Restocker.Common;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly RepriceTab repriceTab;
    private readonly ListTab listTab;
    private readonly SalesTab salesTab = new();
    private readonly CostsTab costsTab;
    private LowStockTab? lowStockTab;
    private readonly MarketCache marketCache;
    private readonly CostLedger costLedger;
    private readonly ConfirmDialog confirmDialog = new();
    private (uint ItemId, int Value, int Frame)? activeFloorInput;

    public MainWindow(Configuration configuration, Executor executor, MarketWatcher marketWatcher, AutoCycleController autoCycle, CostLedger ledger)
        : base($"{Strings.WindowTitle}##MainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.marketCache = marketWatcher.Cache;
        this.costLedger = ledger;
        this.repriceTab = new RepriceTab(configuration, executor, confirmDialog, marketWatcher.Cache, autoCycle, ledger);
        this.listTab = new ListTab(configuration, executor, confirmDialog);
        this.costsTab = new CostsTab(ledger);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(2400, 1600),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        confirmDialog.Draw();
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##restocker-tabs"))
        {
            if (ImGui.BeginTabItem(Strings.TabReprice))
            {
                repriceTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Strings.TabList))
            {
                listTab.Draw();
                ImGui.EndTabItem();
            }
            var lowCount = Plugin.Instance.LowStock.LowItems().Count;
            if (lowCount > 0) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.4f, 1f));
            var lowOpen = ImGui.BeginTabItem(lowCount > 0 ? $"Low stock ({lowCount})###lowstock" : "Low stock###lowstock");
            if (lowCount > 0) ImGui.PopStyleColor();
            if (lowOpen)
            {
                lowStockTab ??= new LowStockTab(configuration, executor, confirmDialog, Plugin.Instance.LowStock);
                lowStockTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Sales"))
            {
                salesTab.Draw();
                ImGui.EndTabItem();
            }
            // Fork: manage purchase and manual cost lots.
            if (ImGui.BeginTabItem(Strings.TabCosts + "###costs"))
            {
                costsTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Strings.TabSettings))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        var snapshots = configuration.Snapshots.Values.ToList();
        var freshest = snapshots.Count == 0
            ? Strings.HeaderUnknown
            : snapshots.Max(s => s.LastRefreshedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        ImGui.TextUnformatted($"{Strings.HeaderCachedRetainers} {snapshots.Count} {Strings.HeaderRetainers} / {Strings.HeaderLastUpdated} {freshest}");

        ImGui.SameLine();
        if (executor.IsRunning)
        {
            var statusLabel = executor.Mode == ExecutionMode.RefreshAll
                ? string.Format(Strings.HeaderRefreshing, executor.CompletedJobs, executor.TotalJobs)
                : string.Format(Strings.ApplyProgress, executor.CompletedActions, executor.TotalActions, executor.CompletedJobs, executor.TotalJobs);
            ImGui.TextUnformatted($"{statusLabel} · {Strings.ProgressEta(executor.EstimatedRemaining)}");
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.HeaderStop)) executor.Cancel();
            if (!string.IsNullOrEmpty(executor.CurrentStateLabel))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{executor.CurrentStateLabel}]");
            }
        }
        else
        {
            var bellOpen = AddonHelper.IsOpen("RetainerList");
            if (!bellOpen) ImGui.BeginDisabled();
            if (ImGui.SmallButton(Strings.HeaderRefreshAll))
            {
                if (Executor.ActiveRetainersInDisplayOrder().Count == 0)
                {
                    Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
                    {
                        Title = Strings.WindowTitle,
                        Content = Strings.ToastNoRetainersInBell,
                        Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
                    });
                }
                else
                {
                    executor.StartRefreshAll();
                }
            }
            if (!bellOpen) ImGui.EndDisabled();
            if (!bellOpen)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(" + Strings.ToastBellNotOpen + ")");
            }
        }

        // Fork: low stock at a glance, without opening the tab.
        var low = Plugin.Instance.LowStock.LowItems();
        if (low.Count > 0)
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var text = string.Join(", ", low.Select(x =>
                $"{(sheet.TryGetRow(x.Entry.ItemId, out var row) ? row.Name.ExtractText() : $"#{x.Entry.ItemId}")}{(x.Entry.IsHq ? " (HQ)" : "")} {x.Listed}"));
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), $"Low stock: {text}");
        }

        // 直近の停止理由を見える化
        if (!string.IsNullOrEmpty(executor.StatusMessage))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.4f, 1f), $"⚠ {executor.StatusMessage}");
        }
    }

    private void DrawSettingsTab()
    {
        var allowTravel = configuration.AllowGilhoundTravelCommands;
        if (ImGui.Checkbox("Allow Gilhound travel commands", ref allowTravel))
        {
            configuration.AllowGilhoundTravelCommands = allowTravel;
            configuration.Save();
        }

        var searchItem = configuration.SearchGilhoundItem;
        if (ImGui.Checkbox("Search Gilhound item when the market board opens", ref searchItem))
        {
            configuration.SearchGilhoundItem = searchItem;
            configuration.Save();
        }

        var autoOpen = configuration.AutoOpenOnBell;
        if (ImGui.Checkbox(Strings.SettingsAutoOpenOnBell, ref autoOpen))
        {
            configuration.AutoOpenOnBell = autoOpen;
            configuration.Save();
        }

        ImGui.Spacing();

        var langIdx = configuration.Language;
        var labels = new[]
        {
            Strings.SettingsLanguageAuto,
            Strings.LanguageNameEnglish,
            Strings.LanguageNameJapanese,
            Strings.LanguageNameGerman,
            Strings.LanguageNameFrench,
            Strings.LanguageNameChinese,
            Strings.LanguageNameKorean,
        };
        // langIdx: -1 = auto / 0..5 = explicit。コンボの 0 番目を auto にマップする。
        var combo = langIdx + 1;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo(Strings.SettingsLanguage, ref combo, labels, labels.Length))
        {
            configuration.Language = combo - 1;
            configuration.Save();
            Strings.SetLanguage(configuration.ResolveLanguage(Plugin.ClientState.ClientLanguage));
        }

        DrawForkSettings();
    }

    // ---------------- Fork (Restocky) settings ----------------

    private string floorFilter = string.Empty;

    private void DrawForkSettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Full / auto cycle");

        var matchDefault = configuration.MatchQuality.Default;
        if (ImGui.Checkbox(Strings.MatchQualityDefault, ref matchDefault))
        {
            configuration.MatchQuality.Default = matchDefault;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.MatchQualityHint);

        var undercut = configuration.UndercutGil;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Undercut (gil below lowest competitor)", ref undercut))
        {
            configuration.UndercutGil = Math.Max(0, undercut);
            configuration.Save();
        }

        var maxDrop = (float)configuration.MaxDropPercent;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Max drop % per change (0 = off)", ref maxDrop, 1f, 5f, "%.0f"))
        {
            configuration.MaxDropPercent = Math.Clamp(maxDrop, 0f, 100f);
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A new price more than this % below the current one is NOT applied; it is logged as max_drop.");

        var maxRaise = (float)configuration.MaxRaisePercent;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat(Strings.MaxRaiseSetting, ref maxRaise, 1f, 5f, "%.2f"))
        {
            configuration.MaxRaisePercent = Math.Max(0f, maxRaise);
            configuration.Save();
        }

        var autoCostFloor = configuration.AutoCostFloor;
        if (ImGui.Checkbox(Strings.AutoCostFloorSetting, ref autoCostFloor))
        {
            configuration.AutoCostFloor = autoCostFloor;
            configuration.Save();
        }

        var saleTax = configuration.SaleTaxPercent;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputDouble(Strings.SaleTaxSetting, ref saleTax, 0.5, 1, "%.2f"))
        {
            configuration.SaleTaxPercent = saleTax;
            configuration.Save();
        }

        var minProfit = configuration.MinProfitPercent;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputDouble(Strings.MinProfitSetting, ref minProfit, 1, 5, "%.2f"))
        {
            configuration.MinProfitPercent = minProfit;
            configuration.Save();
        }

        var history = configuration.FetchSaleHistory;
        if (ImGui.Checkbox(Strings.FetchSaleHistory, ref history))
        {
            configuration.FetchSaleHistory = history;
            configuration.Save();
        }

        var stuckDays = configuration.StuckListingDays;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Strings.StuckSetting, ref stuckDays))
        {
            configuration.StuckListingDays = Math.Max(1, stuckDays);
            configuration.Save();
        }

        var interval = configuration.AutoCycleIntervalMinutes;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Auto cycle interval (minutes)", ref interval))
        {
            configuration.AutoCycleIntervalMinutes = Math.Max(1, interval);
            configuration.Save();
        }

        var jitter = configuration.AutoCycleJitterSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Random jitter (+/- seconds)", ref jitter))
        {
            configuration.AutoCycleJitterSeconds = Math.Max(0, jitter);
            configuration.Save();
        }

        var maxFail = configuration.AutoCycleMaxFailures;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Stop after N failed cycles in a row", ref maxFail))
        {
            configuration.AutoCycleMaxFailures = Math.Max(1, maxFail);
            configuration.Save();
        }

        var afterAutoRetainer = configuration.RunFullCycleAfterAutoRetainer;
        if (ImGui.Checkbox(Strings.RunFullCycleAfterAutoRetainer, ref afterAutoRetainer))
        {
            configuration.RunFullCycleAfterAutoRetainer = afterAutoRetainer;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.AutoRetainerPostprocessHint);

        var autoRestock = configuration.AutoRestockDuringCycle;
        if (ImGui.Checkbox(Strings.AutoRestockSetting, ref autoRestock))
        {
            configuration.AutoRestockDuringCycle = autoRestock;
            configuration.Save();
        }

        var maxScanAge = configuration.AutoRestockMaxScanAgeHours;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Strings.AutoRestockScanAgeSetting, ref maxScanAge))
        {
            configuration.AutoRestockMaxScanAgeHours = Math.Max(0, maxScanAge);
            configuration.Save();
        }

        // Chat channel for Restocky messages (cycle summaries, bait notices, auto-cycle on/off).
        var channels = new[]
        {
            (Dalamud.Game.Text.XivChatType.RetainerSale, "Retainer Sale Notifications"),
            (Dalamud.Game.Text.XivChatType.Echo, "Echo"),
            (Dalamud.Game.Text.XivChatType.SystemMessage, "System Messages"),
            (Dalamud.Game.Text.XivChatType.Notice, "Notice"),
            (Dalamud.Game.Text.XivChatType.Debug, "Debug"),
        };
        var labels = channels.Select(c => c.Item2).ToArray();
        var current = Array.FindIndex(channels, c => (int)c.Item1 == configuration.ChatChannel);
        if (current < 0) current = 0;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Chat channel for Restocky messages", ref current, labels, labels.Length))
        {
            configuration.ChatChannel = (int)channels[current].Item1;
            configuration.Save();
            Plugin.ChatPrint("messages will appear in this channel.");
        }

        ImGui.TextDisabled("Price history: pluginConfigs\\Restocky\\price-history.csv");

        ImGui.Spacing();
        ImGui.TextUnformatted(Strings.PriceFloorSettings);
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##floor-filter", Strings.Filter, ref floorFilter, 64);

        // Items currently listed on any retainer (from snapshots) plus any item that already has a floor.
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var ids = configuration.Snapshots.Values.SelectMany(s => s.Listings).Select(l => l.ItemId)
            .Concat(configuration.PriceFloors.Keys).Concat(configuration.LowStock.Select(e => e.ItemId))
            .Concat(configuration.MatchQuality.NQ.Keys).Concat(configuration.MatchQuality.HQ.Keys).Distinct()
            .Select(id => (Id: id, Name: sheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}"))
            .Where(x => floorFilter.Length == 0 || x.Name.Contains(floorFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name)
            .ToList();

        if (ids.Count == 0)
        {
            ImGui.TextDisabled(Strings.EmptyHint);
            return;
        }
        // Reference columns: our current asking price(s) from the snapshots and the last fetched
        // market lowest (NQ / HQ) from this session's cache.
        var ourPrices = configuration.Snapshots.Values.SelectMany(s => s.Listings)
            .GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => (Min: g.Min(l => l.UnitPrice), Max: g.Max(l => l.UnitPrice)));

        if (!ImGui.BeginTable("##floors", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, System.Math.Max(160f, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Your price", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Market lowest", ImGuiTableColumnFlags.WidthFixed, 130);
        var autoLabel = Strings.ResetFloorToAuto;
        var autoButtonSpace = ImGui.CalcTextSize(autoLabel).X + 2 * ImGui.GetStyle().FramePadding.X
            + ImGui.GetStyle().ItemSpacing.X;
        ImGui.TableSetupColumn("Floor", ImGuiTableColumnFlags.WidthFixed, 120 + autoButtonSpace);
        ImGui.TableSetupColumn(Strings.MatchQuality, ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableHeadersRow();
        var frame = ImGui.GetFrameCount();
        foreach (var (id, name) in ids)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            if (ourPrices.TryGetValue(id, out var ours))
                ImGui.TextUnformatted(ours.Min == ours.Max ? ours.Min.ToString("N0") : $"{ours.Min:N0}–{ours.Max:N0}");
            else
                ImGui.TextDisabled("—");
            ImGui.TableNextColumn();
            var nq = marketCache.GetLowest(id, false, true);
            var hq = marketCache.GetLowest(id, true, true);
            var marketText = (nq > 0 ? nq.ToString("N0") : "") + (hq > 0 ? (nq > 0 ? " / " : "") + hq.ToString("N0") + " HQ" : "");
            if (marketText.Length == 0) ImGui.TextDisabled("not fetched");
            else ImGui.TextUnformatted(marketText);
            ImGui.TableNextColumn();
            var floor = configuration.EffectiveFloorFor(id, false, costLedger);
            var hqFloor = configuration.EffectiveFloorFor(id, true, costLedger);
            if (hqFloor.Price > floor.Price) floor = hqFloor;
            var isManual = floor.Price > 0 && !floor.FromCost;
            var source = floor.FromCost ? Strings.CostFloorTooltip : Strings.ManualFloorTooltip;

            // InputInt can only edit 32-bit values. Also show the exact floor if it exceeds that range.
            var shownValue = (int)Math.Min(floor.Price, int.MaxValue);
            if (floor.Price > int.MaxValue)
                ImGui.TextUnformatted(floor.Price.ToString("N0"));

            // ImGui can copy the active text back on an unchanged frame. Preserve the input
            // value while focused so a changing cost floor cannot be mistaken for a user edit.
            var inputValue = activeFloorInput is { } active && active.ItemId == id && active.Frame == frame - 1
                ? active.Value
                : shownValue;
            var v = inputValue;
            ImGui.SetNextItemWidth(isManual ? Math.Max(1, ImGui.GetContentRegionAvail().X - autoButtonSpace) : -1);
            var edited = ImGui.InputInt($"##floor-{id}", ref v, 0) && v != inputValue;
            if (ImGui.IsItemActive()) activeFloorInput = (id, v, frame);
            if (floor.Price > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(source, floor.Price));

            if (isManual)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"{autoLabel}##floor-auto-{id}"))
                {
                    v = 0;
                    edited = true;
                }
            }

            if (edited && v > 0)
            {
                configuration.PriceFloors[id] = v;
                configuration.Save();
            }
            else if (edited && configuration.PriceFloors.Remove(id))
            {
                configuration.Save();
            }
            ImGui.TableNextColumn();
            DrawMatchQuality(id, false);
            DrawMatchQuality(id, true);
        }
        ImGui.EndTable();
    }

    private void DrawMatchQuality(uint itemId, bool hq)
    {
        var settings = configuration.MatchQuality;
        var overrides = settings.Overrides(hq);
        var value = settings.For(itemId, hq);
        var quality = hq ? "HQ" : "NQ";
        if (ImGui.Checkbox($"{quality}##match-{itemId}", ref value))
        {
            overrides[itemId] = value;
            configuration.Save();
        }
        if (overrides.ContainsKey(itemId))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Strings.UseDefault}##match-{itemId}-{quality}"))
            {
                overrides.Remove(itemId);
                configuration.Save();
            }
        }
    }
}
