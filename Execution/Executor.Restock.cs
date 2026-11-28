using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Restocker.Common;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Execution;

public sealed unsafe partial class Executor
{
    private bool restockComparing;
    private readonly Dictionary<(uint ItemId, bool IsHq), RestockPrice> restockPrices = new();
    // Baits the live price ignored, reported once the listing is confirmed.
    private readonly Dictionary<(uint ItemId, bool IsHq), BaitNotice> restockBaits = new();

    private readonly HashSet<(uint ItemId, bool IsHq)> reportedRestockBaits = new();

    private bool TryPriceRestock(PlannedAction action)
    {
        var key = (action.ItemId, action.IsHQ);
        if (!restockPrices.TryGetValue(key, out var price))
        {
            var floor = AutoRestockFloor(action.ItemId, action.IsHQ);
            if (autoRestockPass || RestockPricing.FromCache(CachedRestockLowest(action), DateTime.UtcNow,
                configuration.AutoRestockMaxScanAgeHours, configuration.UndercutGil, floor) is not { } cached)
            {
                OpenRestockContext(action);
                return false;
            }
            restockPrices[key] = price = cached;
        }

        action.UnitPrice = price.UnitPrice;
        if (action.UnitPrice > 0) return true;
        CompleteListing(RestockSkipReason.PriceUnavailable);
        return false;
    }

    private int LiveCharacterAllowance(InventoryManager* im, PlannedAction action, int? beforeTransfer = null)
    {
        var keep = configuration.KeepFor(action.ItemId, action.IsHQ);
        if (keep == 0) return int.MaxValue;
        var containers = InventoryEntry.IsCrystal(action.ItemId) ? RetainerWatcher.PlayerCrystals
            : CombineUnique(CharBagContainers, SaddleContainers);
        // A closed/unloaded saddlebag cannot prove that the reserve is present.
        var held = ReadTopUpStacks(im, containers, action).Values.Sum();
        return InventoryReserve.AfterTransfer(held, keep, beforeTransfer ?? action.CharacterBeforeCrystalWithdrawal);
    }

    private InventoryType[] ListingSourceContainers(PlannedAction action)
    {
        var key = string.IsNullOrEmpty(action.SourceKey) ? action.RetainerKey : action.SourceKey;
        if (InventoryEntry.IsCrystal(action.ItemId))
            return action.ListsCharacterCrystals || key.StartsWith("char.") ? RetainerWatcher.PlayerCrystals : RetainerWatcher.RetainerCrystals;
        var fixedSource = autoRestockPass || configuration.KeepFor(action.ItemId, action.IsHQ) > 0
            || (action.StockRestock && jobs.Exists(j => j.HasTopUpFor(action)));
        return fixedSource ? SourceContainersFor(key) : ResolveSearchOrder(key);
    }

