using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Restocker.Common;
using Restocker.Data;
using Restocker.Plan;

namespace Restocker.Execution;

public sealed unsafe partial class Executor
{
    private enum WithdrawalStep { Inventory, Retrieve, Quantity, Readback }
    private WithdrawalStep withdrawalStep;
    private int beforeSource, beforeCharacter, sourceSlot;
    private DateTime withdrawalDeadline;
    private readonly HashSet<PlannedAction> confirmedWithdrawals = [];
    private readonly Dictionary<PlannedAction, string> withdrawalFailures = [];

    private string CrystalSourceName(string key) => configuration.Snapshots.GetValueOrDefault(key)?.RetainerName ?? key;

    private bool IsJobSellEntry(string text)
    {
        // Only a job that lists the retainer's own crystals needs "Sell items in your retainer's
        // inventory" (2381). Everything else opens "Sell items in your inventory" (2380), as before
        // 0.3.16.0: with 2381 open, character-bag listings were sometimes not accepted (TimedOut).
        var playerEntry = AddonText.TryRow(2380);
        var retainerCrystals = CurrentJob.Actions.Any(a => a.Kind == PlannedActionKind.NewListing
            && InventoryEntry.IsCrystal(a.ItemId) && !a.ListsCharacterCrystals);
        if (!retainerCrystals && !string.IsNullOrEmpty(playerEntry)) return text == playerEntry;
        if (text == playerEntry) return false;
        return text == AddonText.TryRow(2381) || AddonText.IsHaveRetainerSellItemsEntry(text);
    }

