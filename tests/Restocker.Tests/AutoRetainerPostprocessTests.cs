using Restocker.Execution;

namespace Restocker.Tests;

public sealed class AutoRetainerPostprocessTests
{
    [Fact]
    public void Ineligible_offer_does_not_claim_a_grant()
    {
        var handoff = new AutoRetainerPostprocess();
        Assert.False(handoff.OnOffer(mayRequest: false));
        Assert.False(handoff.OnGrant(forUs: true, now: 0));
        Assert.False(handoff.TakeFinish());
    }

    [Fact]
    public void Other_plugins_grants_do_not_consume_our_request()
    {
        var handoff = new AutoRetainerPostprocess();
        Assert.True(handoff.OnOffer(mayRequest: true));
        Assert.False(handoff.OnGrant(forUs: false, now: 0));
        Assert.True(handoff.Pending);
        Assert.False(handoff.Active);
        Assert.True(handoff.OnGrant(forUs: true, now: 1));
        Assert.False(handoff.Pending);
    }

    [Fact]
    public void Next_offer_replaces_a_pending_request()
    {
        var handoff = new AutoRetainerPostprocess();
        Assert.True(handoff.OnOffer(mayRequest: true));
        Assert.False(handoff.OnOffer(mayRequest: false));
        Assert.False(handoff.Pending);
        Assert.False(handoff.OnGrant(forUs: true, now: 0));
    }

    [Fact]
    public void Pending_request_can_be_drained_on_dispose()
    {
        var handoff = new AutoRetainerPostprocess();
        Assert.True(handoff.OnOffer(mayRequest: true));
        Assert.False(handoff.TakeFinish());
        Assert.True(handoff.Pending);
        Assert.True(handoff.OnGrant(forUs: true, now: 0));
        Assert.True(handoff.TakeFinish());
        Assert.False(handoff.TakeFinish());
    }

    [Fact]
    public void Active_handoff_blocks_duplicates_and_times_out_from_grant()
    {
        var handoff = new AutoRetainerPostprocess();
        const long grantTime = 5_000;
        Assert.True(handoff.OnOffer(mayRequest: true));
        Assert.False(handoff.TimedOut(grantTime));
        Assert.True(handoff.OnGrant(forUs: true, now: grantTime));
        Assert.False(handoff.OnOffer(mayRequest: true));
        Assert.False(handoff.OnGrant(forUs: true, now: grantTime + 100));
        Assert.False(handoff.TimedOut(grantTime + 599_999));
        Assert.True(handoff.TimedOut(grantTime + 600_000));
        Assert.True(handoff.TakeFinish());
        Assert.False(handoff.Active);
        Assert.False(handoff.TimedOut(grantTime + 600_001));
        Assert.True(handoff.OnOffer(mayRequest: true));
    }
}
