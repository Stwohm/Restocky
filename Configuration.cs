using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker;

[Serializable]
public sealed class Configuration : StockTrackingConfiguration, IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// リテイナーベル（RetainerList addon）が開いている間に MainWindow を自動表示するか。
    /// </summary>
    public bool AutoOpenOnBell { get; set; } = true;

    /// <summary>
    /// パッシブ収集したリテイナーごとのスナップショット。
    /// キーは <see cref="RetainerSnapshot.Key"/>（CharacterContentId + RetainerId の文字列）。
    /// </summary>
    public Dictionary<string, RetainerSnapshot> Snapshots { get; set; } = new();

    /// <summary>
    /// キャラクター側の所持品スナップショット。キーは <see cref="CharacterSnapshot.MakeKey"/>。
    /// 新規出品でキャラ側のアイテムも対象にするため。
    /// </summary>
    public Dictionary<string, CharacterSnapshot> Characters { get; set; } = new();

    /// <summary>
    /// UI 言語。
    /// -1 = FFXIV クライアント言語に従う（ZH/KO はグローバルクライアントから返らないため EN/JA/DE/FR にフォールバック）。
    /// 0..5 = <see cref="Language"/> 列挙の値で明示指定。
    /// </summary>
    public int Language { get; set; } = -1;

    // ---------------- Fork (Restocky) ----------------

    public bool SearchGilhoundItem { get; set; } = true;
    public bool AllowGilhoundTravelCommands { get; set; } = true;

    public RepriceSortColumn RepriceSortColumn { get; set; } = RepriceSortColumn.Item;
    public bool RepriceSortDescending { get; set; }

    /// <summary>Gil below the lowest competitor for the full / auto cycle.</summary>
    public int UndercutGil { get; set; } = 1;

    /// <summary>Do not apply a new price that is more than this % below the current one. 0 = off.</summary>
    public double MaxDropPercent { get; set; } = 30;
    public double MaxRaisePercent { get; set; } = 30;

    public MatchQualitySettings MatchQuality { get; set; } = new();

    public int MarketSalesPeriodDays { get; set; } = 1;
    public bool AutoCostFloor { get; set; } = true;

    private double saleTaxPercent = 5;
    public double SaleTaxPercent
    {
        get => saleTaxPercent;
        set => saleTaxPercent = double.IsNaN(value) ? 5 : Math.Clamp(value, 0, 20);
    }

    private double minProfitPercent;
    public double MinProfitPercent
    {
        get => minProfitPercent;
        set => minProfitPercent = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 500);
    }

    /// <summary>Manual floor by item id, shared by HQ and NQ. Missing or nonpositive entries use the automatic setting.</summary>
    public Dictionary<uint, long> PriceFloors { get; set; } = new();

    /// <summary>Automatic-reprice holds, keyed as "itemId:hq" (HQ and NQ are independent).</summary>
    public HashSet<string> HeldItems { get; set; } = new();

    /// <summary>Auto cycle interval in minutes.</summary>
    public int AutoCycleIntervalMinutes { get; set; } = 10;

    // Fork: request each visited retainer's sale history once per executor run.
    public bool FetchSaleHistory { get; set; } = true;

    /// <summary>Random +/- jitter added to each auto cycle interval, in seconds.</summary>
    public int AutoCycleJitterSeconds { get; set; } = 60;

    /// <summary>Consecutive failed cycles before the auto cycle turns itself off.</summary>
    public int AutoCycleMaxFailures { get; set; } = 3;

    public bool RunFullCycleAfterAutoRetainer { get; set; }

    public bool AutoRestockDuringCycle { get; set; }
    public int AutoRestockMaxScanAgeHours { get; set; } = 6;
    public int StockMaxAgeHours { get; set; } = 6;
    public List<CachedMarketPrice> RestockMarketPrices { get; set; } = new();

    /// <summary>Chat channel (XivChatType value) for Restocky messages. Default 71 = RetainerSale,
    /// so a Chat 2 "Market" tab that shows Retainer Sale Notifications picks them up.</summary>
    public int ChatChannel { get; set; } = (int)Dalamud.Game.Text.XivChatType.RetainerSale;

    public bool TopUpPartialStacksOnRestock { get; set; } = true;

    // Fork: age in whole days before an active listing is flagged as stuck.
    public int StuckListingDays { get; set; } = 3;

    /// <summary>Durations (seconds) of the last successful full cycles; used to time AutoRetainer waits.</summary>
    public List<double> RecentCycleSeconds { get; set; } = new();
    public RunEtaTimings RunEtaTimings { get; set; } = new();

    public long FloorFor(uint itemId) => PriceFloors.TryGetValue(itemId, out var f) ? f : 0;

    private readonly Dictionary<(uint, bool), long> loggedFloorLowerBounds = new();

    internal (long Price, bool FromCost) EffectiveFloorFor(uint itemId, bool hq, CostLedger ledger,
        string? liveRetainer = null, long liveListed = 0)
    {
        var manual = FloorFor(itemId);
        if (manual > 0 || !AutoCostFloor || ledger.RemainingUnits(itemId, hq) == 0)
            return (Math.Max(0, manual), false);

        // Ledger lots span all tracked characters. A live retainer replaces its cached listings.
        var (listed, available) = InventorySnapshot.HeldUnits(itemId, hq, Snapshots.Values, Characters.Values,
            liveRetainer, liveListed);
        var complete = Snapshots.Count > 0 && Characters.Count > 0
            && (liveRetainer == null || Snapshots.ContainsKey(liveRetainer))
            && Snapshots.Values.All(s => Characters.ContainsKey(CharacterSnapshot.MakeKey(s.CharacterContentId))
                && RetainerWatcher.RetainerInventoryFreshness(s, itemId) != DateTime.MinValue)
            && Characters.Values.All(c => RetainerWatcher.CharacterInventoryFreshness(c, false, itemId) != DateTime.MinValue
                && (InventoryEntry.IsCrystal(itemId) || RetainerWatcher.CharacterInventoryFreshness(c, true) != DateTime.MinValue));

        var held = listed + available;
        var item = (itemId, hq);
        if (complete) loggedFloorLowerBounds.Remove(item);
        else if (!loggedFloorLowerBounds.TryGetValue(item, out var previous) || previous != held)
        {
            loggedFloorLowerBounds[item] = held;
            Plugin.Log.Debug($"[Restocker] cost floor item={itemId} hq={hq}: total stock unknown, using listed={listed} plus known inventory={available} as a lower bound (at least the remaining lot quantity).");
        }
        return BreakEvenFloor.Resolve(0, ledger.WeightedAverageCost(itemId, hq, held),
            (decimal)SaleTaxPercent, (decimal)MinProfitPercent);
    }

    public bool IsHeld(uint itemId, bool hq) => HeldItems.Contains(HeldKey(itemId, hq));

    public void SetHeld(uint itemId, bool hq, bool held)
    {
        if (held) HeldItems.Add(HeldKey(itemId, hq));
        else HeldItems.Remove(HeldKey(itemId, hq));
    }

    private static string HeldKey(uint itemId, bool hq) => $"{itemId}:{(hq ? 1 : 0)}";

    public Restocker.Market.RepriceSettings ToRepriceSettings(int? undercutGil = null) => new()
    {
        UndercutGil = undercutGil ?? UndercutGil,
        MaxDropPercent = MaxDropPercent,
        MaxRaisePercent = MaxRaisePercent,
    };

    public Language ResolveLanguage(Dalamud.Game.ClientLanguage clientLanguage)
    {
        if (Language >= 0 && Language <= 5)
            return (Language)Language;

        return clientLanguage switch
        {
            Dalamud.Game.ClientLanguage.Japanese => Localization.Language.Japanese,
            Dalamud.Game.ClientLanguage.German => Localization.Language.German,
            Dalamud.Game.ClientLanguage.French => Localization.Language.French,
            _ => Localization.Language.English,
        };
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
