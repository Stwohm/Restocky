using Restocker.Market;

namespace Restocker.Tests;

public class MarketComparisonTests
{
    [Theory]
    [InlineData(599, 3999, false, true)]
    [InlineData(600, 0, false, false)]
    [InlineData(1, 4000, false, true)]
    [InlineData(2000, 999, true, true)]
    [InlineData(999, 1500, true, true)]
    [InlineData(1199, 1500, true, true)]
    [InlineData(1200, 1000, true, false)]
    public void Waits_for_quiet_before_click(int pageAge, int elapsed, bool retry, bool wait)
        => Assert.Equal(wait, MarketResultReadback.WaitBeforeClick(TimeSpan.FromMilliseconds(pageAge),
            TimeSpan.FromMilliseconds(elapsed), retry));

    [Theory]
    [InlineData(799, 600, true)]
    [InlineData(1500, 599, true)]
    [InlineData(1500, 600, false)]
    [InlineData(3000, 100, false)]
    public void DuplicateDrainObservesTheRowCooldownLastPageAndCap(int elapsed, int quiet, bool wait)
        => Assert.Equal(wait, MarketResultReadback.WaitForDuplicate(TimeSpan.FromMilliseconds(elapsed),
            TimeSpan.FromMilliseconds(quiet), TimeSpan.FromMilliseconds(800)));

    [Fact]
    public void LarimarNeedsTwoSettledWaitingResults()
    {
        var elapsed = TimeSpan.FromSeconds(2.5);
        Assert.Equal(MarketAttemptDecision.Retry, Decide(elapsed, waiting: true));
        Assert.Equal(MarketAttemptDecision.Empty, Decide(elapsed, waiting: true, previous: true));
        Assert.Equal(MarketAttemptDecision.Retry, Decide(elapsed, waiting: true, previous: true, overlap: true));
    }

    [Theory]
    [InlineData(8u, 950)] // Fire Crystal
    [InlineData(50973u, 350)] // Polish
    public void ForeignPageRetriesThenAcceptsMatchingOffers(uint item, int age)
    {
        var stream = new OfferingStream();
        var start = DateTime.UtcNow;
        stream.Observe(50971, start.AddMilliseconds(-age));
        Assert.Equal(MarketAttemptDecision.Retry, Decide(TimeSpan.FromSeconds(1),
            empty: true, overlap: stream.Overlaps(item, start)));
        stream.Observe(item, start.AddSeconds(2));
        var cache = new MarketCache { Clock = () => start.AddSeconds(2) };
        cache.Replace(item, new MarketScan([new MarketOffer(50, "Rival", 1)], []));
        Assert.Equal(MarketAttemptDecision.Offers, Decide(TimeSpan.Zero,
            offers: cache.GetScan(item)?.UpdatedUtc >= start, overlap: stream.Overlaps(item, start)));
        Assert.Equal(50, cache.GetLowest(item, false, true));
    }

    [Fact]
    public void ExplicitEmptyMessageFinishesFirstAttempt()
        => Assert.Equal(MarketAttemptDecision.Empty, Decide(TimeSpan.FromSeconds(1), empty: true));

    private static MarketAttemptDecision Decide(TimeSpan elapsed, bool waiting = false, bool previous = false,
        bool overlap = false, bool empty = false, bool offers = false)
        => MarketResultReadback.Decide(0, elapsed, offers, overlap,
            MarketResultReadback.EmptyEvidence(0, 0, false, empty, waiting), previous);

    [Fact]
    public void LatePagesRepairBothQualitiesAndIgnoreRepeatedListings()
    {
        var repair = new LateOfferings();
        var nq = new MarketOffer(10, "Rival", 1);
        var hq = nq with { IsHQ = true };
        Assert.NotNull(repair.Merge(3, [], 8)); // The first late page contained only our retainers.
        Assert.NotNull(repair.Merge(4, [(1, nq)], 8));
        Assert.Null(repair.Merge(4, [(1, nq)], 8));
        Assert.False(repair.Accepts(5));
        Assert.Null(repair.Merge(5, [(3, hq)], 8));
        var scan = repair.Merge(4, [(1, nq), (2, hq)], 8)!;
        Assert.Equal(nq, Assert.Single(scan.Competitors(false, true)));
        Assert.Equal(hq, Assert.Single(scan.Competitors(true, true)));
    }

    [Fact]
    public void MatchingPageCannotHideForeignStreamDuringAutoOpenedSearch()
    {
        var pages = new OfferingStream();
        var start = DateTime.UtcNow;
        pages.Observe(50971, start.AddMilliseconds(-599));
        pages.Observe(50973, start.AddMilliseconds(100));
        Assert.True(pages.Overlaps(50973, start));
        Assert.False(pages.Overlaps(50973, start.AddSeconds(1)));
        pages.Observe(50971, start.AddSeconds(2));
        Assert.True(pages.Overlaps(50973, start));
    }

    [Theory]
    [InlineData(0, 4000, true, (int)MarketAttemptDecision.Retry)]
    [InlineData(1, 1200, true, (int)MarketAttemptDecision.Retry)]
    [InlineData(0, 999, true, (int)MarketAttemptDecision.Wait)]
    [InlineData(2, 999, true, (int)MarketAttemptDecision.Wait)]
    [InlineData(2, 1000, true, (int)MarketAttemptDecision.NoData)]
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
