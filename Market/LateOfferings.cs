using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

// Retain the request identity after repairing an empty scan so additional pages from
// that request can fill both quality pools while the executor is working on other rows.
internal sealed class LateOfferings
{
    private int? requestId;
    private readonly Dictionary<ulong, MarketOffer> listings = new();

    internal bool Accepts(int request) => listings.Count == 0 || requestId == request;
    internal bool HasPages => requestId.HasValue;

    internal MarketScan? Merge(int request, IEnumerable<(ulong Id, MarketOffer Offer)> page, uint world)
    {
        if (!Accepts(request)) return null;
        var changed = !HasPages;
        requestId = request;
        foreach (var (id, offer) in page)
            changed |= listings.TryAdd(id, offer);
        return changed ? new MarketScan(listings.Values.Where(o => !o.IsHQ), listings.Values.Where(o => o.IsHQ))
            { WorldId = world } : null;
    }
}
