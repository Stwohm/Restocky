using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Restocker.Common;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Plan;

namespace Restocker.Execution;

public sealed unsafe partial class Executor
{
    private readonly record struct TopUpSlot(InventoryType Container, int Index);

    private sealed record TopUpMove(TopUpSlot Source, int SourceAfter, TopUpSlot Destination, int DestinationAfter,
        int RetainerUnits = 0, Dictionary<TopUpSlot, int>? BeforeSplit = null);

    private sealed class TopUpRun(PlannedAction action, Dictionary<TopUpSlot, int> bagBefore, int retainerUnits)
    {
        internal PlannedAction Action { get; } = action;
        internal Dictionary<TopUpSlot, int> BagBefore { get; } = bagBefore;
        internal TopUpSlot? ReturnedStack;
        internal TopUpMove? Move;
        internal int RetainerUnitsRemaining = retainerUnits;
        internal int? RelistSourceAfter;
        internal DateTime Deadline;
    }

    private TopUpRun? pendingTopUp;
    private readonly HashSet<(uint, bool)> unconfirmedTopUpItems = new();
    private static readonly TimeSpan TopUpTimeout = TimeSpan.FromSeconds(8);

    private void StartTopUp(PlannedAction action)
    {
        var im = InventoryManager.Instance();
        var topUp = action.TopUp!;
        var market = GetTopUpMarketSlot(im, action);
        if (Mode != ExecutionMode.ApplyActions || unconfirmedTopUpItems.Contains((action.ItemId, action.IsHQ))
            || !ValidWritePrice(action)
            || !MatchesTopUpItem(market, action) || market->Quantity != topUp.OriginalQuantity
            || im->GetRetainerMarketPrice((short)action.ListingIndex) != (ulong)action.UnitPrice)
        {
            FailTopUp("planned listing is no longer available");
            return;
        }

        var bag = ReadTopUpStacks(im, CharBagContainers, action);
        var bagsLoaded = CharBagContainers.All(t => IsTopUpContainerLoaded(im, t));
        if (!bagsLoaded || bag.Values.Sum() < topUp.BagUnits + topUp.SaddleUnits
            || (topUp.RetainerUnits > 0 && ReadTopUpStacks(im, RetainerInventoryContainersStatic, action).Values.Sum() < topUp.RetainerUnits)
            || FindTopUpBagRoom(im, bag, topUp.InventoryStackSize, topUp.OriginalQuantity) == null)
        {
            FailTopUp("planned inventory or bag space is no longer available");
            return;
        }

        pendingTopUp = new TopUpRun(action, bag, topUp.RetainerUnits);
        listedRetainerKey = action.RetainerKey;
        // Keep an unresolved withdrawal/relist slot out of the normal listing allocator.
        usedMarketSlots.Add(action.ListingIndex);
        State = ExecutionState.AwaitingTopUp;
        WaitForTopUpWrite(pendingTopUp);
        var result = im->MoveFromRetainerMarketToPlayerInventory(InventoryType.RetainerMarket,
            (ushort)action.ListingIndex, (uint)topUp.OriginalQuantity);
        log.Information($"[Restocker] top-up withdraw market#{action.ListingIndex} item={action.ItemId} qty={topUp.OriginalQuantity} result={result}");
    }

