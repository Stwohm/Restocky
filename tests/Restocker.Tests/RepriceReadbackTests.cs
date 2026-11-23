using Restocker.Execution;

namespace Restocker.Tests;

public class RepriceReadbackTests
{
    [Fact]
    public void A_slow_or_replaced_slot_cannot_confirm_a_write()
    {
        Assert.Equal(RepriceReadback.Waiting, RepriceReadbackCheck.Check(120, 100, TimeSpan.FromSeconds(2), true));
        Assert.Equal(RepriceReadback.Unconfirmed, RepriceReadbackCheck.Check(120, 100, TimeSpan.FromSeconds(3), true));
        Assert.Equal(RepriceReadback.Unconfirmed, RepriceReadbackCheck.Check(120, null, TimeSpan.FromSeconds(3), true));
    }

    [Fact]
    public void Stop_settles_only_a_write_already_visible_in_its_verified_slot()
    {
        Assert.Equal(RepriceReadback.Unconfirmed, RepriceReadbackCheck.Check(120, null, TimeSpan.Zero, false));
        Assert.Equal(RepriceReadback.Confirmed, RepriceReadbackCheck.Check(120, 120, TimeSpan.Zero, false));
    }
}
