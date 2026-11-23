using System;
using System.Globalization;
using System.Linq;

namespace Restocker.Execution;

internal static class MarketResultReadback
{
    internal static bool IsEmpty(int rows, uint? currentItemOffers, bool waitingForListings, bool hasEmptyMessage)
        => rows == 0 && currentItemOffers == 0 && !waitingForListings && hasEmptyMessage;

    internal static bool MatchesMessage(string actual, string expected)
        => expected.Length > 0 && string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);

    private static string Normalize(string text)
        => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
            && char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.PrivateUse)));
}
