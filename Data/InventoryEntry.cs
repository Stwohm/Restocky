using System;

namespace Restocker.Data;

/// <summary>
/// リテイナーの通常インベントリ（出品ボックス除く）に置かれた 1 アイテム。
/// 新規分割出品の供給源。
/// </summary>
[Serializable]
public sealed class InventoryEntry
{
    internal const int CrystalCapacity = 9999;
    internal static bool CanWithdrawCrystals(int source, int character, int quantity)
        => quantity is > 0 and <= CrystalCapacity && source >= quantity && character >= 0
            && character <= CrystalCapacity - quantity;
    internal static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;
    internal static int ListingLimit(uint itemId, int stackSize)
        => Math.Clamp(stackSize, 1, IsCrystal(itemId) ? CrystalCapacity : 99);

    public uint ItemId { get; set; }
    public bool IsHQ { get; set; }
    public int Quantity { get; set; }

    /// <summary>FFXIV 内部の InventoryType 値（ページ番号）。書き戻し時にスロット特定に使う。</summary>
    public uint ContainerId { get; set; }

    /// <summary>ページ内のスロット番号。</summary>
    public int SlotIndex { get; set; }

    /// <summary>マーケットに出品可能か（trade unrestricted / not bound / not unique 等）。</summary>
    public bool IsListable { get; set; } = true;

    /// <summary>Maximum quantity for one market listing of this item.</summary>
    public int MaxStackPerListing { get; set; } = 99;
}
