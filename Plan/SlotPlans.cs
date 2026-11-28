using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Plan;

public sealed record SlotPlanRow(uint ItemId, bool IsHq, int Slots, int PerListing)
{
    internal bool Matches(uint id, bool hq) => ItemId == id && IsHq == hq;
}

public sealed class SlotPlans : Dictionary<string, List<SlotPlanRow>>
{
    internal IReadOnlyList<SlotPlanRow> Rows(string key) => TryGetValue(key, out var rows) ? rows : [];

    internal IEnumerable<SlotPlanRow> ForCharacter(ulong cid) => this.Where(p => p.Key.StartsWith($"{cid:X}.", StringComparison.Ordinal))
        .SelectMany(p => p.Value);

    internal int Target(ulong cid, uint id, bool hq) => ForCharacter(cid).Where(r => r.Matches(id, hq))
        .Sum(r => r.Slots * r.PerListing);

    internal bool TrySave(string key, List<SlotPlanRow> rows, out string? reason)
    {
        reason = rows.Any(r => r.ItemId == 0 || r.Slots < 1 || r.PerListing < 1
            || r.PerListing > InventoryEntry.ListingLimit(r.ItemId, int.MaxValue)) ? "Enter a valid slot count and listing quantity."
            : rows.Sum(r => (long)r.Slots) > Planner.MaxListingSlots ? "A retainer has only 20 market slots."
            : rows.DistinctBy(r => (r.ItemId, r.IsHq)).Count() != rows.Count ? "This quality is already planned here." : null;
        if (reason != null) return false;
        if (rows.Count == 0) Remove(key);
        else this[key] = rows.ToList();
        return true;
    }

    internal SlotLayout Layout(RetainerSnapshot seller, IEnumerable<PlannedAction>? earlier = null)
    {
        var actual = seller.Listings.ToList();
        foreach (var action in earlier ?? [])
        {
            if (action.Kind != PlannedActionKind.NewListing || action.RetainerKey != seller.Key) continue;
            var index = Enumerable.Range(0, Planner.MaxListingSlots).FirstOrDefault(i => actual.All(l => l.ListingIndex != i), -1);
            if (index < 0) break;
            actual.Add(new() { ListingIndex = index, ItemId = action.ItemId, IsHQ = action.IsHQ });
        }
        return new(Rows(seller.Key), actual);
    }
}

internal sealed record BoardSlot(int Index, ListingEntry? Listing, SlotPlanRow? Plan)
{
    internal bool Filled => Listing != null && Plan?.Matches(Listing.ItemId, Listing.IsHQ) == true;
    internal bool Blocking => Listing != null && Plan != null && !Filled;
}

// Match actual listings first. Allocate missing reservations to empty slots in plan order,
// then overlay any unmet reservations on surplus listings.
internal sealed class SlotLayout
{
    internal BoardSlot[] Slots { get; }
    internal int Used => Slots.Count(s => s.Listing != null);
    internal int Reserved => Slots.Count(s => s.Plan != null && s.Listing == null);
    internal int Free => Slots.Length - Used - Reserved;
    internal int Extra => Slots.Count(s => s.Listing != null && !s.Filled);
    internal int Blocking => Slots.Count(s => s.Blocking);
    internal int Missing => Reserved + Blocking;

    internal SlotLayout(IReadOnlyList<SlotPlanRow> rows, IReadOnlyList<ListingEntry> actual)
    {
        Slots = Enumerable.Range(0, Planner.MaxListingSlots)
            .Select(i => new BoardSlot(i, actual.FirstOrDefault(l => l.ListingIndex == i), null)).ToArray();
        var missing = new List<SlotPlanRow>();
        foreach (var row in rows)
        {
            var matches = Slots.Where(s => s.Plan == null && s.Listing is { } l && row.Matches(l.ItemId, l.IsHQ))
                .Take(row.Slots).ToArray();
            foreach (var slot in matches) Slots[slot.Index] = slot with { Plan = row };
            missing.AddRange(Enumerable.Repeat(row, row.Slots - matches.Length));
        }
        foreach (var (slot, row) in Slots.Where(s => s.Plan == null).OrderBy(s => s.Listing != null).Zip(missing))
            Slots[slot.Index] = slot with { Plan = row };
    }

    internal int ReservedFor(uint id, bool hq) => Slots.Count(s => s.Listing == null && s.Plan?.Matches(id, hq) == true);
    internal bool Assigned(uint id, bool hq) => Slots.Any(s => s.Plan?.Matches(id, hq) == true);
    internal int OverflowFor(uint id, bool hq) => Assigned(id, hq) ? 0 : Free;
    internal int Destination(uint id, bool hq)
    {
        var assigned = Assigned(id, hq);
        return Slots.Where(s => s.Listing == null && (assigned ? s.Plan?.Matches(id, hq) == true : s.Plan == null))
            .Select(s => s.Index).FirstOrDefault(-1);
    }
}
