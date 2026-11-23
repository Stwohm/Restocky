using Restocker.Market;

namespace Restocker.Tests;

public class BaitMatchOverridesTests
{
    [Fact]
    public void Chat_link_ids_identify_item_and_quality_independently_of_registration_order()
    {
        Assert.NotEqual(BaitMatchOverrides.LinkId(42, false), BaitMatchOverrides.LinkId(42, true));
        Assert.NotEqual(BaitMatchOverrides.LinkId(42, true), BaitMatchOverrides.LinkId(43, true));
        Assert.Equal(0x80000055u, BaitMatchOverrides.LinkId(42, true));
    }

    [Fact]
    public void Queued_during_cycle_applies_to_unplanned_item_and_stays_active_after_consumption()
    {
        var matches = new BaitMatchOverrides();
        matches.BeginCycle();
        matches.Queue(1, true);
        matches.Queue(1, true);

        Assert.False(matches.IncludeBaitForPlan(1, false));
        Assert.False(matches.IncludeBaitForPlan(2, true));
        Assert.True(matches.IncludeBaitForPlan(1, true));
        matches.Consume(1, true);
        Assert.True(matches.IncludeBaitForPlan(1, true));

        matches.BeginCycle();
        Assert.False(matches.IncludeBaitForPlan(1, true));
    }

    [Fact]
    public void Late_click_waits_for_next_cycle_and_normal_reprice_does_not_consume_it()
    {
        var matches = new BaitMatchOverrides();
        matches.BeginCycle();
        Assert.False(matches.IncludeBaitForPlan(1, false));
        matches.Queue(1, false);
        matches.Consume(1, false);
        Assert.False(matches.IncludeBaitForPlan(1, false));

        matches.BeginCycle();
        Assert.True(matches.IncludeBaitForPlan(1, false));
    }

    [Fact]
    public void Unconfirmed_match_stays_queued_and_a_new_click_after_success_survives_later_copies()
    {
        var matches = new BaitMatchOverrides();
        matches.Queue(1, false);
        matches.BeginCycle();
        Assert.True(matches.IncludeBaitForPlan(1, false));

        matches.BeginCycle();
        Assert.True(matches.IncludeBaitForPlan(1, false));
        matches.Consume(1, false);
        matches.Queue(1, false);
        matches.Consume(1, false);

        matches.BeginCycle();
        Assert.True(matches.IncludeBaitForPlan(1, false));
    }
}
