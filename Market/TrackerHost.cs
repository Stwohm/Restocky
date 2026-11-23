using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Restocker.Localization;

namespace Restocker.Market;

/// <summary>
/// Fork: owns the <see cref="ListingTracker"/>, feeds it the RetainerSale chat message and
/// saves it (debounced) to pluginConfigs\Restocky\ (listings.json / listings.csv / competitors.csv).
/// </summary>
public sealed class TrackerHost : IDisposable
{
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly string dir;
    private readonly Configuration configuration;
    private readonly List<ListingRecord> pendingStuckAlerts = new();
    private DateTime nextSaveUtc = DateTime.MinValue;

    public ListingTracker Tracker { get; }

    public TrackerHost(IFramework framework, IChatGui chat, IDataManager data, IPluginLog log, string dir,
        Configuration configuration, SaleCostConsumer consumeCost)
    {
        this.framework = framework;
        this.chat = chat;
        this.log = log;
        this.dir = dir;
        this.configuration = configuration;
        Tracker = ListingTracker.Load(System.IO.Path.Combine(dir, "listings.json"), consumeCost, message => log.Information(message));
        Tracker.ListingSold += OnListingSold;
        framework.Update += OnUpdate;
        ChatHook.Subscribe(chat, data, OnGameSaleMessage, OnAllaganSaleMessage);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        ChatHook.Unsubscribe(chat);
        Tracker.ListingSold -= OnListingSold;
        if (Tracker.Dirty) Tracker.Save(dir);
    }

    private void OnListingSold(ListingRecord listing, DateTime soldUtc)
    {
        if (configuration.Snapshots.TryGetValue(listing.RetainerKey, out var snapshot)
            && snapshot.TryRemoveSoldListing(listing.Slot, listing.ItemId, listing.IsHq,
                listing.Quantity, listing.LastPrice, soldUtc))
        {
            Plugin.Instance?.ListingScans.Forget(listing.RetainerKey, listing.Slot, listing.ItemId, listing.IsHq);
            configuration.Save();
        }
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextSaveUtc) return;
        nextSaveUtc = now + TimeSpan.FromSeconds(5);
        var contentId = Plugin.PlayerState.ContentId;
        if (contentId != 0)
        {
            var prefix = $"{contentId:X}.";
            foreach (var listing in Tracker.Records)
                if (listing.RetainerKey.StartsWith(prefix, StringComparison.Ordinal)
                    && Tracker.TryMarkStuckNotified(listing, now, configuration.StuckListingDays))
                    pendingStuckAlerts.Add(listing);
        }

        if (Tracker.Dirty && !Tracker.Save(dir))
        {
            log.Warning("[Restocker] could not write listings.json / csv");
            return;
        }

        // Fork: persist each notification flag before printing so a reload cannot repeat the alert.
        foreach (var listing in pendingStuckAlerts)
            if (ListingTracker.IsStuck(listing, now, configuration.StuckListingDays))
                Plugin.ChatPrint(string.Format(Strings.StuckAlert, listing.ItemName + (listing.IsHq ? " (HQ)" : ""),
                    listing.RetainerName, (now - listing.FirstSeenUtc).TotalDays));
        pendingStuckAlerts.Clear();
    }

    /// <summary>Game RetainerSale message: item/quality from the payload, net gil from the text.</summary>
    private void OnGameSaleMessage(uint itemId, bool isHq, string text)
    {
        var gil = SaleMessage.ParseGil(text);
        var rec = Tracker.OnSale(itemId, isHq, gil, DateTime.UtcNow, Plugin.PlayerState.ContentId);
        if (rec == null)
            log.Information($"[Restocker] sale message source=game item={itemId} hq={isHq} gil={gil}: no tracked listing");
        else
            LogSold(rec, gil, "game");
    }

    private void OnAllaganSaleMessage(IReadOnlyList<uint> itemIds, int qty, string itemName, long grossGil)
    {
        var netGil = ListingTracker.NetFromGross(grossGil);
        var rec = Tracker.OnAllaganSale(itemIds, qty, netGil, DateTime.UtcNow, Plugin.PlayerState.ContentId);
        if (rec == null)
            log.Information($"[Restocker] sale message source=allaganmarket item='{itemName}' ids={string.Join("/", itemIds)} qty={qty} gross={grossGil} net={netGil}: no tracked listing");
        else
        {
            rec.SoldGrossGil = grossGil;
            LogSold(rec, netGil, "allaganmarket");
        }
    }

    private void LogSold(ListingRecord rec, long gil, string source)
    {
        log.Information($"[Restocker] sold: {rec.ItemName} x{rec.Quantity} on {rec.RetainerName} for {gil:N0} gil after {rec.DaysListed:0.0} days, {rec.RepriceCount} reprices, undercut {rec.UndercutCount}x by {string.Join(", ", rec.Competitors.Values.Where(c => c.TimesBelowUs > 0).Select(c => c.Seller))} (source={source})");
    }
}
