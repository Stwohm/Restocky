using System;
using System.Text.RegularExpressions;

namespace Restocker.Bridge;

internal sealed record TravelRequest(int Version, string? Id, string? Command, double? CreatedTs, SearchItem? Item = null);
internal sealed record SearchItem(uint Id, bool Hq = false);
internal sealed record InboxEntry(string Id, TravelRequest? Request)
{
    private static readonly Regex Allowed = new(@"\A/li (?<world>[A-Za-z][A-Za-z' -]{1,30}) market\z", RegexOptions.CultureInvariant);

    // The inbox reader parses and validates the wire data before the framework sees it.
    internal bool ValidEnvelope { get; } = Request is { Version: 1, Command: not null, CreatedTs: double created }
        && double.IsFinite(created) && Id.Length == 36 && Guid.TryParseExact(Id, "D", out _) && Id[14] == '4'
        && "89abAB".Contains(Id[19]) && string.Equals(Request.Id, Id, StringComparison.OrdinalIgnoreCase);
    internal string? Destination { get; } = Request?.Command is { } command && Allowed.Match(command) is { Success: true } match
        ? match.Groups["world"].Value : null;
    internal string? LocalizedName { get; init; }
}
internal sealed record TravelReply(int Version, string Id, string Status, string Reason, double Ts);
internal sealed record Heartbeat(int Version, string PluginVersion, double Ts, bool LoggedIn, string World, bool Enabled)
{
    public string[] Features => ["travel", "mb_search_name"];
}

internal sealed class TravelCommands(TimeProvider clock)
{
    private long? lastAttempt;

    private (SearchItem Item, string World, long Start)? search;

    internal bool HasSearch => search.HasValue;
    internal void ClearSearch() => search = null;

    internal bool AwaitingBoard(string? world)
    {
        if (search is not { } pending) return false;
        if (clock.GetElapsedTime(pending.Start) >= TimeSpan.FromMinutes(10))
        {
            ClearSearch();
            return false;
        }
        return string.Equals(world, pending.World, StringComparison.OrdinalIgnoreCase);
    }

    internal string? TakeSearchName(string? world, Func<uint, string?> resolve, Action<uint> warn)
    {
        if (!AwaitingBoard(world)) return null;
        var id = search!.Value.Item.Id;
        ClearSearch(); // RunSearch can trigger another addon refresh.
        var name = resolve(id);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        warn(id);
        return null;
    }

    internal TravelReply Handle(InboxEntry entry, string? blockedReason, Func<string, bool> dispatch, bool searchEnabled = true)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
        TravelReply Reply(string status, string reason) => new(1, entry.Id, status, reason, now);
        var r = entry.Request;
        if (blockedReason != null) return Reply("rejected", blockedReason);
        if (!entry.ValidEnvelope || r?.CreatedTs is not double created)
            return Reply("rejected", "invalid request");
        if (now - created > 30) return Reply("rejected", "expired");
        if (r.Item is { Id: 0 }) return Reply("rejected", "invalid item");
        var world = entry.Destination;
        if (world == null) return Reply("rejected", "invalid command");
        var tick = clock.GetTimestamp();
        if (lastAttempt is long last && clock.GetElapsedTime(last, tick) < TimeSpan.FromSeconds(2))
            return Reply("rejected", "rate limited");

        lastAttempt = tick;
        if (!dispatch(r.Command!)) return Reply("error", "Lifestream not installed");
        search = searchEnabled && r.Item is { } item ? (item, world, tick) : null;
        return Reply("ok", "");
    }
}
