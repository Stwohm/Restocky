using System.Collections.Generic;

namespace Restocker.Market;

internal sealed class BaitMatchOverrides
{
    private enum CycleMatch { FilterBait, IncludeBait, Consumed }

    private readonly HashSet<(uint ItemId, bool IsHq)> queued = new();
    private readonly Dictionary<(uint ItemId, bool IsHq), CycleMatch> cycle = new();

    // Chat scrollback survives plugin reloads. Keep the ID stable and distinct from old sequential IDs.
    internal static uint LinkId(uint itemId, bool isHq) => 0x80000000u | (itemId << 1) | (isHq ? 1u : 0u);

    public void Queue(uint itemId, bool isHq) => queued.Add((itemId, isHq));

    public void BeginCycle() => cycle.Clear();

    // Freeze the choice at the first retainer plan so every copy uses it for this cycle.
    public bool IncludeBaitForPlan(uint itemId, bool isHq)
    {
        var key = (itemId, isHq);
        if (!cycle.TryGetValue(key, out var match))
        {
            match = queued.Contains(key) ? CycleMatch.IncludeBait : CycleMatch.FilterBait;
            cycle.Add(key, match);
        }
        return match != CycleMatch.FilterBait;
    }

    public void Consume(uint itemId, bool isHq)
    {
        var key = (itemId, isHq);
        if (!cycle.TryGetValue(key, out var match) || match != CycleMatch.IncludeBait) return;
        queued.Remove(key);
        cycle[key] = CycleMatch.Consumed;
    }
}
