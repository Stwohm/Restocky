using Restocker.Execution;

namespace Restocker.Tests;

public sealed class ListingRetryTests
{
    [Theory]
    [InlineData(true, false, 424, true)]
    [InlineData(true, true, 424, false)]
    [InlineData(false, false, 424, false)]
    [InlineData(true, false, 325, false)]
    [InlineData(true, false, 425, false)]
    [InlineData(true, false, null, false)]
    public void Only_first_timeout_with_verified_unchanged_source_can_retry(bool timeout, bool retried, int? observed, bool expected)
        => Assert.Equal(expected, ListingRetry.CanRetry(timeout, retried, 424, observed));
}