    private void StartCrystalWithdrawal()
    {
        var action = CurrentJob.Withdrawal!;
        if (unconfirmedTopUpItems.Contains((action.ItemId, action.IsHQ)))
        {
            FinishCrystalWithdrawal("previous inventory movement unconfirmed");
            return;
        }
        if (ActiveRetainerKey() != action.SourceKey || (autoRestockPass && !CanAutoRestock(action)))
        {
            FinishCrystalWithdrawal("source retainer or restock settings changed");
            return;
        }
        // Addon 2378 includes the dynamic occupied-slot count.
        var label = AddonText.TryRow(2378).Split('(', '（')[0].Trim();
        if (label.Length == 0 || !SelectStringHelper.ClickEntry(s => s.StartsWith(label, StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow - waitingSince > SelectStringTimeout)
                FinishCrystalWithdrawal("entrust or withdraw menu unavailable");
            return;
        }
        State = ExecutionState.WithdrawingCrystals;
        AdvanceWithdrawal(WithdrawalStep.Inventory);
    }

    private void AdvanceWithdrawal(WithdrawalStep next)
    {
        withdrawalStep = next;
        withdrawalDeadline = DateTime.UtcNow.AddSeconds(8);
        Throttle();
    }

    private void TickCrystalWithdrawal()
    {
        var action = CurrentJob.Withdrawal!;
        if (ActiveRetainerKey() != action.SourceKey || DateTime.UtcNow >= withdrawalDeadline)
        {
            FinishCrystalWithdrawal($"withdrawal unconfirmed at {withdrawalStep}");
            return;
        }
        var im = InventoryManager.Instance();
        var module = AgentModule.Instance();
        var owner = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
        var context = AgentInventoryContext.Instance();
        if (im == null || owner == null || context == null || !owner->IsAgentActive()
            || AddonHelper.IsOpen("SelectString")) return;
        var retainer = im->GetInventoryContainer(InventoryType.RetainerCrystals);
        var player = im->GetInventoryContainer(InventoryType.Crystals);
        if (retainer == null || !retainer->IsLoaded || player == null || !player->IsLoaded) return;
        var source = CrystalBalance(retainer, action.ItemId, out var slot);
        var bag = CrystalBalance(player, action.ItemId, out _);
        var qty = action.Quantity;
        // A single crystal can transfer immediately without displaying InputNumeric.
        if (withdrawalStep == WithdrawalStep.Readback ||
            (withdrawalStep == WithdrawalStep.Quantity && beforeSource == 1 && !AddonHelper.IsOpen("InputNumeric")
                && source == 0))
        {
            if (source == beforeSource - qty && bag == beforeCharacter + qty &&
                Plugin.Instance.RetainerWatcher.RefreshCrystalWithdrawal(action.SourceKey, action.ItemId, source, bag))
                FinishCrystalWithdrawal(null);
            return;
        }
        if (!InventoryEntry.CanWithdrawCrystals(source, bag, qty) ||
            (withdrawalStep != WithdrawalStep.Inventory && (source != beforeSource || bag != beforeCharacter || slot != sourceSlot)))
        {
            FinishCrystalWithdrawal("crystal balances or character capacity changed");
            return;
        }
        switch (withdrawalStep)
        {
            case WithdrawalStep.Inventory:
                if (AddonHelper.IsOpen("InputNumeric") || AddonHelper.IsOpen("ContextMenu")) return;
                (beforeSource, beforeCharacter, sourceSlot) = (source, bag, slot);
                context->OpenForItemSlot(InventoryType.RetainerCrystals, slot, 0, owner->GetAddonId());
                AdvanceWithdrawal(WithdrawalStep.Retrieve);
                break;
            case WithdrawalStep.Retrieve:
                if (context->OwnerAddonId != owner->GetAddonId() || context->TargetInventoryId != InventoryType.RetainerCrystals
                    || context->TargetInventorySlotId != slot || AddonHelper.IsOpen("InputNumeric")) return;
                // Artisan uses Retrieve (Addon 98) for crystals, including partial stacks.
                if (ContextMenuHelper.ClickEntry(s => s == AddonText.TryRow(98)))
                    AdvanceWithdrawal(WithdrawalStep.Quantity);
                break;
            case WithdrawalStep.Quantity:
                var numeric = AddonHelper.GetVisible("InputNumeric");
                if (numeric == null || AddonHelper.IsOpen("ContextMenu") || numeric->AtkValuesCount <= 3
                    || numeric->AtkValues == null || numeric->AtkValues[3].UInt < qty) return;
                Callback.Fire(numeric, true, qty);
                AdvanceWithdrawal(WithdrawalStep.Readback);
                break;
        }
    }

    private static int CrystalBalance(InventoryContainer* container, uint itemId, out int slot)
    {
        for (slot = 0; slot < container->Size; slot++)
        {
            var item = container->GetInventorySlot(slot);
            if (item != null && item->ItemId == itemId) return item->Quantity;
        }
        slot = -1;
        return 0;
    }

    private void FinishCrystalWithdrawal(string? error)
    {
        var action = CurrentJob.Withdrawal!;
        if (error == null)
        {
            confirmedWithdrawals.Add(action);
            action.CharacterBeforeCrystalWithdrawal = beforeCharacter;
        }
        else
        {
            withdrawalFailures[action] = $"{CurrentJob.RetainerName}: {error}";
            if (State == ExecutionState.WithdrawingCrystals && withdrawalStep is WithdrawalStep.Quantity or WithdrawalStep.Readback)
                unconfirmedTopUpItems.Add((action.ItemId, action.IsHQ));
        }
        if (State == ExecutionState.WithdrawingCrystals && ActiveRetainerKey() == action.SourceKey)
        {
            var numeric = AddonHelper.GetVisible("InputNumeric");
            if (numeric != null && withdrawalStep == WithdrawalStep.Quantity) numeric->Close(true);
            if (withdrawalStep == WithdrawalStep.Retrieve) ContextMenuHelper.Close();
            var module = AgentModule.Instance();
            var owner = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
            if (owner != null && owner->IsAgentActive()) owner->Hide();
        }
        waitingSince = null;
        State = ExecutionState.AwaitingSelectStringAfterSell;
        Throttle();
    }
}
