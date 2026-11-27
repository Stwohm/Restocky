using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Restocker.Market;

namespace Restocker.Windows;

internal sealed class StatsTab
{
    private readonly DateFilter dates = new();
    private SaleStats? cached;
    private (int, string, string) selection;
    private DateTime refreshAt;
    private static readonly string[] Columns = ["Item", "Units sold", "Net", "Profit", "Earning", "Kind"];

    internal void Draw(ListingTracker tracker)
    {
        if (!dates.Draw(out var start, out var end)) return;
        var now = DateTime.UtcNow;
        if (cached == null || selection != dates.Selection || now >= refreshAt)
        {
            cached = SaleStats.Aggregate(tracker.Records, start, end);
            selection = dates.Selection;
            refreshAt = now.AddSeconds(1);
        }
        var stats = cached;
        var total = stats.Total;
        ImGui.TextUnformatted($"Net after tax   {total.Net:N0} gil");
        ImGui.TextUnformatted($"Flip profit   {total.Profit:N2}     Gather earning   {total.Earning:N2}     Craft   untracked");
        ImGui.TextWrapped("FIFO cost lots cover purchased units first. Flip profit deducts their cost. Units without a recorded cost lot receive their share of net proceeds as Gather earning. Sale records do not identify crafted units.");
        if (total.Incomplete > 0) ImGui.TextDisabled($"{total.Incomplete} sale(s) lack proceeds.");
        if (!ImGui.BeginTabBar("##stats-breakdown")) return;
        if (ImGui.BeginTabItem("Items"))
        {
            DrawTable(stats.Items, "Item");
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Retainers"))
        {
            DrawTable(stats.Retainers, "Retainer");
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private static void DrawTable(List<StatsRow> rows, string label)
    {
        if (!ImGui.BeginTable("##totals" + label, 6, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, Vector2.Zero)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var column in Columns) ImGui.TableSetupColumn(column == "Item" ? label : column);
        ImGui.TableHeadersRow();
        var sort = ImGui.TableGetSortSpecs();
        var columnIndex = sort.SpecsCount > 0 ? sort.Specs.ColumnIndex : 0;
        IComparable SortValue(StatsRow row) => columnIndex switch
        {
            1 => row.Total.Units, 2 => row.Total.Net, 3 => row.Total.Profit, 4 => row.Total.Earning,
            5 => row.Total.Kind, _ => row.Name
        };
        var sorted = sort.SpecsCount > 0 && sort.Specs.SortDirection == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(SortValue) : rows.OrderBy(SortValue);
        foreach (var row in sorted.ThenBy(r => r.Name))
        {
            ImGui.TableNextRow();
            Cell(row.Name);
            Cell(row.Total.Units.ToString("N0"));
            Cell(row.Total.Net.ToString("N0"));
            Cell(row.Total.Profit.ToString("N2"));
            Cell(row.Total.Earning.ToString("N2"));
            Cell(row.Total.Kind);
        }
        ImGui.EndTable();
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }
}
