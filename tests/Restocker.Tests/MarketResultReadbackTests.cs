using Restocker.Execution;

namespace Restocker.Tests;

public sealed class MarketResultReadbackTests
{
    [Theory]
    [InlineData(0, 0u, true, true)]
    [InlineData(0, 0u, false, false)]
    [InlineData(0, 1u, true, false)]
    [InlineData(1, 0u, true, false)]
    [InlineData(-1, 0u, true, false)]
    public void Requires_the_empty_message_and_no_live_listings(int rows, uint offers, bool hasMessage, bool expected)
        => Assert.Equal(expected, MarketResultReadback.IsEmpty(rows, offers, false, hasMessage));

    [Fact]
    public void An_empty_message_cannot_finish_a_loading_or_different_item_search()
    {
        Assert.False(MarketResultReadback.IsEmpty(0, 0, true, true));
        Assert.False(MarketResultReadback.IsEmpty(0, null, false, true));
    }

    [Fact]
    public void Ignores_layout_and_invisible_characters_in_localized_addon_text()
    {
        Assert.True(MarketResultReadback.MatchesMessage(" No items\r\nfound.\u200b ", "No items found."));
        Assert.False(MarketResultReadback.MatchesMessage("Please wait and try your search again.", "No items found."));
        Assert.False(MarketResultReadback.MatchesMessage("", ""));
    }
}
