using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public class AutoRestockPlannerTests
{
    private const uint Twine = 50970;
    [Fact]
    public void Handoff_counts_previous_visit_and_reports_unassigned_skips_once()
    {
        var a = Retainer("RetainerA");
        var b = Retainer("RetainerB");
        a.RetainerName = a.Key;
        b.RetainerName = b.Key;
        a.Inventory.Add(Stock(Twine, 1));
        b.Inventory.Add(Stock(Twine, 2));
        LowStockEntry[] entries = [new() { ItemId = Twine, Target = 2 }, Watch(Other)];
        var first = AutoRestockPlanner.ForVisit(Plan(entries, [a, b]), a, a);
        Assert.Equal(a.Key, Assert.Single(first.First().Actions).RetainerKey);
        Assert.Contains(first, p => p.ItemId == Other);
        a.Inventory.Clear();
        a.Listings.Add(Listed(Twine, 1, 500));
        var next = AutoRestockPlanner.ForVisit(Plan(entries, [a, b]), b, a);
        var action = Assert.Single(Assert.Single(next).Actions);
        Assert.Equal(b.Key, action.RetainerKey);
        Assert.Equal(1, action.Quantity);
        Assert.Equal(500, action.UnitPrice);
    }

    [Fact]
    public void Handoff_requires_a_fresh_active_snapshot_and_a_complete_same_character_roster()
    {
        var a = Retainer("RetainerA");
        var b = Retainer("RetainerB");
        a.LastRefreshedUtc = b.LastRefreshedUtc = DateTime.UtcNow;
        Assert.True(AutoRestockPlanner.CompleteVisitRoster(a, [a, b], 2));
        Assert.False(AutoRestockPlanner.CompleteVisitRoster(null, [a, b], 2));
        Assert.False(AutoRestockPlanner.CompleteVisitRoster(a, [a], 2));
        b.CharacterContentId++;
        Assert.False(AutoRestockPlanner.CompleteVisitRoster(a, [a, b], 2));
        b.CharacterContentId--;
        b.LastRefreshedUtc = DateTime.MinValue;
        Assert.False(AutoRestockPlanner.CompleteVisitRoster(a, [a, b], 2));
    }

    private const uint Other = 50971;

    [Fact]
    public void Tops_up_by_units_at_the_lowest_live_price_after_repricings()
    {
        var a = Retainer("A", Listed(Twine, 1, 1_234), Listed(Twine, 1, 1_600));
        var b = Retainer("B", Listed(Twine, 7, 800, hq: true));
        a.Inventory.Add(Stock(Twine, 2));
        var entry = new LowStockEntry { ItemId = Twine, Threshold = 2, Target = 3, Price = 3_000 };

        var result = Assert.Single(Plan([entry], [a, b]));

        Assert.Equal(AutoRestockPriceKind.Listing, result.PriceKind);
        var action = Assert.Single(result.Actions);
        Assert.Equal("A", action.SourceKey);
        Assert.Equal("A", action.RetainerKey);
        Assert.Equal(1, action.Quantity);
        Assert.Equal(1_234, action.UnitPrice);
        Assert.False(action.IsHQ);
    }

    [Fact]
    public void Requires_threshold_and_target_and_respects_hold_by_quality()
    {
        var a = Retainer("A", Listed(Twine, 2, 100), Listed(Other, 1, 100));
        a.Inventory.AddRange([Stock(Twine, 5), Stock(Twine, 5, hq: true), Stock(Other, 5)]);
        var entries = new[]
        {
            new LowStockEntry { ItemId = Twine, Threshold = 1, Target = 3 },
            new LowStockEntry { ItemId = Twine, IsHq = true, Target = 2 },
            new LowStockEntry { ItemId = Other, Threshold = 1, Target = 1 },
        };

        Assert.Empty(Plan(entries, [a], held: (id, hq) => id == Twine && hq));
        Assert.True(Assert.Single(Plan(entries, [a])).IsHq);
    }

    [Theory]
    [InlineData(StockRestockMode.Default, StockStatus.None, StockRestockMode.Auto)]
    [InlineData(StockRestockMode.Default, StockStatus.Regular, StockRestockMode.Auto)]
    [InlineData(StockRestockMode.Default, StockStatus.Trial, StockRestockMode.Alert)]
    [InlineData(StockRestockMode.Auto, StockStatus.Trial, StockRestockMode.Auto)]
    [InlineData(StockRestockMode.Alert, StockStatus.Regular, StockRestockMode.Alert)]
    public void Resolves_explicit_modes_and_tag_defaults(StockRestockMode mode, StockStatus status, StockRestockMode expected)
    {
        var entry = new LowStockEntry { RestockMode = mode, Status = status };
        Assert.Equal(expected, entry.EffectiveRestockMode);
    }

    [Fact]
    public void Old_trial_entries_default_to_alert()
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<LowStockEntry>("""{"ItemId":50970,"Status":2}""");
        Assert.NotNull(entry);
        Assert.Equal(StockRestockMode.Default, entry.RestockMode);
        Assert.Equal(StockRestockMode.Alert, entry.EffectiveRestockMode);
    }

    [Fact]
    public void Alert_entries_do_not_take_the_last_slot_from_an_auto_entry()
    {
        var a = Retainer("A", Enumerable.Repeat(Listed(Other + 1, 1, 100), 19).ToArray());
        a.Inventory.AddRange([Stock(Twine, 1), Stock(Twine, 1, hq: true), Stock(Other, 1)]);
        var trial = new LowStockEntry { ItemId = Twine, Status = StockStatus.Trial };
        var alert = new LowStockEntry { ItemId = Other, RestockMode = StockRestockMode.Alert };
        var auto = new LowStockEntry { ItemId = Twine, IsHq = true, Status = StockStatus.Trial, RestockMode = StockRestockMode.Auto };

        var result = Assert.Single(Plan([trial, alert, auto], [a]));

        Assert.True(Assert.Single(result.Actions).IsHQ);
    }

    [Fact]
    public void Leaves_unlisted_item_price_for_execution()
    {
        var a = Retainer("A", Listed(Twine, 7, 800, hq: true));
        a.Inventory.Add(Stock(Twine, 1));
        var plan = Assert.Single(Plan([Watch(Twine)], [a], floor: 750));

        Assert.Equal(AutoRestockPriceKind.Pending, plan.PriceKind);
        var action = Assert.Single(plan.Actions);
        Assert.True(action.ResolveRestockPrice);
        Assert.Equal(0, action.UnitPrice);
    }

    [Fact]
    public void Skips_an_unknown_listing_price_and_an_invalid_floor()
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1));
        var invalid = Assert.Single(Plan([Watch(Twine)], [a], floor: (long)uint.MaxValue + 1));
        Assert.Empty(invalid.Actions);
        Assert.Equal(AutoRestockPriceKind.OutOfRange, invalid.PriceKind);

        a.Listings.Add(Listed(Twine, 1, 0));
        var unpriced = Assert.Single(Plan([new LowStockEntry { ItemId = Twine, Target = 2 }], [a]));
        Assert.Empty(unpriced.Actions);
        Assert.Equal(AutoRestockPriceKind.UnknownListingPrice, unpriced.PriceKind);
    }

    [Theory]
    [InlineData("no stock", (int)RestockSkipReason.NoStock)]
    [InlineData("full", (int)RestockSkipReason.NoSlot)]
    [InlineData("preferred full", (int)RestockSkipReason.PreferredRetainerFull)]
    [InlineData("stock on full retainer", (int)RestockSkipReason.SourceRetainerFull)]
    public void Explains_why_a_valid_price_has_no_rows(string scenario, int expected)
    {
        var a = Retainer("A", Listed(Twine, 1, 100));
        a.RetainerName = "RetainerA";
        var entry = new LowStockEntry { ItemId = Twine, Target = 2 };
        var retainers = new[] { a };
        if (scenario != "no stock")
        {
            a.Inventory.Add(Stock(Twine, 1));
            a.Listings.AddRange(Enumerable.Repeat(Listed(Other, 1, 100), 19));
        }
        if (scenario == "preferred full") entry.PreferredRetainers[1] = "RetainerA";
        if (scenario == "stock on full retainer") retainers = [a, Retainer("B")];

        var plan = Assert.Single(Plan([entry], retainers));
        Assert.Empty(plan.Actions);
        Assert.Equal(AutoRestockPriceKind.Listing, plan.PriceKind);
        Assert.Equal((RestockSkipReason)expected, plan.SkipReason);
    }

    [Fact]
    public void Shares_free_slots_across_items()
    {
        var a = Retainer("A", Enumerable.Repeat(Listed(Other, 1, 100), 19).ToArray());
        var b = Retainer("B", Enumerable.Repeat(Listed(Other, 1, 100), 20).ToArray());
        var ch = new CharacterSnapshot { CharacterContentId = 1, Bag = [Stock(Twine, 2), Stock(Twine, 2, hq: true)] };

        var plans = Plan([Watch(Twine), Watch(Twine, hq: true)], [a, b], character: ch);

        Assert.Equal(2, plans.Count);
        Assert.Equal(RestockSkipReason.NoSlot, plans[1].SkipReason);
        Assert.Empty(plans[1].Actions);
        var action = Assert.Single(plans[0].Actions);
        Assert.Equal("A", action.RetainerKey);
        Assert.False(action.IsHQ);
    }

    [Fact]
    public void Plans_a_watched_item_only_once()
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 2));
        var result = Assert.Single(Plan([Watch(Twine), Watch(Twine)], [a]));
        Assert.Equal(1, Assert.Single(result.Actions).Quantity);
    }

    [Fact]
    public void Cycle_uses_the_current_characters_sell_on_preference_and_only_creates_new_listings()
    {
        var a = Retainer("A", Listed(Twine, 74, 100));
        var b = Retainer("B");
        a.RetainerName = "Other";
        b.RetainerName = "RetainerA";
        var entry = new LowStockEntry
        {
            ItemId = Twine, Threshold = 74, Target = 76, PerListing = 1,
            PreferredRetainers = new() { [1] = "RetainerA", [2] = "Other" },
        };
        var character = new CharacterSnapshot { CharacterContentId = 1, Bag = [Stock(Twine, 2)] };
        var result = Assert.Single(Plan([entry], [a, b], character: character));

        Assert.Equal(2, result.Actions.Count);
        Assert.All(result.Actions, action =>
        {
            Assert.Equal("B", action.RetainerKey);
            Assert.Equal(PlannedActionKind.NewListing, action.Kind);
            Assert.Null(action.TopUp);
        });
    }

    [Fact]
    public void Reuses_retainer_bag_and_saddle_sources_without_combining_inventory_stacks()
    {
        var a = Retainer("A");
        a.Inventory.Add(Stock(Twine, 1));
        var ch = new CharacterSnapshot
        {
            CharacterContentId = 1, Bag = [Stock(Twine, 2), Stock(Twine, 1)],
            Saddlebag = [Stock(Twine, 10, max: 2)], PremiumSaddlebag = [Stock(Twine, 5)],
        };
        var entry = new LowStockEntry { ItemId = Twine, Target = 16, PerListing = 5 };
        var result = Assert.Single(Plan([entry], [a], character: ch));

        Assert.Equal(new[] { 1, 2, 1, 2, 2, 2, 2, 2, 2 }, result.Actions.Select(x => x.Quantity));
        Assert.Equal(new[] { "A", "char.1.bag", "char.1.bag" }, result.Actions.Take(3).Select(x => x.SourceKey));
        Assert.All(result.Actions.Skip(3), x => Assert.Equal("char.1.saddle", x.SourceKey));
    }

    private static List<AutoRestockPlan> Plan(LowStockEntry[] entries, RetainerSnapshot[] retainers,
        CharacterSnapshot? character = null, long floor = 0, Func<uint, bool, bool>? held = null)
        => AutoRestockPlanner.Plan(entries, retainers, character, new ListingTracker(),
            held ?? ((_, _) => false), (_, _) => floor);

    private static LowStockEntry Watch(uint id, bool hq = false) => new() { ItemId = id, IsHq = hq, Target = 1 };
    private static RetainerSnapshot Retainer(string key, params ListingEntry[] listings)
        => new() { Key = key, CharacterContentId = 1, Listings = listings.ToList() };
    private static ListingEntry Listed(uint id, int qty, long price, bool hq = false)
        => new() { ItemId = id, IsHQ = hq, Quantity = qty, UnitPrice = price };
    private static InventoryEntry Stock(uint id, int qty, bool hq = false, int max = 99)
        => new() { ItemId = id, IsHQ = hq, Quantity = qty, MaxStackPerListing = max };
}
