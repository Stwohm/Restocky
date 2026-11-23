namespace Restocker.Execution;

public enum ContextMenuRetryDecision
{
    Retry,
    Skip,
}

/// <summary>Counts adjust-price menu retries for the current display row.</summary>
public sealed class ContextMenuRetryPolicy
{
    public const int MaxRetries = 2;

    public int RetryCount { get; private set; }

    public ContextMenuRetryDecision OnEntryMissing()
    {
        if (RetryCount < MaxRetries)
        {
            RetryCount++;
            return ContextMenuRetryDecision.Retry;
        }

        RetryCount = 0;
        return ContextMenuRetryDecision.Skip;
    }

    public void Reset()
    {
        RetryCount = 0;
    }
}
