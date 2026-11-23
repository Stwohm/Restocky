using Restocker.Execution;

namespace Restocker.Tests;

public sealed class TopUpReadbackTests
{
    [Fact]
    public void Delayed_price_confirms_only_when_it_arrives()
    {
        foreach (var read in new[] { 0, 0, 638, 639 })
            Assert.Equal(read == 639, TopUpReadback.Confirmed(true, 92, 92, read, 639, 92));
    }

    [Theory]
    [InlineData(true, 92, 639, 92, true)]
    [InlineData(true, 92, 0, 92, false)]
    [InlineData(true, 92, 638, 92, false)]
    [InlineData(true, 92, 639, 0, false)]
    [InlineData(true, 23, 639, 92, false)]
    [InlineData(false, 92, 639, 92, false)]
    public void Requires_the_relisted_stack_bag_decrement_and_verified_price(
        bool matchingItem, int quantity, long price, int bagDecrement, bool expected)
        => Assert.Equal(expected, TopUpReadback.Confirmed(matchingItem, quantity, 92, price, 639, bagDecrement));
}
