using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Restocker.Common;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>Fork: watched items, their listed / available units, alert threshold and a Restock button.</summary>
public sealed class LowStockTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private readonly LowStockService service;
    private string search = string.Empty;
    private StockKind kindFilter;
    private StockStatus statusFilter;
    private bool onlyLow;
    private readonly Dictionary<(uint, bool), int> restockTo = new();
    /// <summary>Price typed or picked for the next restock (session only).</summary>
    private readonly Dictionary<(uint, bool), RestockPrice> restockPrice = new();
    private readonly HashSet<(uint, bool)> selected = new();
    private string? lastMessage;

    public LowStockTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog, LowStockService service)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
        this.service = service;
    }

    private static string ItemName(uint id)
        => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}";

    public void Draw()
    {
        DrawAdd();
        ImGui.Separator();
        if (!string.IsNullOrEmpty(lastMessage)) ImGui.TextColored(new Vector4(0.95f, 0.8f, 0.4f, 1f), lastMessage);
        DrawTable();
    }

    /// <summary>Search over items we list or hold (retainers, bag, saddlebag) and add them to the watch list.</summary>
    private void DrawAdd()
    {
        ImGui.TextUnformatted("Watch an item:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##lowstock-search", "Search item you list or hold", ref search, 64);
        if (search.Length < 2) return;

        var known = configuration.Snapshots.Values.SelectMany(s => s.Listings.Select(l => (l.ItemId, l.IsHQ))
                .Concat(s.Inventory.Select(i => (i.ItemId, i.IsHQ))))
            .Concat(configuration.Characters.Values.SelectMany(c => c.Bag.Concat(c.Saddlebag).Concat(c.PremiumSaddlebag)
                .Where(i => i.IsListable).Select(i => (i.ItemId, i.IsHQ))))
            .Distinct()
            .Select(k => (k.ItemId, k.IsHQ, Name: ItemName(k.ItemId)))
            .Where(k => k.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(k => !configuration.LowStock.Any(e => e.ItemId == k.ItemId && e.IsHq == k.IsHQ))
            .OrderBy(k => k.Name).Take(10).ToList();

        foreach (var k in known)
        {
            if (ImGui.SmallButton($"+ {k.Name}{(k.IsHQ ? " (HQ)" : "")}##add-{k.ItemId}-{k.IsHQ}"))
            {
                configuration.LowStock.Add(new LowStockEntry { ItemId = k.ItemId, IsHq = k.IsHQ });
                configuration.Save();
                search = string.Empty;
            }
        }
        if (known.Count == 0) ImGui.TextDisabled("no match among items on your retainers / bag / saddlebag (open the bell once to refresh)");
    }

    private void DrawTable()
    {
        if (configuration.LowStock.Count == 0)
        {
            ImGui.TextDisabled("No watched items. Alerts go to the Restocky chat channel when listed units drop to the threshold.");
            return;
        }

        DrawTagCombo("##kind-filter", Strings.StockKindLabel, ref kindFilter, Strings.StockTag, Strings.AllStockTags, 140);
        ImGui.SameLine();
        DrawTagCombo("##status-filter", Strings.StockStatusLabel, ref statusFilter, Strings.StockTag, Strings.AllStockTags, 140);
        ImGui.SameLine();
        ImGui.Checkbox(Strings.StockOnlyLow, ref onlyLow);

        var bellOpen = AddonHelper.IsOpen("RetainerList");
        var cid = Plugin.PlayerState.ContentId;
        var retainers = service.CurrentRetainers();
        var retainerNames = retainers.Select(r => r.RetainerName).OrderBy(name => name).ToArray();
        var ready = bellOpen && !executor.IsRunning;
        ImGui.BeginDisabled(!ready);
        var selectLow = ImGui.Button(Strings.StockSelectLow);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Strings.StockClearSelection)) selected.Clear();
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready || selected.Count == 0);
        if (ImGui.Button(string.Format(Strings.StockRestockSelected, selected.Count))) RestockSelected(cid);
        ImGui.EndDisabled();

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##lowstock", 9, flags, new Vector2(0, Math.Max(120f, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##selected", ImGuiTableColumnFlags.WidthFixed, 21);
        ImGui.TableSetupColumn(Strings.StockItem, ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn(Strings.StockCounts, ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn(Strings.StockThreshold, ImGuiTableColumnFlags.WidthFixed, 49);
        ImGui.TableSetupColumn(Strings.StockPerListing, ImGuiTableColumnFlags.WidthFixed, 63);
        ImGui.TableSetupColumn($"{Strings.StockPrice} ({Strings.GilPerUnit})", ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn($"{Strings.StockMarket} ({Strings.GilPerUnit})", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn(Strings.StockTarget, ImGuiTableColumnFlags.WidthFixed, 91);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 21);
        ImGui.TableHeadersRow();

        LowStockEntry? remove = null;
        var now = DateTime.UtcNow;
        var tracker = Plugin.Instance.Tracker.Tracker;
        foreach (var e in configuration.LowStock.Where(e => e.MatchesTags(kindFilter, statusFilter)))
        {
            var id = $"{e.ItemId}-{e.IsHq}";
            var listed = service.ListedUnits(e.ItemId, e.IsHq);
            var low = listed <= e.Threshold;
            if (onlyLow && !low) continue;
            var (inventory, listings) = service.StockReadings(e, retainers, now);
            var autoPrice = listed == 0;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var key = (e.ItemId, e.IsHq);
            var checkedRow = selected.Contains(key);
            if (ImGui.Checkbox($"##select-{id}", ref checkedRow))
            {
                if (checkedRow) selected.Add(key);
                else selected.Remove(key);
            }
            ImGui.TableNextColumn();
            if (low) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.4f, 1f));
            ImGui.TextWrapped(ItemName(e.ItemId) + (e.IsHq ? " (HQ)" : ""));
            if (low) ImGui.PopStyleColor();
            ImGui.PushID(id);
            DrawRowOptions(e, cid, retainerNames);
            DrawRestockMode(e);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{Strings.StockNextCycle}\n{service.NextCyclePreview(e, retainers, now)}");
            if (ImGui.TreeNode(Strings.StockSources))
            {
                foreach (var source in inventory.Concat(listings)) ImGui.TextWrapped(DescribeStock(source));
                ImGui.TreePop();
            }
            ImGui.PopID();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{StockTotal(listings)} / {StockTotal(inventory)}");
            if (ImGui.IsItemHovered())
            {
                var details = string.Join("\n", inventory.Concat(listings).Select(DescribeStock));
                var provisional = listings.Any(r => r.Provisional) ? $"\n{Strings.StockProvisional}" : string.Empty;
                ImGui.SetTooltip($"{Strings.StockCountsHint}\n{details}\n{Strings.StockFreshCounts}{provisional}");
            }

            ImGui.TableNextColumn();
            var th = e.Threshold;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##th-{id}", ref th, 0)) { e.Threshold = Math.Max(0, th); configuration.Save(); }
            var thresholdHovered = ImGui.IsItemHovered();
            var velocity = SalesVelocity.Calculate(tracker.GetSales(e.ItemId, e.IsHq), now);
            ImGui.TextDisabled($"≈{velocity.SuggestedThreshold:N0}");
            if (thresholdHovered || ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(Strings.SuggestedThreshold, velocity.SuggestedThreshold));

            ImGui.TableNextColumn();
            var per = e.PerListing;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##per-{id}", ref per, 0)) { e.PerListing = Math.Max(0, per); configuration.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"0 = same as your current listings ({service.PerListing(e)})");

            // Price for this restock: typed by the user, or picked from the market suggestion with "Use".
            ImGui.TableNextColumn();
            if (autoPrice)
            {
                ImGui.TextWrapped(Strings.StockMarketPricing);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockAutomaticPricing);
            }
            else
            {
                if (!restockPrice.TryGetValue(key, out var chosen)) chosen = service.PriceForRestock(e);
                // InputScalar with null step refs crashes in Dalamud's binding.
                var price = (int)Math.Clamp(chosen.UnitPrice, 0, int.MaxValue);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt($"##price-{id}", ref price, 0)) restockPrice[key] = new(Math.Max(0, price), AutoRestockPriceKind.Manual);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockUnitPriceHint);
            }

            ImGui.TableNextColumn();
            var sug = service.Suggestion(e);
            if (sug is { Price: > 0 } s)
            {
                var age = DateTime.UtcNow - s.FetchedUtc;
                if (autoPrice) ImGui.TextUnformatted($"{s.Price:N0}");
                else if (ImGui.SmallButton($"Use {s.Price:N0}##use-{id}")) restockPrice[key] = new(s.Price, AutoRestockPriceKind.Cache, age);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Format(Strings.StockMarketPriceHint, configuration.UndercutGil, (int)age.TotalMinutes));
            }
            else
            {
                ImGui.TextDisabled(sug == null ? "not fetched" : configuration.MatchQuality.For(e.ItemId, e.IsHq)
                    ? Strings.NoCompetitor(e.IsHq) : "no competitor");
            }
            var compareFrom = service.RetainerToCompareFrom(e);
            var canCheck = bellOpen && !executor.IsRunning && compareFrom != null;
            if (!canCheck) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Check##chk-{id}") && compareFrom != null)
            {
                executor.OnFetchMarketCompleted = () =>
                {
                    if (configuration.MatchQuality.For(e.ItemId, e.IsHq) &&
                        service.Suggestion(e) is { Price: 0 } empty && empty.FetchedUtc >= executor.JobFetchStartUtc)
                        Plugin.ChatPrint($"{ItemName(e.ItemId)}: {Strings.NoCompetitor(e.IsHq)}");
                };
                executor.OnFetchJobCompleted = null;
                executor.StartFetchMarketPricesForRetainer(compareFrom);
            }
            if (!canCheck) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(compareFrom == null
                    ? Strings.StockMarketCheckHint
                    : bellOpen ? "Fetch live market prices from the retainer that lists it" : "open the summoning bell first");

            ImGui.TableNextColumn();
            var to = restockTo.TryGetValue((e.ItemId, e.IsHq), out var t) ? t : e.Target;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##to-{id}", ref to, 0))
            {
                to = Math.Max(0, to);
                restockTo[(e.ItemId, e.IsHq)] = to;
                e.Target = to;
                configuration.Save();
            }

            var chosenPrice = autoPrice || !ready ? default : restockPrice.TryGetValue(key, out var rp) ? rp : service.PriceForRestock(e);
            // Automatic pricing uses a placeholder while laying out the new listings.
            var actions = ready ? service.PlanRestock(e, to, autoPrice ? 1 : chosenPrice.UnitPrice, allowTopUps: !autoPrice) : [];
            var can = actions.Count > 0;
            if (selectLow && low && can) selected.Add(key);
            if (!can) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"{(autoPrice ? Strings.StockListAndPrice : Strings.StockRestock)}##rs-{id}"))
            {
                if (autoPrice) RestockAtMarket(actions);
                else Restock(e, actions, chosenPrice);
            }
            if (!can) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!bellOpen ? Strings.StockOpenBell
                    : executor.IsRunning ? Strings.StockRunInProgress
                    : !can ? Strings.StockNothingToRestock
                    : autoPrice ? Strings.StockAutomaticPricing
                    : actions.Any(a => a.TopUp != null) ? string.Format(Strings.TopUpRestockHint, to)
                    : string.Format(Strings.StockRestockHint, to));

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##rm-{id}")) remove = e;
        }
        ImGui.EndTable();

        if (remove != null)
        {
            selected.Remove((remove.ItemId, remove.IsHq));
            configuration.LowStock.Remove(remove);
            configuration.Save();
        }
    }

    private static string StockTotal(IReadOnlyCollection<StockSourceReading> sources)
    {
        var (units, incomplete) = StockFreshness.Total(sources.Select(s => s.Reading));
        if (units == null) return "?";
        var marker = incomplete ? "!" : sources.Any(s => s.Provisional) ? "?" : string.Empty;
        return $"{units:N0}{marker}";
    }

    private static string DescribeStock(StockSourceReading source)
    {
        var reading = source.Reading;
        var age = reading.Age.TotalDays >= 1 ? string.Format(Strings.StockReadDays, (int)reading.Age.TotalDays)
            : reading.Age.TotalHours >= 1 ? string.Format(Strings.StockReadHours, (int)reading.Age.TotalHours)
            : reading.Age.TotalMinutes >= 1 ? string.Format(Strings.StockReadMinutes, (int)reading.Age.TotalMinutes)
            : Strings.StockReadNow;
        var when = reading.State switch
        {
            StockReadState.NeverRead => Strings.StockNeverRead,
            StockReadState.PreviousSession => Strings.StockPreviousSession,
            StockReadState.Stale => string.Format(Strings.StockStaleRead, age),
            StockReadState.Incomplete => string.Format(Strings.StockIncompleteRead, age),
            _ => age,
        };
        var count = reading.Units is { } units ? $"{units:N0}{(source.Provisional ? "?" : "")}" : "?";
        return string.Format(Strings.StockSourceCount, source.Label, count, when);
    }

    private void DrawRestockMode(LowStockEntry entry)
    {
        var mode = Strings.StockRestockModeName(entry.RestockMode);
        var effective = Strings.StockRestockModeName(entry.EffectiveRestockMode);
        var label = entry.RestockMode == entry.EffectiveRestockMode ? mode : $"{mode} ({effective})";
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##restock-mode", $"{Strings.StockRestockModeLabel}: {label}")) return;
        foreach (var choice in Enum.GetValues<StockRestockMode>())
            if (ImGui.Selectable(Strings.StockRestockModeName(choice), choice == entry.RestockMode))
            {
                entry.RestockMode = choice;
                configuration.Save();
            }
        ImGui.EndCombo();
    }

    private void DrawRowOptions(LowStockEntry entry, ulong cid, IReadOnlyList<string> names)
    {
        var kind = entry.Kind;
        var status = entry.Status;
        if (kind != StockKind.None)
        {
            var color = kind switch
            {
                StockKind.Flip => new Vector4(1f, 0.75f, 0.3f, 1f),
                StockKind.Gather => new Vector4(0.5f, 0.9f, 0.6f, 1f),
                _ => new Vector4(0.6f, 0.75f, 1f, 1f),
            };
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            DrawTagButton(Strings.StockTag(kind));
            ImGui.PopStyleColor();
        }
        if (status != StockStatus.None) DrawTagButton($"[{Strings.StockTag(status)}]");
        if (kind == StockKind.None && status == StockStatus.None) DrawTagButton(Strings.StockTags);
        if (ImGui.BeginPopup("##tags"))
        {
            var changed = DrawTagCombo("##kind", Strings.StockKindLabel, ref kind, Strings.StockTag, Strings.NoStockTag, 150);
            changed |= DrawTagCombo("##status", Strings.StockStatusLabel, ref status, Strings.StockTag, Strings.NoStockTag, 150);
            if (changed)
            {
                entry.Kind = kind;
                entry.Status = status;
                configuration.Save();
            }
            ImGui.EndPopup();
        }

        var selected = entry.PreferredRetainers.GetValueOrDefault(cid);
        var seller = $"{Strings.SellOn}: {selected ?? Strings.ResetFloorToAuto}";
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(cid == 0);
        if (ImGui.BeginCombo("##seller", seller))
        {
            if (ImGui.Selectable(Strings.ResetFloorToAuto, selected == null))
            {
                entry.PreferredRetainers.Remove(cid);
                configuration.Save();
            }
            foreach (var name in names)
                if (ImGui.Selectable(name, selected == name))
                {
                    entry.PreferredRetainers[cid] = name;
                    configuration.Save();
                }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(seller);
    }

    private static void DrawTagButton(string label)
    {
        var style = ImGui.GetStyle();
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var width = ImGui.CalcTextSize(label).X + 2 * style.FramePadding.X + style.ItemSpacing.X;
        if (ImGui.GetItemRectMax().X + width <= right) ImGui.SameLine();
        if (ImGui.SmallButton(label)) ImGui.OpenPopup("##tags");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockTags);
    }

    private static bool DrawTagCombo<T>(string id, string label, ref T current, Func<T, string> text, string unselected, float width)
        where T : struct, Enum
    {
        var changed = false;
        ImGui.SetNextItemWidth(width);
        string Label(T value) => EqualityComparer<T>.Default.Equals(value, default) ? unselected : text(value);
        if (ImGui.BeginCombo(id, $"{label}: {Label(current)}"))
        {
            foreach (var value in Enum.GetValues<T>())
                if (ImGui.Selectable(Label(value), EqualityComparer<T>.Default.Equals(value, current)))
                {
                    current = value;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        return changed;
    }

    private void RestockSelected(ulong characterId)
    {
        var entries = configuration.LowStock.Where(e => selected.Contains((e.ItemId, e.IsHq)));
        var batch = RestockPlanner.PlanBatch(entries, service.FreeListingSlots(), (entry, free) =>
        {
            var key = (entry.ItemId, entry.IsHq);
            var market = service.ListedUnits(entry.ItemId, entry.IsHq) == 0;
            var price = market ? default : restockPrice.TryGetValue(key, out var chosen) ? chosen : service.PriceForRestock(entry);
            var actions = service.PlanRestock(entry, restockTo.GetValueOrDefault(key, entry.Target),
                market ? 1 : price.UnitPrice, allowTopUps: !market, freeSlots: free);
            if (market) SetMarketPricing(actions);
            else SetRowPrice(actions, price);
            return actions;
        });
        if (batch.Skipped.Count > 0)
        {
            var names = batch.Skipped.Select(e => ItemName(e.ItemId) + (e.IsHq ? " (HQ)" : ""));
            Plugin.ChatPrint($"{Strings.StockNothingToRestock}: {string.Join(", ", names)}");
        }
        if (batch.Actions.Count == 0) return;
        confirmDialog.Request(batch.Actions, actions =>
        {
            if (executor.IsRunning || Plugin.PlayerState.ContentId != characterId) return;
            executor.StartApplyActions(actions);
            if (executor.IsRunning) selected.ExceptWith(actions.Select(a => (a.ItemId, a.IsHQ)));
        });
    }

    private static void SetMarketPricing(List<PlannedAction> actions)
    {
        foreach (var action in actions)
        {
            action.ResolveRestockPrice = true;
            action.UnitPrice = 0;
        }
    }

    private void RestockAtMarket(List<PlannedAction> actions)
    {
        SetMarketPricing(actions);
        confirmDialog.Request(actions, executor.StartApplyActions);
    }

    private static void SetRowPrice(List<PlannedAction> actions, RestockPrice price)
    {
        foreach (var action in actions.Where(a => a.TopUp == null))
            action.RestockPrice = price;
    }

    private void Restock(LowStockEntry e, List<PlannedAction> actions, RestockPrice price)
    {
        SetRowPrice(actions, price);
        confirmDialog.Request(actions, list =>
        {
            var added = list.Where(a => a.TopUp == null).ToList();
            var topUps = list.Count - added.Count;
            var lines = new List<string>();
            if (added.Count > 0) lines.Add(string.Format(Strings.LowStockListing, ItemName(e.ItemId), added.Sum(a => a.Quantity), added.Count, price.UnitPrice));
            if (topUps > 0) lines.Add(string.Format(Strings.TopUpCount, topUps));
            lastMessage = string.Join(" · ", lines);
            Plugin.ChatPrint(lastMessage);
            executor.StartApplyActions(list);
        });
    }
}
