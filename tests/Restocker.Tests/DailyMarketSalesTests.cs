using Restocker.Market;

namespace Restocker.Tests;

public sealed class DailyMarketSalesTests
{
    private static readonly DateTime Noon = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MarketSale Sale = new(8, true, 2, 100, Noon);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void IncludesFirstLocalDayThroughToday(int days)
    {
        var first = Noon.Date.AddDays(1 - days);
        var rows = new[] { Sale, Sale with { SoldUtc = first.AddTicks(-1) }, Sale with { SoldUtc = first },
            Sale with { SoldUtc = Noon.Date.AddDays(1) } };
        var daily = DailyMarketSales.Calculate(rows, [Own(first)], Noon, TimeZoneInfo.Utc, days);
        Assert.Equal(2, daily.Total);
        Assert.Equal(1, daily.Matched);
        Assert.Equal(1, daily.RecordedOurs);
        Assert.Equal(Sale, Assert.Single(daily.Lost));
    }

    [Fact]
    public void Identical_sale_to_a_different_known_buyer_is_lost()
    {
        var own = Own(Noon);
        own.BuyerName = "Buyer A";
        var other = Sale with { BuyerName = "Buyer B" };
        var daily = DailyMarketSales.Calculate([other], [own], Noon, TimeZoneInfo.Utc);
        Assert.Equal(0, daily.Matched);
        Assert.Equal(other, Assert.Single(daily.Lost));
    }

    [Fact]
    public void RequiresSameItemQualityQuantityAndPriceWithinFiveMinutes()
    {
        var lost = new[]
        {
            Sale with { ItemId = 9 }, Sale with { IsHq = false }, Sale with { Quantity = 1 },
            Sale with { UnitPrice = 101 }, Sale with { SoldUtc = Noon.AddMinutes(5).AddSeconds(1) },
        };
        var daily = DailyMarketSales.Calculate([Sale, .. lost], [Own(Noon)], Noon, TimeZoneInfo.Utc);
        Assert.Equal(6, daily.Total);
        Assert.Equal(1, daily.Matched);
        Assert.Equal(1, daily.RecordedOurs);
        Assert.Equal(lost.Length, daily.Lost.Count);
        Assert.All(lost, sale => Assert.Contains(sale, daily.Lost));
    }

    [Fact]
    public void OneOwnSaleMatchesTheClosestTransactionOnly()
    {
        var later = Sale with { SoldUtc = Noon.AddMinutes(2) };
        var daily = DailyMarketSales.Calculate([later, Sale], [Own(Noon)], Noon, TimeZoneInfo.Utc);
        Assert.Equal(1, daily.Matched);
        Assert.Equal(later, Assert.Single(daily.Lost));
    }

    [Fact]
    public void ReassignsNearbySalesWhenThatAllowsBothToMatch()
    {
        var later = Sale with { SoldUtc = Noon.AddMinutes(4) };
        var daily = DailyMarketSales.Calculate([Sale, later],
            [Own(Noon.AddMinutes(1)), Own(Noon.AddMinutes(-4))], Noon, TimeZoneInfo.Utc);
        Assert.Equal(2, daily.Matched);
        Assert.Empty(daily.Lost);
    }

    [Fact]
    public void UsesLocalMidnightAndCanMatchALateNotice()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var midnightUtc = Noon.Date.AddHours(21);
        var lastOfToday = Sale with { SoldUtc = midnightUtc.AddMinutes(-1) };
        var firstOfTomorrow = Sale with { SoldUtc = midnightUtc, Quantity = 1 };
        var notice = Own(midnightUtc.AddMinutes(2));
        var daily = DailyMarketSales.Calculate([lastOfToday, firstOfTomorrow], [notice], Noon, zone);
        Assert.Equal(1, daily.Total);
        Assert.Equal(0, daily.RecordedOurs);
        Assert.Equal(1, daily.Matched);
    }

    [Fact]
    public void PriorDaySaleCanClaimItsOwnRecordBeforeTodaysIdenticalSale()
    {
        var midnight = Noon.Date;
        var previous = Sale with { SoldUtc = midnight.AddMinutes(-1) };
        var today = Sale with { SoldUtc = midnight.AddMinutes(1) };
        var daily = DailyMarketSales.Calculate([previous, today], [Own(previous.SoldUtc)], midnight, TimeZoneInfo.Utc);
        Assert.Equal(0, daily.Matched);
        Assert.Equal(today, Assert.Single(daily.Lost));
    }

    [Fact]
    public void MatchesHistoryOnlyRecordsByNetProceeds()
    {
        var own = Own(Noon);
        own.SoldGrossGil = null;
        own.SoldTotalGil = 190;
        var daily = DailyMarketSales.Calculate([Sale], [own], Noon, TimeZoneInfo.Utc);
        Assert.Equal(1, daily.Matched);
    }

    private static ListingRecord Own(DateTime time) => new()
    {
        Outcome = ListingTracker.Sold, ItemId = Sale.ItemId, IsHq = Sale.IsHq, Quantity = Sale.Quantity,
        SoldRecordedUtc = time, SoldGrossGil = 200,
    };
}
