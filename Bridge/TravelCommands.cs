using System;
using System.Text.RegularExpressions;

namespace Restocker.Bridge;

internal sealed record TravelRequest(int Version, string? Id, string? Command, double? CreatedTs);
internal sealed record InboxEntry(string Id, TravelRequest? Request);
internal sealed record TravelReply(int Version, string Id, string Status, string Reason, double Ts);
internal sealed record Heartbeat(int Version, string PluginVersion, double Ts, bool LoggedIn, string World, bool Enabled);

internal sealed class TravelCommands(TimeProvider clock)
{
    private static readonly Regex Allowed = new(@"\A/li [A-Za-z][A-Za-z' -]{1,30} market\z", RegexOptions.CultureInvariant);
    private long? lastAttempt;

    internal TravelReply Handle(InboxEntry entry, string? blockedReason, Func<string, bool> dispatch)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
        TravelReply Reply(string status, string reason) => new(1, entry.Id, status, reason, now);
        var r = entry.Request;
        if (blockedReason != null) return Reply("rejected", blockedReason);
        if (r is not { Version: 1, Command: not null, CreatedTs: double created }
            || !double.IsFinite(created) || entry.Id.Length != 36 || !Guid.TryParseExact(entry.Id, "D", out _) || entry.Id[14] != '4'
            || !"89abAB".Contains(entry.Id[19]) || !string.Equals(r.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
            return Reply("rejected", "invalid request");
        if (now - created > 30) return Reply("rejected", "expired");
        if (!Allowed.IsMatch(r.Command)) return Reply("rejected", "invalid command");
        var tick = clock.GetTimestamp();
        if (lastAttempt is long last && clock.GetElapsedTime(last, tick) < TimeSpan.FromSeconds(2))
            return Reply("rejected", "rate limited");

        lastAttempt = tick;
        return dispatch(r.Command) ? Reply("ok", "") : Reply("error", "Lifestream not installed");
    }
}
