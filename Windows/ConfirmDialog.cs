using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

/// <summary>
/// 「適用」前に必ず通す確認モーダル。対象アクション件数・リテイナー件数・予想時間と
/// 簡単な詳細リストを表示し、ユーザーが OK したら onConfirm を発火する。
/// </summary>
public sealed class ConfirmDialog
{
    private const string PopupId = "##restocker-confirm";

    private bool requestedOpen;
    private List<PlannedAction>? pendingActions;
    private Action<List<PlannedAction>>? onConfirm;

    public void Request(List<PlannedAction> actions, Action<List<PlannedAction>> onConfirm)
    {
        if (actions.Count == 0) return;
        this.pendingActions = actions;
        this.onConfirm = onConfirm;
        this.requestedOpen = true;
    }

    public void Draw()
    {
        if (requestedOpen)
        {
            ImGui.OpenPopup(PopupId);
            requestedOpen = false;
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(420, 220), new System.Numerics.Vector2(720, 600));

        if (!ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (pendingActions == null || pendingActions.Count == 0)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        DrawSummary(pendingActions);
        ImGui.Separator();
        DrawDetails(pendingActions);
        ImGui.Separator();
        DrawButtons();

        ImGui.EndPopup();
    }

    private static void DrawSummary(List<PlannedAction> actions)
    {
        var retainers = actions.Select(a => a.RetainerKey).Distinct().Count();
        var reprice = actions.Count(a => a.Kind == PlannedActionKind.Reprice);
        var newListing = actions.Count(a => a.Kind == PlannedActionKind.NewListing);
        // 1 アクション ~3 秒 + 1 リテイナー ~5 秒オーバーヘッドの粗い見積もり
        var estimateSec = actions.Count * 3 + retainers * 5;

        ImGui.TextUnformatted(string.Format(Strings.ConfirmTitle, actions.Count, retainers));
        ImGui.TextUnformatted(string.Format(Strings.ConfirmBreakdown, reprice, newListing));
        var topUps = actions.Count(a => a.Kind == PlannedActionKind.TopUp);
        if (topUps > 0) ImGui.TextUnformatted(string.Format(Strings.TopUpCount, topUps));
        ImGui.TextUnformatted(string.Format(Strings.ConfirmEta, estimateSec));
    }

    private static void DrawDetails(List<PlannedAction> actions)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (!ImGui.BeginChild("##confirm-details", new System.Numerics.Vector2(0, 200), false))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var grp in actions.GroupBy(a => a.RetainerKey))
        {
            var retainerName = Plugin.Configuration.Snapshots.TryGetValue(grp.Key, out var s) ? s.RetainerName : grp.Key;
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.85f, 1f, 1f), retainerName);
            ImGui.Indent();
            foreach (var a in grp)
            {
                var name = sheet.TryGetRow(a.ItemId, out var row) ? row.Name.ExtractText() : $"#{a.ItemId}";
                if (a.IsHQ) name += " (HQ)";
                if (a.Kind == PlannedActionKind.Reprice)
                {
                    ImGui.BulletText($"{name} #{a.ListingIndex} → {a.UnitPrice:N0} g");
                    var config = Plugin.Configuration;
                    if (RepriceRules.LimitExceeded(a.OldPrice, a.UnitPrice, config.MaxDropPercent, config.MaxRaisePercent) is { } reason)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, RepriceTab.WarningColor(reason));
                        ImGui.TextWrapped(Strings.PriceWarning(reason, a.OldPrice, a.UnitPrice));
                        ImGui.PopStyleColor();
                    }
                }
                else if (a.TopUp is { } topUp)
                {
                    ImGui.Bullet();
                    ImGui.TextWrapped(string.Format(Strings.TopUpPlan, name, topUp.OriginalQuantity, a.Quantity, a.UnitPrice, retainerName));
                }
                else if (a.ResolveRestockPrice)
                {
                    ImGui.BulletText($"{name} ×{a.Quantity} · {Strings.StockMarketPricing}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockAutomaticPricing);
                }
                else
                {
                    ImGui.BulletText($"{name} ×{a.Quantity} @ {a.UnitPrice:N0} g");
                }
            }
            foreach (var a in grp.Where(a => a.PreferredRetainerFallback != null).DistinctBy(a => (a.ItemId, a.IsHQ)))
            {
                var name = sheet.TryGetRow(a.ItemId, out var row) ? row.Name.ExtractText() : $"#{a.ItemId}";
                if (a.IsHQ) name += " (HQ)";
                ImGui.TextWrapped(Strings.RetainerFallback(name, a.PreferredRetainerFallback!, retainerName, a.PreferredRetainerFullAtUtc));
            }
            ImGui.Unindent();
        }

        ImGui.EndChild();
    }

    private void DrawButtons()
    {
        if (ImGui.Button(Strings.Cancel, new System.Numerics.Vector2(140, 0)))
        {
            pendingActions = null;
            onConfirm = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Strings.ConfirmStart, new System.Numerics.Vector2(160, 0)))
        {
            var actions = pendingActions;
            var cb = onConfirm;
            pendingActions = null;
            onConfirm = null;
            ImGui.CloseCurrentPopup();
            if (actions != null && cb != null) cb(actions);
        }
    }
}
