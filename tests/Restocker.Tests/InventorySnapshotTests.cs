using System.Collections.Generic;
using Restocker.Data;

namespace Restocker.Tests;

public sealed class InventorySnapshotTests
{
    private static readonly DateTime Old = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Old.AddDays(3);

    [Fact]
    public void Updates_only_loaded_containers_including_empty_ones()
    {
        var kept = new InventoryEntry { ContainerId = 1, ItemId = 5121, Quantity = 48 };
        var cleared = new InventoryEntry { ContainerId = 2, ItemId = 5121, Quantity = 44 };
        var read = new InventoryEntry { ContainerId = 3, ItemId = 5121, Quantity = 4 };
        var timestamps = new Dictionary<uint, DateTime> { [1] = Old, [2] = Old };
        var loaded = new Dictionary<uint, List<InventoryEntry>> { [2] = [], [3] = [read] };

        var snapshot = InventorySnapshot.Refresh([kept, cleared], timestamps, loaded, Now);

        Assert.Equal(new[] { kept, read }, snapshot);
        Assert.Equal(Old, timestamps[1]);
        Assert.Equal(Now, timestamps[2]);
        Assert.Equal(Now, timestamps[3]);
        Assert.Equal(Old, InventorySnapshot.OldestRefresh([1u, 2u, 3u], timestamps, Now));
        Assert.Equal(DateTime.MinValue, InventorySnapshot.OldestRefresh([3u, 4u], timestamps, Now));
    }

    [Fact]
    public void Retains_legacy_stock_without_inventing_a_container_read()
    {
        var previous = new InventoryEntry { ContainerId = 1, ItemId = 5121, Quantity = 48 };
        var timestamps = new Dictionary<uint, DateTime>();
        var snapshot = InventorySnapshot.Refresh([previous], timestamps,
            new Dictionary<uint, List<InventoryEntry>>(), Now);

        Assert.Same(previous, Assert.Single(snapshot));
        Assert.Empty(timestamps);
        Assert.Equal(Old, InventorySnapshot.OldestRefresh([1u], null, Old));
        Assert.Equal(DateTime.MinValue, InventorySnapshot.OldestRefresh([1u], new Dictionary<uint, DateTime>(), Now));
    }
}
