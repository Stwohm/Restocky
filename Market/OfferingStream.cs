using System;

namespace Restocker.Market;

// The two most recent distinct items suffice to find the last foreign page for any item.
internal sealed class OfferingStream
{
    private readonly object gate = new();
    private uint latestItem, previousItem;
    private DateTime latest, previous;

    internal DateTime Latest { get { lock (gate) return latest; } }

    internal void Observe(uint itemId, DateTime received)
    {
        lock (gate)
        {
            if (itemId != latestItem)
            {
                previous = latest;
                previousItem = latestItem;
            }
            latestItem = itemId;
            latest = received;
        }
    }

    internal DateTime LastPageFor(uint itemId)
    {
        lock (gate) return latestItem == itemId ? latest : previousItem == itemId ? previous : default;
    }

    internal bool Overlaps(uint itemId, DateTime attemptStart)
    {
        lock (gate)
            return attemptStart - (latestItem == itemId ? previous : latest) < TimeSpan.FromMilliseconds(1500);
    }
}
