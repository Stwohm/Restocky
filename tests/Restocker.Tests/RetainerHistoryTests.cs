using System.Buffers.Binary;
using System.IO;
using System.Collections.Generic;
using Restocker.Execution;
using System.Linq;
using System.Text;
using Restocker.Common;
using Restocker.Market;
using Restocker.Data;

namespace Restocker.Tests;

// Fork: history reads can repeat across visits and plugin restarts.
public class RetainerHistoryTests
{
    private const uint Time = 1_700_000_000;
    private static readonly DateTime SoldAt = DateTimeOffset.FromUnixTimeSeconds(Time).UtcDateTime;
    private static readonly RetainerHistorySale Sale = new(100, 190, Time, 2, true, false, "BuyerA");

    [Fact]
    public void Packet_uses_header_stride_offsets_terminator_and_record_limit()
    {
        var packet = new byte[8 + 22 * 52];
        for (var i = 0; i < 22; i++)
        {
            var row = packet.AsSpan(8 + i * 52, 52);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)(100 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], 190);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], Time);
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], 2);
            row[16] = 1;
            row[17] = 255;
            row[18] = (byte)(i % 2);
            Encoding.UTF8.GetBytes("BuyerA", row[19..]);
            row[26] = 255;
        }
        var sales = RetainerHistorySale.Parse(packet);
        Assert.Equal(20, sales.Count);
        Assert.Equal(Sale, sales[0]);
        Assert.Equal(Sale with { ItemId = 101, IsMannequin = true }, sales[1]);
        packet.AsSpan(8 + 52, 4).Clear();
        Assert.Equal(Sale, Assert.Single(RetainerHistorySale.Parse(packet)));
        Assert.Empty(RetainerHistorySale.Parse(packet.AsSpan(0, 59)));
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(1_000_100, 0)]
    [InlineData(1_000_100, 1)]
    public void Both_HQ_encodings_match_the_same_listing_and_deduplicate(int rawId, byte flag)
    {
        var packet = new byte[RetainerHistorySale.PacketSize];
        var row = packet.AsSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)rawId);
        BinaryPrimitives.WriteUInt32LittleEndian(row[4..], Sale.NetGil);
        BinaryPrimitives.WriteUInt32LittleEndian(row[8..], Time);
        BinaryPrimitives.WriteUInt32LittleEndian(row[12..], (uint)Sale.Quantity);
        row[16] = flag;
        Encoding.UTF8.GetBytes(Sale.Buyer, row[19..]);
        var parsed = Assert.Single(RetainerHistorySale.Parse(packet));
        Assert.Equal(Sale, parsed);

        var tracker = new ListingTracker();
        var key = RetainerSnapshot.MakeKey(123, 456);
        tracker.ObserveRetainer(key, "RetainerA", [new(0, 100, true, 2, 100)], _ => "Item", SoldAt.AddHours(-1));
        Assert.Equal(HistorySaleResult.MatchedListing, tracker.OnHistorySale(key, "RetainerA", parsed, _ => "Item"));
        string? reason = null;
        Assert.Equal(HistorySaleResult.Duplicate, tracker.OnHistorySale(key, "RetainerA", Sale, _ => "Item", s => reason = s));
        Assert.Contains("skipped duplicate", reason);
        Assert.DoesNotContain(key, reason);
        Assert.Single(tracker.Records);
    }

    [Fact]
    public void Invalid_unsigned_quantity_reports_reason_and_continues_to_next_row()
    {
        var packet = new byte[RetainerHistorySale.PacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + 12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + 52), 101);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + 52 + 12), 1);
        string? reason = null;
        Assert.Equal(101u, Assert.Single(RetainerHistorySale.Parse(packet, s => reason = s)).ItemId);
        Assert.Contains("invalid quantity=4294967295", reason);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("gone")]
    [InlineData("allaganmarket")]
    [InlineData("delayed")]
    public void History_closes_or_enriches_the_existing_listing(string prior)
    {
        var tracker = new ListingTracker();
        LiveListing[] stack = [new(0, 100, true, 2, 100)];
        tracker.ObserveRetainer("B", "RetainerB", stack, _ => "Item", SoldAt.AddDays(-1));
        tracker.ObserveRetainer("A", "RetainerA", stack, _ => "Item", SoldAt.AddDays(-1));
        var listing = tracker.Records.Last();
        if (prior == "gone")
            tracker.ObserveRetainer("A", "RetainerA", [], _ => "Item", SoldAt.AddDays(4), true);
        if (prior is "allaganmarket" or "delayed")
        {
            tracker.ObserveRetainer("B", "RetainerB", [], _ => "Item", SoldAt, true);
            var receipt = prior == "delayed" ? SoldAt.AddDays(2) : SoldAt.AddMinutes(1);
            Assert.Same(listing, tracker.OnAllaganSale([100], 2, 190, receipt));
        }
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        Assert.Equal(2, tracker.Records.Count);
        Assert.Same(listing, Assert.Single(tracker.GetSales(100, true)));
        Assert.Equal(SoldAt, listing.EndedUtc);
        Assert.Equal(SoldAt, listing.SaleTimeUtc);
        Assert.Equal(190, listing.SoldTotalGil);
        Assert.Equal(200, listing.SoldGrossGil);
        Assert.Equal("BuyerA", listing.BuyerName);
        Assert.False(listing.HistoryOnly);
    }

    [Fact]
    public void Enrichment_preserves_same_source_matching_for_the_next_sale()
    {
        var tracker = new ListingTracker();
        tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100), new(1, 100, true, 2, 100)],
            _ => "Item", SoldAt.AddMinutes(-5));
        tracker.OnAllaganSale([100], 2, 190, SoldAt.AddSeconds(10));
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        Assert.Null(tracker.OnSale(100, true, 190, SoldAt.AddSeconds(20)));
        Assert.NotNull(tracker.OnAllaganSale([100], 2, 190, SoldAt.AddMinutes(1)));
        Assert.Equal(2, tracker.GetSales(100, true).Count());
    }

    [Fact]
    public void Old_unmatched_sales_survive_reload_and_do_not_close_a_new_listing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var tracker = new ListingTracker();
            tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100)],
                _ => "Item", SoldAt.AddDays(1));
            var sale = Sale with { IsMannequin = true };
            tracker.OnHistorySale("A", "RetainerA", sale, _ => "Item");
            tracker.OnHistorySale("A", "RetainerA", sale with { UnixSeconds = Time + 1 }, _ => "Item");
            tracker.OnHistorySale("B", "RetainerB", sale, _ => "Item");
            Assert.True(tracker.Save(dir.FullName));
            var restored = ListingTracker.Load(Path.Combine(dir.FullName, "listings.json"));
            restored.OnHistorySale("A", "RetainerA", sale, _ => "Item");
            Assert.False(restored.Dirty);
            Assert.Single(restored.ActiveFor(100, true));
            Assert.Empty(restored.GetSales(100, false));
            var sales = restored.GetSales(100, true).ToList();
            Assert.Equal(3, sales.Count);
            Assert.Equal(SoldAt.AddSeconds(1), sales[0].SaleTimeUtc);
            Assert.All(sales, s =>
            {
                Assert.True(s.HistoryOnly);
                Assert.True(s.IsMannequin);
                Assert.Null(s.DaysListed);
                Assert.Null(s.SoldGrossGil);
            });
            var csv = File.ReadAllLines(Path.Combine(dir.FullName, "listings.csv"));
            Assert.EndsWith("days_listed,buyer,sale_time_utc,sold_gross_gil,is_mannequin,cost,profit,earning", csv[0]);
            Assert.Contains(csv, line => line.EndsWith(",BuyerA,2023-11-14T22:13:20Z,,true,,,190"));
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData(14, -1, true)]
    [InlineData(120, 1, true)]
    [InlineData(4800, 0, false)]
    [InlineData(-6, 0, false)]
    [InlineData(-5, 0, true)]
    [InlineData(4320, 0, true)]
    public void Notice_window_allows_delayed_receipts_and_rounding(int minutes, int rounding, bool matches)
    {
        var tracker = new ListingTracker();
        tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100)], _ => "Item", SoldAt.AddDays(-1));
        var notice = tracker.OnAllaganSale([100], 2, 190 + rounding, SoldAt.AddMinutes(minutes));
        Assert.NotNull(notice);
        var result = tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        Assert.Equal(matches ? HistorySaleResult.MatchedListing : HistorySaleResult.HistoryOnly, result);
        Assert.Equal(matches, notice.HistoryKey != null);
        Assert.Equal(matches ? 1 : 2, tracker.Records.Count);
    }

    [Fact]
    public void Closest_notice_wins_even_when_another_has_the_exact_amount()
    {
        var tracker = new ListingTracker();
        tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100), new(1, 100, true, 2, 100)],
            _ => "Item", SoldAt.AddHours(-1));
        var near = tracker.OnAllaganSale([100], 2, 189, SoldAt.AddMinutes(14));
        var far = tracker.OnAllaganSale([100], 2, 190, SoldAt.AddHours(2));
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        Assert.Equal(SoldAt, near!.SaleTimeUtc);
        Assert.Null(far!.HistoryKey);
    }

    [Fact]
    public void Identical_gone_stacks_match_by_end_then_first_seen_once_each()
    {
        var tracker = new ListingTracker();
        // All three were last seen before the first sale. Slots 1 and 2 disappeared
        // together, and slot 2 was first listed earlier. Slot 0 disappeared last.
        for (var slot = 0; slot < 3; slot++)
        {
            tracker.Records.Add(new ListingRecord
            {
                RetainerKey = "A", ItemId = 100, IsHq = true, Quantity = 2, LastPrice = 150, Slot = slot,
                Outcome = ListingTracker.Gone, FirstSeenUtc = SoldAt.AddDays(slot == 2 ? -2 : -1),
                LastSeenUtc = SoldAt.AddHours(-1), EndedUtc = SoldAt.AddHours(slot == 0 ? 7 : 6),
            });
        }
        int[] slots = [2, 1, 0];
        uint[] offsets = [0, 3600, 7200];
        for (var i = 0; i < slots.Length; i++)
        {
            var sale = Sale with { UnixSeconds = Time + offsets[i] };
            Assert.Equal(HistorySaleResult.MatchedListing, tracker.OnHistorySale("A", "RetainerA", sale, _ => "Item"));
            Assert.Equal(SoldAt.AddSeconds(offsets[i]), tracker.Records.Single(r => r.Slot == slots[i]).SaleTimeUtc);
        }
        Assert.Equal(3, tracker.GetSales().Count());
        Assert.DoesNotContain(tracker.Records, r => r.HistoryOnly);
    }

    [Fact]
    public void Active_stack_verified_after_sale_stays_open()
    {
        var tracker = new ListingTracker();
        LiveListing[] stack = [new(0, 100, true, 2, 100)];
        tracker.ObserveRetainer("A", "RetainerA", stack, _ => "Item", SoldAt.AddDays(-1));
        tracker.ObserveRetainer("A", "RetainerA", stack, _ => "Item", SoldAt.AddSeconds(2));
        Assert.Equal(HistorySaleResult.HistoryOnly, tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item"));
        Assert.Single(tracker.ActiveFor(100, true));
    }

    [Theory]
    [InlineData("gone", false, false, 1)]
    [InlineData("gone", true, false, 0)]
    [InlineData("allagan", true, false, 0)]
    [InlineData("allagan", true, true, 0)]
    public void Load_reconciles_once_and_preserves_cost(string kind, bool historyHasCost, bool targetHasCost, int expectedCalls)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var tracker = new ListingTracker();
            tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
            var history = Assert.Single(tracker.Records);
            if (historyHasCost) (history.ConsumedCost, history.UnknownCostUnits) = (12m, 0);

            tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100)], _ => "Item", SoldAt.AddHours(-1));
            var target = Assert.Single(tracker.ActiveFor(100, true));
            if (kind == "allagan")
            {
                // Persist the duplicate sold record that older versions could create.
                target.Outcome = ListingTracker.Sold;
                target.SoldSource = ListingTracker.AllaganMarketSaleSource;
                target.SoldTotalGil = 189;
                target.SoldRecordedUtc = target.EndedUtc = SoldAt.AddHours(2);
            }
            else
                tracker.ObserveRetainer("A", "RetainerA", [], _ => "Item", SoldAt.AddHours(2), true);
            if (targetHasCost) (target.ConsumedCost, target.UnknownCostUnits) = (25m, 1);
            Assert.True(tracker.Save(dir.FullName));

            var calls = 0;
            var messages = new List<string>();
            tracker = ListingTracker.Load(Path.Combine(dir.FullName, "listings.json"), (_, _, _, time) =>
            {
                Assert.Equal(SoldAt, time);
                calls++;
                return (8m, 2);
            }, messages.Add);
            var merged = Assert.Single(tracker.Records);
            Assert.Equal(target.Id, merged.Id);
            Assert.Equal(ListingTracker.Sold, merged.Outcome);
            Assert.False(merged.HistoryOnly);
            Assert.Equal(history.HistoryKey, merged.HistoryKey);
            Assert.Equal(Sale.Buyer, merged.BuyerName);
            Assert.Equal(SoldAt, merged.SaleTimeUtc);
            Assert.Equal(190, merged.SoldTotalGil);
            Assert.Equal(targetHasCost ? 25m : historyHasCost ? 12m : 8m, merged.ConsumedCost);
            Assert.Equal(targetHasCost ? 1 : historyHasCost ? 0 : 2, merged.UnknownCostUnits);
            Assert.True(tracker.Dirty);
            Assert.True(tracker.Save(dir.FullName));
            tracker.ReconcileHistory(messages.Add);
            Assert.Equal(HistorySaleResult.Duplicate, tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item"));
            Assert.False(tracker.Dirty);
            Assert.Equal(expectedCalls, calls);
            Assert.Equal($"[Restocky] history: reconcile merged Item into {kind} record", Assert.Single(messages));
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData(ExecutionMode.ApplyActions)]
    [InlineData(ExecutionMode.RefreshAll)]
    [InlineData(ExecutionMode.FullCycle)]
    public void History_request_requires_enabled_toggle_and_first_visit(ExecutionMode mode)
    {
        Assert.Null(SaleHistoryPolicy.SkipReason(mode, true, false));
        Assert.Equal("toggle off", SaleHistoryPolicy.SkipReason(mode, false, false));
        Assert.Equal("already requested this run", SaleHistoryPolicy.SkipReason(mode, true, true));
    }

    [Theory]
    [InlineData("View sale history.")]
    [InlineData("販売履歴を見る")]
    [InlineData("Transaktionsverlauf ansehen")]
    [InlineData("Voir l’historique des transactions")]
    [InlineData("查看出售记录")]
    [InlineData("판매 내역 보기")]
    public void Matches_regional_history_menu(string label) => Assert.True(AddonText.IsSaleHistoryEntry(label));
}
