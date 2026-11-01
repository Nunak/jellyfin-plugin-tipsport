using System.Xml.Linq;
using Jellyfin.Plugin.Tipsport;
using Xunit;
namespace Tipsport.Tests;
public sealed class ProgrammeTests
{
    [Fact]
    public void FavouriteIdsRemainStableWhenTeamsAreReorderedAndPrimaryKeepsExistingId()
    {
        var before = Favourites.List("Kometa", "Sparta; Zbrojovka; kometa");
        var after = Favourites.List("Kometa", "Zbrojovka; Sparta");
        Assert.Equal(3, before.Count);
        Assert.Equal("favourite", before[0].Id);
        Assert.Equal(before.Single(t => t.Name == "Sparta").Id, after.Single(t => t.Name == "Sparta").Id);
    }
    [Fact]
    public void AdditionalTeamAndCurrentItemResolveToActualMatchId()
    {
        var config = new PluginConfiguration { FavouriteTeam = "Kometa", AdditionalFavouriteTeams = "Zbrojovka" };
        SportMatch[] matches = [new("123", "Zbrojovka - A", "1", "Fotbal", "Liga", true, null)];
        Assert.Equal("123", Favourites.Select(matches, Favourites.List("Kometa", "Zbrojovka")[1].Id, config).Id);
        Assert.Equal("123", Favourites.Select(matches, "current-123", config).Id);
        Assert.Equal("123", Favourites.Select(matches, "current-123-2", config).Id);
        Assert.Equal("123", Favourites.Select(matches, "live-123", config).Id);
        Assert.Equal("123", Favourites.Select(matches, "event-live-123", config).Id);
        matches = [matches[0] with { Live = false }];
        Assert.Throws<TipsportException>(() => Favourites.Select(matches, "upcoming-123", config));
        Assert.Throws<TipsportException>(() => Favourites.Select(matches, "current-../../x", config));
    }
    [Fact]
    public void XmltvUsesKnownStartAndClearlyLabelsEstimatedEndForJellyfin()
    {
        var start = DateTimeOffset.Parse("2026-11-01T17:00:00+01:00");
        SportMatch[] matches = [new("1", "Kometa & <Brno> - A", "1", "Hokej", "Liga", false, start),
            new("2", "Kometa - B", "1", "Hokej", "Liga", false, null)];
        var xml = XDocument.Parse(Programme.Xml(matches, new() { FavouriteTeam = "Kometa" }));
        var programme = Assert.Single(xml.Root!.Elements("programme"));
        Assert.Equal("20261101160000 +0000", programme.Attribute("start")!.Value);
        Assert.Equal("20261101190000 +0000", programme.Attribute("stop")!.Value);
        Assert.Contains("orientační", programme.Element("desc")!.Value);
        Assert.Equal("tipsport.favourite", programme.Attribute("channel")!.Value);
        Assert.Equal(matches[0].Name, programme.Element("title")!.Value);
    }
    [Fact]
    public void XmltvIncludesOnlyConfiguredPlaylistChannels()
    {
        SportMatch[] matches = [new("1", "A - B", "43", "Tenis", "ATP", true, DateTimeOffset.UtcNow)];
        var xml = XDocument.Parse(Programme.Xml(matches, new() { FavouriteTeam = "Kometa" }));
        Assert.Single(xml.Root!.Elements("channel"));
        Assert.Empty(xml.Root.Elements("programme"));
    }
    [Fact]
    public void JellyfinXmltvReaderActuallyReturnsTheScheduledMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        var start = DateTimeOffset.Parse("2026-11-01T16:00:00Z");
        try
        {
            File.WriteAllText(path, Programme.Xml([new("1", "Kometa-Olomouc", "1", "Hokej", "Liga", false, start)], new() { FavouriteTeam = "Kometa" }));
            var reader = new Jellyfin.XmlTv.XmlTvReader(path, "cs");
            var programmes = reader.GetProgrammes("tipsport.favourite", start.AddHours(-1), start.AddHours(5), CancellationToken.None).ToArray();
            Assert.Single(programmes);
            Assert.Equal(start, programmes[0].StartDate);
            Assert.Equal(start.AddHours(3), programmes[0].EndDate);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("live")][InlineData("upcoming")][InlineData("sport")]
    public void EmbeddedArtworkHasWideAspectRatio(string kind)
    {
        using var image = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Tipsport.Artwork." + kind + ".png");
        Assert.NotNull(image);
        byte[] header = new byte[24]; image.ReadExactly(header);
        Assert.Equal(960, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
        Assert.Equal(540, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }
}
