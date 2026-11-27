using System.Collections.Generic;
using Restocker.Market;

namespace Restocker.Tests;

public class ManualFloorTests
{
    [Theory]
    [InlineData("50", 50)]
    [InlineData("200", 200)]
    [InlineData("100", 100)]
    public void Committed_override_applies_to_both_qualities_until_cleared(string text, long expected)
    {
        var floors = new Dictionary<uint, long>();
        Assert.True(BreakEvenFloor.CommitManual(floors, 8, text));
        foreach (var cost in new[] { 95m, 114m })
            Assert.Equal((expected, false), BreakEvenFloor.Resolve(floors[8], cost, 5, 0));
        Assert.True(BreakEvenFloor.CommitManual(floors, 8, "0"));
        Assert.Equal((100L, true), BreakEvenFloor.Resolve(floors.GetValueOrDefault(8u), 95, 5, 0));
        Assert.Equal((120L, true), BreakEvenFloor.Resolve(floors.GetValueOrDefault(8u), 114, 5, 0));
        floors[8] = expected;
        Assert.True(BreakEvenFloor.CommitManual(floors, 8, ""));
        Assert.Empty(floors);
    }
}
