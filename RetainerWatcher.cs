using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Restocker.Data;

namespace Restocker;

/// <summary>
/// 「リテイナーが召喚されてマーケット出品リストが開いた瞬間」を検知し、
/// その時点のインベントリ＋出品状態を <see cref="Configuration.Snapshots"/> に書き込む。
/// 受動収集（ユーザーが普通にリテイナーを開いただけで貯まる）。
/// </summary>
public sealed unsafe class RetainerWatcher : IDisposable
{
    private readonly Configuration configuration;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    internal RetainerSnapshot? LastSeen { get; private set; }

    public RetainerWatcher(
        Configuration configuration,
        IAddonLifecycle addonLifecycle,
        IPlayerState playerState,
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.addonLifecycle = addonLifecycle;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;

        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "RetainerSellList", OnSellListUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, ["InventoryBuddy", "InventoryBuddy2"], OnSaddlebagOpen);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "RetainerSellList", OnSellListUpdate);
        addonLifecycle.UnregisterListener(OnSaddlebagOpen);
    }

    private void OnSaddlebagOpen(AddonEvent type, AddonArgs args)
    {
        try
        {
            CaptureCharacterSnapshot();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] failed to capture saddlebag snapshot");
        }
    }

    /// <summary>
    /// RetainerSellList addon の PostRequestedUpdate（ゲーム側が再描画したタイミング）でスナップショットを取得。
    /// PostSetup だと中身が埋まる前に発火することがあるため、Update の方が安定。
    /// </summary>
    private void OnSellListUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            CaptureActiveRetainerSnapshot();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] failed to capture retainer snapshot");
        }
    }

    public RetainerSnapshot? CaptureActiveRetainerSnapshot(bool authoritative = false)
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null || !retainerManager->IsReady) return null;

        var active = retainerManager->GetActiveRetainer();
        if (active == null || active->RetainerId == 0) return null;

        var characterId = playerState.ContentId;
        if (characterId == 0) return null;

        var localPlayer = objectTable.LocalPlayer;
        var snapshot = new RetainerSnapshot
        {
            CharacterContentId = characterId,
            CharacterName = localPlayer?.Name.TextValue ?? string.Empty,
            RetainerId = active->RetainerId,
            RetainerName = active->NameString,
            WorldId = localPlayer?.HomeWorld.RowId ?? 0,
            LastRefreshedUtc = DateTime.UtcNow,
        };
        snapshot.Key = RetainerSnapshot.MakeKey(snapshot.CharacterContentId, snapshot.RetainerId);
        snapshot.Listings = ReadListings(out var marketLoaded);
        if (!marketLoaded) return null;
        configuration.Snapshots.TryGetValue(snapshot.Key, out var previous);
        snapshot.InventoryRefreshedUtc = new(previous?.InventoryRefreshedUtc ?? []);
        snapshot.Inventory = InventorySnapshot.Refresh(previous?.Inventory ?? [], snapshot.InventoryRefreshedUtc,
            ReadContainers(RetainerInventoryTypes), snapshot.LastRefreshedUtc);
        LastSeen = snapshot;

        configuration.Snapshots[snapshot.Key] = snapshot;
        configuration.Save();

        // Fork: feed the listing lifecycle tracker. Only with a loaded market container, so a
        // half-loaded read never looks like "everything sold".
        var items = dataManager.GetExcelSheet<Item>();
        var live = new List<LiveListing>(snapshot.Listings.Count);
        foreach (var l in snapshot.Listings)
            live.Add(new LiveListing(l.ListingIndex, l.ItemId, l.IsHQ, l.Quantity, l.UnitPrice));
        Plugin.Instance?.Tracker.Tracker.ObserveRetainer(
            snapshot.Key, snapshot.RetainerName, live,
            id => items.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}",
            snapshot.LastRefreshedUtc, authoritative);

        log.Debug($"[Restocker] snapshot updated: {snapshot.RetainerName} listings={snapshot.Listings.Count} inv={snapshot.Inventory.Count}");
        return snapshot;
    }

    private List<ListingEntry> ReadListings(out bool loaded)
    {
        loaded = false;
        var result = new List<ListingEntry>();
        var im = InventoryManager.Instance();
        if (im == null) return result;

        var container = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded) return result;
        loaded = true;

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0) continue;

            // 現在価格は InventoryManager.GetRetainerMarketPrice(slot) で直接取得
            var price = (long)im->GetRetainerMarketPrice((short)i);

            result.Add(new ListingEntry
            {
                ItemId = item->ItemId,
                IsHQ = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                Quantity = (int)item->Quantity,
                UnitPrice = price,
                ListingIndex = item->Slot,
            });
        }

        return result;
    }

    internal static int ResolveMaxListingStack(Item row)
        => InventoryEntry.ListingLimit(row.RowId, (int)row.StackSize);

    internal static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    internal static readonly InventoryType[] CharacterPages =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    internal static readonly InventoryType[] PlayerCrystals = [InventoryType.Crystals];
    internal static readonly InventoryType[] RetainerCrystals = [InventoryType.RetainerCrystals];
    private static readonly InventoryType[] CharacterBagTypes = [.. CharacterPages, .. PlayerCrystals];
    private static readonly InventoryType[] RetainerInventoryTypes = [.. RetainerPages, .. RetainerCrystals];

    internal static readonly InventoryType[] SaddlebagTypes =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
    ];

    internal static readonly InventoryType[] PremiumSaddlebagTypes =
    [
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    /// <summary>
    /// キャラクター側のバッグ + サドルバッグ + プレミアムサドルを読み、
    /// <see cref="Configuration.Characters"/> に保存する。
    /// 召喚中リテイナーが居なくても呼べる（ベル開時など）。
    /// </summary>
    public void CaptureCharacterSnapshot() => CaptureCharacterSnapshot(loadedOnly: false);

    internal CharacterSnapshot? CaptureCharacterSnapshot(bool loadedOnly)
    {
        var characterId = playerState.ContentId;
        if (characterId == 0) return null;
        var localPlayer = objectTable.LocalPlayer;
        var name = localPlayer?.Name.TextValue ?? string.Empty;

        var key = CharacterSnapshot.MakeKey(characterId);
        configuration.Characters.TryGetValue(key, out var previous);
        var now = DateTime.UtcNow;
        var lastRefresh = previous?.LastRefreshedUtc ?? DateTime.MinValue;
        var timestamps = new Dictionary<uint, DateTime>(previous?.InventoryRefreshedUtc ?? []);
        var bag = ReadContainers(CharacterBagTypes);
        var saddle = ReadContainers(SaddlebagTypes);
        var premium = ReadContainers(PremiumSaddlebagTypes);
        var snapshot = new CharacterSnapshot
        {
            CharacterContentId = characterId,
            CharacterName = name,
            Bag = InventorySnapshot.Refresh(previous?.Bag ?? [], timestamps, bag, now),
            Saddlebag = InventorySnapshot.Refresh(previous?.Saddlebag ?? [], timestamps, saddle, now),
            PremiumSaddlebag = InventorySnapshot.Refresh(previous?.PremiumSaddlebag ?? [], timestamps, premium, now),
            InventoryRefreshedUtc = timestamps,
            LastRefreshedUtc = bag.Count + saddle.Count + premium.Count > 0 ? now : lastRefresh,
        };
        configuration.Characters[key] = snapshot;
        configuration.Save();

        log.Debug($"[Restocker] character snapshot updated: bag={snapshot.Bag.Count} saddle={snapshot.Saddlebag.Count} premium={snapshot.PremiumSaddlebag.Count}");
        if (!loadedOnly) return snapshot;
        return new CharacterSnapshot
        {
            CharacterContentId = characterId,
            Bag = bag.Values.SelectMany(entries => entries).ToList(),
            Saddlebag = saddle.Values.SelectMany(entries => entries).ToList(),
            PremiumSaddlebag = premium.Values.SelectMany(entries => entries).ToList(),
        };
    }

    private static InventoryType[] StockContainers(uint? itemId, InventoryType[] all, InventoryType[] pages, InventoryType[] crystals)
        => itemId is { } id ? (InventoryEntry.IsCrystal(id) ? crystals : pages) : all;

    // Snapshots predating per-container timestamps never captured crystal balances.
    private static DateTime LegacyInventoryRead(uint? itemId, DateTime previous)
        => itemId is { } id && !InventoryEntry.IsCrystal(id) ? previous : DateTime.MinValue;

    internal static DateTime RetainerInventoryFreshness(RetainerSnapshot snapshot, uint? itemId = null)
    {
        var types = StockContainers(itemId, RetainerInventoryTypes, RetainerPages, RetainerCrystals);
        return InventorySnapshot.OldestRefresh(types.Select(type => (uint)type), snapshot.InventoryRefreshedUtc,
            LegacyInventoryRead(itemId, snapshot.LastRefreshedUtc));
    }

    internal static DateTime CharacterInventoryFreshness(CharacterSnapshot snapshot, bool saddle, uint? itemId = null)
    {
        var types = saddle ? SaddlebagTypes : StockContainers(itemId, CharacterBagTypes, CharacterPages, PlayerCrystals);
        var containers = types.Select(type => (uint)type);
        if (saddle && PremiumSaddlebagTypes.Any(type => snapshot.InventoryRefreshedUtc?.ContainsKey((uint)type) == true))
            containers = containers.Concat(PremiumSaddlebagTypes.Select(type => (uint)type));
        return InventorySnapshot.OldestRefresh(containers, snapshot.InventoryRefreshedUtc,
            saddle ? snapshot.LastRefreshedUtc : LegacyInventoryRead(itemId, snapshot.LastRefreshedUtc));
    }

    internal bool RefreshCrystalWithdrawal(string sourceKey, uint itemId, int expectedSource, int expectedCharacter)
    {
        if (Market.RetainerHistoryCapture.ActiveKey(out _) != sourceKey ||
            !configuration.Snapshots.TryGetValue(sourceKey, out var source)) return false;
        var balances = ReadContainers(RetainerCrystals);
        if (!balances.TryGetValue((uint)InventoryType.RetainerCrystals, out var crystals)) return false;
        source.Inventory = InventorySnapshot.Refresh(source.Inventory, source.InventoryRefreshedUtc ??= new(), balances, DateTime.UtcNow);
        var character = CaptureCharacterSnapshot(loadedOnly: false);
        return character != null && character.InventoryRefreshedUtc?.GetValueOrDefault((uint)InventoryType.Crystals) == character.LastRefreshedUtc
            && crystals.Where(i => i.ItemId == itemId).Sum(i => i.Quantity) == expectedSource
            && character.Bag.Where(i => i.ContainerId == (uint)InventoryType.Crystals && i.ItemId == itemId).Sum(i => i.Quantity) == expectedCharacter;
    }

    private Dictionary<uint, List<InventoryEntry>> ReadContainers(InventoryType[] types)
    {
        var result = new Dictionary<uint, List<InventoryEntry>>();
        var im = InventoryManager.Instance();
        if (im == null) return result;
        var itemSheet = dataManager.GetExcelSheet<Item>();

        foreach (var t in types)
        {
            var container = im->GetInventoryContainer(t);
            if (container == null || !container->IsLoaded) continue;
            var entries = result[(uint)t] = new List<InventoryEntry>();
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0) continue;
                var entry = new InventoryEntry
                {
                    ItemId = item->ItemId,
                    IsHQ = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                    Quantity = (int)item->Quantity,
                    ContainerId = (uint)t,
                    SlotIndex = item->Slot,
                };
                if (itemSheet.TryGetRow(item->ItemId, out var row))
                {
                    entry.MaxStackPerListing = ResolveMaxListingStack(row);
                    entry.IsListable = row.ItemSearchCategory.RowId != 0;
                }
                entries.Add(entry);
            }
        }
        return result;
    }
}
