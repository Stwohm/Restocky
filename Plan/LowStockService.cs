using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

/// <summary>
/// Fork: low-stock alerts and restock planning for the items in <see cref="Configuration.LowStock"/>.
/// "Listed" = units on the current character's retainer markets, from the listing tracker (a sale
/// message closes a listing immediately). "Available" = retainer inventories + bag + saddlebag
/// from the snapshots taken at the bell.
/// </summary>
public sealed class LowStockService : IDisposable
{
    private readonly IFramework framework;
    private readonly Configuration configuration;
    private readonly ListingTracker tracker;
    private readonly MarketCache marketCache;
    private readonly CostLedger costLedger;
    private readonly Dictionary<(uint, bool), bool> notified = new();
    private DateTime nextCheckUtc = DateTime.MinValue;

    public LowStockService(IFramework framework, Configuration configuration, ListingTracker tracker, MarketCache marketCache, CostLedger costLedger)
    {
        this.framework = framework;
        this.configuration = configuration;
        this.tracker = tracker;
        this.marketCache = marketCache;
        this.costLedger = costLedger;
        framework.Update += OnUpdate;
    }

    public void Dispose() => framework.Update -= OnUpdate;

    private static ulong Cid() => Plugin.PlayerState.ContentId;

    private IEnumerable<RetainerSnapshot> MyRetainers()
    {
        var cid = Cid();
        return configuration.Snapshots.Values.Where(s => cid == 0 || s.CharacterContentId == cid);
    }

    /// <summary>Watched items at or below their threshold, with the listed count.</summary>
    public List<(LowStockEntry Entry, int Listed)> LowItems()
        => configuration.LowStock.Select(e => (e, ListedUnits(e.ItemId, e.IsHq)))
            .Where(x => x.Item2 <= x.e.Threshold).ToList();

    public int ListedUnits(uint itemId, bool hq)
    {
        var keys = MyRetainers().Select(s => s.Key).ToHashSet();
        return tracker.ActiveFor(itemId, hq).Where(r => keys.Contains(r.RetainerKey)).Sum(r => r.Quantity);
    }

    // Fork: match the scope of recorded sales using stock across all tracked characters.
    public Dictionary<(uint ItemId, bool IsHq), (long Listed, long Available)> GetTrackedStock()
    {
        var stock = new Dictionary<(uint, bool), (long Listed, long Available)>();
        foreach (var listing in tracker.Records.Where(r => r.Outcome == ListingTracker.Active && r.MissingSinceUtc == null))
        {
            var key = (listing.ItemId, listing.IsHq);
            var count = stock.GetValueOrDefault(key);
            stock[key] = (count.Listed + listing.Quantity, count.Available);
        }

        var inventory = configuration.Snapshots.Values.SelectMany(s => s.Inventory)
            .Concat(configuration.Characters.Values.SelectMany(c => c.Bag.Concat(c.Saddlebag).Concat(c.PremiumSaddlebag)));
        foreach (var item in inventory.Where(i => i.IsListable))
        {
            var key = (item.ItemId, item.IsHQ);
            var count = stock.GetValueOrDefault(key);
            stock[key] = (count.Listed, count.Available + item.Quantity);
        }
        return stock;
    }

    public List<RestockSource> Sources(uint itemId, bool hq)
    {
        var list = new List<RestockSource>();
        foreach (var s in MyRetainers())
        {
            var n = s.Inventory.Where(e => e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity);
            if (n > 0) list.Add(new RestockSource(s.Key, s.Key, n));
        }
        if (configuration.Characters.TryGetValue(CharacterSnapshot.MakeKey(Cid()), out var ch))
        {
            var prefix = CharacterSnapshot.MakeKey(ch.CharacterContentId);
            var bag = ch.Bag.Where(e => e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity);
            if (bag > 0) list.Add(new RestockSource(prefix + ".bag", null, bag));
            var saddle = ch.Saddlebag.Concat(ch.PremiumSaddlebag).Where(e => e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity);
            if (saddle > 0) list.Add(new RestockSource(prefix + ".saddle", null, saddle));
        }
        return list;
    }

    public int PerListing(LowStockEntry e)
    {
        if (e.PerListing > 0) return e.PerListing;
        var qty = tracker.ActiveFor(e.ItemId, e.IsHq).GroupBy(r => r.Quantity).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return qty > 0 ? qty : 1;
    }

