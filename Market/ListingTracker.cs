using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restocker.Data;

namespace Restocker.Market;

public enum HistorySaleResult { Duplicate, MatchedListing, HistoryOnly }

/// <summary>Who else sells the same item (retainer name; the game does not send player names).</summary>
public sealed class CompetitorStat
{
    public string Seller { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long LowestPrice { get; set; }
    public int TimesSeen { get; set; }

    /// <summary>How many observations had this seller priced below our listing.</summary>
    public int TimesBelowUs { get; set; }

    /// <summary>Units this seller had up (all their listings summed) at the last observation.</summary>
    public int LastQuantity { get; set; }

    /// <summary>Most units this seller had up at once.</summary>
    public int MaxQuantity { get; set; }
}

/// <summary>One listing from the moment it is first seen on a retainer until it sells or disappears.</summary>
public sealed class ListingRecord
{
    public string Id { get; set; } = string.Empty;
    public string RetainerKey { get; set; } = string.Empty;
    public string RetainerName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public int Quantity { get; set; }
    public int Slot { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public long FirstPrice { get; set; }
    public long LastPrice { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Price changes on this listing (by Restocky or by hand in game).</summary>
    public int RepriceCount { get; set; }

    /// <summary>Observations where at least one competitor was below our price.</summary>
    public int UndercutCount { get; set; }

    public Dictionary<string, CompetitorStat> Competitors { get; set; } = new();

    public DateTime? MissingSinceUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    /// <summary>active / sold (sale message seen) / gone (disappeared: sold offline or taken down).</summary>
    public string Outcome { get; set; } = ListingTracker.Active;

    /// <summary>Gil received according to the sale message (after fees). 0 = unknown.</summary>
    public long SoldTotalGil { get; set; }

    /// <summary>When Restocky received the sale message; distinct from EndedUtc for gone→sold recovery.</summary>
    public DateTime? SoldRecordedUtc { get; set; }

    /// <summary>First source to report the sale: game, allaganmarket, or history.</summary>
    public string SoldSource { get; set; } = string.Empty;

    public DateTime? AllaganNoticeUtc { get; set; }

    // Fork: authoritative sale history and the transaction identity survive reloads.
    public string? HistoryKey { get; set; }
    public string? BuyerName { get; set; }
    public bool? IsMannequin { get; set; }
    public long? SoldGrossGil { get; set; }
    public DateTime? SaleTimeUtc { get; set; }
    public bool HistoryOnly { get; set; }

    // Fork: null cost identifies sales recorded before cost tracking was available.
    public decimal? ConsumedCost { get; set; }
    public int? UnknownCostUnits { get; set; }
    public bool StuckNotified { get; set; }

    [JsonIgnore]
    public DateTime? RecordedSaleUtc => SaleTimeUtc ?? SoldRecordedUtc ?? EndedUtc;
    [JsonIgnore]
    public decimal? RealizedProfit => Outcome == ListingTracker.Sold ? SaleReturn.Realized(this).Profit : null;
    public double? DaysListed => !HistoryOnly && EndedUtc is { } e ? (e - FirstSeenUtc).TotalDays : null;
}

public delegate (decimal ConsumedCost, int UnknownUnits) SaleCostConsumer(uint itemId, bool hq, int quantity, DateTime saleUtc);

/// <summary>
/// Fork: per-listing lifecycle + competitor bookkeeping. Pure logic with an injected clock;
/// the plugin feeds it from RetainerWatcher (listings), MarketWatcher (competitors), Executor
/// (applied reprices) and the RetainerSale chat message.
/// </summary>
public sealed class ListingTracker(SaleCostConsumer? consumeCost = null)
{
    public const string Active = "active";
    public const string Sold = "sold";
    public const string Gone = "gone";
    public const string GameSaleSource = "game";
    public const string AllaganMarketSaleSource = "allaganmarket";

    /// <summary>Retainer sale proceeds after the market's five-percent fee.</summary>
    public const decimal SaleNetRate = 0.95m;

    private static readonly TimeSpan GoneSaleWindow = TimeSpan.FromHours(72);
    private static readonly TimeSpan DuplicateSaleWindow = TimeSpan.FromMinutes(10);

    /// <summary>A listing must stay missing this long (across observations) before it is closed as gone.</summary>
    public TimeSpan GoneAfter { get; set; } = TimeSpan.FromSeconds(60);

    public List<ListingRecord> Records { get; set; } = new();

    /// <summary>Set by every mutation; the owner saves and clears it.</summary>
    public bool Dirty { get; set; }
    internal event Action<ListingRecord, DateTime>? ListingSold;

    // A provisional absence is still listed until a sale or a confirmed disappearance.
    internal IEnumerable<ListingRecord> ActiveListings => Records.Where(r => r.Outcome == Active);

    public IEnumerable<ListingRecord> ActiveFor(uint itemId, bool hq)
        => ActiveListings.Where(r => r.ItemId == itemId && r.IsHq == hq);

    internal (int Listed, bool Provisional) ListedStock(uint itemId, bool hq, IReadOnlySet<string> retainers)
    {
        var listed = 0;
        var provisional = false;
        foreach (var listing in ActiveFor(itemId, hq, retainers))
        {
            listed += listing.Quantity;
            provisional |= listing.MissingSinceUtc != null;
        }
        return (listed, provisional);
    }

    internal IEnumerable<ListingRecord> ActiveFor(uint itemId, bool hq, IReadOnlySet<string> retainers)
        => ActiveFor(itemId, hq).Where(r => retainers.Contains(r.RetainerKey));

    internal List<string> PreferredRetainers(uint itemId, bool hq, IReadOnlySet<string> keys)
        => Records.Where(r => r.ItemId == itemId && r.IsHq == hq && keys.Contains(r.RetainerKey))
            .OrderBy(r => r.Outcome == Active ? 0 : 1)
            .ThenByDescending(r => r.EndedUtc ?? r.LastSeenUtc)
            .Select(r => r.RetainerKey)
            .Distinct()
            .ToList();

    // Fork: stuck age follows the listing through reprices and slot moves.
    public static bool IsStuck(ListingRecord listing, DateTime nowUtc, int thresholdDays)
        => listing.Outcome == Active && listing.MissingSinceUtc == null
            && (nowUtc - listing.FirstSeenUtc).TotalDays >= Math.Max(1, thresholdDays);

    public bool TryMarkStuckNotified(ListingRecord listing, DateTime nowUtc, int thresholdDays)
    {
        if (listing.StuckNotified || !IsStuck(listing, nowUtc, thresholdDays)) return false;
        listing.StuckNotified = true;
        Dirty = true;
        return true;
    }

    /// <summary>The retainer's sell list as read now. Only call with a loaded container.</summary>
    public void ObserveRetainer(
        string retainerKey,
        string retainerName,
        IReadOnlyList<LiveListing> live,
        Func<uint, string> itemName,
        DateTime nowUtc,
        bool authoritative = false)
    {
        var open = Records.Where(r => r.Outcome == Active && r.RetainerKey == retainerKey).ToList();
        var matched = new HashSet<ListingRecord>();

        foreach (var l in live)
        {
            var rec = open.FirstOrDefault(r => !matched.Contains(r) && r.Slot == l.Slot && Same(r, l))
                      ?? open.FirstOrDefault(r => !matched.Contains(r) && Same(r, l));
            if (rec == null)
            {
                rec = new ListingRecord
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    RetainerKey = retainerKey,
                    RetainerName = retainerName,
                    ItemId = l.ItemId,
                    ItemName = itemName(l.ItemId),
                    IsHq = l.IsHQ,
                    Quantity = l.Quantity,
                    Slot = l.Slot,
                    FirstSeenUtc = nowUtc,
                    FirstPrice = l.UnitPrice,
                    LastPrice = l.UnitPrice,
                    LastSeenUtc = nowUtc,
                };
                Records.Add(rec);
                Dirty = true;
            }
            else
            {
                if (l.UnitPrice > 0 && l.UnitPrice != rec.LastPrice)
                {
                    rec.LastPrice = l.UnitPrice;
                    rec.RepriceCount++;
                }
                rec.Slot = l.Slot;
                rec.RetainerName = retainerName;
                rec.MissingSinceUtc = null;
                rec.LastSeenUtc = nowUtc;
                Dirty = true;
            }
            matched.Add(rec);
        }

        foreach (var rec in open)
        {
            if (matched.Contains(rec)) continue;
            rec.MissingSinceUtc ??= nowUtc;
            // An authoritative read (executor, after the sell list settled) or any non-empty read
            // is trusted: a listing that is not there is gone now. Only a passive EMPTY read waits,
            // because the container can briefly read empty while the list is still loading.
            if (authoritative || live.Count > 0 || nowUtc - rec.MissingSinceUtc.Value >= GoneAfter)
            {
                rec.Outcome = Gone;
                rec.EndedUtc = rec.MissingSinceUtc;
            }
            Dirty = true;
        }
    }

    private static bool Same(ListingRecord r, LiveListing l)
        => r.ItemId == l.ItemId && r.IsHq == l.IsHQ && r.Quantity == l.Quantity;

    /// <summary>A fresh market answer for (item, hq) from our own fetch.</summary>
    public void ObserveCompetitors(uint itemId, bool hq, IReadOnlyList<MarketOffer> offers, DateTime nowUtc)
        => ObserveCompetitors(itemId, hq, offers, nowUtc, null);

    internal void ObserveCompetitors(uint itemId, bool hq, IReadOnlyList<MarketOffer> offers, DateTime nowUtc,
        IReadOnlySet<string>? retainers)
    {
        foreach (var rec in retainers == null ? ActiveFor(itemId, hq) : ActiveFor(itemId, hq, retainers))
        {
            var undercut = false;
            // One observation per seller, even if they have several listings up.
            foreach (var g in offers.GroupBy(o => string.IsNullOrEmpty(o.Seller) ? "?" : o.Seller))
            {
                var name = g.Key;
                var lowest = g.Min(o => o.Price);
                var units = g.Sum(o => Math.Max(1, o.Quantity));
                if (!rec.Competitors.TryGetValue(name, out var st))
                {
                    st = new CompetitorStat { Seller = name, FirstSeenUtc = nowUtc, LowestPrice = lowest };
                    rec.Competitors[name] = st;
                }
                st.TimesSeen++;
                st.LastSeenUtc = nowUtc;
                st.LastQuantity = units;
                if (units > st.MaxQuantity) st.MaxQuantity = units;
                if (lowest < st.LowestPrice) st.LowestPrice = lowest;
                if (lowest < rec.LastPrice)
                {
                    st.TimesBelowUs++;
                    undercut = true;
                }
            }
            if (undercut) rec.UndercutCount++;
            Dirty = true;
        }
    }

    public void OnRepriceApplied(string retainerKey, int slot, uint itemId, bool hq, long newPrice)
    {
        var rec = Records.FirstOrDefault(r => r.Outcome == Active && r.RetainerKey == retainerKey
                                              && r.Slot == slot && r.ItemId == itemId && r.IsHq == hq);
        if (rec == null || rec.LastPrice == newPrice) return;
        rec.LastPrice = newPrice;
        rec.RepriceCount++;
        Dirty = true;
    }

    /// <summary>
    /// RetainerSale chat message. The message names the item and the gil received, not the
    /// retainer or the stack, so the active listing whose qty x price (minus ~5% tax) is closest wins.
    /// </summary>
    public ListingRecord? OnSale(uint itemId, bool hq, long totalGil, DateTime nowUtc)
        => OnSale(itemId, hq, totalGil, nowUtc, null);

    internal ListingRecord? OnSale(uint itemId, bool hq, long totalGil, DateTime nowUtc, ulong? characterId)
    {
        var records = SaleCandidates(characterId);
        if (IsDuplicateSale(records, new[] { itemId }, totalGil, nowUtc, GameSaleSource)) return null;
        var candidates = records.Where(r => r.Outcome == Active && r.ItemId == itemId && r.IsHq == hq).ToList();
        if (candidates.Count == 0) return null;
        var rec = totalGil > 0
            ? candidates.OrderBy(r => Difference(r, totalGil)).First()
            : candidates.First();
        MarkSold(rec, totalGil, nowUtc, keepEndedUtc: false, GameSaleSource);
        return rec;
    }

    /// <summary>
    /// AllaganMarket sale message. Item quality is unknown, but stack quantity is known. Active
    /// listings are preferred; a recent unclassified gone listing is the fallback.
    /// </summary>
    public ListingRecord? OnAllaganSale(
        IReadOnlyCollection<uint> candidateItemIds,
        int quantity,
        long netGil,
        DateTime nowUtc)
        => OnAllaganSale(candidateItemIds, quantity, netGil, nowUtc, null);

    internal ListingRecord? OnAllaganSale(IReadOnlyCollection<uint> candidateItemIds, int quantity, long netGil,
        DateTime nowUtc, ulong? characterId)
    {
        if (candidateItemIds.Count == 0 || quantity <= 0 || netGil <= 0) return null;
        var ids = candidateItemIds as HashSet<uint> ?? candidateItemIds.ToHashSet();
        var records = SaleCandidates(characterId);
        var knownSales = records.Where(r => r.Outcome == Sold && r.SoldTotalGil > 0
            && ids.Contains(r.ItemId) && r.Quantity == quantity
            && !string.IsNullOrEmpty(r.SoldSource) && r.SoldSource != AllaganMarketSaleSource
            && Math.Abs((decimal)r.SoldTotalGil - netGil) <= Tolerance(netGil));

        var known = knownSales.Where(r => r.AllaganNoticeUtc == null && r.SoldRecordedUtc is { } recorded
                && nowUtc >= recorded && nowUtc - recorded <= GoneSaleWindow)
            .OrderBy(r => Math.Abs((decimal)r.SoldTotalGil - netGil))
            .ThenBy(r => r.SoldRecordedUtc)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (known != null)
        {
            known.AllaganNoticeUtc = nowUtc;
            Dirty = true;
            return null;
        }

        // After pairing known sales, treat an extra identical notice within the window as a repeat.
        // If it represents a real sale, retainer sale history recovers it.
        if (knownSales.Any(r => r.AllaganNoticeUtc is { } notice && nowUtc >= notice
            && nowUtc - notice <= DuplicateSaleWindow)) return null;

        if (IsDuplicateSale(records, ids, netGil, nowUtc, AllaganMarketSaleSource)) return null;

        var active = records.Where(r => r.Outcome == Active && ids.Contains(r.ItemId) && r.Quantity == quantity);
        var rec = BestWithinTolerance(active, netGil);
        if (rec != null)
        {
            MarkSold(rec, netGil, nowUtc, keepEndedUtc: false, AllaganMarketSaleSource);
            return rec;
        }

        var gone = records.Where(r => r.Outcome == Gone && r.SoldTotalGil == 0
            && ids.Contains(r.ItemId) && r.Quantity == quantity && r.EndedUtc is { } ended
            && nowUtc >= ended && nowUtc - ended <= GoneSaleWindow);
        rec = BestWithinTolerance(gone, netGil);
        if (rec == null) return null;
        MarkSold(rec, netGil, nowUtc, keepEndedUtc: true, AllaganMarketSaleSource);
        return rec;
    }

    /// <summary>The game rounds the fee down, so the proceeds round up.</summary>
    public static long NetFromGross(long grossGil) => grossGil <= 0 ? 0 : (long)decimal.Ceiling(grossGil * SaleNetRate);

    // Fork: include chat-only sales, whose receipt time is the best available timestamp.
    public IEnumerable<ListingRecord> GetSales() => Records.Where(r => r.Outcome == Sold);

    public IEnumerable<ListingRecord> GetSales(uint itemId, bool hq)
        => GetSales().Where(r => r.ItemId == itemId && r.IsHq == hq)
            .OrderByDescending(r => r.RecordedSaleUtc);

    public HistorySaleResult OnHistorySale(string retainerKey, string retainerName, RetainerHistorySale sale,
        Func<uint, string> itemName, Action<string>? log = null)
    {
        var key = FormattableString.Invariant($"{retainerKey}|{sale.ItemId}|{sale.IsHq}|{sale.Quantity}|{sale.NetGil}|{sale.UnixSeconds}|{sale.Buyer}");
        if (Records.Any(r => r.HistoryKey == key))
        {
            log?.Invoke($"[Restocky] history: OnHistorySale skipped duplicate item={sale.ItemId} qty={sale.Quantity} unix={sale.UnixSeconds}");
            return HistorySaleResult.Duplicate;
        }
        var time = DateTimeOffset.FromUnixTimeSeconds(sale.UnixSeconds).UtcDateTime;
        var candidates = Records.Where(r => !r.HistoryOnly && r.HistoryKey == null
            && StackIdentity(r) == (retainerKey, sale.ItemId, sale.IsHq, sale.Quantity));
        var rec = FindReported(candidates, sale.NetGil, time);
        // The game notice can omit proceeds. Keep its narrower, unambiguous price fallback.
        if (rec == null)
        {
            var unpriced = candidates.Where(r => r.Outcome == Sold && r.SoldSource == GameSaleSource
                && r.SoldTotalGil == 0 && r.SoldRecordedUtc is { } receipt
                && (receipt - time).Duration() <= DuplicateSaleWindow && r.FirstSeenUtc < time.AddSeconds(1)
                && Difference(r, sale.NetGil) <= Tolerance(sale.NetGil)).Take(2).ToList();
            if (unpriced.Count == 1) rec = unpriced[0];
        }
        rec ??= FindListing(candidates, time, includeActive: true);
        var newRecord = rec == null;
        if (rec == null)
        {
            rec = new ListingRecord
            {
                Id = Guid.NewGuid().ToString("N")[..10], RetainerKey = retainerKey,
                RetainerName = retainerName, ItemId = sale.ItemId, ItemName = itemName(sale.ItemId),
                IsHq = sale.IsHq, Quantity = sale.Quantity, Slot = -1, HistoryOnly = true,
                FirstSeenUtc = time, LastSeenUtc = time,
            };
        }
        ApplyHistory(rec, key, time, sale.NetGil, sale.Buyer, sale.IsMannequin);
        if (newRecord) Records.Add(rec);
        return newRecord ? HistorySaleResult.HistoryOnly : HistorySaleResult.MatchedListing;
    }

    private static (string RetainerKey, uint ItemId, bool IsHq, int Quantity) StackIdentity(ListingRecord r)
        => (r.RetainerKey, r.ItemId, r.IsHq, r.Quantity);

    private static ListingRecord? FindReported(IEnumerable<ListingRecord> candidates, long netGil, DateTime time)
        => candidates.Where(r => r.Outcome == Sold && Math.Abs((decimal)r.SoldTotalGil - netGil) <= Tolerance(netGil))
            .Select(r => (Record: r, Delay: (r.SoldRecordedUtc ?? r.EndedUtc) - time))
            .Where(x => x.Delay >= TimeSpan.FromMinutes(-5) && x.Delay <= TimeSpan.FromHours(72))
            .OrderBy(x => x.Delay!.Value.Duration()).Select(x => x.Record).FirstOrDefault();

    private static ListingRecord? FindListing(IEnumerable<ListingRecord> candidates, DateTime time, bool includeActive)
        => candidates.Where(r => (r.Outcome == Gone || (includeActive && r.Outcome == Active))
                && r.LastSeenUtc <= time.AddSeconds(1) && (r.EndedUtc == null || r.EndedUtc >= time))
            .OrderByDescending(r => r.Outcome == Gone).ThenBy(r => r.EndedUtc ?? DateTime.MaxValue)
            .ThenBy(r => r.FirstSeenUtc).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();

    private void ApplyHistory(ListingRecord rec, string key, DateTime time, long netGil, string? buyer, bool? mannequin)
    {
        if (!rec.HistoryOnly && rec.SoldGrossGil == null && NetFromGross(rec.LastPrice * rec.Quantity) == netGil)
            rec.SoldGrossGil = rec.LastPrice * rec.Quantity;

        // Preserve the original notice source and receipt time for paired-notice deduplication.
        var source = rec.Outcome == Sold ? rec.SoldSource : "history";
        var receipt = rec.SoldRecordedUtc ?? (rec.Outcome == Sold ? rec.EndedUtc : null) ?? time;
        MarkSold(rec, netGil, receipt, keepEndedUtc: false, source, saleUtc: time);
        rec.EndedUtc = time;
        rec.HistoryKey = key;
        rec.SaleTimeUtc = time;
        rec.BuyerName = buyer;
        rec.IsMannequin = mannequin;
    }

    public void ReconcileHistory(Action<string>? log = null)
    {
        var history = Records.Where(r => r.HistoryOnly && r.HistoryKey != null && r.SaleTimeUtc != null)
            .OrderBy(r => r.SaleTimeUtc).ToList();
        if (history.Count == 0) return;
        var targets = Records.Where(r => !r.HistoryOnly && r.HistoryKey == null &&
                (r.Outcome == Gone || (r.Outcome == Sold && r.SoldSource == AllaganMarketSaleSource)))
            .ToLookup(StackIdentity);
        foreach (var row in history)
        {
            var time = row.SaleTimeUtc!.Value;
            var candidates = targets[StackIdentity(row)].Where(r => r.HistoryKey == null);
            var target = FindReported(candidates.Where(r => r.SoldSource == AllaganMarketSaleSource), row.SoldTotalGil, time)
                ?? FindListing(candidates, time, includeActive: false);
            if (target == null) continue;

            var kind = target.Outcome == Sold ? "allagan" : "gone";
            if (target.ConsumedCost == null && row.ConsumedCost != null)
            {
                target.ConsumedCost = row.ConsumedCost;
                target.UnknownCostUnits = row.UnknownCostUnits;
            }
            ApplyHistory(target, row.HistoryKey!, time, row.SoldTotalGil, row.BuyerName, row.IsMannequin);
            target.AllaganNoticeUtc ??= row.AllaganNoticeUtc;
            Records.Remove(row);
            log?.Invoke($"[Restocky] history: reconcile merged {target.ItemName} into {kind} record");
        }
    }

    private static ListingRecord? BestWithinTolerance(IEnumerable<ListingRecord> candidates, long netGil)
    {
        var rec = candidates.OrderBy(r => Difference(r, netGil)).FirstOrDefault();
        return rec != null && Difference(rec, netGil) <= Tolerance(netGil) ? rec : null;
    }

    private IEnumerable<ListingRecord> SaleCandidates(ulong? characterId)
    {
        if (characterId == null) return Records;
        var prefix = $"{characterId:X}.";
        return Records.Where(r => r.RetainerKey.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsDuplicateSale(IEnumerable<ListingRecord> records, IEnumerable<uint> itemIds, long netGil, DateTime nowUtc, string source)
    {
        if (netGil <= 0) return false;
        var ids = itemIds as ISet<uint> ?? itemIds.ToHashSet();
        return records.Any(r => r.Outcome == Sold && ids.Contains(r.ItemId) && r.SoldTotalGil > 0
            && !string.IsNullOrEmpty(r.SoldSource) && r.SoldSource != source
            && r.SoldRecordedUtc is { } recorded && nowUtc >= recorded
            && nowUtc - recorded <= DuplicateSaleWindow
            && Math.Abs((decimal)r.SoldTotalGil - netGil) <= Tolerance(netGil));
    }

    private void MarkSold(ListingRecord rec, long totalGil, DateTime nowUtc, bool keepEndedUtc, string source,
        DateTime? saleUtc = null)
    {
        var newlySold = rec.Outcome != Sold;
        // Fork: history enrichment updates proceeds and timing on the same sold record.
        if (rec.Outcome != Sold && rec.ConsumedCost == null && consumeCost != null)
            (rec.ConsumedCost, rec.UnknownCostUnits) = consumeCost(rec.ItemId, rec.IsHq, rec.Quantity, saleUtc ?? nowUtc);

        rec.Outcome = Sold;
        if (!keepEndedUtc) rec.EndedUtc = nowUtc;
        rec.SoldTotalGil = totalGil;
        rec.SoldRecordedUtc = nowUtc;
        rec.SoldSource = source;
        rec.MissingSinceUtc = null;
        Dirty = true;
        if (newlySold && !rec.HistoryOnly && rec.Slot >= 0)
            ListingSold?.Invoke(rec, saleUtc ?? rec.EndedUtc ?? nowUtc);
    }

    private static decimal Difference(ListingRecord rec, long netGil)
        => Math.Abs(rec.LastPrice * rec.Quantity * SaleNetRate - netGil);

    private static decimal Tolerance(long netGil) => Math.Max(2m, netGil * 0.02m);

    // ---------------- persistence / export ----------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static ListingTracker Load(string path, SaleCostConsumer? consumeCost = null, Action<string>? log = null)
    {
        var tracker = new ListingTracker(consumeCost);
        try
        {
            if (File.Exists(path))
                tracker.Records = JsonSerializer.Deserialize<List<ListingRecord>>(File.ReadAllText(path)) ?? new();
        }
        catch { /* corrupt file: start fresh, keep the old one aside */ TryBackup(path); }
        tracker.ReconcileHistory(log);
        return tracker;
    }

    private static void TryBackup(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
    }

    /// <summary>Writes listings.json, listings.csv and competitors.csv into <paramref name="dir"/>.</summary>
    public bool Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            WriteAtomic(Path.Combine(dir, "listings.json"), JsonSerializer.Serialize(Records, JsonOpts));
            WriteAtomic(Path.Combine(dir, "listings.csv"), ListingsCsv());
            WriteAtomic(Path.Combine(dir, "competitors.csv"), CompetitorsCsv());
            Dirty = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(true)); // BOM so Excel opens UTF-8 names correctly
        File.Move(tmp, path, overwrite: true);
    }

    public const string ListingsHeader =
        "listing_id,retainer,item_id,item_name,hq,qty,first_seen_utc,first_price,last_price,reprice_count,undercut_count,undercutters,competitors,ended_utc,outcome,sold_total_gil,days_listed,buyer,sale_time_utc,sold_gross_gil,is_mannequin,cost,profit,earning";

    public string ListingsCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ListingsHeader);
        foreach (var r in Records.OrderBy(r => r.FirstSeenUtc))
        {
            // "Rival 4x" = seller and the most units they had up at once.
            var undercutters = string.Join(";", r.Competitors.Values.Where(c => c.TimesBelowUs > 0)
                .OrderByDescending(c => c.TimesBelowUs).Select(c => $"{c.Seller} {c.MaxQuantity}x"));
            var competitors = string.Join(";", r.Competitors.Values.OrderByDescending(c => c.TimesSeen)
                .Select(c => $"{c.Seller} {c.MaxQuantity}x"));
            var returns = r.Outcome == Sold ? SaleReturn.Realized(r) : default;
            sb.AppendLine(string.Join(",",
                r.Id,
                Csv(r.RetainerName),
                r.ItemId.ToString(inv),
                Csv(r.ItemName),
                r.IsHq ? "true" : "false",
                r.Quantity.ToString(inv),
                r.HistoryOnly ? string.Empty : Iso(r.FirstSeenUtc),
                r.HistoryOnly ? string.Empty : r.FirstPrice.ToString(inv),
                r.HistoryOnly ? string.Empty : r.LastPrice.ToString(inv),
                r.RepriceCount.ToString(inv),
                r.UndercutCount.ToString(inv),
                Csv(undercutters),
                Csv(competitors),
                r.EndedUtc is { } e ? Iso(e) : string.Empty,
                r.Outcome,
                r.SoldTotalGil.ToString(inv),
                r.DaysListed is { } d ? d.ToString("0.00", inv) : string.Empty,
                Csv(r.BuyerName ?? string.Empty),
                (r.SaleTimeUtc ?? r.SoldRecordedUtc) is { } soldAt ? Iso(soldAt) : string.Empty,
                r.SoldGrossGil?.ToString(inv) ?? string.Empty,
                r.IsMannequin?.ToString().ToLowerInvariant() ?? string.Empty,
                r.ConsumedCost?.ToString(inv) ?? string.Empty,
                returns.Profit?.ToString(inv) ?? string.Empty,
                returns.Earning?.ToString(inv) ?? string.Empty));
        }
        return sb.ToString();
    }

