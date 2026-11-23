using System;
using System.IO;
using Dalamud.Plugin.Services;

namespace Restocker.Market;

// Fork: record fresh home-world scans using the logged-in character's retainer snapshots.
internal sealed class UndercutHistoryHost : IDisposable
{
    private readonly MarketWatcher watcher;
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly string path;
    private DateTime nextSaveUtc;
    private bool disposed;

    public UndercutHistory History { get; }
    public ListingMarketScans ListingScans { get; } = new();

    public UndercutHistoryHost(MarketWatcher watcher, Configuration configuration, IFramework framework,
        IPlayerState playerState, IObjectTable objectTable, IPluginLog log, string directory)
    {
        this.watcher = watcher;
        this.configuration = configuration;
        this.framework = framework;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.log = log;
        path = Path.Combine(directory, "undercut-history.json");
        History = UndercutHistory.Load(path, DateTime.UtcNow);
        watcher.OffersUpdated += OnOffersUpdated;
        framework.Update += OnUpdate;
    }

    private void OnOffersUpdated(uint itemId, MarketScan scan, bool executorFetch)
    {
        var observedUtc = DateTime.UtcNow;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (disposed) return;
            try
            {
                RecordScan(itemId, scan, observedUtc, executorFetch);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Restocker] could not record undercut history");
            }
        });
    }

    private void RecordScan(uint itemId, MarketScan scan, DateTime observedUtc, bool executorFetch)
    {
        var player = objectTable.LocalPlayer;
        var contentId = playerState.ContentId;
        if (contentId == 0 || player == null || player.HomeWorld.RowId == 0
            || player.CurrentWorld.RowId != player.HomeWorld.RowId) return;

        long lowestNq = 0;
        long lowestHq = 0;
        foreach (var snapshot in configuration.Snapshots.Values)
        {
            if (snapshot.CharacterContentId != contentId) continue;
            foreach (var listing in snapshot.Listings)
            {
                if (listing.ItemId != itemId) continue;
                // The executor records its scans against the live listings of the visited retainer.
                if (!executorFetch)
                    ListingScans.Record(snapshot.Key, listing.ListingIndex, itemId, listing.IsHQ,
                        new ListingMarketScan(observedUtc, listing.UnitPrice,
                            scan.Competitors(listing.IsHQ, configuration.MatchQuality.For(itemId, listing.IsHQ))));
                if (listing.UnitPrice <= 0) continue;
                ref var lowest = ref (listing.IsHQ ? ref lowestHq : ref lowestNq);
                if (lowest == 0 || listing.UnitPrice < lowest) lowest = listing.UnitPrice;
            }
        }

        if (lowestNq > 0) History.RecordScan(itemId, false, lowestNq, scan.Competitors(false, configuration.MatchQuality.For(itemId, false)), observedUtc);
        if (lowestHq > 0) History.RecordScan(itemId, true, lowestHq, scan.Competitors(true, configuration.MatchQuality.For(itemId, true)), observedUtc);
    }

    private void OnUpdate(IFramework _)
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < nextSaveUtc) return;
        nextSaveUtc = nowUtc.AddSeconds(5);
        SaveIfDirty(nowUtc);
    }

    private void SaveIfDirty(DateTime nowUtc)
    {
        if (History.Dirty && !History.Save(path, nowUtc))
            log.Warning("[Restocker] could not write undercut-history.json");
    }

    public void Dispose()
    {
        disposed = true;
        watcher.OffersUpdated -= OnOffersUpdated;
        framework.Update -= OnUpdate;
        SaveIfDirty(DateTime.UtcNow);
    }
}
