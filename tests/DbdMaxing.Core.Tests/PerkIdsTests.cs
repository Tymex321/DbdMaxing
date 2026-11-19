namespace DbdMaxing.Core.Tests;

public class PerkIdsTests
{
    [Theory]
    [InlineData("Barbecue & Chili", "barbecue-chili")]
    [InlineData("Scourge Hook: Pain Resonance", "scourge-hook-pain-resonance")]
    [InlineData("Hex: No One Escapes Death", "hex-no-one-escapes-death")]
    [InlineData("We'll Make It", "well-make-it")]
    [InlineData("Déjà Vu", "deja-vu")]
    public void Slug_normalizes_names(string name, string expected) =>
        Assert.Equal(expected, PerkIds.Slug(name));
}
