using System.IO;
using System.Linq;
using System.Text.Json;
using Restocker.Common;
using Restocker.Market;

namespace Restocker.Tests;

public sealed class BatchUiTests
{
    [Fact]
    public void Chat_groups_only_present_categories_and_keeps_each_review_target()
    {
        var first = new ChatEvent(ChatCategory.Guards, "", 42, true, "RetainerA", 12000, 16500, 30, true);
        var second = first with { Retainer = "RetainerB", Proposed = 4500 };
        var groups = ChatSummary.Format([first, new(ChatCategory.Problems, "Readback failed"), second]).ToList();
        Assert.Equal(2, groups.Count);
        var lines = groups[0].Lines;
        Assert.Equal(2, lines.Count);
        Assert.Equal(first, lines[0].Event);
        Assert.Equal(second, lines[1].Event);
        Assert.Contains(16500.ToString("N0"), lines[0].Text);
        Assert.Contains(37.5.ToString("+0.##") + "%", lines[0].Text);
        Assert.Contains("limit 30%", lines[0].Text);
        Assert.True(lines.All(line => line.Event.Match));
        Assert.Empty(ChatSummary.Format([]));
    }

    [Theory]
    [InlineData(76, true, true)]
    [InlineData(76, false, false)]
    [InlineData(75, true, false)]
    public void Floor_raise_requires_review_of_the_actual_floor(long reviewed, bool floorLink, bool applies)
    {
        var decision = RepriceRules.Decide(56, [57], 76, new(), reviewedPrice: reviewed, reviewedFloor: floorLink);
        Assert.Equal(applies, decision.Apply);
        Assert.Equal(applies, decision.MatchedPrice);
        Assert.Equal(76, decision.NewPrice);
    }

    [Fact]
    public void Stats_split_mixed_sales_once_and_filter_by_sale_date()
    {
        var day = DateTime.UnixEpoch;
        var mixed = new ListingRecord { Outcome = ListingTracker.Sold, ItemId = 1, ItemName = "Sand",
            RetainerName = "RetainerA", Quantity = 10, ConsumedCost = 400, UnknownCostUnits = 6,
            SoldTotalGil = 950, SaleTimeUtc = day, LastSeenUtc = day.AddDays(-1) };
        var gathered = new ListingRecord { Outcome = ListingTracker.Sold, ItemId = 1, ItemName = "Sand",
            RetainerName = "RetainerB", Quantity = 2, SoldTotalGil = 190, LastSeenUtc = day };
        var excluded = new ListingRecord { Outcome = ListingTracker.Sold, SaleTimeUtc = day.AddDays(1), SoldTotalGil = 5000 };
        var missing = new ListingRecord { Outcome = ListingTracker.Sold, ItemId = 2,
            RetainerName = "RetainerA", Quantity = 1, EndedUtc = day };
        var stats = SaleStats.Aggregate([mixed, gathered, excluded, missing, new()], day, day.AddDays(1));
        Assert.Equal(1140, stats.Total.Net);
        Assert.Equal(-20, stats.Total.Profit);
        Assert.Equal(760, stats.Total.Earning);
        Assert.Equal(13, stats.Total.Units);
        Assert.Equal(1, stats.Total.Incomplete);
        var item = stats.Items.Single(r => r.Name == "Sand");
        Assert.Equal("Flip + Gather", item.Total.Kind);
        Assert.Equal(12, item.Total.Units);
        Assert.Equal(2, stats.Retainers.Count);
        Assert.Equal(stats.Total.Profit, stats.Retainers.Sum(r => r.Total.Profit));
        Assert.Equal(stats.Total.Earning, stats.Items.Sum(r => r.Total.Earning));
    }

    [Fact]
    public void Log_rotation_preserves_the_boundary_and_survives_reload()
    {
        var path = Path.GetTempFileName();
        try
        {
            var now = DateTime.UtcNow.AddHours(1);
            var cutoff = now.AddDays(-14);
            File.WriteAllLines(path, new[] { cutoff.AddTicks(-1), cutoff, now }.Select(time =>
                JsonSerializer.Serialize(new ActivityEntry(time, "Sale", "Sand"))));
            var log = new ActivityLog(path, Assert.Fail);
            log.Prune(now);
            Assert.Equal(new[] { cutoff, now }, log.Entries.Select(e => e.Utc));
            log.Add("Cycle", "Started");
            Assert.Equal(log.Entries.ToArray(), new ActivityLog(path, Assert.Fail).Entries.ToArray());
        }
        finally { File.Delete(path); }
    }
}
