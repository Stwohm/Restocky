using Restocker.Market;

namespace Restocker.Tests;

public class PriceMatchOverridesTests
{
    [Theory]
    [InlineData(439, 200, 190, true)]
    [InlineData(439, 200, 189, false)]
    [InlineData(100, 200, 210, true)]
    [InlineData(100, 200, 211, false)]
    [InlineData(439, 200, 250, true)]
    [InlineData(439, 200, 600, false)] // reviewed a drop, market now calls for a raise
    public void Guard_uses_distance_from_current_and_five_percent_of_reviewed_target(
        long current, long reviewed, long target, bool permitted)
    {
        var decision = RepriceRules.Decide(current, [target + 1], 0, new(), reviewedPrice: reviewed);
        Assert.Equal(permitted, decision.Apply);
        Assert.Equal(permitted, decision.MatchedPrice);
    }

    [Fact]
    public void Matches_filtered_price_and_preserves_floor()
    {
        var decision = RepriceRules.Decide(900, [10, 275, 280, 290], 300, new(), reviewedPrice: 300);
        Assert.True(decision.MatchedPrice);
        Assert.False(decision.MatchedBait);
        Assert.Equal(275, decision.LowestCompetitor);
        Assert.Equal(300, decision.NewPrice);
    }

    [Fact]
    public void Notices_deduplicate_across_retainers_and_reoffer_after_a_rejected_review()
    {
        var matches = new PriceMatchOverrides();
        Assert.True(matches.ShouldReport(42, false, RepriceReason.MaxDrop, 190));
        Assert.False(matches.ShouldReport(42, false, RepriceReason.MaxDrop, 190));
        Assert.True(matches.ShouldReport(42, true, RepriceReason.MaxDrop, 190));
        Assert.True(matches.ShouldReport(42, false, RepriceReason.MaxRaise, 190));
        Assert.True(matches.ShouldReport(42, false, RepriceReason.MaxDrop, 190, rejectedReview: 250));
        Assert.False(matches.ShouldReport(42, false, RepriceReason.MaxDrop, 190, rejectedReview: 250));
    }

    [Fact]
    public void Clicking_the_same_price_again_during_apply_queues_the_next_cycle()
    {
        var matches = new PriceMatchOverrides();
        matches.Queue(42, false, 274);
        matches.BeginCycle();
        matches.Queue(42, false, 274);
        matches.Consume(42, false);
        matches.BeginCycle();
        Assert.Equal(274, matches.ForPlan(42, false));
    }

    [Fact]
    public void Cycle_freezes_price_and_consumes_only_the_used_click()
    {
        var matches = new PriceMatchOverrides();
        matches.Queue(42, true, 200);
        matches.BeginCycle();
        matches.Queue(42, true, 250);
        Assert.Equal(200, matches.ForPlan(42, true));
        Assert.Null(matches.ForPlan(42, false));
        matches.Consume(42, true);
        Assert.Equal(200, matches.ForPlan(42, true));
        matches.BeginCycle();
        Assert.Equal(250, matches.ForPlan(42, true));
        matches.Consume(42, true);
        matches.BeginCycle();
        Assert.Null(matches.ForPlan(42, true));
    }
}
