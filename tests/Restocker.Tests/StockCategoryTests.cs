using System.Linq;
using System.Text.Json;
using Restocker.Plan;

namespace Restocker.Tests;

public sealed class StockCategoryTests
{
    [Fact]
    public void Legacy_watch_entries_load_uncategorized_and_keep_kind_and_settings()
    {
        const string legacy = """{"LowStock":[{"ItemId":42,"Kind":1,"Enabled":false,"Note":"keep","Target":99}]}""";
        var config = JsonSerializer.Deserialize<StockTrackingConfiguration>(legacy)!;
        var entry = Assert.Single(config.LowStock);
        Assert.Empty(config.StockCategories.Ordered);
        Assert.Equal(Guid.Empty, entry.CategoryId);
        Assert.Equal(StockKind.Flip, entry.Kind);
        Assert.Equal("keep", entry.Note);
        Assert.False(entry.Enabled);
        Assert.Equal(99, entry.Target);
    }

    [Fact]
    public void Categories_round_trip_in_order_and_delete_only_clears_assignments()
    {
        var config = new StockTrackingConfiguration();
        var categories = config.StockCategories;
        Assert.True(categories.Create("Flips"));
        Assert.True(categories.Create("Crafts"));
        Assert.False(categories.Create(" flips "));
        Assert.False(categories.Create("Uncategorized"));
        var first = categories.Ordered.First();
        var second = categories.Ordered.Last();
        config.LowStock.AddRange([new() { ItemId = 42, CategoryId = first.Id }, new() { ItemId = 13, CategoryId = second.Id }]);
        first.Collapsed = true;
        categories.UncategorizedCollapsed = true;
        Assert.False(categories.Rename(first.Id, "Crafts"));
        Assert.True(categories.Rename(first.Id, "Resale"));
        config = JsonSerializer.Deserialize<StockTrackingConfiguration>(JsonSerializer.Serialize(config))!;
        categories = config.StockCategories;
        Assert.Equal(new[] { "Resale", "Crafts" }, categories.Ordered.Select(c => c.Name));
        Assert.True(categories.UncategorizedCollapsed && categories.Ordered.First().Collapsed);
        Assert.Equal(first.Id, config.LowStock.First().CategoryId);
        categories.Delete(first.Id, config.LowStock);
        Assert.Equal(second.Id, Assert.Single(categories.Ordered).Id);
        Assert.Equal(new[] { Guid.Empty, second.Id }, config.LowStock.Select(e => e.CategoryId));
        Assert.Equal(new uint[] { 42, 13 }, config.LowStock.Select(e => e.ItemId));
    }
}