    private void TickTopUp()
    {
        var run = pendingTopUp!;
        var action = run.Action;
        var topUp = action.TopUp!;
        var im = InventoryManager.Instance();
        var market = GetTopUpMarketSlot(im, action);
        if (market == null || !CharBagContainers.All(t => IsTopUpContainerLoaded(im, t)))
        {
            AwaitTopUpReadback(run, "active sell list or bags unavailable");
            return;
        }

        var bag = ReadTopUpStacks(im, CharBagContainers, action);
        if (run.RelistSourceAfter is { } relistSourceAfter)
        {
            if (MatchesTopUpItem(market, action) && market->Quantity == action.Quantity
                && im->GetRetainerMarketPrice((short)action.ListingIndex) == (ulong)action.UnitPrice
                && TopUpQuantity(im, run.ReturnedStack!.Value, action) == relistSourceAfter)
                FinishTopUp();
            else
                AwaitTopUpReadback(run, "relist was not confirmed");
            return;
        }

        if (run.ReturnedStack == null)
        {
            if (market->ItemId != 0 || bag.Values.Sum() != run.BagBefore.Values.Sum() + topUp.OriginalQuantity)
            {
                AwaitTopUpReadback(run, "withdrawal was not confirmed");
                return;
            }

            run.ReturnedStack = bag.Where(p => p.Value > run.BagBefore.GetValueOrDefault(p.Key))
                .OrderByDescending(p => p.Value).First().Key;
        }
        if (market->ItemId != 0)
        {
            FailTopUp("market slot changed after withdrawal");
            return;
        }

        if (run.Move is { } move)
        {
            if (!TopUpMoveConfirmed(im, action, move))
            {
                AwaitTopUpReadback(run, "inventory move was not confirmed");
                return;
            }
            run.RetainerUnitsRemaining -= move.RetainerUnits;
            run.Move = null;
        }

        var destination = run.ReturnedStack.Value;
        if (!bag.TryGetValue(destination, out var stackQuantity))
        {
            FailTopUp("withdrawn stack changed");
            return;
        }

        if (run.RetainerUnitsRemaining > 0)
        {
            TransferTopUpRetainerStock(im, run, bag);
            return;
        }
        if (stackQuantity < action.Quantity)
        {
            var room = topUp.InventoryStackSize - stackQuantity;
            var source = bag.Where(p => p.Key != destination)
                .OrderBy(p => p.Value > room).ThenByDescending(p => p.Value).FirstOrDefault();
            if (source.Value == 0)
            {
                FailTopUp("insufficient stack quantity after withdrawal");
                return;
            }
            var quantity = Math.Min(source.Value, room);
            if (source.Value > quantity)
                SplitTopUpSource(im, run, bag, source.Key, quantity);
            else
                MoveTopUpStack(im, run, source.Key, destination, quantity, stackQuantity);
            return;
        }

        if (!ValidWritePrice(action))
        {
            FailTopUp("original price no longer meets the configured floor");
            return;
        }
        run.RelistSourceAfter = stackQuantity - action.Quantity;
        WaitForTopUpWrite(run);
        im->MoveToRetainerMarket(destination.Container, (ushort)destination.Index, InventoryType.RetainerMarket,
            (ushort)action.ListingIndex, (uint)action.Quantity, (uint)action.UnitPrice);
    }

    private void TransferTopUpRetainerStock(InventoryManager* im, TopUpRun run, Dictionary<TopUpSlot, int> bag)
    {
        var inventory = ReadTopUpStacks(im, RetainerInventoryContainersStatic, run.Action);
        var need = run.RetainerUnitsRemaining;
        var source = inventory.OrderBy(p => p.Value > need).ThenByDescending(p => p.Value).FirstOrDefault();
        var quantity = Math.Min(source.Value, need);
        var destination = FindTopUpBagRoom(im, bag, run.Action.TopUp!.InventoryStackSize, quantity);
        if (quantity <= 0 || destination == null)
        {
            FailTopUp("retainer stock or transfer space is no longer available");
            return;
        }
        if (source.Value > quantity)
            SplitTopUpSource(im, run, inventory, source.Key, quantity);
        else
            MoveTopUpStack(im, run, source.Key, destination.Value, quantity, bag.GetValueOrDefault(destination.Value), quantity);
    }

    private void SplitTopUpSource(InventoryManager* im, TopUpRun run, Dictionary<TopUpSlot, int> before, TopUpSlot source, int quantity)
    {
        run.Move = new TopUpMove(source, before[source] - quantity, default, quantity, BeforeSplit: before);
        WaitForTopUpWrite(run);
        im->SplitItem(source.Container, (ushort)source.Index, quantity);
    }

    private void MoveTopUpStack(InventoryManager* im, TopUpRun run, TopUpSlot source, TopUpSlot destination,
        int quantity, int destinationBefore, int retainerUnits = 0)
    {
        run.Move = new TopUpMove(source, 0, destination, destinationBefore + quantity, retainerUnits);
        WaitForTopUpWrite(run);
        im->MoveItemSlot(source.Container, (ushort)source.Index, destination.Container, (ushort)destination.Index, true);
        if (retainerUnits > 0) Wait(SaddleSettleAfterStage);
    }

    private static bool TopUpMoveConfirmed(InventoryManager* im, PlannedAction action, TopUpMove move)
    {
        if (TopUpQuantity(im, move.Source, action) != move.SourceAfter) return false;
        if (move.BeforeSplit == null)
            return TopUpQuantity(im, move.Destination, action) == move.DestinationAfter;

        var containers = CharBagContainers.Contains(move.Source.Container) ? CharBagContainers : RetainerInventoryContainersStatic;
        var current = ReadTopUpStacks(im, containers, action);
        return current.Values.Sum() == move.BeforeSplit.Values.Sum()
            && current.Any(p => p.Value == move.DestinationAfter && !move.BeforeSplit.ContainsKey(p.Key));
    }

