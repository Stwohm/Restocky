using System;
using System.Globalization;
using Dalamud.Bindings.ImGui;

namespace Restocker.Windows;

internal sealed class DateFilter
{
    private static readonly string[] Periods = ["All", "Today", "7 days", "30 days", "Custom"];
    private int period;
    private string from = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string to = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal (int, string, string) Selection => (period, from, to);

    public bool Draw(out DateTime? start, out DateTime? end)
    {
        start = end = null;
        ImGui.SetNextItemWidth(125);
        ImGui.Combo("Period", ref period, Periods, Periods.Length);
        var now = DateTime.UtcNow;
        if (period == 0) return true;
        if (period < 4)
        {
            start = period == 1 ? DateTime.Today.ToUniversalTime() : now.AddDays(period == 2 ? -7 : -30);
            end = now.AddTicks(1);
            return true;
        }
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("From", ref from, 10);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Through", ref to, 10);
        if (!Parse(from, out var first) || !Parse(to, out var last) || last < first || last == DateTime.MaxValue.Date)
        {
            ImGui.TextDisabled("Enter local dates as YYYY-MM-DD, earliest first.");
            return false;
        }
        start = first.ToUniversalTime();
        end = last.AddDays(1).ToUniversalTime();
        return true;
    }

    private static bool Parse(string value, out DateTime date)
        => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
}
