using Restocker.Execution;

namespace Restocker.Tests;

public class ContextMenuRetryPolicyTests
{
    [Fact]
    public void Success_restores_the_full_retry_budget()
    {
        var policy = new ContextMenuRetryPolicy();
        Assert.Equal(ContextMenuRetryDecision.Retry, policy.OnEntryMissing());

        policy.Reset();

        Assert.Equal(0, policy.RetryCount);
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
    }

    [Fact]
    public void Two_retries_then_skip_and_reset_the_row_retry_count()
    {
        var policy = new ContextMenuRetryPolicy();

        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(0, policy.RetryCount);
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
    }

    [Fact]
    public void Consecutive_failed_rows_each_skip_and_leave_the_next_row_runnable()
    {
        var policy = new ContextMenuRetryPolicy();

        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(ContextMenuRetryDecision.Retry, policy.OnEntryMissing());
    }

    [Fact]
    public void Success_between_failed_rows_starts_a_fresh_retry_budget()
    {
        var policy = new ContextMenuRetryPolicy();
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));

        policy.Reset();

        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
        Assert.Equal(ContextMenuRetryDecision.Skip, ExhaustRetries(policy));
    }

    private static ContextMenuRetryDecision ExhaustRetries(ContextMenuRetryPolicy policy)
    {
        Assert.Equal(ContextMenuRetryDecision.Retry, policy.OnEntryMissing());
        Assert.Equal(1, policy.RetryCount);
        Assert.Equal(ContextMenuRetryDecision.Retry, policy.OnEntryMissing());
        Assert.Equal(2, policy.RetryCount);
        return policy.OnEntryMissing();
    }
}
