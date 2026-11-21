using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Restocker.Bridge;

internal sealed class BridgeFiles(string root)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string inbox = Path.Combine(root, "inbox");
    private readonly string outbox = Path.Combine(root, "outbox");

    internal InboxEntry? ReadOldest(DateTimeOffset now)
    {
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(outbox);
        foreach (var file in new DirectoryInfo(inbox).EnumerateFiles("*.json")
                     .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            try
            {
                // A reply may already be durable if deletion failed on the previous poll.
                if (File.Exists(Path.Combine(outbox, file.Name)))
                {
                    file.Delete();
                    return null;
                }
                using var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var document = JsonDocument.Parse(stream);
                try { return new(id, document.Deserialize<TravelRequest>(Json)); }
                catch (JsonException) { return new(id, null); } // Complete JSON with invalid fields.
            }
            catch (JsonException)
            {
                // Allow a partial write to finish within Gilhound's five-second reply window.
                if (file.LastWriteTimeUtc <= now.UtcDateTime.AddSeconds(-2)) return new(id, null);
            }
            catch (IOException) { /* Writer still holds the file, or it disappeared. */ }
            catch (UnauthorizedAccessException) { /* Retry on a later poll. */ }
        }
        return null;
    }

    internal void Finish(TravelReply reply)
    {
        Write(Path.Combine(outbox, reply.Id + ".json"), reply);
        File.Delete(Path.Combine(inbox, reply.Id + ".json"));
    }

    internal void UpdateHeartbeat(Heartbeat heartbeat, DateTimeOffset now)
    {
        Directory.CreateDirectory(outbox);
        Directory.CreateDirectory(inbox);
        Write(Path.Combine(root, "heartbeat.json"), heartbeat);
        foreach (var file in new DirectoryInfo(outbox).EnumerateFiles("*.json"))
        {
            if (file.LastWriteTimeUtc < now.UtcDateTime.AddMinutes(-10) && !File.Exists(Path.Combine(inbox, file.Name)))
                file.Delete();
        }
    }

    private static void Write<T>(string path, T value)
    {
        var tmp = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
