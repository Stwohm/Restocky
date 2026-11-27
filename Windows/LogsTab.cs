using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Restocker.Common;

namespace Restocker.Windows;

internal sealed class LogsTab
{
    private readonly DateFilter dates = new();
    private int page;
    private List<ActivityEntry> rows = [];
    private ((int, string, string), string, string) selection;
    private DateTime refreshAt;
    private const int PageSize = 100;
    private string type = "All";
    private string query = "";

    internal void Draw(ActivityLog log)
    {
        if (!dates.Draw(out var start, out var end)) return;
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("Type", type))
        {
            foreach (var value in log.Entries.Select(e => e.Type).Distinct().Order().Prepend("All"))
                if (ImGui.Selectable(value, value == type)) type = value;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##activity-search", "Item / retainer / text", ref query, 100);
        var current = (dates.Selection, type, query);
        var now = DateTime.UtcNow;
        if (selection != current || now >= refreshAt)
        {
            if (selection != current) page = 0;
            selection = current;
            refreshAt = now.AddSeconds(1);
            rows = log.Entries.Reverse().Where(e => (type == "All" || e.Type == type)
                && (start == null || e.Utc >= start) && (end == null || e.Utc < end)
                && e.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (ImGui.SmallButton("Copy filtered log")) ImGui.SetClipboardText(string.Join(Environment.NewLine, rows.Select(e => e.Display)));
        page = Math.Clamp(page, 0, Math.Max(0, (rows.Count - 1) / PageSize));
        ImGui.SameLine();
        if (ImGui.SmallButton("Newer") && page > 0) page--;
        ImGui.SameLine();
        if (ImGui.SmallButton("Older") && (page + 1) * PageSize < rows.Count) page++;
        ImGui.SameLine();
        ImGui.TextDisabled(rows.Count == 0 ? "0 entries" : $"{page * PageSize + 1}–{Math.Min((page + 1) * PageSize, rows.Count)} of {rows.Count}");
        if (ImGui.BeginChild("##activity", Vector2.Zero, true))
        {
            if (rows.Count == 0) ImGui.TextDisabled("No matching activity.");
            foreach (var entry in rows.Skip(page * PageSize).Take(PageSize)) ImGui.TextWrapped(entry.Display);
        }
        ImGui.EndChild();
    }
}
