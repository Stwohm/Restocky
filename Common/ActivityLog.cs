using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Restocker.Common;

internal sealed record ActivityEntry(DateTime Utc, string Type, string Detail)
{
    public string Display => $"{Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {Type}  {Detail}";
}

internal sealed class ActivityLog
{
    private readonly string path;
    private readonly Action<string> warn;
    private readonly List<ActivityEntry> entries = [];
    private DateTime nextPrune;
    public IReadOnlyList<ActivityEntry> Entries => entries;

    public ActivityLog(string path, Action<string> warn)
    {
        this.path = path;
        this.warn = warn;
        try
        {
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                    try
                    {
                        if (JsonSerializer.Deserialize<ActivityEntry>(line) is { Type: not null, Detail: not null } entry)
                            entries.Add(entry);
                    }
                    catch (JsonException) { /* Recover complete records around a damaged line. */ }
                // A crash can leave the last JSON record without a line terminator.
                using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
                }
            }
            Prune(DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warn(ex.Message); }
    }

    public void Add(string type, string detail)
    {
        var now = DateTime.UtcNow;
        var entry = new ActivityEntry(now, type, detail);
        try
        {
            if (now >= nextPrune) Prune(now);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warn(ex.Message); }
        entries.Add(entry);
    }

    internal void Prune(DateTime now)
    {
        var cutoff = now.AddDays(-14);
        if (entries.Exists(e => e.Utc < cutoff))
        {
            var retained = entries.Where(e => e.Utc >= cutoff).ToList();
            File.WriteAllLines(path + ".tmp", retained.Select(e => JsonSerializer.Serialize(e)));
            File.Move(path + ".tmp", path, true);
            entries.Clear();
            entries.AddRange(retained);
        }
        nextPrune = now.Date.AddDays(1);
    }
}
