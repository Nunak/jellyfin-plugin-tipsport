using Jellyfin.Plugin.Tipsport;
using SkiaSharp;
using Xunit;
namespace Tipsport.Tests;
public sealed class MatchArtworkTests
{
    [Theory]
    [InlineData("Kometa Brno-Olomouc")]
    [InlineData("Lehigh Valley Phantoms-Providence Bruins")]
    [InlineData("České Budějovice-Karlovy Vary")]
    [InlineData("AReallyLongUnbrokenNameThatWouldOtherwiseOverflowTheWholeMatchCard")]
    public void NamesWrapWithinCardWithoutLosingOpponents(string name)
    {
        using var font = new SKFont(SKTypeface.Default, 54);
        var lines = MatchArtwork.Wrap(name, font, 825);
        Assert.All(lines, line => Assert.True(font.MeasureText(line) <= 825));
        Assert.Equal(name.Replace(" ", ""), string.Concat(lines).Replace(" ", ""));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void NamedCardIsARealWidePngWithDistinctContent(bool live)
    {
        var first = MatchArtwork.Render(new("1", "Kometa Brno-Olomouc", "1", "Hokej", "Liga", live, DateTimeOffset.Parse("2026-11-01T16:00:00Z")));
        var second = MatchArtwork.Render(new("2", "Sparta-Litvínov", "1", "Hokej", "Liga", live, null));
        using var bitmap = SKBitmap.Decode(first);
        Assert.Equal(960, bitmap.Width); Assert.Equal(540, bitmap.Height);
        Assert.False(first.SequenceEqual(second));
    }
}
