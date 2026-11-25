using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Tests;

public class ListingTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string RetainerA = "RetainerA";
    private const string RetainerB = "RetainerB";
    private const string K = "k1";
    private static string Name(uint id) => id == 100 ? "Twine" : $"#{id}";

    private static List<LiveListing> L(params LiveListing[] l) => l.ToList();

    [Fact]
    public void A_scan_and_the_current_listing_sizes_are_scoped_to_this_characters_retainers()
    {
        var tracker = new ListingTracker();
        tracker.ObserveRetainer("2.B", "Other", [new(0, 100, false, 99, 1000)], Name, T0);
        tracker.ObserveRetainer("1.A", "Here", [new(0, 100, false, 3, 1000)], Name, T0);
        var keys = new HashSet<string> { "1.A" };
        tracker.ObserveCompetitors(100, false, [new(900, "Rival", 1)], T0, keys);
        Assert.Equal(3, Assert.Single(tracker.ActiveFor(100, false, keys)).Quantity);
        Assert.Equal(0, tracker.Records[0].UndercutCount);
        Assert.Equal(1, tracker.Records[1].UndercutCount);
    }

    [Fact]
    public void Full_lifecycle_first_price_reprices_undercutters_and_sale()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 85_000)), Name, T0);
        var rec = Assert.Single(t.Records);
        Assert.Equal(85_000, rec.FirstPrice);
        Assert.Equal("Twine", rec.ItemName);

        // Rival undercuts, Restocky fixes it twice.
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_999, "Rival", 1), new MarketOffer(90_000, "Bob", 1) }, T0.AddMinutes(10));
        t.OnRepriceApplied(K, 0, 100, false, 84_998);
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_997, "Rival", 1) }, T0.AddMinutes(20));
        t.OnRepriceApplied(K, 0, 100, false, 84_996);
        // A later sell-list read sees the same price: no extra reprice counted.
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 84_996)), Name, T0.AddMinutes(21));

        var sold = t.OnSale(100, false, 80_746, T0.AddDays(2));
        Assert.Same(rec, sold);
        Assert.Equal(2, rec.RepriceCount);
        Assert.Equal(2, rec.UndercutCount);
        Assert.Equal(2, rec.Competitors["Rival"].TimesBelowUs);
        Assert.Equal(0, rec.Competitors["Bob"].TimesBelowUs);
        Assert.Equal(84_997, rec.Competitors["Rival"].LowestPrice);
        Assert.Equal(ListingTracker.Sold, rec.Outcome);
        Assert.Equal(2.0, rec.DaysListed!.Value, 3);
        Assert.Equal(84_996, rec.LastPrice);
    }

    [Fact]
    public void Seller_with_several_listings_counts_once_with_summed_units()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 85_000)), Name, T0);
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_999, "Rival", 3), new MarketOffer(84_998, "Rival", 1) }, T0);
        var st = t.Records[0].Competitors["Rival"];
        Assert.Equal(1, st.TimesSeen);
        Assert.Equal(4, st.MaxQuantity);
        Assert.Equal(84_998, st.LowestPrice);
    }

    [Fact]
    public void Manual_price_change_in_game_counts_as_reprice()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(3, 100, false, 1, 50_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(3, 100, false, 1, 49_000)), Name, T0.AddHours(1));
        Assert.Equal(1, t.Records[0].RepriceCount);
        Assert.Equal(50_000, t.Records[0].FirstPrice);
    }

    [Fact]
    public void Missing_listing_closes_as_gone_only_after_grace_period()
    {
        var t = new ListingTracker();
        var keys = new HashSet<string> { K };
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 1_000)), Name, T0);
        Assert.Equal((1, false), t.ListedStock(100, false, keys));
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddSeconds(5));
        Assert.Equal(ListingTracker.Active, t.Records[0].Outcome);
        Assert.Single(t.ActiveListings);
        Assert.Equal((1, true), t.ListedStock(100, false, keys));
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddMinutes(10));
        Assert.Equal(ListingTracker.Gone, t.Records[0].Outcome);
        Assert.Equal(T0.AddSeconds(5), t.Records[0].EndedUtc);
        Assert.Empty(t.ActiveListings);
        Assert.Equal((0, false), t.ListedStock(100, false, keys));
    }

    [Fact]
    public void Listing_missing_from_a_non_empty_read_closes_immediately()
    {
        // Placeholder twine on slot 1, other listings remain: one read later it is gone.
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 10), new LiveListing(1, 100, false, 1, 99_999_999)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 10)), Name, T0.AddSeconds(15));
        Assert.Equal(ListingTracker.Gone, t.Records.Single(r => r.ItemId == 100).Outcome);
    }

    [Fact]
    public void Authoritative_empty_read_closes_immediately()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(1, 100, false, 1, 10)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddSeconds(15), authoritative: true);
        Assert.Equal(ListingTracker.Gone, t.Records[0].Outcome);
    }

    [Fact]
    public void Transient_empty_read_does_not_break_the_lifecycle()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 1_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddSeconds(1));
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 1_000)), Name, T0.AddMinutes(5));
        var rec = Assert.Single(t.Records);
        Assert.Equal(ListingTracker.Active, rec.Outcome);
        Assert.Null(rec.MissingSinceUtc);
    }

    [Fact]
    public void Moved_slot_keeps_the_same_record()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(4, 100, false, 1, 1_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(1, 100, false, 1, 1_000)), Name, T0.AddMinutes(1));
        Assert.Single(t.Records);
        Assert.Equal(1, t.Records[0].Slot);
    }

    [Fact]
    public void Sale_picks_the_stack_whose_value_matches_the_gil()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 5_500), new LiveListing(1, 7, false, 5, 5_500)), Name, T0);
        var rec = t.OnSale(7, false, 26_125, T0.AddHours(3)); // 5 x 5,500 x 0.95
        Assert.Equal(5, rec!.Quantity);
    }

    [Fact]
    public void Allagan_sale_matches_candidate_id_quantity_either_quality_and_closest_value()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(
            new LiveListing(0, 7, false, 2, 10_000),
            new LiveListing(1, 8, true, 2, 10_100),
            new LiveListing(2, 8, false, 1, 20_200)), Name, T0);

        var rec = t.OnAllaganSale(new uint[] { 7, 8 }, 2, 19_190, T0.AddMinutes(1));

        Assert.NotNull(rec);
        Assert.Equal((uint)8, rec!.ItemId);
        Assert.True(rec.IsHq);
        Assert.Equal(ListingTracker.Sold, rec.Outcome);
        Assert.Equal(19_190, rec.SoldTotalGil);
    }

    [Fact]
    public void Allagan_sale_rejects_wrong_quantity_or_value_outside_tolerance()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 2, 10_000)), Name, T0);

        Assert.Null(t.OnAllaganSale(new uint[] { 7 }, 1, 19_000, T0.AddMinutes(1)));
        Assert.Null(t.OnAllaganSale(new uint[] { 7 }, 2, 18_000, T0.AddMinutes(1)));
        Assert.Equal(ListingTracker.Active, t.Records[0].Outcome);
    }

    [Fact]
    public void Allagan_sale_converts_recent_gone_listing_to_sold_and_keeps_end_time()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, true, 3, 10_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddHours(1), authoritative: true);
        var ended = t.Records[0].EndedUtc;

        var rec = t.OnAllaganSale(new uint[] { 7 }, 3, 28_500, T0.AddHours(2));

        Assert.Same(t.Records[0], rec);
        Assert.Equal(ListingTracker.Sold, rec!.Outcome);
        Assert.Equal(ended, rec.EndedUtc);
        Assert.Equal(T0.AddHours(2), rec.SoldRecordedUtc);
    }

    [Fact]
    public void Allagan_sale_prefers_an_acceptable_active_listing_over_a_closer_gone_listing()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 10_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddHours(1), authoritative: true);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(1, 7, true, 1, 10_100)), Name, T0.AddHours(2));

        var rec = t.OnAllaganSale(new uint[] { 7 }, 1, 9_500, T0.AddHours(3));

        Assert.NotNull(rec);
        Assert.True(rec!.IsHq);
        Assert.Equal(ListingTracker.Gone, t.Records.Single(r => !r.IsHq).Outcome);
    }

    [Fact]
    public void Allagan_sale_does_not_reopen_gone_listing_older_than_72_hours()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 10_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddHours(1), authoritative: true);

        Assert.Null(t.OnAllaganSale(new uint[] { 7 }, 1, 9_500, T0.AddHours(74)));
        Assert.Equal(ListingTracker.Gone, t.Records[0].Outcome);
    }

    [Fact]
    public void Two_same_source_allaganmarket_messages_close_two_identical_listings()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(
            new LiveListing(0, 7, false, 1, 10_000),
            new LiveListing(1, 7, false, 1, 10_000)), Name, T0);

        Assert.NotNull(t.OnAllaganSale(new uint[] { 7 }, 1, 9_500, T0.AddMinutes(1)));
        Assert.NotNull(t.OnAllaganSale(new uint[] { 7 }, 1, 9_500, T0.AddMinutes(2)));

        Assert.Equal(2, t.Records.Count(r => r.Outcome == ListingTracker.Sold));
        Assert.All(t.Records, r => Assert.Equal(ListingTracker.AllaganMarketSaleSource, r.SoldSource));
    }

    [Fact]
    public void Game_then_allaganmarket_for_one_sale_closes_one_listing()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(
            new LiveListing(0, 7, false, 1, 10_000),
            new LiveListing(1, 7, false, 1, 10_000)), Name, T0);

        Assert.NotNull(t.OnSale(7, false, 9_500, T0.AddMinutes(1)));
        Assert.Null(t.OnAllaganSale(new uint[] { 7 }, 1, 9_501, T0.AddMinutes(5)));

        Assert.Single(t.Records, r => r.Outcome == ListingTracker.Sold);
        Assert.Single(t.Records, r => r.Outcome == ListingTracker.Active);
        Assert.Equal(ListingTracker.GameSaleSource, t.Records.Single(r => r.Outcome == ListingTracker.Sold).SoldSource);
    }

    [Fact]
    public void Allaganmarket_then_game_for_one_sale_closes_one_listing()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(
            new LiveListing(0, 7, true, 1, 10_000),
            new LiveListing(1, 7, true, 1, 10_000)), Name, T0);

        Assert.NotNull(t.OnAllaganSale(new uint[] { 7 }, 1, 9_500, T0.AddMinutes(1)));
        Assert.Null(t.OnSale(7, true, 9_499, T0.AddMinutes(5)));

        Assert.Single(t.Records, r => r.Outcome == ListingTracker.Sold);
        Assert.Single(t.Records, r => r.Outcome == ListingTracker.Active);
        Assert.Equal(ListingTracker.AllaganMarketSaleSource, t.Records.Single(r => r.Outcome == ListingTracker.Sold).SoldSource);
    }

    [Theory]
    [InlineData(914, 99, 1196, 1176, 110603, 110603)]
    [InlineData(1599, 99, 1196, 1176, 110603, 110603)]
    [InlineData(21600, 99, 1196, 1176, 110603, 110603)]
    [InlineData(21600, 24, 997, 999, 22778, 22777)]
    public void Delayed_allagan_notice_leaves_an_unrelated_gone_listing_alone(
        int delaySeconds, int quantity, long oldPrice, long newPrice, long gameNet, long noticeNet)
    {
        var t = new ListingTracker();
        var notice = T0.AddSeconds(delaySeconds);
        var ended = notice.AddHours(-18);
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, quantity, oldPrice)], Name, ended.AddMinutes(-1));
        t.ObserveRetainer(K, RetainerA, [], Name, ended, authoritative: true);
        var gone = Assert.Single(t.Records);
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, quantity, newPrice)], Name, T0.AddMinutes(-1));
        var sold = t.OnSale(7, false, gameNet, T0);
        Assert.NotNull(sold);
        t.Dirty = false;

        Assert.Null(t.OnAllaganSale([7], quantity, noticeNet, notice));

        Assert.Equal(ListingTracker.Gone, gone.Outcome);
        Assert.Equal(0, gone.SoldTotalGil);
        Assert.Null(gone.SoldRecordedUtc);
        Assert.Same(sold, Assert.Single(t.GetSales()));
        Assert.Equal(notice, sold.AllaganNoticeUtc);
        Assert.True(t.Dirty);
    }

    [Fact]
    public void Delayed_allagan_notice_leaves_a_relisted_stack_active()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 99, 1176)], Name, T0.AddMinutes(-1));
        var sold = t.OnSale(7, false, 110603, T0);
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 99, 1196)], Name, T0.AddMinutes(1));
        var relisted = Assert.Single(t.ActiveListings);

        Assert.Null(t.OnAllaganSale([7], 99, 110603, T0.AddHours(6)));

        Assert.Equal(ListingTracker.Active, relisted.Outcome);
        Assert.Same(sold, Assert.Single(t.GetSales()));
    }

    [Fact]
    public void Delayed_allagan_notice_pairs_with_a_history_recorded_sale()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, T0.AddMinutes(-1));
        var sale = new RetainerHistorySale(7, 9500, (uint)new DateTimeOffset(T0).ToUnixTimeSeconds(), 1, false, false, string.Empty);
        Assert.Equal(HistorySaleResult.MatchedListing, t.OnHistorySale(K, RetainerA, sale, Name));
        var sold = Assert.Single(t.GetSales());
        Assert.Equal("history", sold.SoldSource);
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, T0.AddMinutes(1));
        var active = Assert.Single(t.ActiveListings);

        Assert.Null(t.OnAllaganSale([7], 1, 9500, T0.AddHours(6)));

        Assert.Equal(ListingTracker.Active, active.Outcome);
        Assert.Same(sold, Assert.Single(t.GetSales()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(360)]
    public void Paired_allagan_notice_allows_a_second_sale_eleven_minutes_later(int delayMinutes)
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000), new(1, 7, false, 1, 10000)], Name, T0.AddMinutes(-1));
        var sold = t.OnSale(7, false, 9500, T0);
        Assert.NotNull(sold);
        var unseen = Assert.Single(t.ActiveListings);
        var notice = T0.AddMinutes(delayMinutes);
        t.Dirty = false;

        Assert.Null(t.OnAllaganSale([7], 1, 9500, notice));
        Assert.True(t.Dirty);
        Assert.Equal(notice, sold.AllaganNoticeUtc);
        Assert.Same(unseen, t.OnAllaganSale([7], 1, 9500, notice.AddMinutes(11)));
        Assert.Null(unseen.AllaganNoticeUtc);

        Assert.Equal(2, t.GetSales().Count());
    }

    [Fact]
    public void Simultaneous_allagan_notices_each_pair_with_a_known_game_sale()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA,
            [new(0, 7, false, 1, 10000), new(1, 7, false, 1, 10000),
             new(2, 7, false, 1, 10000), new(3, 7, false, 1, 10000)], Name, T0.AddMinutes(-1));
        Assert.NotNull(t.OnSale(7, false, 9500, T0));
        Assert.NotNull(t.OnSale(7, false, 9500, T0));
        Assert.NotNull(t.OnSale(7, false, 9500, T0));
        var active = Assert.Single(t.ActiveListings);
        var notice = T0.AddHours(6);

        for (var i = 0; i < 3; i++)
            Assert.Null(t.OnAllaganSale([7], 1, 9500, notice));

        var sales = t.GetSales().ToList();
        Assert.Equal(3, sales.Count);
        Assert.All(sales, sale => Assert.Equal(notice, sale.AllaganNoticeUtc));
        Assert.Equal(ListingTracker.Active, active.Outcome);
        Assert.Same(active, t.OnAllaganSale([7], 1, 9500, notice.AddMinutes(11)));
    }

    [Fact]
    public void Reconcile_history_preserves_a_paired_allagan_notice_on_the_merged_listing()
    {
        var t = new ListingTracker();
        var sale = new RetainerHistorySale(7, 9500, (uint)new DateTimeOffset(T0).ToUnixTimeSeconds(), 1, false, false, string.Empty);
        Assert.Equal(HistorySaleResult.HistoryOnly, t.OnHistorySale(K, RetainerA, sale, Name));
        var history = Assert.Single(t.GetSales());
        var notice = T0.AddHours(6);
        Assert.Null(t.OnAllaganSale([7], 1, 9500, notice));
        Assert.Equal(notice, history.AllaganNoticeUtc);

        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, T0.AddMinutes(-1));
        var target = Assert.Single(t.ActiveListings);
        t.ObserveRetainer(K, RetainerA, [], Name, notice, authoritative: true);
        Assert.Equal(ListingTracker.Gone, target.Outcome);
        t.ReconcileHistory();

        Assert.Same(target, Assert.Single(t.Records));
        Assert.Equal(notice, target.AllaganNoticeUtc);
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, notice.AddMinutes(1));
        var active = Assert.Single(t.ActiveListings);
        Assert.Same(active, t.OnAllaganSale([7], 1, 9500, notice.AddMinutes(11)));
    }

    [Fact]
    public void Repeated_delayed_allagan_notice_does_not_sell_a_listing_or_extend_the_window()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, T0.AddMinutes(-1));
        Assert.NotNull(t.OnSale(7, false, 9500, T0));
        var notice = T0.AddHours(6);
        Assert.Null(t.OnAllaganSale([7], 1, 9500, notice));
        t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, notice.AddSeconds(1));
        var active = Assert.Single(t.ActiveListings);
        t.Dirty = false;

        Assert.Null(t.OnAllaganSale([7], 1, 9501, notice.AddSeconds(88)));
        Assert.Equal(ListingTracker.Active, active.Outcome);
        Assert.Single(t.GetSales());
        Assert.False(t.Dirty);
        Assert.Same(active, t.OnAllaganSale([7], 1, 9500, notice.AddMinutes(11)));
    }

    [Fact]
    public void Allagan_notice_pairing_survives_save_and_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "restocky-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = new ListingTracker();
            t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, T0.AddMinutes(-1));
            Assert.NotNull(t.OnSale(7, false, 9500, T0));
            var notice = T0.AddHours(6);
            Assert.Null(t.OnAllaganSale([7], 1, 9500, notice));
            t.ObserveRetainer(K, RetainerA, [new(0, 7, false, 1, 10000)], Name, notice.AddMinutes(1));
            Assert.True(t.Save(dir));

            var back = ListingTracker.Load(Path.Combine(dir, "listings.json"));
            Assert.Equal(notice, Assert.Single(back.GetSales()).AllaganNoticeUtc);
            var unseen = Assert.Single(back.ActiveListings);
            Assert.Same(unseen, back.OnAllaganSale([7], 1, 9500, notice.AddMinutes(11)));
            Assert.Equal(2, back.GetSales().Count());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(73, 1, 9500, 1)]
    [InlineData(6, 2, 9500, 1)]
    [InlineData(6, 1, 9309, 1)]
    [InlineData(6, 1, 9500, 2)]
    public void Unrelated_known_sale_does_not_absorb_an_allagan_notice(
        int delayHours, int knownQuantity, long knownNet, ulong knownCharacter)
    {
        var t = new ListingTracker();
        var knownKey = $"{knownCharacter:X}.A";
        t.ObserveRetainer(knownKey, RetainerA, [new(0, 7, false, knownQuantity, 10000)], Name, T0.AddMinutes(-1));
        Assert.NotNull(t.OnSale(7, false, knownNet, T0, knownCharacter));
        var notice = T0.AddHours(delayHours);
        t.ObserveRetainer("1.B", RetainerB, [new(0, 7, false, 1, 10000)], Name, notice.AddMinutes(-1));
        var unseen = Assert.Single(t.ActiveListings);

        Assert.Same(unseen, t.OnAllaganSale([7], 1, 9500, notice, 1));
        Assert.Equal(2, t.GetSales().Count());
    }

    // The game rounds the tax down, not the proceeds (133 of 140 recorded sales; none round the proceeds down).
    [Theory]
    [InlineData(75_739, 71_953)]
    [InlineData(34_452, 32_730)]
    [InlineData(33_066, 31_413)]
    [InlineData(100, 95)]
    public void Gross_gil_is_converted_to_net_with_tax_rounded_down(long gross, long net)
        => Assert.Equal(net, ListingTracker.NetFromGross(gross));

    [Fact]
    public void Competitors_on_another_retainers_listing_and_hq_are_separate()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, true, 1, 1_000)), Name, T0);
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(10, "NqGuy", 1) }, T0);
        Assert.Empty(t.Records[0].Competitors);
    }

    [Fact]
    public void Save_and_load_roundtrip_and_csv_headers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "restocky-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = new ListingTracker();
            t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 85_000)), Name, T0);
            t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_999, "Rival", 1) }, T0);
            t.OnSale(100, false, 80_750, T0.AddHours(1));
            Assert.True(t.Save(dir));
            Assert.False(t.Dirty);

            var back = ListingTracker.Load(Path.Combine(dir, "listings.json"));
            Assert.Equal("Rival", back.Records[0].Competitors.Keys.Single());
            Assert.Equal(ListingTracker.GameSaleSource, back.Records[0].SoldSource);

            var listings = File.ReadAllLines(Path.Combine(dir, "listings.csv"));
            Assert.Equal(ListingTracker.ListingsHeader, listings[0].TrimStart('﻿'));
            Assert.Contains(",Rival 1x,Rival 1x,", listings[1]);
            var comps = File.ReadAllLines(Path.Combine(dir, "competitors.csv"));
            Assert.StartsWith("100,Twine,false,Rival,1,1,84999,1,1,", comps[1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("The Twine you put up for sale in the Limsa Lominsa markets has sold for 80,746 gil (after fees).", 80_746)]
    [InlineData("リテイナーに預けたTwineが売れました！　80,746ギルを受け取りました。", 80_746)]
    [InlineData("Dein Twine wurde für 80.746 Gil verkauft.", 80_746)]
    [InlineData("no amount here", 0)]
    public void Sale_message_gil_is_parsed(string text, long expected)
        => Assert.Equal(expected, SaleMessage.ParseGil(text));
}
