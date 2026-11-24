using System.Collections.Generic;

namespace Restocker.Market;

internal sealed class PriceMatchOverrides
{
    // Click identity matters if the user queues the same price during a running cycle.
    private sealed record Review(long Price);
    private readonly Dictionary<(uint, bool), Review> queued = new();
    private Dictionary<(uint, bool), Review> cycle = new();
    private readonly Dictionary<(uint, bool, RepriceReason, long), long?> reported = new();

    public bool ShouldReport(uint item, bool hq, RepriceReason reason, long proposed, long? rejectedReview = null)
    {
        var key = (item, hq, reason, proposed);
        if (reported.TryGetValue(key, out var previous) && (rejectedReview == null || rejectedReview == previous))
            return false;
        reported[key] = rejectedReview;
        return true;
    }

    public void Queue(uint itemId, bool isHq, long proposed) => queued[(itemId, isHq)] = new(proposed);

    public void BeginCycle() => cycle = new(queued);

    public long? ForPlan(uint itemId, bool isHq)
        => cycle.GetValueOrDefault((itemId, isHq))?.Price;

    public void Consume(uint itemId, bool isHq)
    {
        var key = (itemId, isHq);
        if (cycle.TryGetValue(key, out var review) && ReferenceEquals(queued.GetValueOrDefault(key), review))
            queued.Remove(key);
    }
}
