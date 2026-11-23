using System;

namespace Restocker.Market;

internal readonly record struct SaleReturn(decimal? Profit, decimal? Earning)
{
    internal static SaleReturn Estimate(long unitPrice, decimal taxRate, decimal? unitCost)
        => Calculate(unitPrice * (1m - taxRate), unitCost, 1, unitCost.HasValue ? 0 : 1);

    internal static SaleReturn Realized(ListingRecord sale)
        => sale.SoldTotalGil > 0 || sale.HistoryKey != null
            ? Calculate(sale.SoldTotalGil, sale.ConsumedCost, sale.Quantity, sale.UnknownCostUnits ?? 0)
            : default;

    private static SaleReturn Calculate(decimal proceeds, decimal? cost, int quantity, int unknownUnits)
    {
        if (cost == null || (quantity > 0 && unknownUnits >= quantity))
            return new SaleReturn(null, proceeds);
        if (unknownUnits <= 0 || quantity <= 0)
            return new SaleReturn(proceeds - cost, null);

        var earning = proceeds * unknownUnits / quantity;
        return new SaleReturn(proceeds - earning - cost, earning);
    }
}
