using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Restocker.Execution;
using Restocker.Localization;

namespace Restocker.Windows;

internal sealed class RunsTab
{
    private RunReport? selected;

    public void Draw(RunReportHistory history)
    {
        if (history.Reports.Count == 0)
        {
            ImGui.TextDisabled(Strings.RunNone);
            return;
        }
        if (selected == null || !history.Reports.Contains(selected))
            selected = history.Reports[0];

        var height = Math.Max(72, ImGui.GetContentRegionAvail().Y * 0.36f);
        if (ImGui.BeginChild("##run-list", new Vector2(0, height), true))
        {
            for (var i = 0; i < history.Reports.Count; i++)
            {
                var run = history.Reports[i];
                var warning = run.NeedsAttention ? Strings.RunWarning + " " : string.Empty;
                var time = run.StartedUtc.ToLocalTime();
                var label = $"{warning}{time:g} · {Strings.RunModeLabel(run.Mode)}";
                if (ImGui.Selectable($"{label}##run-{i}", selected == run))
                    selected = run;
                ImGui.TextWrapped(run.Counts());
            }
        }
        ImGui.EndChild();

        var report = selected;
        if (ImGui.SmallButton(Strings.RunCopy))
            ImGui.SetClipboardText(report.Summary());
        ImGui.SameLine();
        ImGui.TextWrapped(string.Format(Strings.RunStopReason, report.StopReason));
        ImGui.TextUnformatted(string.Format(Strings.RunTimeRange, Strings.RunModeLabel(report.Mode),
            report.StartedUtc.ToLocalTime(), report.EndedUtc.ToLocalTime()));

        if (ImGui.BeginChild("##run-details", Vector2.Zero, true))
        {
            ImGui.PushID(report.StartedUtc.Ticks.ToString());
            foreach (var group in report.ByRetainer())
            {
                var attention = group.Any(e => e.NeedsAttention);
                var name = string.IsNullOrEmpty(group.Key) ? Strings.RunGeneral : group.Key;
                var label = $"{(attention ? Strings.RunWarning + " " : "")}{name} ({group.Count()})##{group.Key}";
                if (!ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                foreach (var entry in group)
                    ImGui.TextWrapped(entry.NeedsAttention ? $"{Strings.RunWarning} {entry.Detail}" : entry.Detail);
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
    }
}
