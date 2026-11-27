using System;
using System.Globalization;
using System.Linq;

namespace Restocker.Market;

internal enum MarketAttemptDecision { Wait, Retry, NoData, Empty, Offers }

internal enum EmptyBoardEvidence { None, Explicit, Uncertain }

internal static class MarketResultReadback
{
    internal static readonly TimeSpan MessageSettle = TimeSpan.FromSeconds(1);

    internal static MarketAttemptDecision Decide(int retries, TimeSpan elapsed, bool offers, bool unreliable,
        EmptyBoardEvidence evidence, bool previousUncertain)
    {
        if (offers) return MarketAttemptDecision.Offers;
        if (unreliable) return Attempt(retries, elapsed, true);
        if (evidence == EmptyBoardEvidence.Explicit && elapsed >= MessageSettle) return MarketAttemptDecision.Empty;
        if (evidence == EmptyBoardEvidence.Uncertain && elapsed >= TimeSpan.FromSeconds(2.5))
            return previousUncertain ? MarketAttemptDecision.Empty : Attempt(retries, elapsed, true);
        return Attempt(retries, elapsed, false);
    }

    internal static MarketAttemptDecision Attempt(int retries, TimeSpan elapsed, bool error)
    {
        // Offers arrive within a second. A message before that can still be replaced by results.
        if (error && elapsed >= MessageSettle) return retries < 2 ? MarketAttemptDecision.Retry : MarketAttemptDecision.NoData;
        return elapsed >= TimeSpan.FromSeconds(4) ? MarketAttemptDecision.NoData : MarketAttemptDecision.Wait;
    }

    internal static bool WaitBeforeClick(TimeSpan pageAge, TimeSpan elapsed, bool retry)
    {
        return retry ? elapsed < TimeSpan.FromSeconds(1) || pageAge < TimeSpan.FromMilliseconds(1200)
            : pageAge < TimeSpan.FromMilliseconds(600);
    }

    internal static bool EmptyItemMatches(uint expected, uint titleItem, uint agentItem, string title)
        => expected != 0 && agentItem == expected
            && (titleItem == expected || (titleItem == 0 && string.IsNullOrWhiteSpace(title)));

    internal static EmptyBoardEvidence EmptyEvidence(int rows, uint? currentItemOffers, bool loading,
        bool emptyMessage, bool waitingMessage)
    {
        if (rows != 0 || currentItemOffers != 0 || loading) return EmptyBoardEvidence.None;
        if (emptyMessage) return EmptyBoardEvidence.Explicit;
        return waitingMessage ? EmptyBoardEvidence.Uncertain : EmptyBoardEvidence.None;
    }

    internal static bool WaitForDuplicate(TimeSpan elapsed, TimeSpan quiet, TimeSpan rowCooldown)
        => elapsed < rowCooldown || (elapsed < TimeSpan.FromSeconds(3) && quiet < TimeSpan.FromMilliseconds(600));

    internal static bool MatchesMessage(string actual, string expected)
        => expected.Length > 0 && string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);

    private static string Normalize(string text)
        => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
            && char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.PrivateUse)));
}
