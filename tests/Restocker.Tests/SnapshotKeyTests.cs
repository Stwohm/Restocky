using Restocker.Data;

namespace Restocker.Tests;

public class SnapshotKeyTests
{
    [Fact]
    public void RetainerSnapshot_MakeKey_is_hex_dotted_pair()
    {
        var key = RetainerSnapshot.MakeKey(0xABCDEF, 0x1234);
        Assert.Equal("ABCDEF.1234", key);
    }

    [Fact]
    public void RetainerSnapshot_MakeKey_supports_64bit_ids()
    {
        var key = RetainerSnapshot.MakeKey(0x12345678ABCDEFUL, 0xABCDEF12UL);
        Assert.Equal("12345678ABCDEF.ABCDEF12", key);
    }

    [Theory]
    [InlineData(3, 42u, true)]
    [InlineData(2, 43u, true)]
    [InlineData(2, 42u, false)]
    public void RetainerSnapshot_reprice_requires_matching_slot_item_and_quality(int slot, uint itemId, bool isHq)
    {
        var listing = new ListingEntry { ListingIndex = 2, ItemId = 42, IsHQ = true, UnitPrice = 100 };
        var snapshot = new RetainerSnapshot { Listings = [listing] };

        Assert.False(snapshot.TryUpdateListingPrice(slot, itemId, isHq, 89));
        Assert.Equal(100L, listing.UnitPrice);
    }

    [Fact]
    public void CharacterSnapshot_MakeKey_uses_char_prefix()
    {
        Assert.Equal("char.ABCDEF", CharacterSnapshot.MakeKey(0xABCDEF));
    }

    [Fact]
    public void CharacterSnapshot_MakeKey_zero_id_yields_well_formed_string()
    {
        // Defensive: even if the content id ever resolves to 0, the key is still parseable.
        Assert.Equal("char.0", CharacterSnapshot.MakeKey(0));
    }
}
