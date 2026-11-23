using System;

namespace Restocker.Execution;

internal enum RepriceReadback { Waiting, Confirmed, Unconfirmed }

internal static class RepriceReadbackCheck
{
    internal static RepriceReadback Check(long requested, long? live, TimeSpan elapsed, bool running)
    {
        if (live == requested) return RepriceReadback.Confirmed;
        return running && elapsed < TimeSpan.FromSeconds(3) ? RepriceReadback.Waiting : RepriceReadback.Unconfirmed;
    }
}
