using System.Collections.Generic;
using System.IO;
using System.Linq;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public class RepriceRulesTests
{
    private static readonly RepriceSettings Default = new();

    [Theory]
    [InlineData(0, 95, RepriceReason.Undercut)]
    [InlineData(250, 250, RepriceReason.Floor)]
    public void Include_bait_uses_the_lowest_offer_with_undercut_and_floor(
        long floor, long expectedPrice, RepriceReason reason)
    {
        long[] prices = [100, 900, 920];
        var settings = new RepriceSettings { UndercutGil = 5 };
        var normal = RepriceRules.Decide(1_000, prices, floor, settings);
        var match = RepriceRules.Decide(1_000, prices, floor, settings, includeBait: true);

        Assert.Equal(900, normal.LowestCompetitor);
        Assert.False(normal.BypassedMaxDrop);
        Assert.Equal(100, match.LowestCompetitor);
        Assert.Equal(expectedPrice, match.NewPrice);
        Assert.Equal(reason, match.Reason);
        Assert.True(match.Apply);
        Assert.True(match.BypassedMaxDrop);
    }

    [Fact]
    public void Include_bait_still_enforces_max_raise()
    {
        var match = RepriceRules.Decide(100, [50, 100, 120], 150, Default, includeBait: true);

        Assert.Equal(150, match.NewPrice);
        Assert.Equal(RepriceReason.MaxRaise, match.Reason);
        Assert.False(match.Apply);
        Assert.False(match.BypassedMaxDrop);
    }

    [Fact]
    public void Planner_matches_all_unheld_copies_and_remembers_bait_per_listing()
    {
        LiveListing[] listings = [new(0, 1, true, 1, 1_000), new(1, 1, true, 5, 1_000),
            new(2, 1, false, 1, 1_000), new(3, 2, true, 1, 1_000)];
        MarketOffer[] offers = [new(100, "Bait", 1), new(900, "Rival", 1), new(920, "Rival", 1)];
        var plan = AutoRepricePlanner.Plan("k", "R", listings, (_, _) => offers, (_, _) => 0,
            Default, DateTime.UtcNow, isHeld: (id, _) => id == 2, includeBaitForPlan: (_, hq) => hq);

        Assert.All(plan.Actions.Where(a => a.IsHQ), a =>
        {
            Assert.Equal(99, a.UnitPrice);
            Assert.True(a.BypassedMaxDrop);
        });
        Assert.Equal(2, plan.Actions.Count(a => a.IsHQ));
        Assert.Equal(RepriceReason.Held, Assert.Single(plan.NotApplied).Reason);
        var bait = Assert.Single(plan.BaitPrices);
        Assert.Equal((2, 1u, false), bait.Key);
        Assert.Equal(100, bait.Value);
        Assert.False(Assert.Single(plan.Baits).IsHq);
    }

    [Fact]
    public void Undercuts_lowest_competitor_by_one()
    {
        var d = RepriceRules.Decide(80_000, new long[] { 75_000, 76_000 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(74_999, d.NewPrice);
        Assert.Equal(RepriceReason.Undercut, d.Reason);
        Assert.Equal(75_000, d.LowestCompetitor);
    }

    [Fact]
    public void Planner_applies_the_fifo_floor_for_each_quality_and_records_floor_reason()
    {
        var now = DateTime.UtcNow;
        var ledger = new CostLedger();
        ledger.AddManualLot(1, false, 1, 95m, now);
        ledger.RecordLot(1, false, 1, 190m, now.AddSeconds(1));
        ledger.RecordLot(1, true, 1, 114m, now);
        var listings = new[] { new LiveListing(0, 1, false, 1, 110), new LiveListing(1, 1, true, 1, 110) };
        var offers = new[] { new MarketOffer(91, "Rival", 1) };

        var plan = AutoRepricePlanner.Plan("k", "R", listings, (_, _) => offers,
            (item, hq) => BreakEvenFloor.Resolve(0, ledger.CurrentUnitCost(item, hq), 5, 0).Price,
            Default, now);

        Assert.Collection(plan.Actions,
            nq => { Assert.Equal(100, nq.UnitPrice); Assert.Equal(RepriceReason.Floor, nq.Reason); },
            hq => { Assert.Equal(120, hq.UnitPrice); Assert.Equal(RepriceReason.Floor, hq.Reason); });
    }

    [Fact]
    public void Zero_raise_limit_allows_large_increases()
    {
        var d = RepriceRules.Decide(50_000, new long[] { 75_000 }, 0, new RepriceSettings { MaxRaisePercent = 0 });
        Assert.True(d.Apply);
        Assert.Equal(74_999, d.NewPrice);
    }

    [Fact]
    public void Low_group_is_evaluated_for_each_listing_on_the_same_board()
    {
        var offers = new[]
        {
            new MarketOffer(15_000, "A", 1), new MarketOffer(15_000, "A", 1),
            new MarketOffer(99_998, "B", 1), new MarketOffer(99_999, "C", 1),
            new MarketOffer(100_000, "D", 1), new MarketOffer(100_000, "E", 1),
        };
        var listings = new[] { new LiveListing(0, 1, false, 1, 14_999), new LiveListing(1, 1, false, 1, 100_000) };
        var nearby = AutoRepricePlanner.Plan("k", "R", listings[..1], (_, _) => offers, (_, _) => 0, Default, DateTime.UtcNow);
        Assert.Empty(nearby.Baits);
        Assert.Empty(nearby.Actions);
        var unchanged = Assert.Single(nearby.NotApplied);
        Assert.Equal(15_000, unchanged.LowestCompetitor);
        Assert.Equal(14_999, unchanged.NewPrice);
        Assert.Equal(RepriceReason.NoChange, unchanged.Reason);

        var both = AutoRepricePlanner.Plan("k", "R", listings, (_, _) => offers, (_, _) => 0, Default, DateTime.UtcNow);
        var action = Assert.Single(both.Actions);
        Assert.Equal(RepriceReason.Outlier, action.Reason);
        Assert.Equal(99_997, action.UnitPrice);
        var notice = Assert.Single(both.Baits);
        Assert.Equal(99_998, notice.RealLowest);
        Assert.Equal(2, notice.Baits.Count);
    }

    [Theory]
    [InlineData(13_000, true, RepriceReason.Undercut)]
    [InlineData(13_001, false, RepriceReason.MaxRaise)]
    [InlineData(6_999, false, RepriceReason.MaxDrop)]
    public void Prefill_preserves_target_and_marks_changes_beyond_the_limit(long target, bool apply, RepriceReason reason)
    {
        var d = RepriceRules.Decide(10_000, new[] { target + 1 }, 0, Default);
        Assert.Equal(target, d.NewPrice);
        Assert.Equal(apply, d.Apply);
        Assert.Equal(reason, d.Reason);
        Assert.Equal(!apply, d.MarkForReview);
    }

    [Fact]
    public void Single_high_offer_records_skipped_raise_and_proposed_price()
    {
        var plan = AutoRepricePlanner.Plan("k", "R", new[] { new LiveListing(0, 1, false, 1, 449_999) },
            (_, _) => new[] { new MarketOffer(799_999, "A", 1) }, (_, _) => 0, Default, DateTime.UtcNow);
        Assert.Empty(plan.Actions);
        var row = Assert.Single(plan.NotApplied);
        Assert.False(row.Applied);
        Assert.Equal(799_998, row.NewPrice);
        Assert.Equal(RepriceReason.MaxRaise, row.Reason);
        Assert.EndsWith(",449999,799999,799998,false,max_raise,", PriceHistoryLog.Format(row));
    }

    [Fact]
    public void Floor_driven_raise_is_marked_for_review()
    {
        var d = RepriceRules.Decide(100, new long[] { 95 }, 135, Default);
        Assert.False(d.Apply);
        Assert.True(d.MarkForReview);
        Assert.Equal(135, d.NewPrice);
        Assert.Equal(RepriceReason.MaxRaise, d.Reason);
    }

    [Theory]
    [InlineData(22_500, 14_900)]
    [InlineData(22_501, 99_998)]
    [InlineData(0, 99_998)]
    public void Low_group_exception_uses_group_max_and_inclusive_ratio(long current, long lowest)
    {
        Assert.Equal(lowest, LowestPriceResolver.Pick(new long[] { 14_900, 15_000, 99_998 }, ourPrice: current));
    }

    [Fact]
    public void Already_at_target_is_no_change()
    {
        var d = RepriceRules.Decide(74_999, new long[] { 75_000 }, 0, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.NoChange, d.Reason);
    }

    [Fact]
    public void No_competitor_keeps_price()
    {
        var d = RepriceRules.Decide(10_000, new long[0], 0, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.NoCompetitor, d.Reason);
    }

    [Fact]
    public void Neotunic_single_bait_listing_is_refused_by_max_drop()
    {
        // 2026-11-18: one 11,111 listing, real price ~75K. Only one competitor, so the
        // outlier filter cannot help; the max-drop guard must stop it.
        var d = RepriceRules.Decide(75_000, new long[] { 11_111 }, 0, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.MaxDrop, d.Reason);
        Assert.Equal(11_110, d.NewPrice);
    }

    [Fact]
    public void Bait_listing_with_real_cluster_behind_it_is_ignored()
    {
        var d = RepriceRules.Decide(78_000, new long[] { 11_111, 75_000, 75_500, 76_000 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(74_999, d.NewPrice);
        Assert.Equal(RepriceReason.Outlier, d.Reason);
    }

    [Fact]
    public void Floor_clamps_the_target()
    {
        var d = RepriceRules.Decide(60_000, new long[] { 50_000, 51_000 }, floor: 55_000, Default);
        Assert.True(d.Apply);
        Assert.Equal(55_000, d.NewPrice);
        Assert.Equal(RepriceReason.Floor, d.Reason);
    }

    [Fact]
    public void Floor_equal_to_current_is_no_change()
    {
        var d = RepriceRules.Decide(55_000, new long[] { 50_000 }, floor: 55_000, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.NoChange, d.Reason);
    }

    [Fact]
    public void Max_drop_zero_disables_the_guard()
    {
        var s = new RepriceSettings { MaxDropPercent = 0 };
        var d = RepriceRules.Decide(75_000, new long[] { 11_111 }, 0, s);
        Assert.True(d.Apply);
    }

    [Fact]
    public void Drop_exactly_at_limit_is_allowed()
    {
        // 100 -> 70 is exactly 30%.
        var d = RepriceRules.Decide(100, new long[] { 71 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(70, d.NewPrice);
    }

    [Fact]
    public void Price_never_goes_below_one()
    {
        var s = new RepriceSettings { MaxDropPercent = 0, UndercutGil = 5 };
        var d = RepriceRules.Decide(10, new long[] { 2 }, 0, s);
        Assert.Equal(1, d.NewPrice);
    }

    [Fact]
    public void Planner_splits_applied_and_not_applied_and_compares_hq_separately()
    {
        var listings = new List<LiveListing>
        {
            new(0, 100, false, 99, 80_000),   // NQ, undercut to 74,999
            new(1, 100, true, 10, 120_000),   // HQ, no HQ competitor
            new(2, 200, false, 5, 1_000),     // no fresh data
        };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => (item, hq) switch
        {
            (100, false) => new[] { new MarketOffer(75_000, "Rival", 1), new MarketOffer(90_000, "Bob", 1) },
            (100, true) => new MarketOffer[0],
            _ => null,
        };
        var r = AutoRepricePlanner.Plan("k", "RetainerA", listings, Comp, (_, _) => 0, Default, new System.DateTime(2026, 11, 20));

        var a = Assert.Single(r.Actions);
        Assert.Equal(0, a.ListingIndex);
        Assert.Equal(74_999, a.UnitPrice);
        Assert.Equal(80_000, a.OldPrice);
        Assert.Equal("Rival 1x", a.Undercutters);
        Assert.Equal(2, r.NotApplied.Count);
        Assert.Equal(RepriceReason.NoCompetitor, r.NotApplied[0].Reason);
        Assert.Equal(RepriceReason.Error, r.NotApplied[1].Reason);
        Assert.False(r.NotApplied[1].Applied);
        Assert.Equal(r.NotApplied[1].OldPrice, r.NotApplied[1].NewPrice);
    }

    [Fact]
    public void Auto_planner_held_listing_has_no_action_and_writes_held_history()
    {
        var listings = new List<LiveListing> { new(3, 100, true, 1, 99_999) };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[] { new MarketOffer(200_000, "Rival", 1) };

        var r = AutoRepricePlanner.Plan("k", "RetainerA", listings, Comp, (_, _) => 0, Default,
            new System.DateTime(2026, 11, 20), (item, hq) => item == 100 && hq);

        Assert.Empty(r.Actions);
        var row = Assert.Single(r.NotApplied);
        Assert.False(row.Applied);
        Assert.Equal(RepriceReason.Held, row.Reason);
        Assert.Equal(99_999, row.NewPrice);
        Assert.Equal(1, r.HeldCount);
        Assert.EndsWith(",false,held,", PriceHistoryLog.Format(row));
    }

    [Fact]
    public void Auto_planner_hold_is_independent_for_hq_and_nq()
    {
        var listings = new List<LiveListing>
        {
            new(0, 100, false, 1, 100_000),
            new(1, 100, true, 1, 100_000),
        };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[] { new MarketOffer(90_000, "Rival", 1) };

        var r = AutoRepricePlanner.Plan("k", "RetainerA", listings, Comp, (_, _) => 0, Default,
            new System.DateTime(2026, 11, 20), (item, hq) => item == 100 && hq);

        Assert.Single(r.Actions);
        Assert.False(r.Actions[0].IsHQ);
        Assert.Equal(RepriceReason.Held, Assert.Single(r.NotApplied).Reason);
    }

    [Fact]
    public void History_row_format_matches_header()
    {
        var row = new PriceHistoryRow
        {
            UtcTime = new System.DateTime(2026, 11, 20, 18, 5, 0, System.DateTimeKind.Utc),
            Retainer = "RetainerA", Slot = 3, ItemId = 44_000, Hq = true, Qty = 5,
            OldPrice = 80_000, LowestCompetitor = 75_000, NewPrice = 74_999, Applied = true,
            Reason = RepriceReason.Undercut,
        };
        Assert.Equal("2026-11-20T18:05:00Z,RetainerA,3,44000,true,5,80000,75000,74999,true,undercut,",
            PriceHistoryLog.Format(row));
        Assert.Equal(12, PriceHistoryLog.Header.Split(',').Length);
    }

    [Fact]
    public void Undercutters_sum_quantity_per_seller_cheapest_first()
    {
        var offers = new[]
        {
            new MarketOffer(84_999, "Rival", 3),
            new MarketOffer(84_990, "Bob", 1),
            new MarketOffer(84_998, "Rival", 1),
            new MarketOffer(90_000, "Expensive", 9),
        };
        Assert.Equal("Bob 1x;Rival 4x", MarketOfferFormat.Undercutters(offers, 85_000));
        Assert.Equal(string.Empty, MarketOfferFormat.Undercutters(offers, 1_000));
    }

    [Fact]
    public void Lemonette_bait_is_reported_with_seller_quantity_and_price()
    {
        var listings = new List<LiveListing> { new(9, 27835, false, 99, 943) };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[]
        {
            new MarketOffer(520, "Meowsugar", 190),
            new MarketOffer(944, "Arthurrr", 128),
            new MarketOffer(945, "Izzywizzy", 10),
            new MarketOffer(997, "Mineralprincess", 198),
        };
        var r = AutoRepricePlanner.Plan("k", "RetainerB", listings, Comp, (_, _) => 0, Default, new System.DateTime(2026, 11, 20));
        var bait = Assert.Single(r.Baits);
        Assert.Equal(944, bait.RealLowest);
        Assert.Equal("Meowsugar", Assert.Single(bait.Baits).Seller);
        Assert.Equal("Lemonette [RetainerB]: bait listing skipped: Meowsugar 190x @ 520 (priced against 944)",
            BaitFinder.Format("Lemonette", bait));
    }

    [Fact]
    public void No_bait_notice_when_lowest_is_used()
    {
        var listings = new List<LiveListing> { new(0, 1, false, 1, 1_000) };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[] { new MarketOffer(900, "A", 1), new MarketOffer(950, "B", 1) };
        var r = AutoRepricePlanner.Plan("k", "R", listings, Comp, (_, _) => 0, Default, new System.DateTime(2026, 11, 20));
        Assert.Empty(r.Baits);
    }

    [Fact]
    public void History_append_writes_header_once()
    {
        var path = Path.Combine(Path.GetTempPath(), "restocky-test-" + System.Guid.NewGuid() + ".csv");
        try
        {
            var log = new PriceHistoryLog(path);
            var row = new PriceHistoryRow { Retainer = "RetainerB", Reason = RepriceReason.MaxDrop };
            Assert.True(log.Append(row));
            Assert.True(log.Append(row));
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(PriceHistoryLog.Header, lines[0]);
            Assert.EndsWith(",false,max_drop,", lines[2]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true, true, 999)]
    [InlineData(true, false, 899)]
    [InlineData(false, true, 999)]
    [InlineData(false, false, 899)]
    public void Cheaper_opposite_quality_offer_only_competes_when_matching_is_off(bool hq, bool match, long target)
    {
        MarketOffer[] same = [new(1000, "Same", 1)];
        MarketOffer[] other = [new(900, "Other", 2)];
        var scan = new MarketScan(hq ? other : same, hq ? same : other);
        string? competitor = null;
        var plan = AutoRepricePlanner.Plan("key", "Retainer", [new(0, 1, hq, 1, 1050)],
            (_, quality) => scan.Competitors(quality, match), (_, _) => 0, Default, DateTime.UtcNow,
            onDecision: (_, decision, offers) =>
                competitor = MarketOfferFormat.RepriceCompetitor(offers, match, decision.LowestCompetitor));
        var action = Assert.Single(plan.Actions);
        Assert.Equal(target, action.UnitPrice);
        Assert.Equal(match ? "Same 1x" : "Other 2x;Same 1x", action.Undercutters);
        var chosenHq = (match ? hq : !hq) ? "true" : "false";
        Assert.Equal($"quality_pool={(match ? "same" : "both")} lowest_hq={chosenHq} lowest={target + 1}", competitor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_opposite_quality_keeps_current_price_even_with_floor(bool hq)
    {
        MarketOffer[] other = [new(900, "Other", 2)];
        var scan = new MarketScan(hq ? other : [], hq ? [] : other);
        var plan = AutoRepricePlanner.Plan("key", "Retainer", [new(0, 1, hq, 1, 1050)],
            (_, quality) => scan.Competitors(quality, true), (_, _) => 2000, Default, DateTime.UtcNow);
        Assert.Empty(plan.Actions);
        var skipped = Assert.Single(plan.NotApplied);
        Assert.Equal(RepriceReason.NoCompetitor, skipped.Reason);
        Assert.Equal(1050, skipped.NewPrice);
        Assert.Empty(skipped.Undercutters);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bait_and_rival_counts_use_the_selected_pool(bool match)
    {
        var scan = new MarketScan([new(40, "NQ bait", 1)], [new(50, "HQ bait", 1), new(999, "HQ", 1)]);
        var offers = scan.Competitors(true, match);
        var plan = AutoRepricePlanner.Plan("key", "Retainer", [new(0, 1, true, 1, 1000)],
            (_, _) => offers, (_, _) => 0, Default, DateTime.UtcNow);
        var bait = Assert.Single(plan.Baits);
        Assert.Equal(match ? new[] { "HQ bait" } : new[] { "NQ bait", "HQ bait" }, bait.Baits.Select(o => o.Seller));
        var action = Assert.Single(plan.Actions);
        Assert.Equal(998, action.UnitPrice);
        Assert.Equal($"quality_pool={(match ? "same" : "both")} lowest_hq=true lowest=999",
            MarketOfferFormat.RepriceCompetitor(offers, match, action.LowestCompetitor));
        var history = new UndercutHistory();
        var now = DateTime.UtcNow;
        history.RecordScan(1, true, 1000, offers, now);
        Assert.Equal(match ? 0 : 1, history.Counts(1, true, "NQ bait", now).Last24h);
        Assert.Equal(1, history.Counts(1, true, "HQ bait", now).Last24h);
    }

    [Fact]
    public void Market_suggestion_selects_quality_before_testing_the_bait_gap()
    {
        var cache = new MarketCache();
        cache.Replace(1, new MarketScan([new(100, "NQ", 1)], [new(500, "HQ", 1)]));
        Assert.Equal(500, cache.GetLowest(1, true, true, 110));
        Assert.Equal(100, cache.GetLowest(1, true, false, 110));
        Assert.Equal(500, cache.GetLowest(1, true, false, 510));
        cache.Replace(1, new MarketScan([new(100, "NQ", 1)], []));
        Assert.Equal(0, cache.GetLowest(1, true, true, 510));
    }

    [Fact]
    public void Missing_quality_settings_default_on_when_upgrading()
    {
        Assert.True(new MatchQualitySettings().Default);
        var settings = System.Text.Json.JsonSerializer.Deserialize<MatchQualitySettings>("{}")!;
        Assert.True(settings.For(1, true));
        Assert.True(settings.For(1, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_quality_default_and_per_quality_overrides_round_trip(bool matchByDefault)
    {
        var settings = new MatchQualitySettings { Default = matchByDefault };
        settings.NQ[1] = !matchByDefault;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        settings = System.Text.Json.JsonSerializer.Deserialize<MatchQualitySettings>(json)!;
        Assert.Equal(matchByDefault, settings.For(1, true));
        Assert.Equal(!matchByDefault, settings.For(1, false));
        Assert.Equal(matchByDefault, settings.For(2, false));
        settings.NQ.Remove(1);
        Assert.Equal(matchByDefault, settings.For(1, false));
    }

    [Fact]
    public void Cache_fresh_check_uses_update_time()
    {
        var now = new System.DateTime(2026, 11, 20, 12, 0, 0);
        var c = new MarketCache { Clock = () => now };
        c.Replace(1, new MarketScan([new(300, "A", 1), new(100, "B", 1), new(200, "C", 1)], []));
        Assert.True(c.HasDataSince(1, now));
        Assert.False(c.HasDataSince(1, now.AddSeconds(1)));
        Assert.Equal(new long[] { 100, 200, 300 }, c.GetOffers(1, false, true).Select(o => o.Price));
    }
}
