namespace Restocker.Execution;

internal static class ListingRetry
{
    internal static bool CanRetry(bool timedOut, bool alreadyRetried, int before, int? observed)
        => timedOut && !alreadyRetried && before > 0 && observed == before;
}