    private InventoryItem* GetTopUpMarketSlot(InventoryManager* im, PlannedAction action)
    {
        if (im == null || ActiveRetainerKey() != action.RetainerKey || !AddonHelper.IsOpen("RetainerSellList")) return null;
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        return market != null && market->IsLoaded && action.ListingIndex >= 0 && action.ListingIndex < market->Size
            ? market->GetInventorySlot(action.ListingIndex) : null;
    }

    private static bool IsTopUpContainerLoaded(InventoryManager* im, InventoryType type)
    {
        var container = im->GetInventoryContainer(type);
        return container != null && container->IsLoaded;
    }

    private static bool MatchesTopUpItem(InventoryItem* item, PlannedAction action)
        => item != null && item->ItemId == action.ItemId
            && item->Flags == (action.IsHQ ? InventoryItem.ItemFlags.HighQuality : InventoryItem.ItemFlags.None);

    private static Dictionary<TopUpSlot, int> ReadTopUpStacks(InventoryManager* im, InventoryType[] types, PlannedAction action)
    {
        var result = new Dictionary<TopUpSlot, int>();
        if (im == null) return result;
        foreach (var type in types)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (MatchesTopUpItem(item, action) && item->Quantity > 0)
                    result.Add(new TopUpSlot(type, i), item->Quantity);
            }
        }
        return result;
    }

    private static int TopUpQuantity(InventoryManager* im, TopUpSlot slot, PlannedAction action)
    {
        var container = im->GetInventoryContainer(slot.Container);
        if (container == null || !container->IsLoaded) return -1;
        var item = container->GetInventorySlot(slot.Index);
        if (MatchesTopUpItem(item, action)) return item->Quantity;
        return item != null && item->ItemId == 0 ? 0 : -1;
    }

    private static TopUpSlot? FindTopUpBagRoom(InventoryManager* im, Dictionary<TopUpSlot, int> bag, int capacity, int quantity)
    {
        foreach (var (slot, count) in bag)
            if (capacity - count >= quantity) return slot;

        foreach (var type in CharBagContainers)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId == 0) return new TopUpSlot(type, i);
            }
        }
        return null;
    }

    private void WaitForTopUpWrite(TopUpRun run)
    {
        run.Deadline = DateTime.UtcNow + TopUpTimeout;
        Throttle();
    }

    private void AwaitTopUpReadback(TopUpRun run, string reason)
    {
        if (DateTime.UtcNow >= run.Deadline) FailTopUp(reason);
        else Throttle();
    }

    private void FailTopUp(string reason)
    {
        var action = currentJobActions.Peek();
        var name = AutoRestockItemName(action.ItemId, action.IsHQ);
        var retainer = CurrentJob.RetainerName;
        log.Warning($"[Restocker] top-up skipped retainer={retainer} market#{action.ListingIndex} item={action.ItemId} hq={action.IsHQ}: {reason}");
        if (pendingTopUp is { } run)
        {
            var im = InventoryManager.Instance();
            var bagCount = ReadTopUpStacks(im, CharBagContainers, action).Values.Sum();
            var market = GetTopUpMarketSlot(im, action);
            if (run.ReturnedStack == null && MatchesTopUpItem(market, action)
                && market->Quantity == action.TopUp!.OriginalQuantity
                && im->GetRetainerMarketPrice((short)action.ListingIndex) == (ulong)action.UnitPrice
                && bagCount == run.BagBefore.Values.Sum())
            {
                Plugin.ChatPrint(string.Format(Strings.TopUpStillListed, name, retainer, market->Quantity));
                FinishTopUp();
                return;
            }

            // Keep a withdrawn or unresolved stack available for recovery.
            unconfirmedTopUpItems.Add((action.ItemId, action.IsHQ));
            if (market != null && market->ItemId == 0 && bagCount >= run.BagBefore.Values.Sum() + action.TopUp!.OriginalQuantity)
                Plugin.ChatPrint(string.Format(Strings.TopUpInBag, name, retainer, bagCount, action.TopUp.OriginalQuantity));
            else
            {
                var retainerCount = market != null
                    ? ReadTopUpStacks(im, RetainerInventoryContainersStatic, action).Values.Sum().ToString("N0")
                    : "?";
                Plugin.ChatPrint(string.Format(Strings.TopUpUnconfirmed, name, retainer, bagCount, retainerCount, action.ListingIndex + 1));
            }
        }
        else
            Plugin.ChatPrint(string.Format(Strings.TopUpSkipped, name, retainer));
        FinishTopUp();
    }

    private void FinishTopUp()
    {
        pendingTopUp = null;
        currentJobActions.Dequeue();
        CompletedActions++;
        runEta!.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
        State = ExecutionState.PerformingAction;
        Throttle();
    }
}
