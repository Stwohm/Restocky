using System;
using System.Globalization;
using System.Linq;

namespace Restocker.Execution;

internal enum MarketAttemptDecision { Wait, Retry, NoData }

internal static class MarketResultReadback
{
    internal static MarketAttemptDecision Attempt(int retries, TimeSpan elapsed, bool error)
    {
        if (error) return retries < 2 ? MarketAttemptDecision.Retry : MarketAttemptDecision.NoData;
        return elapsed >= TimeSpan.FromSeconds(4) ? MarketAttemptDecision.NoData : MarketAttemptDecision.Wait;
    }

    internal static bool WaitBeforeClick(TimeSpan pageAge, TimeSpan elapsed, bool retry)
    {
        if (elapsed >= TimeSpan.FromSeconds(4)) return false;
        return retry ? elapsed < TimeSpan.FromSeconds(1) || pageAge < TimeSpan.FromSeconds(1)
            : pageAge < TimeSpan.FromMilliseconds(600);
    }

    internal static bool EmptyItemMatches(uint expected, uint titleItem, uint agentItem, string title)
        => expected != 0 && agentItem == expected
            && (titleItem == expected || (titleItem == 0 && string.IsNullOrWhiteSpace(title)));

    internal static bool IsEmpty(int rows, uint? currentItemOffers, bool waitingForListings, bool hasEmptyMessage,
        bool hasWaitingMessage = false, bool freshHistory = false, TimeSpan elapsed = default)
        => rows == 0 && currentItemOffers == 0 && !waitingForListings
            && (hasEmptyMessage || (hasWaitingMessage && elapsed >= TimeSpan.FromMilliseconds(freshHistory ? 1200 : 2500)));

    internal static bool MatchesMessage(string actual, string expected)
        => expected.Length > 0 && string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);

    private static string Normalize(string text)
        => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
            && char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.PrivateUse)));
}
