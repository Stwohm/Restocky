using System;
using System.Collections.Generic;

namespace Restocker.Plan;

internal static class InventoryReserve
{
    internal static int Available(int held, int keep) => Math.Max(0, held - Math.Max(0, keep));

    // If the character started below Keep, selling confirmed incoming units may restore
    // that starting balance. The listing is still capped at the confirmed transfer size.
    internal static int AfterTransfer(int held, int keep, int? beforeTransfer)
        => Available(held, Math.Min(keep, beforeTransfer ?? int.MaxValue));

    // Protect the last character stacks first so the planner can still prefer the bag.
    internal static void Apply(List<RestockSource> sources, int keep)
    {
        for (var i = sources.Count - 1; i >= 0 && keep > 0; i--)
        {
            var source = sources[i];
            if (source.FixedTargetKey != null) continue;
            var protectedUnits = Math.Min(source.Available, keep);
            keep -= protectedUnits;
            if (protectedUnits == source.Available) sources.RemoveAt(i);
            else sources[i] = source with { Available = source.Available - protectedUnits, Reserved = protectedUnits };
        }
    }
}
