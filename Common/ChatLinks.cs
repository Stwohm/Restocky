using System.Globalization;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;

namespace Restocker.Common;

internal static class ChatLinks
{
    /// <summary>
    /// Builds a [Match] price link as a typed <see cref="DalamudLinkPayload"/>.
    /// Chat 2 only renders typed link payloads as clickable; the same bytes wrapped in a
    /// <see cref="RawPayload"/> show as plain text (0.3.16.0 regression).
    /// </summary>
    internal static DalamudLinkPayload? MatchPriceLink(uint commandId, string plugin, uint item, bool floorReviewed, long proposed)
    {
        var encoded = new Lumina.Text.SeStringBuilder().PushDalamudLink(commandId, plugin, checked((int)item),
            floorReviewed ? 1 : 0, proposed.ToString(CultureInfo.InvariantCulture));
        return encoded.ToReadOnlySeString().Single().TryReadDalamudLink(out var link) ? link : null;
    }
}
