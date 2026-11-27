using System.Collections.Generic;
using System.Linq;
using Restocker.Market;

namespace Restocker.Common;

internal enum ChatCategory { Prices, Guards, Bait, Skipped, Restocked, Problems }
internal record ChatEvent(ChatCategory Category, string Detail, uint Item = 0, bool Hq = false,
    string Retainer = "", long Old = 0, long Proposed = 0, double Limit = 0, bool Match = false, long Floor = 0);
internal record ChatLine(ChatEvent Event, string Text);
internal record ChatGroup(string Title, List<ChatLine> Lines);

internal static class ChatSummary
{
    internal static ChatEvent Bait(BaitNotice bait)
        => new(ChatCategory.Bait, $"{bait.Baits.Count} offer(s) from {bait.Baits.Min(b => b.Price):N0} · used {bait.RealLowest:N0}",
            bait.ItemId, bait.IsHq, bait.Retainer, Match: true);

    public static IEnumerable<ChatGroup> Format(IEnumerable<ChatEvent> events)
        => events.GroupBy(e => e.Category).OrderBy(g => g.Key).Select(g => new ChatGroup(g.Key switch
        {
            ChatCategory.Prices => "Price changes",
            ChatCategory.Guards => "Held by guard",
            ChatCategory.Bait => "Bait skipped",
            ChatCategory.Skipped => "Skipped",
            ChatCategory.Restocked => "Restocked",
            _ => "Problems"
        }, g.Select(e => new ChatLine(e, Describe(e))).ToList()));

    private static string Describe(ChatEvent e)
    {
        var prefix = e.Retainer.Length > 0 ? $"[{e.Retainer}] " : "";
        var prices = $"{e.Old:N0} → {e.Proposed:N0}";
        return prefix + (e.Category switch
        {
            ChatCategory.Guards => $"{prices} ({RepriceRules.ChangePercent(e.Old, e.Proposed):+0.##;-0.##;0}%, limit {e.Limit:0.##}%){(e.Floor > 0 ? " · floor" : "")}",
            ChatCategory.Prices => $"{prices} {(e.Proposed < e.Old ? "↓" : "↑")}{e.Detail}",
            _ => e.Detail
        });
    }
}
