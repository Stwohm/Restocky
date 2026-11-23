using Restocker.Market;

namespace Restocker.Tests;

public sealed class SaleReturnTests
{
    [Fact]
    public void EstimatesBothReturnsAfterConfiguredTax()
    {
        Assert.Equal(default, SaleReturn.Estimate(0, 0.05m, 200m, 1));
        Assert.Equal(default, SaleReturn.Estimate(1000, 0.05m, 200m, 0));
        Assert.Equal(new SaleReturn(750m, null), SaleReturn.Estimate(1000, 0.05m, 200m, 1));
        Assert.Equal(new SaleReturn(null, 950m), SaleReturn.Estimate(1000, 0.05m, null, 1));
        Assert.Equal(new SaleReturn(950m, null), SaleReturn.Estimate(1000, 0.05m, 0m, 1));
    }

    [Fact]
    public void StackEstimatesScaleNetProceedsAndUnitCostBeforeRounding()
    {
        Assert.Equal(new SaleReturn(41758.2m, null), SaleReturn.Estimate(444, 0.05m, 0m, 99));
        Assert.Equal(new SaleReturn(null, 41758.2m), SaleReturn.Estimate(444, 0.05m, null, 99));
        Assert.Equal(new SaleReturn(29383.2m, null), SaleReturn.Estimate(444, 0.05m, 125m, 99));
        Assert.Equal(new SaleReturn(-316.8m, null), SaleReturn.Estimate(444, 0.05m, 425m, 99));
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
