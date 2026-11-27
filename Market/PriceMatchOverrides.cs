using System.Collections.Generic;

namespace Restocker.Market;

internal sealed class PriceMatchOverrides
{
    // Click identity matters if the user queues the same price during a running cycle.
    private sealed record Review(long Price, bool Floor);
    private readonly Dictionary<(uint, bool), Review> queued = new();
    private Dictionary<(uint, bool), Review> cycle = new();

    public void Queue(uint itemId, bool isHq, long proposed, bool floor = false) => queued[(itemId, isHq)] = new(proposed, floor);

    public bool IsPending(uint item, bool hq, long price, bool floor, bool running)
    {
        var review = new Review(price, floor);
        return queued.GetValueOrDefault((item, hq)) == review
            || (running && cycle.GetValueOrDefault((item, hq)) == review);
    }

    public void BeginApply() => cycle.Clear();

    public void Started(uint item, bool hq, long price, bool floor)
    {
        queued.Remove((item, hq));
        cycle[(item, hq)] = new(price, floor);
    }

    public bool ReviewedFloor(uint itemId, bool isHq) => cycle.GetValueOrDefault((itemId, isHq))?.Floor == true;

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
