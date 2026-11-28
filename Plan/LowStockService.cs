using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Plan;

internal readonly record struct StockSourceReading(string Label, StockReading Reading, bool Provisional = false);

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
    private readonly Dictionary<(ulong, uint, bool), bool> notified = new();
    private DateTime nextCheckUtc = DateTime.MinValue;
    private DateTime loginUtc = DateTime.UtcNow;

    public LowStockService(IFramework framework, Configuration configuration, ListingTracker tracker, MarketCache marketCache, CostLedger costLedger)
    {
        this.framework = framework;
        this.configuration = configuration;
        this.tracker = tracker;
        this.marketCache = marketCache;
        this.costLedger = costLedger;
        Plugin.ClientState.Login += OnLogin;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.ClientState.Login -= OnLogin;
        framework.Update -= OnUpdate;
    }

    private void OnLogin() => loginUtc = DateTime.UtcNow;

    private static ulong Cid() => Plugin.PlayerState.ContentId;

    private IEnumerable<RetainerSnapshot> MyRetainers()
    {
        var cid = Cid();
        return configuration.Snapshots.Values.Where(s => cid != 0 && s.CharacterContentId == cid);
    }

    /// <summary>Watched items at or below their threshold, with the listed count.</summary>
    public List<(LowStockEntry Entry, int Listed)> LowItems()
        => configuration.LowStock.Select(e => (e, ListedUnits(e.ItemId, e.IsHq)))
            .Where(x => x.e.IsLow(x.Item2)).ToList();

    public int ListedUnits(uint itemId, bool hq) => ListedStock(itemId, hq).Listed;

    internal (int Listed, bool Provisional) ListedStock(uint itemId, bool hq)
    {
        var keys = MyRetainers().Select(s => s.Key).ToHashSet();
        return tracker.ListedStock(itemId, hq, keys);
    }

    // Fork: match the scope of recorded sales using stock across all tracked characters.
    public Dictionary<(uint ItemId, bool IsHq), (long Listed, long Available, bool Provisional)> GetTrackedStock()
    {
        var stock = new Dictionary<(uint, bool), (long Listed, long Available, bool Provisional)>();
        foreach (var listing in tracker.ActiveListings)
        {
            var key = (listing.ItemId, listing.IsHq);
            var count = stock.GetValueOrDefault(key);
            stock[key] = (count.Listed + listing.Quantity, count.Available, count.Provisional || listing.MissingSinceUtc != null);
        }

        foreach (var item in configuration.Snapshots.Values.SelectMany(s => s.Inventory).Where(i => i.IsListable))
            AddAvailable((item.ItemId, item.IsHQ), item.Quantity);
        foreach (var character in configuration.Characters.Values)
        {
            var groups = character.Bag.Concat(character.Saddlebag).Concat(character.PremiumSaddlebag)
                .Where(i => i.IsListable).GroupBy(i => (i.ItemId, i.IsHQ));
            foreach (var group in groups)
                AddAvailable(group.Key, InventoryReserve.Available(group.Sum(i => i.Quantity),
                    configuration.KeepFor(group.Key.ItemId, group.Key.IsHQ)));
        }
        return stock;

        void AddAvailable((uint, bool) key, int quantity)
        {
            var count = stock.GetValueOrDefault(key);
            stock[key] = (count.Listed, count.Available + quantity, count.Provisional);
        }
    }

    public List<RestockSource> Sources(uint itemId, bool hq)
        => AutoRestockPlanner.Sources(itemId, hq, MyRetainers(),
            configuration.Characters.GetValueOrDefault(CharacterSnapshot.MakeKey(Cid())), configuration.KeepFor(itemId, hq));

    public int PerListing(LowStockEntry e)
        => RestockPlanner.PerListing(e, tracker.ActiveFor(e.ItemId, e.IsHq, MyRetainers().Select(r => r.Key).ToHashSet())
            .Select(r => r.Quantity));

    /// <summary>Entry price, else a floored suggestion from our listing or the market, else 0.</summary>
    public long Price(LowStockEntry e) => PriceForRestock(e).UnitPrice;

    internal RestockPrice PriceForRestock(LowStockEntry e)
    {
        if (e.Price > 0) return new(ApplyFloor(e, e.Price), AutoRestockPriceKind.Manual);
        var ours = CurrentPrice(e);
        if (ours > 0) return new(ApplyFloor(e, ours), AutoRestockPriceKind.Listing);
        var offer = marketCache.CachedLowest(Plugin.Instance.MarketWatcher.HomeWorldId, e.ItemId, e.IsHq,
            configuration.MatchQuality.For(e.ItemId, e.IsHq));
        return RestockPricing.FromCache(offer, DateTime.UtcNow, configuration.AutoRestockMaxScanAgeHours,
            configuration.UndercutGil, configuration.EffectiveFloorFor(e.ItemId, e.IsHq, costLedger).Price) ?? default;
    }

    internal long EffectiveFloor(uint id, bool hq) => configuration.EffectiveFloorFor(id, hq, costLedger).Price;

    internal StockReading Freshness(DateTime stamp, DateTime now)
        => new StockFreshness(now, loginUtc, Math.Max(1, configuration.StockMaxAgeHours)).Count(0, stamp);

    internal unsafe RetainerSnapshot[] CurrentRetainers()
    {
        var cid = Cid();
        if (cid == 0) return [];
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) return MyRetainers().OrderBy(r => r.DisplayOrder ?? int.MaxValue).ThenBy(r => r.RetainerName).ToArray();

        var retainers = new List<RetainerSnapshot>();
        for (uint i = 0; i < manager->GetRetainerCount(); ++i)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0) continue;
            var key = RetainerSnapshot.MakeKey(cid, retainer->RetainerId);
            retainers.Add(configuration.Snapshots.GetValueOrDefault(key) ?? new RetainerSnapshot
            {
                Key = key,
                CharacterContentId = cid,
                RetainerName = retainer->NameString,
            });
        }
        return retainers.ToArray();
    }

    internal (List<StockSourceReading> Inventory, List<StockSourceReading> Listings) StockReadings(
        LowStockEntry entry, IReadOnlyList<RetainerSnapshot> retainers, DateTime now)
    {
        var freshness = new StockFreshness(now, loginUtc, Math.Max(1, configuration.StockMaxAgeHours));
        var character = configuration.Characters.GetValueOrDefault(CharacterSnapshot.MakeKey(Cid()));
        var crystals = InventoryEntry.IsCrystal(entry.ItemId);
        var inventory = new List<StockSourceReading>
        {
            new(crystals ? "Crystals" : Strings.StockBag, ReadInventory(character?.Bag ?? [],
                crystals ? RetainerWatcher.PlayerCrystals : RetainerWatcher.CharacterPages, character?.InventoryRefreshedUtc)),
        };
        if (!crystals)
        {
            inventory.Add(new(Strings.StockSaddlebag, ReadInventory(character?.Saddlebag ?? [],
                RetainerWatcher.SaddlebagTypes, character?.InventoryRefreshedUtc)));
            inventory.Add(new(Strings.StockPremiumSaddlebag, ReadInventory(character?.PremiumSaddlebag ?? [],
                RetainerWatcher.PremiumSaddlebagTypes, character?.InventoryRefreshedUtc)));
        }
        var listings = new List<StockSourceReading>();
        var active = tracker.ActiveFor(entry.ItemId, entry.IsHq).ToLookup(l => l.RetainerKey);
        foreach (var retainer in retainers)
        {
            inventory.Add(new(string.Format(Strings.StockRetainerInventory, retainer.RetainerName),
                ReadInventory(retainer.Inventory, crystals ? RetainerWatcher.RetainerCrystals : RetainerWatcher.RetainerPages, retainer.InventoryRefreshedUtc)));
            var records = active[retainer.Key];
            listings.Add(new(string.Format(Strings.StockRetainerListed, retainer.RetainerName),
                freshness.Count(records.Sum(l => l.Quantity), retainer.LastRefreshedUtc),
                records.Any(l => l.MissingSinceUtc != null)));
        }
        return (inventory, listings);

        StockReading ReadInventory(IEnumerable<InventoryEntry> entries, InventoryType[] types,
            IReadOnlyDictionary<uint, DateTime>? timestamps)
            => freshness.Inventory(entries, types.Select(t => (uint)t), timestamps, entry.ItemId, entry.IsHq);
    }

    internal string NextCyclePreview(LowStockEntry entry, IReadOnlyList<RetainerSnapshot> retainers, DateTime now)
    {
        if (entry.EffectiveRestockMode == StockRestockMode.Alert) return Strings.StockAlertOnly;
        if (!configuration.AutoRestockDuringCycle) return Strings.StockCycleDisabled;
        if (configuration.IsHeld(entry.ItemId, entry.IsHq)) return Strings.StockCycleHeld;
        if (retainers.Count == 0 || retainers.Any(r => r.LastRefreshedUtc == DateTime.MinValue))
            return Strings.StockCycleListingsUnavailable;

        var character = configuration.Characters.GetValueOrDefault(CharacterSnapshot.MakeKey(Cid()));
        var plan = AutoRestockPlanner.Plan(configuration.LowStock, retainers, character, tracker, configuration.IsHeld,
                (id, hq) => configuration.EffectiveFloorFor(id, hq, costLedger).Price, configuration.KeepFor, configuration.SlotPlans)
            .Find(p => p.ItemId == entry.ItemId && p.IsHq == entry.IsHq);
        if (plan == null) return $"{Strings.StockCycleEnoughListed}\n{Strings.StockCycleSnapshot}";
        if (plan.Actions.Count == 0)
        {
            var reason = plan.SkipReason == RestockSkipReason.None ? Strings.AutoRestockPriceLabel(plan.PriceKind)
                : Strings.RestockSkipLabel(plan.SkipReason);
            return $"{string.Format(Strings.StockCycleSkip, reason)}\n{Strings.StockCycleSnapshot}";
        }

        var lines = new List<string>();
        foreach (var actions in plan.Actions.GroupBy(a => a.RetainerKey))
        {
            var name = retainers.First(r => r.Key == actions.Key).RetainerName;
            foreach (var source in actions.Where(a => a.NeedsCrystalWithdrawal).GroupBy(a => a.SourceKey))
                lines.Add(Strings.CrystalTransferPlan(retainers.First(r => r.Key == source.Key).RetainerName, source.Sum(a => a.Quantity), name));
            var units = actions.Sum(a => a.Quantity);
            lines.Add(plan.PriceKind == AutoRestockPriceKind.Pending
                ? string.Format(Strings.StockCycleCompare, name, units)
                : string.Format(Strings.StockCycleListing, name, units, actions.First().UnitPrice,
                    Strings.AutoRestockPriceLabel(plan.PriceKind)));
        }
        if (plan.PriceKind == AutoRestockPriceKind.Pending)
        {
            var world = Plugin.Instance.MarketWatcher.HomeWorldId;
            var cached = marketCache.CachedLowest(world, entry.ItemId, entry.IsHq, configuration.MatchQuality.For(entry.ItemId, entry.IsHq));
            var floor = configuration.EffectiveFloorFor(entry.ItemId, entry.IsHq, costLedger).Price;
            var fallback = RestockPricing.Resolve(null, cached, now, configuration.AutoRestockMaxScanAgeHours,
                configuration.UndercutGil, floor, () => RestockPricing.LastSale(tracker, entry.ItemId, entry.IsHq, Cid(),
                    world, configuration.Snapshots));
            if (fallback.UnitPrice > 0)
                lines.Add(string.Format(Strings.StockCycleFallback, fallback.UnitPrice,
                    Strings.AutoRestockPriceLabel(fallback.Kind, fallback.CacheAge)));
        }
        lines.Add(Strings.StockCycleSnapshot);
        return string.Join("\n", lines);
    }

    internal int PlannedTarget(LowStockEntry e) => configuration.SlotPlans.Target(Cid(), e.ItemId, e.IsHq);

    internal Dictionary<string, int> FreeListingSlots() => MyRetainers().ToDictionary(
        s => s.Key, s => Math.Max(0, Planner.MaxListingSlots - s.Listings.Count));

    public List<PlannedAction> PlanRestock(LowStockEntry e, int targetUnits, long unitPrice, bool allowTopUps = true,
        IReadOnlyDictionary<string, int>? freeSlots = null, IReadOnlyList<PlannedAction>? earlier = null,
        bool fillOnly = false, string? fillRetainer = null)
    {
        var cid = Cid();
        if (!e.Enabled || cid == 0) return [];
        var retainers = configuration.SlotPlans.ForCharacter(cid).Any() ? CurrentRetainers().ToList() : MyRetainers().ToList();
        var sources = AutoRestockPlanner.Sources(e.ItemId, e.IsHq, retainers,
            configuration.Characters.GetValueOrDefault(CharacterSnapshot.MakeKey(cid)), configuration.KeepFor(e.ItemId, e.IsHq));
        var listed = ListedUnits(e.ItemId, e.IsHq);
        var actions = new List<PlannedAction>();
        var floor = configuration.EffectiveFloorFor(e.ItemId, e.IsHq, costLedger).Price;
        if (unitPrice > 0) unitPrice = Math.Max(unitPrice, floor);
        var plannedTarget = PlannedTarget(e);
        if (plannedTarget > 0) targetUnits = plannedTarget;
        if (allowTopUps && plannedTarget == 0 && !fillOnly && configuration.TopUpPartialStacksOnRestock && listed > 0 && sources.Count > 0
            && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(e.ItemId, out var row))
        {
            var active = tracker.ActiveFor(e.ItemId, e.IsHq).Where(r => r.MissingSinceUtc == null)
                .ToLookup(r => (r.RetainerKey, r.Slot));
            var listings = retainers.SelectMany(r => r.Listings
                .Where(l => l.UnitPrice >= floor && active[(r.Key, l.ListingIndex)].Any(record => l.ItemId == record.ItemId
                    && l.IsHQ == record.IsHq && l.Quantity == record.Quantity))
                .Select(l => (r.Key, l)));
            actions = StackTopUpPlanner.Plan(e.ItemId, e.IsHq, RetainerWatcher.ResolveMaxListingStack(row),
                (int)row.StackSize, listings, sources, e.PerListing,
                InventoryEntry.IsCrystal(e.ItemId) ? Math.Max(0, targetUnits - listed) : int.MaxValue);
            listed += actions.Sum(a => a.TopUp!.AddedUnits);
        }

        var free = freeSlots ?? retainers.ToDictionary(s => s.Key, s => Math.Max(0, Planner.MaxListingSlots - s.Listings.Count));
        actions.AddRange(RestockPlanner.Plan(e.ItemId, e.IsHq, targetUnits, listed, PerListing(e), unitPrice,
            sources, free, PreferredRetainers(e), e.PreferredRetainers.GetValueOrDefault(cid), retainers,
            configuration.SlotPlans, earlier, fillOnly, fillRetainer));
        return actions;
    }

    /// <summary>
    /// Retainers that sell this item now (active listing), then the ones that sold it most recently.
    /// Restock lists there first when they have a free slot.
    /// </summary>
    public List<string> PreferredRetainers(LowStockEntry e)
        => tracker.PreferredRetainers(e.ItemId, e.IsHq, MyRetainers().Select(s => s.Key).ToHashSet());

    /// <summary>
    /// Market lowest (bait filtered) - undercut from the last fetch, with when it was fetched.
    /// Null when neither a live scan nor a saved price is available for the home world.
    /// </summary>
    public (long Price, DateTime FetchedUtc)? Suggestion(LowStockEntry e)
    {
        var world = Plugin.Instance.MarketWatcher.HomeWorldId;
        var matchQuality = configuration.MatchQuality.For(e.ItemId, e.IsHq);
        var offer = marketCache.CachedLowest(world, e.ItemId, e.IsHq, matchQuality, CurrentPrice(e));
        return offer is { } found ? (PriceFromLowest(e, found.Lowest), found.UpdatedUtc) : null;
    }

    private long CurrentPrice(LowStockEntry e)
    {
        var keys = MyRetainers().Select(s => s.Key).ToHashSet();
        return tracker.ActiveFor(e.ItemId, e.IsHq).Where(r => keys.Contains(r.RetainerKey))
            .Select(r => r.LastPrice).Where(p => p > 0).DefaultIfEmpty(0).Min();
    }

    private long PriceFromLowest(LowStockEntry e, long lowest)
        => lowest > 0 ? ApplyFloor(e, Math.Max(1, lowest - Math.Max(0, configuration.UndercutGil))) : 0;

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
        var cid = Cid();
        if (cid == 0 || configuration.LowStock.Count == 0) return;

        foreach (var e in configuration.LowStock)
        {
            var key = (cid, e.ItemId, e.IsHq);
            if (!e.Enabled)
            {
                notified.Remove(key);
                continue;
            }
            var listed = ListedUnits(e.ItemId, e.IsHq);
            var flag = notified.TryGetValue(key, out var f) && f;
            var fire = RestockPlanner.ShouldNotify(listed, e.Threshold, ref flag);
            notified[key] = flag;
            if (!fire) continue;

            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(e.ItemId, out var row)
                ? row.Name.ExtractText() : $"#{e.ItemId}";
            var avail = Sources(e.ItemId, e.IsHq).Sum(s => s.Available);
            Plugin.Log.Information($"[Restocker] low stock alert: {name} listed={listed} threshold={e.Threshold}");
            Plugin.ChatPrint(string.Format(Strings.StockTrackingAlert, name + (e.IsHq ? " (HQ)" : ""), listed, e.Threshold, avail));
        }
    }
}
