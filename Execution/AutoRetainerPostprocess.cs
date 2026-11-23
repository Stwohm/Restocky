using System;

namespace Restocker.Execution;

/// <summary>Tracks the request separately from the handoff, since only a grant owns AutoRetainer's lock.</summary>
internal sealed class AutoRetainerPostprocess
{
    private static readonly long TimeoutMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private long? deadline;

    public bool Pending { get; private set; }
    public bool Active => deadline.HasValue;
    public bool FinishPending { get; private set; }

    public bool OnOffer(bool mayRequest)
    {
        // AutoRetainer clears the previous request list before announcing each retainer.
        Pending = mayRequest && !Active;
        return Pending;
    }

    public bool OnGrant(bool forUs, long now)
    {
        if (!forUs || !Pending || Active) return false;
        Pending = false;
        deadline = now + TimeoutMs;
        return true;
    }

    public bool TimedOut(long now) => deadline is { } end && now >= end;

    public bool TakeFinish()
    {
        if (!Active) return false;
        deadline = null;
        return true;
    }

    // AR's finish signal has no owner argument. Retry only until a new offer/grant.
    public void Finish(Action signal)
    {
        if (!TakeFinish() && !FinishPending) return;
        FinishPending = true;
        signal();
        FinishPending = false;
    }

    public void ForgetFinish() => FinishPending = false;
}