    public const string CompetitorsHeader =
        "item_id,item_name,hq,competitor,times_seen,times_below_us,lowest_price,last_qty,max_qty,first_seen_utc,last_seen_utc,listings_contested";

    /// <summary>One row per (item, hq, competitor), aggregated over every listing of ours.</summary>
    public string CompetitorsCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(CompetitorsHeader);
        var rows = Records
            .SelectMany(r => r.Competitors.Values.Select(c => (r, c)))
            .GroupBy(x => (x.r.ItemId, x.r.IsHq, x.c.Seller))
            .Select(g => new
            {
                g.Key.ItemId,
                Name = g.First().r.ItemName,
                g.Key.IsHq,
                g.Key.Seller,
                Seen = g.Sum(x => x.c.TimesSeen),
                Below = g.Sum(x => x.c.TimesBelowUs),
                Lowest = g.Min(x => x.c.LowestPrice),
                LastQty = g.OrderByDescending(x => x.c.LastSeenUtc).First().c.LastQuantity,
                MaxQty = g.Max(x => x.c.MaxQuantity),
                First = g.Min(x => x.c.FirstSeenUtc),
                Last = g.Max(x => x.c.LastSeenUtc),
                Listings = g.Count(),
            })
            .OrderBy(x => x.Name).ThenByDescending(x => x.Below);
        foreach (var x in rows)
        {
            sb.AppendLine(string.Join(",",
                x.ItemId.ToString(inv), Csv(x.Name), x.IsHq ? "true" : "false", Csv(x.Seller),
                x.Seen.ToString(inv), x.Below.ToString(inv), x.Lowest.ToString(inv),
                x.LastQty.ToString(inv), x.MaxQty.ToString(inv),
                Iso(x.First), Iso(x.Last), x.Listings.ToString(inv)));
        }
        return sb.ToString();
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Csv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
