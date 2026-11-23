namespace Restocker.Execution;

internal static class TopUpReadback
{
    internal static bool Confirmed(bool matchingItem, int listedQuantity, int requestedQuantity,
        long livePrice, long requestedPrice, int bagDecrement)
        => matchingItem && listedQuantity == requestedQuantity && bagDecrement == requestedQuantity
            && livePrice > 0 && livePrice == requestedPrice;
}
