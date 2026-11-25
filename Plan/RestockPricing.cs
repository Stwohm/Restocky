using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

internal enum AutoRestockPriceKind { Pending, Listing, LiveMarket, Cache, LastSale, UnknownListingPrice, NoMarketOrSale, OutOfRange, Manual }

internal readonly record struct RestockPrice(long UnitPrice, AutoRestockPriceKind Kind, TimeSpan CacheAge = default);

internal static class RestockPricing
{
    internal static RestockPrice FromListings(IEnumerable<ListingEntry> listings, long floor)
    {
        var ours = listings.Where(l => l.UnitPrice > 0).Select(l => l.UnitPrice).DefaultIfEmpty().Min();
        return ours > 0 ? WithFloor(ours, floor, AutoRestockPriceKind.Listing) : new(0, AutoRestockPriceKind.UnknownListingPrice);
    }

    internal static RestockPrice? FromCache((long Lowest, DateTime UpdatedUtc)? cached, DateTime nowUtc,
        int maxScanAgeHours, int undercutGil, long floor)
    {
        if (cached is not { Lowest: > 0 } market || market.UpdatedUtc > nowUtc) return null;
        var age = nowUtc - market.UpdatedUtc;
        if (age.TotalHours >= maxScanAgeHours) return null;
        return FromMarket(market.Lowest, undercutGil, floor, AutoRestockPriceKind.Cache) with { CacheAge = age };
    }

    // A null live lowest means the search failed. Zero means the game returned no eligible offers.
    internal static RestockPrice Resolve(long? liveLowest, (long Lowest, DateTime UpdatedUtc)? cached,
        DateTime nowUtc, int maxScanAgeHours, int undercutGil, long floor, Func<ListingRecord?> lastSale)
    {
        if (liveLowest is > 0) return FromMarket(liveLowest.Value, undercutGil, floor, AutoRestockPriceKind.LiveMarket);
        if (liveLowest == null && FromCache(cached, nowUtc, maxScanAgeHours, undercutGil, floor) is { } cache)
            return cache;
        var sale = lastSale();
        var price = sale == null ? 0 : SaleUnitPrice(sale);
        return price > 0 ? WithFloor(price, floor, AutoRestockPriceKind.LastSale) : new(0, AutoRestockPriceKind.NoMarketOrSale);
    }

    internal static ListingRecord? LastSale(ListingTracker tracker, uint itemId, bool hq, ulong characterId, uint worldId,
        IReadOnlyDictionary<string, RetainerSnapshot> retainers)
    {
        var prefix = $"{characterId:X}.";
        return tracker.GetSales(itemId, hq).FirstOrDefault(s =>
            s.RetainerKey.StartsWith(prefix, StringComparison.Ordinal)
            || (retainers.TryGetValue(s.RetainerKey, out var seller) && (seller.CharacterContentId == characterId
                || (worldId != 0 && seller.WorldId == worldId))));
    }

    private static RestockPrice FromMarket(long lowest, int undercutGil, long floor, AutoRestockPriceKind kind)
        => WithFloor(Math.Max(1, lowest - Math.Max(0, undercutGil)), floor, kind);

    private static long SaleUnitPrice(ListingRecord sale)
    {
        if (sale.Quantity <= 0) return 0;
        if (sale.SoldGrossGil is > 0) return sale.SoldGrossGil.Value / sale.Quantity;
        if (sale.LastPrice is > 0 and <= uint.MaxValue && (sale.SoldTotalGil <= 0
            || ListingTracker.NetFromGross(sale.LastPrice * sale.Quantity) == sale.SoldTotalGil))
            return sale.LastPrice;
        if (sale.SoldTotalGil <= 0) return 0;

        // History-only records have net proceeds. Recover a consistent whole-gil unit price.
        var unitPrice = decimal.Floor(sale.SoldTotalGil / (ListingTracker.SaleNetRate * sale.Quantity));
        if (unitPrice > uint.MaxValue) return (long)uint.MaxValue + 1;
        var price = (long)unitPrice;
        return ListingTracker.NetFromGross(price * sale.Quantity) == sale.SoldTotalGil ? price : 0;
    }

    internal static bool IsValidPrice(long price, long floor) => price is > 0 and <= uint.MaxValue && price >= floor;

    private static RestockPrice WithFloor(long price, long floor, AutoRestockPriceKind kind)
    {
        var target = Math.Max(price, floor);
        return IsValidPrice(target, floor) ? new(target, kind) : new(0, AutoRestockPriceKind.OutOfRange);
    }
}
