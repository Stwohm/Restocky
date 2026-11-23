using System.Numerics;
using Dalamud.Bindings.ImGui;
using Restocker.Localization;

namespace Restocker.Windows;

internal static class SaleReturnDisplay
{
    internal static void DrawEarning(decimal earning, string format, string? detail = null)
    {
        ImGui.TextColored(new Vector4(0.45f, 0.75f, 0.82f, 1f), earning.ToString(format));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(detail == null ? Strings.EarningTooltip : $"{Strings.EarningTooltip}\n{detail}");
    }
}
