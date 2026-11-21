using System.IO;
using System.Text.Json;
using Restocker.Bridge;

namespace Restocker.Tests;

public sealed class GilhoundBridgeTests
{
    private const string Command = "/li Raiden market";
    private static readonly string Id = Guid.NewGuid().ToString();
    private readonly Clock clock = new();
    private readonly TravelCommands commands;
    private int calls;

    public GilhoundBridgeTests() => commands = new(clock);

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds { get; set; } = 100_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Milliseconds);
        public override long GetTimestamp() => Milliseconds;
        public override long TimestampFrequency => 1000;
    }

    private TravelReply Handle(string command = Command, double created = 100, string? blocked = null, bool handled = true)
        => commands.Handle(new(Id, new(1, Id, command, created)), blocked, actual =>
        {
            Assert.Equal(command, actual);
            calls++;
            return handled;
        });

    [Theory]
    [InlineData("/li A'B-C D market", true)]
    [InlineData("/li Ab market", true)]
    [InlineData("/li A market", false)]
    [InlineData("/li 'Ab market", false)]
    [InlineData("/li Ráiden market", false)]
    [InlineData("/li Raiden market\n", false)]
    [InlineData("/li Raiden market\n/echo hello", false)]
    [InlineData("/li Raiden Market", false)]
    [InlineData("/echo Raiden market", false)]
    public void AllowlistGatesDispatch(string command, bool allowed)
    {
        Assert.Equal(allowed ? "ok" : "rejected", Handle(command).Status);
        Assert.Equal(allowed ? 1 : 0, calls);
    }

    [Fact]
    public void WorldLengthBoundary()
    {
        Assert.Equal("rejected", Handle("/li " + new string('A', 32) + " market").Status);
        Assert.Equal("ok", Handle("/li " + new string('A', 31) + " market").Status);
    }

    [Fact]
    public void ExpiryIncludesExactlyThirtySeconds()
    {
        Assert.Equal("expired", Handle(created: 69.999).Reason);
        Assert.Equal("ok", Handle(created: 70).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RateLimitUsesTimeSinceLastDispatch()
    {
        Assert.Equal("ok", Handle().Status);
        clock.Milliseconds += 1999;
        Assert.Equal("rate limited", Handle().Reason);
        clock.Milliseconds++;
        Assert.Equal("ok", Handle().Status);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("not logged in")]
    [InlineData("in combat")]
    [InlineData("in a duty")]
    [InlineData("cutscene")]
    [InlineData("between areas")]
    public void BlockedRequestsReturnReasonWithoutDispatch(string reason)
    {
        var reply = Handle(blocked: reason);
        Assert.Equal(("rejected", reason), (reply.Status, reply.Reason));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MissingHandlerReturnsLifestreamError()
    {
        var reply = Handle(handled: false);
        Assert.Equal(("error", "Lifestream not installed"), (reply.Status, reply.Reason));
    }

    [Fact]
    public void InvalidEnvelopesNeverDispatch()
    {
        TravelRequest valid = new(1, Id, Command, 100);
        foreach (var request in new TravelRequest?[] { null, valid with { Version = 2 },
                     valid with { Id = "../wrong" }, valid with { CreatedTs = null } })
            Assert.Equal("rejected", commands.Handle(new(Id, request), null, _ => throw new Exception()).Status);
    }

    private TravelReply Go(SearchItem? item, bool enabled = true, bool dispatched = true)
    {
        var created = clock.GetUtcNow().ToUnixTimeSeconds();
        return commands.Handle(new(Id, new(1, Id, Command, created, item)), null, _ => dispatched, enabled);
    }

    [Fact]
    public void ParsesItemAndUsesSheetNameOnceOnTargetWorld()
    {
        using var m = new Mailbox();
        File.WriteAllText(m.Inbox(Id), JsonSerializer.Serialize(new
        {
            version = 1, id = Id, command = Command, created_ts = 100,
            item = new { id = 50971, hq = true, name = "Sender's name" }
        }));
        var entry = Assert.IsType<InboxEntry>(m.Files.ReadOldest(m.Now));
        Assert.Equal(new SearchItem(50971, true), entry.Request!.Item);
        Assert.Equal("ok", commands.Handle(entry, null, _ => true).Status);
        Assert.Null(commands.TakeSearchName("Cerberus", _ => throw new Exception(), _ => throw new Exception()));
        Assert.Equal("Localized name", commands.TakeSearchName("raiden",
            id => id == 50971 ? "Localized name" : null, _ => throw new Exception()));
        Assert.False(commands.AwaitingBoard("Raiden"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"id\":-1}")]
    [InlineData("{\"id\":1.5}")]
    [InlineData("{\"id\":42,\"hq\":1}")]
    public void InvalidItemRejectedBeforeDispatch(string json)
    {
        using var m = new Mailbox();
        var item = JsonDocument.Parse(json);
        using (item)
            File.WriteAllText(m.Inbox(Id), JsonSerializer.Serialize(new
                { version = 1, id = Id, command = Command, created_ts = 100, item = item.RootElement }));
        var entry = Assert.IsType<InboxEntry>(m.Files.ReadOldest(m.Now));
        Assert.Equal("rejected", commands.Handle(entry, null, _ => throw new Exception()).Status);
    }

    [Fact]
    public void SearchExpiresAtTenMinutes()
    {
        Go(new(42));
        clock.Milliseconds += 599_999;
        Assert.True(commands.AwaitingBoard("Raiden"));
        clock.Milliseconds++;
        Assert.False(commands.AwaitingBoard("Raiden"));
        Assert.False(commands.HasSearch);
    }

    [Fact]
    public void OnlySuccessfulGoReplacesSearch()
    {
        Go(new(1));
        clock.Milliseconds += 2000;
        Go(new(2));
        clock.Milliseconds += 2000;
        Go(new(3), dispatched: false);
        Assert.Equal("2", commands.TakeSearchName("Raiden", id => id.ToString(), _ => throw new Exception()));
    }

    [Fact]
    public void TravelOnlyOrDisabledSearchClearsPendingItem()
    {
        Go(new(1));
        clock.Milliseconds += 2000;
        Assert.Equal("ok", Go(null).Status);
        Assert.False(commands.HasSearch);
        clock.Milliseconds += 2000;
        Assert.Equal("ok", Go(new(2), enabled: false).Status);
        Assert.False(commands.HasSearch);
    }

    [Fact]
    public void UnknownItemIsConsumedWithOneWarning()
    {
        Go(new(42));
        var warnings = new System.Collections.Generic.List<uint>();
        Assert.Null(commands.TakeSearchName("Raiden", _ => null, warnings.Add));
        Assert.Null(commands.TakeSearchName("Raiden", _ => throw new Exception(), warnings.Add));
        Assert.Equal(new uint[] { 42 }, warnings);
    }

    private sealed class Mailbox : IDisposable
    {
        public readonly string Root = Directory.CreateTempSubdirectory().FullName;
        public readonly BridgeFiles Files;
        public readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public Mailbox()
        {
            Files = new(Root);
            Beat();
        }
        public string Inbox(string id) => Path.Combine(Root, "inbox", id + ".json");
        public string Outbox(string id) => Path.Combine(Root, "outbox", id + ".json");
        public void Put(string id) => File.WriteAllText(Inbox(id),
            JsonSerializer.Serialize(new { version = 1, id, command = Command, created_ts = 100.25 }));
        public void Beat() => Files.UpdateHeartbeat(new(1, "0.3.10.0", Now.ToUnixTimeSeconds(), true, "Raiden", false), Now);
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public void OldestRequestGetsOneReplyAndIsRemoved()
    {
        using var m = new Mailbox();
        var later = Guid.NewGuid().ToString();
        m.Put(later);
        m.Put(Id);
        File.SetLastWriteTimeUtc(m.Inbox(Id), m.Now.AddSeconds(-1).UtcDateTime);
        var entry = Assert.IsType<InboxEntry>(m.Files.ReadOldest(m.Now));
        Assert.Equal(Id, entry.Id);
        Assert.Equal(100.25, entry.Request!.CreatedTs);
        m.Files.Finish(commands.Handle(entry, null, _ => true));
        Assert.False(File.Exists(m.Inbox(Id)));
        Assert.Equal(later, m.Files.ReadOldest(m.Now)!.Id);
        using var reply = JsonDocument.Parse(File.ReadAllText(m.Outbox(Id)));
        Assert.Equal("ok", reply.RootElement.GetProperty("status").GetString());
        Assert.Equal(Id, reply.RootElement.GetProperty("id").GetString());
        // A surviving inbox file after publication must never dispatch again.
        m.Put(Id);
        File.SetLastWriteTimeUtc(m.Inbox(Id), m.Now.AddSeconds(-2).UtcDateTime);
        Assert.Null(m.Files.ReadOldest(m.Now));
        Assert.False(File.Exists(m.Inbox(Id)));
    }

    [Fact]
    public void IgnoresTempsAndRetriesPartialAndLockedRequests()
    {
        using var m = new Mailbox();
        m.Put(Id);
        File.Move(m.Inbox(Id), Path.ChangeExtension(m.Inbox(Id), ".tmp"));
        Assert.Null(m.Files.ReadOldest(m.Now));
        File.WriteAllText(m.Inbox(Id), "{");
        Assert.Null(m.Files.ReadOldest(m.Now));
        m.Put(Id);
        using (File.Open(m.Inbox(Id), FileMode.Open, FileAccess.Write, FileShare.None))
            Assert.Null(m.Files.ReadOldest(m.Now));
        Assert.Equal(Command, m.Files.ReadOldest(m.Now)!.Request!.Command);
    }

    [Fact]
    public void MalformedRequestsReceiveARejection()
    {
        using var m = new Mailbox();
        File.WriteAllText(m.Inbox(Id), """{"created_ts":"invalid"}""");
        var invalid = Assert.IsType<InboxEntry>(m.Files.ReadOldest(m.Now));
        Assert.Null(invalid.Request);
        File.WriteAllText(m.Inbox(Id), "{");
        File.SetLastWriteTimeUtc(m.Inbox(Id), m.Now.AddSeconds(-3).UtcDateTime);
        var incomplete = Assert.IsType<InboxEntry>(m.Files.ReadOldest(m.Now));
        var reply = commands.Handle(incomplete, null, _ => throw new Exception());
        Assert.Equal("rejected", reply.Status);
        m.Files.Finish(reply);
        Assert.False(File.Exists(m.Inbox(Id)));
    }

    [Fact]
    public void FailedReplyPublicationCanRetryWithoutLosingInbox()
    {
        using var m = new Mailbox();
        m.Put(Id);
        var entry = Assert.IsType<InboxEntry>(m.Files.ReadOldest(m.Now));
        var reply = commands.Handle(entry, null, _ => true);
        using (File.Open(Path.ChangeExtension(m.Outbox(Id), ".tmp"), FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.ThrowsAny<IOException>(() => m.Files.Finish(reply));
        Assert.True(File.Exists(m.Inbox(Id)));
        Assert.False(File.Exists(m.Outbox(Id)));
        m.Files.Finish(reply);
        Assert.False(File.Exists(m.Inbox(Id)));
        Assert.True(File.Exists(m.Outbox(Id)));
    }

    [Fact]
    public void HeartbeatReportsSettingAndPrunesOnlyOldJsonReplies()
    {
        using var m = new Mailbox();
        var old = m.Outbox("old");
        var boundary = m.Outbox("boundary");
        foreach (var path in new[] { old, boundary }) File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(old, m.Now.AddMinutes(-11).UtcDateTime);
        File.SetLastWriteTimeUtc(boundary, m.Now.AddMinutes(-10).UtcDateTime);
        m.Beat();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(boundary));
        using var heartbeat = JsonDocument.Parse(File.ReadAllText(Path.Combine(m.Root, "heartbeat.json")));
        var state = heartbeat.RootElement;
        Assert.Equal(1, state.GetProperty("version").GetInt32());
        Assert.Equal("0.3.10.0", state.GetProperty("plugin_version").GetString());
        Assert.Equal(m.Now.ToUnixTimeSeconds(), state.GetProperty("ts").GetInt64());
        Assert.True(state.GetProperty("logged_in").GetBoolean());
        Assert.Equal("Raiden", state.GetProperty("world").GetString());
        Assert.False(state.GetProperty("enabled").GetBoolean());
        var features = state.GetProperty("features");
        Assert.Equal(new[] { "travel", "mb_search_name" }, features.Deserialize<string[]>());
    }
}
