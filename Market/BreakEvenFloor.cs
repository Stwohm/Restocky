using System;
using System.Collections.Generic;
using System.Globalization;

namespace Restocker.Market;

public static class BreakEvenFloor
{
    public static (long Price, bool FromCost) Resolve(long manualFloor, decimal? unitCost,
        decimal saleTaxPercent, decimal minProfitPercent)
    {
        if (manualFloor > 0) return (manualFloor, false);
        if (unitCost is not { } cost) return (0, false);

        var retainedShare = 1m - saleTaxPercent / 100m;
        var profitFactor = 1m + minProfitPercent / 100m;
        var floor = cost >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Clamp(decimal.Ceiling(cost * profitFactor / retainedShare), 1m, long.MaxValue);
        return (floor, true);
    }

    internal static bool CommitManual(Dictionary<uint, long> floors, uint itemId, string text)
    {
        long value = 0;
        if (text.Length > 0 && !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;
        if (value == 0) return floors.Remove(itemId);
        if (floors.GetValueOrDefault(itemId) == value) return false;
        floors[itemId] = value;
        return true;
    }
}
