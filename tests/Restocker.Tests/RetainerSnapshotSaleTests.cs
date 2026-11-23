using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public class RetainerSnapshotSaleTests
{
    private const uint Item = 42;
    private const long UnitPrice = 1_000;
    private static readonly DateTime Seen = DateTime.UnixEpoch.AddHours(1);
    private static readonly DateTime Sold = Seen.AddMinutes(3);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chat_sale_matching_and_paired_notice_dedup_are_character_scoped(bool gameNotice)
    {
        var tracker = new ListingTracker();
        foreach (var key in new[] { "2.A", "1.B" })
            tracker.ObserveRetainer(key, key, [new LiveListing(0, Item, false, 1, UnitPrice)], _ => "Item", Seen);
        var net = ListingTracker.NetFromGross(UnitPrice);
        var first = gameNotice ? tracker.OnSale(Item, false, net, Sold, 1)
            : tracker.OnAllaganSale([Item], 1, net, Sold, 1);
        Assert.Equal("1.B", first?.RetainerKey);
        var second = gameNotice ? tracker.OnAllaganSale([Item], 1, net, Sold, 2)
            : tracker.OnSale(Item, false, net, Sold, 2);
        Assert.Equal("2.A", second?.RetainerKey);
    }

    [Theory]
    [InlineData("game", false)]
    [InlineData("allagan", false)]
    [InlineData("history", false)]
    [InlineData("allagan", true)]
    [InlineData("history", true)]
    public void A_recorded_sale_frees_a_full_slot_and_stock_before_the_next_visit(string source, bool gone)
    {
        var seller = FullSeller();
        var alternate = new RetainerSnapshot { Key = "1.3", RetainerName = "RetainerA", LastRefreshedUtc = Seen };
        var tracker = new ListingTracker();
        tracker.ObserveRetainer(seller.Key, seller.RetainerName, [new LiveListing(0, Item, true, 2, UnitPrice)], _ => "Item", Seen);
        if (gone) tracker.ObserveRetainer(seller.Key, seller.RetainerName, [], _ => "Item", Sold.AddSeconds(1), authoritative: true);
        tracker.ListingSold += (listing, time) => Assert.True(seller.TryRemoveSoldListing(listing.Slot, listing.ItemId,
            listing.IsHq, listing.Quantity, listing.LastPrice, time));
        var net = ListingTracker.NetFromGross(UnitPrice * 2);
        var sale = new RetainerHistorySale(Item, (uint)net, (uint)new DateTimeOffset(Sold).ToUnixTimeSeconds(), 2, true, false, "Buyer");
        var before = Plan(null);

        Assert.Equal("RetainerA", before.Target.RetainerName);
        Assert.Equal("RetainerB", before.Action.PreferredRetainerFallback);
        Assert.Equal(Seen, before.Action.PreferredRetainerFullAtUtc);
        Assert.Contains($"RetainerB full (20/20 at {Seen.ToLocalTime():HH:mm}) -> RetainerA",
            Strings.RetainerFallback("Item", "RetainerB", "RetainerA", before.Action.PreferredRetainerFullAtUtc));
        Assert.Equal(Seen, Plan("RetainerB").Action.PreferredRetainerFullAtUtc);
        switch (source)
        {
            case "game": Assert.NotNull(tracker.OnSale(Item, true, net, Sold)); break;
            case "allagan": Assert.NotNull(tracker.OnAllaganSale([Item], 2, net, Sold.AddSeconds(2))); break;
            case "history": Assert.Equal(HistorySaleResult.MatchedListing, tracker.OnHistorySale(seller.Key, seller.RetainerName, sale, _ => "Item")); break;
        }

        Assert.Equal(19, seller.Listings.Count);
        Assert.Equal(Seen, seller.LastRefreshedUtc);
        Assert.Equal(0, tracker.ListedStock(Item, true, new HashSet<string> { seller.Key }).Listed);
        if (source != "history")
            Assert.Equal(HistorySaleResult.MatchedListing, tracker.OnHistorySale(seller.Key, seller.RetainerName, sale, _ => "Item"));
        Assert.Equal(HistorySaleResult.Duplicate, tracker.OnHistorySale(seller.Key, seller.RetainerName, sale, _ => "Item"));
        Assert.Same(seller, Plan(null).Target);
        Assert.Null(Plan("RetainerB").Action.PreferredRetainerFallback);

        (RetainerSnapshot Target, PlannedAction Action) Plan(string? preferred)
        {
            RetainerSnapshot[] retainers = [seller, alternate];
            var free = retainers.ToDictionary(r => r.Key, r => Planner.MaxListingSlots - r.Listings.Count);
            var action = Assert.Single(RestockPlanner.Plan(Item, true, 1, 0, 1, UnitPrice,
                [new RestockSource("char.1.bag", null, 1)], free, [seller.Key], preferred, retainers));
            return (retainers.Single(r => r.Key == action.RetainerKey), action);
        }
    }

    [Fact]
    public void Preserves_other_slots_and_rejects_reused_slots_and_older_sales()
    {
        var seller = FullSeller();
        Assert.False(seller.TryRemoveSoldListing(1, Item, true, 2, UnitPrice, Sold));
        Assert.False(seller.TryRemoveSoldListing(0, Item, false, 2, UnitPrice, Sold));
        Assert.False(seller.TryRemoveSoldListing(0, Item, true, 1, UnitPrice, Sold));
        Assert.False(seller.TryRemoveSoldListing(0, Item, true, 2, UnitPrice + 1, Sold));
        seller.LastRefreshedUtc = Sold.AddSeconds(1);
        Assert.False(seller.TryRemoveSoldListing(0, Item, true, 2, UnitPrice, Sold));
        Assert.Equal(20, seller.Listings.Count);
    }

    private static RetainerSnapshot FullSeller() => new()
    {
        Key = "1.2", RetainerName = "RetainerB", LastRefreshedUtc = Seen,
        Listings = Enumerable.Range(0, Planner.MaxListingSlots)
            .Select(i => new ListingEntry
            {
                ListingIndex = i, ItemId = i == 0 ? Item : Item + 1,
                IsHQ = true, Quantity = 2, UnitPrice = UnitPrice,
            }).ToList(),
    };
}
