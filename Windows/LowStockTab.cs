using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Restocker.Common;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>Fork: watched items, their listed / available units, alert threshold and a Restock button.</summary>
public sealed partial class LowStockTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private readonly LowStockService service;
    private string search = string.Empty;
    private StockKind kindFilter;
    private StockStatus statusFilter;
    private bool onlyLow;
    private Guid? categoryFilter;
    private Guid editingCategory;
    private string categoryName = string.Empty;
    private string? categoryError;
    private bool noteDirty;
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
        if (ImGui.Button(configuration.ShowRetainerBoard ? "Stock list" : "Retainer board"))
        {
            configuration.ShowRetainerBoard = !configuration.ShowRetainerBoard;
            configuration.Save();
        }
        if (configuration.ShowRetainerBoard)
        {
            DrawBoard();
            return;
        }
        var bellOpen = AddonHelper.IsOpen("RetainerList");
        var ready = bellOpen && !executor.IsRunning;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        if (ImGui.InputTextWithHint("##stock-search", "Find or watch item", ref search, 64)) focusedItem = null;
        ImGui.SameLine();
        ImGui.Checkbox(Strings.StockOnlyLow, ref onlyLow);
        ImGui.SameLine();
        if (ImGui.Button($"{Strings.StockKindLabel}: {(kindFilter == StockKind.None ? Strings.AllStockTags : Strings.StockTag(kindFilter))}")) ImGui.OpenPopup("filters");
        if (ImGui.BeginPopup("filters"))
        {
            DrawTagCombo("##kind-filter", Strings.StockKindLabel, ref kindFilter, Strings.StockTag, Strings.AllStockTags, 170);
            DrawTagCombo("##status-filter", Strings.StockStatusLabel, ref statusFilter, Strings.StockTag, Strings.AllStockTags, 170);
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready);
        var selectLow = ImGui.Button(Strings.StockSelectLow);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Strings.StockClearSelection)) selected.Clear();
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready || selected.Count == 0);
        if (ImGui.Button(string.Format(Strings.StockRestockSelected, selected.Count.ToString("N0"))))
            RestockSelected(Plugin.PlayerState.ContentId);
        ImGui.EndDisabled();
        DrawCategoryToolbar();
        if (search.Length >= 2) DrawAddResults();
        if (!string.IsNullOrEmpty(lastMessage)) ImGui.TextColored(new Vector4(0.95f, 0.8f, 0.4f, 1f), lastMessage);
        DrawTable(bellOpen, ready, selectLow);
    }

    private void DrawCategoryToolbar()
    {
        var categories = configuration.StockCategories;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
        if (ImGui.BeginCombo("##category-filter", categoryFilter == null ? "All categories" : CategoryLabel(categoryFilter.Value)))
        {
            if (ImGui.Selectable("All categories", categoryFilter == null)) categoryFilter = null;
            foreach (var id in CategoryIds())
                if (ImGui.Selectable(CategoryLabel(id) + $"##filter-{id}", categoryFilter == id)) categoryFilter = id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Manage categories"))
        {
            editingCategory = Guid.Empty;
            categoryName = string.Empty;
            categoryError = null;
            ImGui.OpenPopup("categories");
        }
        if (!ImGui.BeginPopup("categories")) return;
        if (ImGui.BeginCombo("Category", editingCategory == Guid.Empty ? "New category" : CategoryLabel(editingCategory)))
        {
            if (ImGui.Selectable("New category##new")) EditCategory(Guid.Empty, "");
            foreach (var c in categories.Ordered)
                if (ImGui.Selectable(c.Name + $"##edit-{c.Id}")) EditCategory(c.Id, c.Name);
            ImGui.EndCombo();
        }
        ImGui.InputText("Name", ref categoryName, 64);
        var creating = editingCategory == Guid.Empty;
        if (ImGui.Button(creating ? "Create" : "Rename"))
        {
            if (creating ? categories.Create(categoryName) : categories.Rename(editingCategory, categoryName))
            {
                EditCategory(Guid.Empty, "");
                configuration.Save();
            }
            else categoryError = "Enter a unique name without ##.";
        }
        if (!creating)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete category"))
            {
                categories.Delete(editingCategory, configuration.LowStock);
                if (categoryFilter == editingCategory) categoryFilter = null;
                EditCategory(Guid.Empty, "");
                configuration.Save();
            }
        }
        if (categoryError != null) ImGui.TextUnformatted(categoryError);
        if (ImGui.Button("Add default categories"))
        {
            foreach (var name in new[] { "Crystals", "Flips", "Crafts", "Gather" }) categories.Create(name);
            configuration.Save();
        }
        ImGui.EndPopup();
    }

    private void EditCategory(Guid id, string name)
    {
        editingCategory = id;
        categoryName = name;
        categoryError = null;
    }

    private IEnumerable<Guid> CategoryIds()
        => configuration.StockCategories.Ordered.Select(c => c.Id).Append(Guid.Empty);

    private string CategoryLabel(Guid id)
        => configuration.StockCategories.Ordered.Find(c => c.Id == id)?.Name ?? StockCategories.Uncategorized;

    private void DrawAddResults()
    {
        var known = configuration.Snapshots.Values.SelectMany(s => s.Listings.Select(l => (l.ItemId, l.IsHQ))
                .Concat(s.Inventory.Select(i => (i.ItemId, i.IsHQ))))
            .Concat(configuration.Characters.Values.SelectMany(c => c.Bag.Concat(c.Saddlebag).Concat(c.PremiumSaddlebag)
                .Where(i => i.IsListable).Select(i => (i.ItemId, i.IsHQ))))
            .Distinct().Where(k => focusedItem == null || focusedItem == k)
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
                focusedItem = null;
            }
        }
        if (known.Count == 0 && !configuration.LowStock.Any(e => ItemName(e.ItemId).Contains(search, StringComparison.OrdinalIgnoreCase))) ImGui.TextDisabled("no match among items on your retainers / bag / saddlebag (open the bell once to refresh)");
    }

    private void DrawTable(bool bellOpen, bool ready, bool selectLow)
    {
        if (configuration.LowStock.Count == 0)
        {
            ImGui.TextDisabled("Search for an item you list or hold to start tracking it.");
            return;
        }
        var retainers = service.CurrentRetainers();
        var cid = Plugin.PlayerState.ContentId;
        var rows = configuration.LowStock.Where(e => e.MatchesTags(kindFilter, statusFilter)
                && (focusedItem == null || focusedItem == (e.ItemId, e.IsHq)))
            .Select(e => (Entry: e, Name: ItemName(e.ItemId), Listed: service.ListedUnits(e.ItemId, e.IsHq)))
            .Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && (!onlyLow || r.Entry.IsLow(r.Listed)))
            .OrderByDescending(r => r.Entry.Enabled).ThenByDescending(r => r.Entry.IsLow(r.Listed))
            .ThenBy(r => r.Name).ThenBy(r => r.Entry.IsHq).ToLookup(r => r.Entry.CategoryId);
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (!ImGui.BeginTable("##stock-items", 5, flags)) return;
        var em = ImGui.GetFontSize();
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.StockItem, ImGuiTableColumnFlags.WidthFixed, Math.Max(14 * em, availableWidth - 37 * em));
        ImGui.TableSetupColumn(Strings.StockCounts, ImGuiTableColumnFlags.WidthFixed, 9 * em);
        ImGui.TableSetupColumn($"{Strings.StockThreshold} / {Strings.StockTarget}", ImGuiTableColumnFlags.WidthFixed, 9 * em);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 4 * em);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 11 * em);
        ImGui.TableHeadersRow();
        LowStockEntry? remove = null;
        var now = DateTime.UtcNow;
        var editingNote = false;
        foreach (var categoryId in CategoryIds())
        {
            if (categoryFilter != null && categoryFilter != categoryId) continue;
            var group = rows[categoryId];
            var count = group.Count();
            var category = configuration.StockCategories.Ordered.Find(c => c.Id == categoryId);
            if (count == 0 && (search.Length > 0 || onlyLow || statusFilter != StockStatus.None || kindFilter != StockKind.None)) continue;
            var collapsed = focusedItem == null && (category?.Collapsed ?? configuration.StockCategories.UncategorizedCollapsed);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{(collapsed ? ">" : "v")} {CategoryLabel(categoryId)} ({count}, {group.Count(r => r.Entry.IsLow(r.Listed))} low)###group-{categoryId}"))
            {
                collapsed = !collapsed;
                if (category != null) category.Collapsed = collapsed;
                else configuration.StockCategories.UncategorizedCollapsed = collapsed;
                configuration.Save();
            }
            foreach (var (e, name, listed) in group) DrawRow(e, name, listed, collapsed);
        }
        ImGui.EndTable();
        if (noteDirty && !editingNote)
        {
            configuration.Save();
            noteDirty = false;
        }
        if (remove != null)
        {
            var key = (remove.ItemId, remove.IsHq);
            selected.Remove(key);
            restockPrice.Remove(key);
            configuration.LowStock.Remove(remove);
            configuration.Save();
        }

        void DrawRow(LowStockEntry e, string name, int listed, bool collapsed)
        {
            var key = (e.ItemId, e.IsHq);
            var id = $"{e.ItemId}-{e.IsHq}";
            var low = e.IsLow(listed);
            var plannedTarget = service.PlannedTarget(e);
            var to = plannedTarget > 0 ? plannedTarget : e.Target;
            var autoPrice = listed == 0;
            if (collapsed && !(selectLow && low)) return;
            var chosenPrice = autoPrice || !ready || !e.Enabled ? default : restockPrice.TryGetValue(key, out var rp) ? rp : service.PriceForRestock(e);
            var actions = ready && e.Enabled ? service.PlanRestock(e, to, autoPrice ? 1 : chosenPrice.UnitPrice, allowTopUps: !autoPrice) : [];
            var can = actions.Count > 0;
            if (selectLow && low && can) selected.Add(key);
            if (collapsed) return;
            var (inventory, listings) = service.StockReadings(e, retainers, now);
            ImGui.PushID(id);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var checkedRow = selected.Contains(key);
            ImGui.BeginDisabled(!e.Enabled);
            if (ImGui.Checkbox("##select", ref checkedRow))
            {
                if (checkedRow) selected.Add(key);
                else selected.Remove(key);
            }
            ImGui.SameLine();
            if (low) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.65f, 0.42f, 1f));
            ImGui.TextUnformatted(name + (e.IsHq ? " (HQ)" : ""));
            if (low) ImGui.PopStyleColor();
            var hover = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            if (!string.IsNullOrWhiteSpace(e.Note))
            {
                ImGui.SameLine();
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextUnformatted(FontAwesomeIcon.StickyNote.ToIconString());
                ImGui.PopFont();
                hover |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            }
            if (e.Kind != StockKind.None)
            {
                ImGui.SameLine();
                var color = e.Kind switch
                {
                    StockKind.Flip => new Vector4(1f, 0.75f, 0.3f, 1f),
                    StockKind.Gather => new Vector4(0.5f, 0.9f, 0.6f, 1f),
                    _ => new Vector4(0.6f, 0.75f, 1f, 1f),
                };
                ImGui.TextColored(color, $"[{Strings.StockTag(e.Kind)}]");
            }
            if (e.Status != StockStatus.None)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(.8f, .7f, .9f, 1), $"[{Strings.StockTag(e.Status)}]");
            }
            ImGui.TableNextColumn();
            var stock = FormatSupply(e, inventory);
            RightAligned($"{StockTotal(listings)} / {stock.Available}");
            hover |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            if (hover)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(name + (e.IsHq ? " (HQ)" : ""));
                if (!string.IsNullOrWhiteSpace(e.Note))
                {
                    ImGui.PushTextWrapPos(em * 28);
                    ImGui.TextUnformatted(e.Note);
                    ImGui.PopTextWrapPos();
                }
                ImGui.Separator();
                ImGui.TextUnformatted(stock.Detail);
                foreach (var source in inventory.Concat(listings)) ImGui.TextUnformatted(DescribeStock(source));
                ImGui.TextDisabled(Strings.StockFreshCounts);
                if (listings.Any(r => r.Provisional)) ImGui.TextDisabled(Strings.StockProvisional);
                ImGui.EndTooltip();
            }
            ImGui.TableNextColumn();
            RightAligned($"{e.Threshold:N0} / {to:N0}");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                var velocity = SalesVelocity.Calculate(Plugin.Instance.Tracker.Tracker.GetSales(e.ItemId, e.IsHq), now);
                ImGui.SetTooltip(string.Format(Strings.SuggestedThreshold, velocity.SuggestedThreshold));
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Strings.StockRestockModeName(e.EffectiveRestockMode));
            if (ImGui.IsItemHovered() && e.Enabled) ImGui.SetTooltip(service.NextCyclePreview(e, retainers, now));
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            if (!can) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"{(autoPrice ? Strings.StockListAndPrice : Strings.StockRestock)}##rs-{id}"))
            {
                if (autoPrice) RestockAtMarket(actions);
                else Restock(e, actions, chosenPrice);
            }
            if (!can) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!e.Enabled ? "disabled" : !bellOpen ? Strings.StockOpenBell
                    : executor.IsRunning ? Strings.StockRunInProgress
                    : !can ? Strings.StockNothingToRestock
                    : autoPrice ? Strings.StockAutomaticPricing
                    : actions.Any(a => a.TopUp != null) ? string.Format(Strings.TopUpRestockHint, to)
                    : string.Format(Strings.StockRestockHint, to));

            ImGui.SameLine();
            if (IconButton(FontAwesomeIcon.Cog, "Settings")) ImGui.OpenPopup("settings");
            ImGui.SameLine();
            if (IconButton(e.Enabled ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, e.Enabled ? "Disable" : "Enable"))
            {
                e.Enabled = !e.Enabled;
                selected.Remove(key);
                configuration.Save();
            }
            if (ImGui.BeginPopup("settings"))
            {
                var names = retainers.Select(r => r.RetainerName).OrderBy(n => n).ToArray();
                if (DrawSettings(e, name, cid, names, autoPrice, bellOpen, ref editingNote)) remove = e;
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
    }

    private static void RightAligned(string value)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(value).X));
        ImGui.TextUnformatted(value);
    }

    private static bool IconButton(FontAwesomeIcon icon, string hint)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var clicked = ImGui.SmallButton(icon.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
        return clicked;
    }

    private bool DrawSettings(LowStockEntry e, string name, ulong cid, string[] names,
        bool autoPrice, bool bellOpen, ref bool editingNote)
    {
        var em = ImGui.GetFontSize();
        ImGui.TextUnformatted(name + (e.IsHq ? " (HQ)" : ""));
        if (ImGui.BeginCombo("Category", CategoryLabel(e.CategoryId)))
        {
            foreach (var id in CategoryIds())
                if (ImGui.Selectable(CategoryLabel(id), id == e.CategoryId))
                {
                    e.CategoryId = id;
                    configuration.Save();
                }
            ImGui.EndCombo();
        }
        DrawRowOptions(e, cid, names);
        DrawRestockMode(e);
        var plannedTarget = service.PlannedTarget(e);
        var to = plannedTarget > 0 ? plannedTarget : e.Target;
        var enabled = e.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { e.Enabled = enabled; configuration.Save(); }
        var threshold = e.Threshold;
        if (ImGui.InputInt(Strings.StockThreshold, ref threshold, 0)) { e.Threshold = Math.Max(0, threshold); configuration.Save(); }
        ImGui.BeginDisabled(plannedTarget > 0);
        if (ImGui.InputInt(Strings.StockTarget, ref to, 0)) { e.Target = Math.Max(0, to); configuration.Save(); }
        ImGui.EndDisabled();
        if (plannedTarget > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Sum of planned slots × units per listing across your retainers.");
        ListTab.DrawKeep(configuration, e.ItemId, e.IsHq, "Keep in inventory");
        var per = e.PerListing;
        ImGui.BeginDisabled(plannedTarget > 0);
        if (ImGui.InputInt(Strings.StockPerListing, ref per, 0)) { e.PerListing = Math.Max(0, per); configuration.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"0 = same as your current listings ({service.PerListing(e):N0})");
        ImGui.EndDisabled();
        DrawPricing(e, autoPrice, bellOpen);
        ImGui.Separator();
        var note = e.Note ?? string.Empty;
        ImGui.TextUnformatted("Note");
        if (ImGui.InputTextMultiline("##note", ref note, 8192, new Vector2(22 * em, 4 * em)))
        {
            e.Note = note;
            noteDirty = true;
        }
        editingNote |= ImGui.IsItemActive();
        var assigned = configuration.SlotPlans.Values.Any(rows => rows.Any(r => r.Matches(e.ItemId, e.IsHq)));
        ImGui.BeginDisabled(assigned);
        var remove = ImGui.SmallButton("Remove item");
        ImGui.EndDisabled();
        if (assigned && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Remove this item's plan rows first.");
        return remove;
    }

    private void DrawPricing(LowStockEntry e, bool autoPrice, bool bellOpen)
    {
        var key = (e.ItemId, e.IsHq);
        var id = $"{e.ItemId}-{e.IsHq}";
        if (autoPrice)
        {
            ImGui.TextUnformatted(Strings.StockMarketPricing);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockAutomaticPricing);
        }
        else
        {
            if (!restockPrice.TryGetValue(key, out var chosen)) chosen = service.PriceForRestock(e);
            // InputScalar with null step refs crashes in Dalamud's binding.
            var price = (int)Math.Clamp(chosen.UnitPrice, 0, int.MaxValue);
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
            if (ImGui.InputInt($"{Strings.StockPrice} ({Strings.GilPerUnit})##price", ref price, 0)) restockPrice[key] = new(Math.Max(0, price), AutoRestockPriceKind.Manual);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.StockUnitPriceHint);
        }

        ImGui.TextUnformatted(Strings.StockMarket);
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
    }

    private (string Available, string Detail) FormatSupply(LowStockEntry item, List<StockSourceReading> inventory)
    {
        var character = inventory.Take(InventoryEntry.IsCrystal(item.ItemId) ? 1 : 3).ToList();
        var keep = configuration.KeepFor(item.ItemId, item.IsHq);
        var held = StockFreshness.Total(character.Select(s => s.Reading)).Units;
        var reserved = Math.Min(keep, held ?? 0);
        var available = StockTotal(inventory, reserved);
        return (available, $"Character held {StockTotal(character)} · Keep {keep:N0} · Character available {StockTotal(character, reserved)}\nAvailable including retainers {available}");
    }

    private static string StockTotal(IReadOnlyCollection<StockSourceReading> sources, int reserved = 0)
    {
        var (units, incomplete) = StockFreshness.Total(sources.Select(s => s.Reading));
        if (units == null) return "?";
        var marker = incomplete ? "!" : sources.Any(s => s.Provisional) ? "?" : string.Empty;
        return $"{units - reserved:N0}{marker}";
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
        var count = reading.Units is { } units ? $"{units:N0}{(source.Provisional ? "?" : "")}" : reading.State == StockReadState.Incomplete ? "!" : "?";
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
        var changed = DrawTagCombo("##kind", Strings.StockKindLabel, ref kind, Strings.StockTag, Strings.NoStockTag, 150);
        changed |= DrawTagCombo("##status", Strings.StockStatusLabel, ref status, Strings.StockTag, Strings.NoStockTag, 150);
        if (changed)
        {
            entry.Kind = kind;
            entry.Status = status;
            configuration.Save();
        }
        if (service.PlannedTarget(entry) > 0)
        {
            var planned = service.CurrentRetainers().Where(r => configuration.SlotPlans.Rows(r.Key).Any(p => p.Matches(entry.ItemId, entry.IsHq)));
            ImGui.TextWrapped($"{Strings.SellOn}: {string.Join(", ", planned.Select(r => r.RetainerName))}");
            return;
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
        SubmitRestock(configuration.LowStock.Where(e => selected.Contains((e.ItemId, e.IsHq))), characterId);
    }

    private void SubmitRestock(IEnumerable<LowStockEntry> entries, ulong characterId, bool fillOnly = false, string? seller = null)
    {
        var earlier = new List<PlannedAction>();
        var batch = RestockPlanner.PlanBatch(entries, service.FreeListingSlots(), (entry, free) =>
        {
            var key = (entry.ItemId, entry.IsHq);
            var market = service.ListedUnits(entry.ItemId, entry.IsHq) == 0;
            var price = market ? default : restockPrice.TryGetValue(key, out var chosen) ? chosen : service.PriceForRestock(entry);
            var actions = service.PlanRestock(entry, entry.Target,
                market ? 1 : price.UnitPrice, allowTopUps: !market && !fillOnly, freeSlots: free,
                earlier: earlier, fillOnly: fillOnly, fillRetainer: seller);
            earlier.AddRange(actions);
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
            action.RestockPrice = price with { UnitPrice = action.UnitPrice };
    }

    private void Restock(LowStockEntry e, List<PlannedAction> actions, RestockPrice price)
    {
        SetRowPrice(actions, price);
        confirmDialog.Request(actions, list =>
        {
            var added = list.Where(a => a.TopUp == null).ToList();
            var topUps = list.Count - added.Count;
            var lines = new List<string>();
            if (added.Count > 0) lines.Add(string.Format(Strings.LowStockListing, ItemName(e.ItemId), added.Sum(a => a.Quantity), added.Count, added[0].UnitPrice));
            if (topUps > 0) lines.Add(string.Format(Strings.TopUpCount, topUps));
            lastMessage = string.Join(" · ", lines);
            executor.StartApplyActions(list);
        });
    }
}
