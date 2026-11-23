using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Restocker.Market;

public sealed class PriceHistoryRow
{
    public DateTime UtcTime { get; init; }
    public string Retainer { get; init; } = string.Empty;
    public int Slot { get; init; }
    public uint ItemId { get; init; }
    public bool Hq { get; init; }
    public int Qty { get; init; }
    public long OldPrice { get; init; }
    public long LowestCompetitor { get; init; }
    public long NewPrice { get; init; }
    public bool Applied { get; init; }
    public RepriceReason Reason { get; init; }
    internal long ClampedFloor { get; init; }
    internal string? ErrorDetail { get; init; }

    /// <summary>Competitor retainers priced below our old price, ';'-separated.</summary>
    public string Undercutters { get; init; } = string.Empty;
}

/// <summary>
/// Append-only CSV of every planned price change and whether it was applied.
/// <c>pluginConfigs\Restocky\price-history.csv</c>. Never rewritten, only appended.
/// </summary>
public sealed class PriceHistoryLog
{
    public const string Header =
        "utc_time,retainer,slot,item_id,hq,qty,old_price,lowest_competitor,new_price,applied,reason,undercutters";

    private readonly string path;
    private readonly object gate = new();

    public PriceHistoryLog(string path) => this.path = path;

    public string Path => path;

    public static string Format(PriceHistoryRow r)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Join(",",
            r.UtcTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", inv),
            Escape(r.Retainer),
            r.Slot.ToString(inv),
            r.ItemId.ToString(inv),
            r.Hq ? "true" : "false",
            r.Qty.ToString(inv),
            r.OldPrice.ToString(inv),
            r.LowestCompetitor.ToString(inv),
            r.NewPrice.ToString(inv),
            r.Applied ? "true" : "false",
            RepriceRules.ReasonCode(r.Reason),
            Escape(r.Undercutters));
    }

    private static string Escape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Appends one row. Returns false (and never throws) if the file could not be written.</summary>
    public bool Append(PriceHistoryRow row)
    {
        try
        {
            lock (gate)
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                using var w = new StreamWriter(path, append: true, new UTF8Encoding(false));
                if (isNew) w.WriteLine(Header);
                w.WriteLine(Format(row));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
