using System;

namespace Restocker.Data;

/// <summary>実行計画 1 ステップ。Executor がこれを順次走らせる。</summary>
[Serializable]
public sealed class PlannedAction
{
    public PlannedActionKind Kind { get; set; }

    /// <summary>対象リテイナー（CharacterContentId + RetainerId）。<see cref="RetainerSnapshot.MakeKey"/> と同形式。</summary>
    public string RetainerKey { get; set; } = string.Empty;

    public uint ItemId { get; set; }
    public bool IsHQ { get; set; }

    /// <summary>新規出品の場合の出品個数（端数または最大スタック）。</summary>
    public int Quantity { get; set; }

    /// <summary>提示価格（gil）。</summary>
    public long UnitPrice { get; set; }

    /// <summary>
    /// リプライス時の対象出品スロット（0..19）。新規出品では未使用。
    /// </summary>
    public int ListingIndex { get; set; }

    /// <summary>
    /// 新規出品の供給元。
    /// 通常はリテイナー自身のインベントリ（=<see cref="RetainerKey"/>と同じ）だが、
    /// キャラ所持品から target リテイナーで出品するパターンでは
    /// <see cref="CharacterSnapshot.MakeKey"/> 形式の文字列が入る。
    /// </summary>
    public string SourceKey { get; set; } = string.Empty;

    internal string? PreferredRetainerFallback { get; init; }
    internal DateTime? PreferredRetainerFullAtUtc { get; init; }
    internal bool StockRestock { get; init; }
    internal bool NeedsCrystalWithdrawal => StockRestock && InventoryEntry.IsCrystal(ItemId)
        && SourceKey != RetainerKey && !SourceKey.StartsWith("char.");
    internal bool ListsCharacterCrystals => StockRestock && InventoryEntry.IsCrystal(ItemId) && SourceKey != RetainerKey;
    internal bool ResolveRestockPrice { get; set; }
    internal Restocker.Plan.RestockPrice? RestockPrice { get; set; }
    internal Restocker.Plan.StackTopUp? TopUp { get; init; }

    // ---- Fork: price-history context for Reprice actions (not persisted) ----

    /// <summary>Asking price when the plan was made.</summary>
    public long OldPrice { get; set; }

    /// <summary>Lowest competitor price the plan was based on (0 = unknown / manual).</summary>
    public long LowestCompetitor { get; set; }

    public Restocker.Market.RepriceReason Reason { get; set; } = Restocker.Market.RepriceReason.Manual;
    internal bool BypassedMaxDrop { get; set; }
    internal bool MatchedBait { get; set; }
    internal bool MatchedPrice { get; set; }
    internal long ClampedFloor { get; set; }

    /// <summary>Competitor retainers below our old price when planned, ';'-separated.</summary>
    public string Undercutters { get; set; } = string.Empty;
}

public enum PlannedActionKind
{
    /// <summary>既存出品 ListingIndex の価格を UnitPrice に書き換える。</summary>
    Reprice,

    /// <summary>インベントリの ItemId(HQ) を Quantity 個・UnitPrice で新規出品する。</summary>
    NewListing,

    /// <summary>
    /// listing slot N のダイアログを開いて ComparePrices 経由で
    /// マーケット相場を取得しキャッシュに入れる。価格変更は行わない。
    /// </summary>
    FetchMarketPrice,

    TopUp,
}
