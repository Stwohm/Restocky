using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Restocker.Market;

/// <summary>
/// Captures home-world competitor offers by quality from <see cref="IMarketBoard.OfferingsReceived"/>.
/// Own retainers are excluded before publishing the scan.
/// </summary>
public sealed unsafe class MarketWatcher : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private int requestGeneration;
    private volatile bool disposed;
    public MarketCache Cache { get; } = new();
    private readonly ConcurrentDictionary<(uint WorldId, uint ItemId), MarketSaleScan> saleHistory = new();

    internal uint HomeWorldId => objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
    internal IEnumerable<KeyValuePair<(uint WorldId, uint ItemId), MarketSaleScan>> SaleHistory => saleHistory;

    /// <summary>
    /// Fork: fired for answers that belong to our own fetch (expected item set), i.e. the home
    /// world board seen from our retainer. Browsing another world's board never fires it.
    /// Args: itemId and the complete market scan.
    /// </summary>
    public event Action<uint, MarketScan>? CompetitorsObserved;

    // Fork: fresh offers from an identified search, with whether the executor requested it.
    internal event Action<uint, MarketScan, bool>? OffersUpdated;

    /// <summary>同じ検索結果が複数回飛んでくる server エラー時の保護。</summary>
    private int lastRequestId = -1;

    /// <summary>Executor が自動 fetch している間にセットされる「期待アイテム」。
    /// これと違う itemId の OfferingsReceived は server からの遅延着信扱いで cache 更新しない。
    /// null のとき (= 自動 fetch 中でない) は通常通り全結果を cache に入れる。</summary>
    private uint? expectedItemId;
    private bool expectedIsHq;

    public void SetExpectedItem(uint itemId, bool isHq)
    {
        expectedItemId = itemId;
        expectedIsHq = isHq;
        Interlocked.Increment(ref requestGeneration);
    }

    public void ClearExpectedItem()
    {
        expectedItemId = null;
        Interlocked.Increment(ref requestGeneration);
    }

    internal bool RecordEmptySearch(uint itemId)
    {
        var player = objectTable.LocalPlayer;
        if (itemId == 0 || expectedItemId != itemId
            || !MarketWorldGate.Accept(player?.CurrentWorld.RowId ?? 0, player?.HomeWorld.RowId ?? 0))
            return false;

        Cache.Replace(itemId, new MarketScan([], []) { WorldId = HomeWorldId });
        return true;
    }

    public MarketWatcher(IMarketBoard marketBoard, IObjectTable objectTable, IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.objectTable = objectTable;
        this.log = log;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
        marketBoard.HistoryReceived += OnHistoryReceived;
    }

    public void Dispose()
    {
        disposed = true;
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
        marketBoard.HistoryReceived -= OnHistoryReceived;
    }

    private void OnHistoryReceived(IMarketBoardHistory history)
        => OnFramework(() => RecordHistory(history));

    private void OnFramework(Action record)
    {
        var generation = Volatile.Read(ref requestGeneration);
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!disposed && generation == Volatile.Read(ref requestGeneration)) record();
        });
    }

    private void RecordHistory(IMarketBoardHistory history)
    {
        var player = objectTable.LocalPlayer;
        var homeWorld = player?.HomeWorld.RowId ?? 0;
        var itemId = history.ItemId;
        if (!MarketWorldGate.Accept(player?.CurrentWorld.RowId ?? 0, homeWorld)
            || itemId == 0 || (expectedItemId is { } expected && expected != itemId)) return;

        var sales = history.HistoryListings
            .Where(s => s.SalePrice > 0 && s.Quantity is > 0 and <= int.MaxValue)
            .Select(s => new MarketSale(itemId, s.IsHq, (int)s.Quantity, s.SalePrice, s.PurchaseTime, s.BuyerName))
            .ToArray();
        saleHistory[(homeWorld, itemId)] = new MarketSaleScan(DateTime.UtcNow, sales);
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        => OnFramework(() => RecordOffers(offerings));

    private void RecordOffers(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            // An empty packet has no item identity. The executor checks the search addon instead.
            if (offerings.ItemListings.Count == 0 || offerings.RequestId == lastRequestId) return;
            var itemId = offerings.ItemListings[0].ItemId;
            var player = objectTable.LocalPlayer;
            if (!MarketWorldGate.Accept(player?.CurrentWorld.RowId ?? 0, player?.HomeWorld.RowId ?? 0))
            {
                log.Information($"[Restocker] OfferingsReceived item={itemId} ignored: not on home world");
                return;
            }

            if (itemId == 0) return;

            // 自動 fetch 中は「期待アイテム」と一致するものだけ採用する。
            // server が古いリクエストの応答を遅延配送してくると、現在 fetch 中の
            // listing と違うアイテムの結果が「最新の cache」になってしまうため。
            if (expectedItemId.HasValue && (expectedItemId.Value == 0 || itemId != expectedItemId.Value))
            {
                log.Information($"[Restocker] OfferingsReceived for item={itemId} ignored (expected {expectedItemId.Value})");
                return;
            }
            lastRequestId = offerings.RequestId;

            // デバッグ用に受信 listing をすべてログに出す。OWN マークは自リテイナーで
            // スキップされた listing。各行: HQ/NQ, price, retainerId。
            var dump = new System.Text.StringBuilder();
            dump.Append($"[Restocker] OfferingsReceived item={itemId} count={offerings.ItemListings.Count} reqId={offerings.RequestId}");

            // Fork: keep every competitor price (ascending as delivered) so
            // LowestPriceResolver can actually drop bait listings. Upstream kept only the first.
            var nqPrices = new System.Collections.Generic.List<MarketOffer>();
            var hqPrices = new System.Collections.Generic.List<MarketOffer>();
            for (var idx = 0; idx < offerings.ItemListings.Count; idx++)
            {
                var listing = offerings.ItemListings[idx];
                if (listing.ItemId != itemId) continue;
                var isOwn = IsOwnRetainer(listing.RetainerId);
                var price = (long)listing.PricePerUnit;
                var hqMark = listing.IsHq ? "HQ" : "NQ";
                var ownMark = isOwn ? " OWN" : "";
                dump.Append($"\n  [{idx,2}] {hqMark} price={price,9:N0} retainer={listing.RetainerId}{ownMark}");

                if (isOwn) continue;
                if (price <= 0) continue;
                var offer = new MarketOffer(price, listing.RetainerName ?? string.Empty, (int)listing.ItemQuantity);
                if (listing.IsHq) hqPrices.Add(offer);
                else nqPrices.Add(offer);
            }

            // Both flags are always written (possibly empty) so "no HQ competitor" is a known
            // answer, not a timeout.
            var scan = new MarketScan(nqPrices, hqPrices) { WorldId = HomeWorldId };
            Cache.Replace(itemId, scan);
            if (expectedItemId.HasValue)
            {
                try
                {
                    CompetitorsObserved?.Invoke(itemId, scan);
                }
                catch (Exception ex) { log.Error(ex, "[Restocker] CompetitorsObserved handler threw"); }
            }
            OffersUpdated?.Invoke(itemId, scan, expectedItemId.HasValue);

            dump.Append($"\n  → nq={nqPrices.Count} (lowest {Cache.GetLowest(itemId, false, true):N0})  hq={hqPrices.Count} (lowest {Cache.GetLowest(itemId, true, true):N0})");
            log.Information(dump.ToString());
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] OfferingsReceived handler exception");
        }
    }

    private static bool IsOwnRetainer(ulong retainerId)
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return false;
        for (uint i = 0; i < rm->GetRetainerCount(); ++i)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId == retainerId) return true;
        }
        return false;
    }
}
