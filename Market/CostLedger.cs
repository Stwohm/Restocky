using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Restocker.Market;

public sealed record CostLot(Guid Id, DateTime Utc, uint ItemId, bool IsHq, int RemainingQuantity, decimal UnitCost, string Source);

/// <summary>Fork: FIFO cost lots for each item and quality.</summary>
public sealed class CostLedger
{
    public const string FileName = "cost-ledger.json";
    public const string MarketSource = "market";
    public const string ManualSource = "manual";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, RespectRequiredConstructorParameters = true };
    private readonly object gate = new();
    private readonly Dictionary<(uint ItemId, bool IsHq), List<CostLot>> lots = new();
    private bool dirty;

    public CostLot RecordLot(uint itemId, bool hq, int quantity, decimal unitCost, DateTime utc)
        => AddLot(itemId, hq, quantity, unitCost, utc, MarketSource);

    public CostLot AddManualLot(uint itemId, bool hq, int quantity, decimal unitCost, DateTime utc)
        => AddLot(itemId, hq, quantity, unitCost, utc, ManualSource);

    private CostLot AddLot(uint itemId, bool hq, int quantity, decimal unitCost, DateTime utc, string source)
    {
        ArgumentOutOfRangeException.ThrowIfZero(itemId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(unitCost);
        var lot = new CostLot(Guid.NewGuid(), utc.ToUniversalTime(), itemId, hq, quantity, unitCost, source);
        lock (gate)
        {
            InsertLot(lot);
            dirty = true;
        }
        return lot;
    }

    private void InsertLot(CostLot lot)
    {
        var key = (lot.ItemId, lot.IsHq);
        if (!lots.TryGetValue(key, out var queue))
            lots.Add(key, queue = new List<CostLot>());

        // Fork: order by acquisition time, preserving insertion order for equal timestamps.
        var index = queue.FindLastIndex(existing => existing.Utc <= lot.Utc);
        queue.Insert(index + 1, lot);
    }

    public bool RemoveLot(Guid id)
    {
        lock (gate)
        {
            foreach (var (key, queue) in lots)
            {
                if (queue.RemoveAll(lot => lot.Id == id) == 0) continue;
                if (queue.Count == 0) lots.Remove(key);
                dirty = true;
                return true;
            }
            return false;
        }
    }

    // Preview allocations use the same bought-first FIFO rules as recorded sales.
    // A private copy lets multiple listed stacks share the available lots once.
    internal CostLedger CopyForEstimate()
    {
        var copy = new CostLedger();
        lock (gate)
            foreach (var (key, queue) in lots)
                copy.lots.Add(key, new List<CostLot>(queue));
        return copy;
    }

    /// <summary>Consumes only lots acquired by the given UTC time.</summary>
    public (decimal ConsumedCost, int UnknownUnits) Consume(uint itemId, bool hq, int qty, DateTime utc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(qty);
        utc = utc.ToUniversalTime();
        lock (gate)
        {
            var key = (itemId, hq);
            if (!lots.TryGetValue(key, out var queue)) return (0, qty);

            decimal cost = 0;
            var exhausted = 0;
            CostLot? partial = null;
            foreach (var lot in queue)
            {
                if (qty == 0 || lot.Utc > utc) break;
                var taken = Math.Min(qty, lot.RemainingQuantity);
                cost += taken * lot.UnitCost;
                qty -= taken;
                if (taken == lot.RemainingQuantity)
                    exhausted++;
                else
                    partial = lot with { RemainingQuantity = lot.RemainingQuantity - taken };
            }

            if (partial != null) queue[exhausted] = partial;
            if (exhausted > 0) queue.RemoveRange(0, exhausted);
            if (queue.Count == 0) lots.Remove(key);
            if (exhausted > 0 || partial != null) dirty = true;
            return (cost, qty);
        }
    }

    public decimal? CurrentUnitCost(uint itemId, bool hq)
    {
        lock (gate)
            return lots.TryGetValue((itemId, hq), out var queue) ? queue[0].UnitCost : null;
    }

    public long RemainingUnits(uint itemId, bool hq)
    {
        lock (gate)
            return lots.TryGetValue((itemId, hq), out var queue) ? queue.Sum(lot => (long)lot.RemainingQuantity) : 0;
    }

    public decimal? WeightedAverageCost(uint itemId, bool hq, long heldUnits = 0)
    {
        lock (gate)
            return lots.TryGetValue((itemId, hq), out var queue)
                ? queue.Sum(lot => lot.RemainingQuantity * lot.UnitCost) / Math.Max(heldUnits, queue.Sum(lot => (long)lot.RemainingQuantity))
                : null;
    }

    public IReadOnlyList<CostLot> GetLots(uint itemId, bool hq)
    {
        lock (gate)
            return lots.TryGetValue((itemId, hq), out var queue) ? queue.ToArray() : Array.Empty<CostLot>();
    }

    public IReadOnlyList<CostLot> GetLots()
    {
        lock (gate)
            return lots.Values.SelectMany(queue => queue).ToArray();
    }

    public static CostLedger Load(string path, Action<string>? warn = null)
    {
        var ledger = new CostLedger();
        try
        {
            var records = JsonSerializer.Deserialize<List<CostLot>>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            var ids = new HashSet<Guid>();
            if (records == null || records.Any(lot => lot == null || lot.Id == Guid.Empty || !ids.Add(lot.Id)
                || lot.ItemId == 0 || lot.Utc.Kind != DateTimeKind.Utc || lot.RemainingQuantity <= 0
                || lot.UnitCost < 0 || lot.Source is not (MarketSource or ManualSource)))
                throw new JsonException("Invalid cost lot data.");

            foreach (var lot in records) ledger.InsertLot(lot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warn?.Invoke($"[Restocker] Starting with an empty cost ledger. Could not read {path}. {ex.Message}");
        }
        return ledger;
    }

    public void SaveIfDirty(string path)
    {
        lock (gate)
        {
            if (!dirty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(lots.Values.SelectMany(queue => queue), JsonOptions), Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
            dirty = false;
        }
    }
}
