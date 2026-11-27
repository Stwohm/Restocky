using Restocker.Market;

namespace Restocker.Tests;

public sealed class SaleReturnTests
{
    [Fact]
    public void PreviewChargesOnlyTwentyPurchasedUnitsInMixedStack()
    {
        var ledger = new CostLedger();
        var now = DateTime.UtcNow;
        ledger.RecordLot(1, false, 20, 5m, now);
        var preview = ledger.CopyForEstimate();
        Assert.Equal(new SaleReturn(100m, 5000m),
            SaleReturn.Estimate(10, 0m, preview.Consume(1, false, 520, now), 520));
        Assert.Equal(new SaleReturn(null, 100m), SaleReturn.Estimate(10, 0m, preview.Consume(1, false, 10, now), 10));
        Assert.Equal(20, ledger.RemainingUnits(1, false));
        var (cost, uncovered) = ledger.Consume(1, false, 520, now);
        Assert.Equal(new SaleReturn(100m, 5000m), SaleReturn.Realized(new ListingRecord
            { Quantity = 520, SoldTotalGil = 5200, ConsumedCost = cost, UnknownCostUnits = uncovered }));
    }

    [Fact]
    public void SellingAllThreeHundredRemovesCostAndNextSaleIsEarning()
    {
        var now = DateTime.UtcNow;
        var ledger = new CostLedger();
        ledger.RecordLot(1, false, 300, 5m, now);
        SaleReturn Sell(int quantity)
        {
            var (cost, uncovered) = ledger.Consume(1, false, quantity, now);
            return SaleReturn.Realized(new ListingRecord { Quantity = quantity, SoldTotalGil = quantity * 9,
                ConsumedCost = cost, UnknownCostUnits = uncovered });
        }
        Assert.Equal(new SaleReturn(1200m, null), Sell(300));
        Assert.Null(ledger.CurrentUnitCost(1, false));
        Assert.Equal(new SaleReturn(null, 9m), Sell(1));
    }

    [Fact]
    public void EstimatesBothReturnsAfterConfiguredTax()
    {
        Assert.Equal(default, SaleReturn.Estimate(0, 0.05m, (200m, 0), 1));
        Assert.Equal(default, SaleReturn.Estimate(1000, 0.05m, (0m, 0), 0));
        Assert.Equal(new SaleReturn(750m, null), SaleReturn.Estimate(1000, 0.05m, (200m, 0), 1));
        Assert.Equal(new SaleReturn(null, 950m), SaleReturn.Estimate(1000, 0.05m, (0m, 1), 1));
        Assert.Equal(new SaleReturn(950m, null), SaleReturn.Estimate(1000, 0.05m, (0m, 0), 1));
    }

    [Fact]
    public void StackEstimatesScaleNetProceedsAndUnitCostBeforeRounding()
    {
        Assert.Equal(new SaleReturn(41758.2m, null), SaleReturn.Estimate(444, 0.05m, (0m, 0), 99));
        Assert.Equal(new SaleReturn(null, 41758.2m), SaleReturn.Estimate(444, 0.05m, (0m, 99), 99));
        Assert.Equal(new SaleReturn(29383.2m, null), SaleReturn.Estimate(444, 0.05m, (125m * 99, 0), 99));
        Assert.Equal(new SaleReturn(-316.8m, null), SaleReturn.Estimate(444, 0.05m, (425m * 99, 0), 99));
    }

    [Fact]
    public void SplitsActualNetProceedsForMixedCostSale()
    {
        var sale = new ListingRecord { SoldTotalGil = 950, Quantity = 10, ConsumedCost = 100m, UnknownCostUnits = 4 };
        Assert.Equal(new SaleReturn(470m, 380m), SaleReturn.Realized(sale));
    }

    [Fact]
    public void HandlesLegacyCostAndMissingProceeds()
    {
        Assert.Equal(new SaleReturn(null, 95m), SaleReturn.Realized(new ListingRecord { SoldTotalGil = 95 }));
        Assert.Equal(new SaleReturn(null, 95m), SaleReturn.Realized(new ListingRecord
            { SoldTotalGil = 95, Quantity = 2, ConsumedCost = 0m, UnknownCostUnits = 2 }));
        Assert.Equal(default, SaleReturn.Realized(new ListingRecord { ConsumedCost = 0m }));
        Assert.Equal(new SaleReturn(null, 0m), SaleReturn.Realized(new ListingRecord { HistoryKey = "known-zero" }));
    }
}