    /// <summary>Entry price, else a floored suggestion from our listing or the market, else 0.</summary>
    public long Price(LowStockEntry e)
    {
        if (e.Price > 0) return e.Price;
        var ours = CurrentPrice(e);
        return ours > 0 ? ApplyFloor(e, ours) : MarketSuggestion(e, ours);
    }

    public List<PlannedAction> PlanRestock(LowStockEntry e, int targetUnits, long unitPrice)
    {
        var free = MyRetainers().ToDictionary(s => s.Key, s => Math.Max(0, Planner.MaxListingSlots - s.Listings.Count));
        return RestockPlanner.Plan(e.ItemId, e.IsHq, targetUnits, ListedUnits(e.ItemId, e.IsHq),
            PerListing(e), unitPrice, Sources(e.ItemId, e.IsHq), free, PreferredRetainers(e));
    }

    /// <summary>
    /// Retainers that sell this item now (active listing), then the ones that sold it most recently.
    /// Restock lists there first when they have a free slot.
    /// </summary>
    public List<string> PreferredRetainers(LowStockEntry e)
    {
        var keys = MyRetainers().Select(s => s.Key).ToHashSet();
        return tracker.Records
            .Where(r => r.ItemId == e.ItemId && r.IsHq == e.IsHq && keys.Contains(r.RetainerKey))
            .OrderBy(r => r.Outcome == ListingTracker.Active ? 0 : 1)
            .ThenByDescending(r => r.EndedUtc ?? r.LastSeenUtc)
            .Select(r => r.RetainerKey)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Market lowest (bait filtered) - undercut from the last fetch, with when it was fetched.
    /// Null when the item was never fetched in this session.
    /// </summary>
    public (long Price, DateTime FetchedUtc)? Suggestion(LowStockEntry e)
    {
        var at = marketCache.UpdatedUtc(e.ItemId);
        if (at == null) return null;
        var price = MarketSuggestion(e, CurrentPrice(e));
        return (price, at.Value);
    }

    private long CurrentPrice(LowStockEntry e) =>
        tracker.ActiveFor(e.ItemId, e.IsHq).Select(r => r.LastPrice).Where(p => p > 0).DefaultIfEmpty(0).Min();

    private long MarketSuggestion(LowStockEntry e, long currentPrice)
    {
        var lowest = marketCache.GetLowest(e.ItemId, e.IsHq, configuration.MatchQuality.For(e.ItemId, e.IsHq), currentPrice);
        return lowest > 0 ? ApplyFloor(e, Math.Max(1, lowest - configuration.UndercutGil)) : 0;
    }

    private long ApplyFloor(LowStockEntry e, long price) =>
        Math.Max(price, configuration.EffectiveFloorFor(e.ItemId, e.IsHq, costLedger).Price);

    /// <summary>A retainer (of this character) that has the item listed, so its sell list can be used to compare prices.</summary>
    public string? RetainerToCompareFrom(LowStockEntry e)
    {
        var keys = MyRetainers().Select(s => s.Key).ToHashSet();
        return tracker.ActiveFor(e.ItemId, e.IsHq).Select(r => r.RetainerKey).FirstOrDefault(keys.Contains);
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextCheckUtc) return;
        nextCheckUtc = now + TimeSpan.FromSeconds(5);
        if (Cid() == 0 || configuration.LowStock.Count == 0) return;

        foreach (var e in configuration.LowStock)
        {
            var key = (e.ItemId, e.IsHq);
            var listed = ListedUnits(e.ItemId, e.IsHq);
            var flag = notified.TryGetValue(key, out var f) && f;
            var fire = RestockPlanner.ShouldNotify(listed, e.Threshold, ref flag);
            notified[key] = flag;
            if (!fire) continue;

            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(e.ItemId, out var row)
                ? row.Name.ExtractText() : $"#{e.ItemId}";
            var avail = Sources(e.ItemId, e.IsHq).Sum(s => s.Available);
            Plugin.Log.Information($"[Restocker] low stock alert: {name} listed={listed} threshold={e.Threshold}");
            Plugin.ChatPrint($"low stock: {name}{(e.IsHq ? " (HQ)" : "")}: {listed} listed (alert at ≤{e.Threshold}), {avail} available in retainers / bag / saddlebag. Restocky › Low stock to restock.");
        }
    }
}
