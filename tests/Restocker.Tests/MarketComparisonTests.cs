using Restocker.Execution;
using Restocker.Market;

namespace Restocker.Tests;

public class MarketComparisonTests
{
    [Theory]
    [InlineData(599, 3999, false, true)]
    [InlineData(600, 0, false, false)]
    [InlineData(1, 4000, false, false)]
    [InlineData(2000, 999, true, true)]
    [InlineData(999, 1500, true, true)]
    [InlineData(1000, 1000, true, false)]
    public void Waits_for_quiet_before_click(int pageAge, int elapsed, bool retry, bool wait)
        => Assert.Equal(wait, MarketResultReadback.WaitBeforeClick(TimeSpan.FromMilliseconds(pageAge),
            TimeSpan.FromMilliseconds(elapsed), retry));

    [Theory]
    [InlineData(true, 1199, false)]
    [InlineData(true, 1200, true)]
    [InlineData(false, 2499, false)]
    [InlineData(false, 2500, true)]
    public void Waiting_message_needs_time_and_matching_empty_proxy(bool history, int ms, bool empty)
    {
        var elapsed = TimeSpan.FromMilliseconds(ms);
        Assert.Equal(empty, MarketResultReadback.IsEmpty(0, 0, false, false, true, history, elapsed));
        Assert.False(MarketResultReadback.IsEmpty(0, null, false, false, true, history, elapsed));
        Assert.False(MarketResultReadback.IsEmpty(0, 0, true, false, true, history, elapsed));
        Assert.False(MarketResultReadback.IsEmpty(1, 0, false, false, true, history, elapsed));
        Assert.False(MarketResultReadback.IsEmpty(0, 0, false, false, false, history, elapsed));
    }

    [Theory]
    [InlineData(0, 4000, true, (int)MarketAttemptDecision.Retry)]
    [InlineData(1, 1200, true, (int)MarketAttemptDecision.Retry)]
    [InlineData(2, 200, true, (int)MarketAttemptDecision.NoData)]
    [InlineData(2, 3999, false, (int)MarketAttemptDecision.Wait)]
    [InlineData(0, 4000, false, (int)MarketAttemptDecision.NoData)]
    public void Error_retries_and_timeout_are_per_attempt(int retries, int elapsed, bool error, int expected)
        => Assert.Equal(expected, (int)MarketResultReadback.Attempt(retries, TimeSpan.FromMilliseconds(elapsed), error));

    [Theory]
    [InlineData(42u, 42u, "", true)]
    [InlineData(0u, 42u, "", true)]
    [InlineData(0u, 42u, "unknown title", false)]
    [InlineData(43u, 42u, "", false)]
    [InlineData(42u, 43u, "", false)]
    [InlineData(42u, 0u, "", false)]
    public void Empty_search_requires_consistent_item_identity(uint titleItem, uint agentItem, string title, bool expected)
        => Assert.Equal(expected, MarketResultReadback.EmptyItemMatches(42, titleItem, agentItem, title));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Undercuts_equal_rivals_by_one(int rivals)
    {
        var prices = new long[rivals];
        Array.Fill(prices, 275);
        var decision = RepriceRules.Decide(275, prices, 0, new());
        Assert.True(decision.Apply);
        Assert.Equal(274, decision.NewPrice);
    }
}