    private static bool IsListingSource(InventoryItem* item, PlannedAction action)
        => item != null && item->ItemId == action.ItemId && item->Quantity >= action.Quantity
            && ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) == action.IsHQ;

    private void OpenRestockContext(PlannedAction action)
    {
        var im = InventoryManager.Instance();
        var agent = AgentInventoryContext.Instance();
        if (im == null || agent == null)
        {
            log.Warning("[Restocker] inventory context unavailable for restock price comparison");
            restockPrices[(action.ItemId, action.IsHQ)] = ResolveRestockPrice(action, null);
            Throttle();
            return;
        }

        foreach (var type in ListingSourceContainers(action))
        {
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                if (!IsListingSource(container->GetInventorySlot(i), action)) continue;
                // The sale entry is available with owner addon ID 0, as in List & price.
                Plugin.Instance?.SetMarketWatcherExpected(0, false);
                restockComparing = true;
                BeginSearchRow();
                agent->OpenForItemSlot(type, i, 0, 0);
                log.Information($"[Restocker] restock comparison: context menu for item={action.ItemId} at {type}#{i}");
                State = ExecutionState.HandoverAwaitContext;
                waitingSince = DateTime.UtcNow;
                Throttle();
                return;
            }
        }
        CompleteListing(RestockSkipReason.SourceUnavailable);
    }

    private void TickHandoverAwaitContext()
    {
        if (AddonHelper.IsOpen("ContextMenu") && ContextMenuHelper.ClickEntry(AddonText.IsPutUpForSaleEntry))
        {
            State = ExecutionState.HandoverAwaitDialog;
            waitingSince = DateTime.UtcNow;
            Throttle();
            return;
        }
        waitingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(3))
            FinishRestockComparison(null, Strings.RunRestockSaleEntryMissing);
    }

    private void TickHandoverAwaitDialog()
    {
        var action = currentJobActions.Peek();
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon != null)
        {
            var item = ResolveItemFromDialogName(ReadNodeText(((AddonRetainerSell*)addon)->ItemName));
            if (item == (action.ItemId, action.IsHQ) && ActiveRetainerKey() == action.RetainerKey)
            {
                StartMarketComparison(addon, action.ItemId, action.IsHQ);
                return;
            }
            if (item.itemId != 0)
            {
                FinishRestockComparison(null, Strings.RunRestockSaleWindowMismatch);
                return;
            }
        }
        waitingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(6))
            FinishRestockComparison(null, Strings.RunRestockSaleWindowMissing);
    }

    private void FinishRestockComparison(MarketScan? scan, string? failure = null)
    {
        var action = currentJobActions.Peek();
        if (scan == null)
        {
            failure ??= Strings.RunMarketSearchTimeout;
            log.Warning($"[Restocker] restock comparison failed for item={action.ItemId} hq={action.IsHQ}: {failure}");
            RunHistory?.Add(new RunEntry(CurrentJob.RetainerName, RunEntryKind.NoData,
                string.Format(Strings.RunRestockComparisonFailed, AutoRestockItemName(action.ItemId, action.IsHQ), failure)));
        }
        var price = ResolveRestockPrice(action, scan);
        restockPrices[(action.ItemId, action.IsHQ)] = price;
        if (scan != null && price.Kind == AutoRestockPriceKind.LiveMarket && !configuration.IsHeld(action.ItemId, action.IsHQ))
        {
            var matchQuality = configuration.MatchQuality.For(action.ItemId, action.IsHQ);
            var lowest = scan.Lowest(action.IsHQ, matchQuality);
            var dropped = BaitFinder.Dropped(scan.Competitors(action.IsHQ, matchQuality), lowest);
            if (dropped.Count > 0)
                restockBaits[(action.ItemId, action.IsHQ)] = new BaitNotice(action.ItemId, action.IsHQ, CurrentJob.RetainerName, dropped, lowest);
        }

        // Close the pricing windows and let the usual direct listing path verify the server slot.
        Plugin.Instance?.SetMarketWatcherExpected(0, false);
        CloseRestockComparison();
        State = ExecutionState.FetchAwaitingSellListAfter;
        waitingSince = null;
        Throttle();
    }

    private static void CloseRestockComparison()
    {
        if (AddonHelper.IsOpen("ContextMenu")) ContextMenuHelper.Close();
        var search = AddonHelper.GetVisible("ItemSearchResult");
        if (search != null) Callback.Fire(search, true, -1);
        var sell = AddonHelper.GetVisible("RetainerSell");
        if (sell != null) Callback.Fire(sell, true, -1);
    }

    private (long Lowest, DateTime UpdatedUtc)? CachedRestockLowest(PlannedAction action)
        => marketCache?.CachedLowest(Plugin.Instance.MarketWatcher.HomeWorldId, action.ItemId, action.IsHQ,
            configuration.MatchQuality.For(action.ItemId, action.IsHQ));

    private RestockPrice ResolveRestockPrice(PlannedAction action, MarketScan? live)
    {
        var world = Plugin.Instance.MarketWatcher.HomeWorldId;
        var lowest = live != null && live.WorldId == world
            ? live.Lowest(action.IsHQ, configuration.MatchQuality.For(action.ItemId, action.IsHQ)) : (long?)null;
        return RestockPricing.Resolve(lowest, CachedRestockLowest(action), DateTime.UtcNow,
            configuration.AutoRestockMaxScanAgeHours, configuration.UndercutGil, AutoRestockFloor(action.ItemId, action.IsHQ),
            () => RestockPricing.LastSale(Plugin.Instance.Tracker.Tracker, action.ItemId, action.IsHQ,
                Plugin.PlayerState.ContentId, world, configuration.Snapshots));
    }

    private void CompleteListing(RestockSkipReason reason = RestockSkipReason.None)
    {
        var slot = pendingListingSlot;
        pendingListingSlot = -1;
        var action = currentJobActions.Dequeue();
        var key = (action.ItemId, action.IsHQ);
        if (reason != RestockSkipReason.None)
        {
            log.Warning($"[Restocker] listing skipped for {action.RetainerKey} item={action.ItemId} hq={action.IsHQ} qty={action.Quantity}: {reason}");
            PrintRestockFailure(action, reason);
            RunHistory?.Add(new RunEntry(CurrentJob.RetainerName, RunEntryKind.RestockSkipped,
                string.Format(Strings.RestockListingFailed, CurrentJob.RetainerName,
                        AutoRestockItemName(action.ItemId, action.IsHQ), action.Quantity, RestockFailureDetails(action, reason))));
        }
        else
        {
            if (restockBaits.TryGetValue(key, out var bait))
            {
                // The new listing is priced above the bait: offer the match here, as the reprice plan does.
                LastBaitPricesFor(action.RetainerKey)[(slot, action.ItemId, action.IsHQ)] = bait.Baits[0].Price;
                reportedBaits.Add(BaitFinder.Key(bait));
                if (reportedRestockBaits.Add(key)) ReportBait(bait);
            }
            var price = action.ResolveRestockPrice ? restockPrices[key]
                : action.RestockPrice ?? new RestockPrice(action.UnitPrice, AutoRestockPriceKind.Listing);
            RunHistory?.Add(new RunEntry(CurrentJob.RetainerName, RunEntryKind.Restocked,
                RestockedMessage(action.ItemId, action.IsHQ, action.Quantity, price)));
            ChatEvents.Add(new(ChatCategory.Restocked,
                $"×{action.Quantity:N0} @ {price.UnitPrice:N0} · {Strings.AutoRestockPriceLabel(price.Kind, price.CacheAge)}",
                action.ItemId, action.IsHQ, CurrentJob.RetainerName));
        }

        CompletedActions++;
        runEta!.FinishListing(CurrentJob, runClock.Elapsed, StepThrottle);
        waitingSince = null;
        if (IsRunning)
        {
            State = ExecutionState.PerformingAction;
            Throttle();
        }
    }

    private void PrintRestockFailure(PlannedAction action, RestockSkipReason reason)
    {
        var retainer = configuration.Snapshots.GetValueOrDefault(action.RetainerKey)?.RetainerName ?? action.RetainerKey;
        ChatEvents.Add(new(ChatCategory.Problems, $"×{action.Quantity} · {RestockFailureDetails(action, reason)}",
            action.ItemId, action.IsHQ, retainer));
    }

    private string RestockFailureDetails(PlannedAction action, RestockSkipReason reason)
    {
        if (reason == RestockSkipReason.CrystalWithdrawalFailed && withdrawalFailures.TryGetValue(action, out var failure))
            return failure;
        var detail = reason == RestockSkipReason.PriceUnavailable
            ? Strings.AutoRestockPriceLabel(restockPrices[(action.ItemId, action.IsHQ)].Kind)
            : Strings.RestockSkipLabel(reason);
        if (!confirmedWithdrawals.Contains(action)) return detail;
        return $"{detail} · {CrystalSourceName(action.SourceKey)} → {Strings.StockBag} ×{action.Quantity:N0}";
    }

    private static string RestockedMessage(uint itemId, bool isHq, int quantity, RestockPrice price)
        => string.Format(Strings.AutoRestocked, AutoRestockItemName(itemId, isHq), quantity,
            price.UnitPrice, Strings.AutoRestockPriceLabel(price.Kind, price.CacheAge));
}
