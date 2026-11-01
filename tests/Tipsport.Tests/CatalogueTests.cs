using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Tipsport;
using Xunit;

namespace Tipsport.Tests;

public sealed class CatalogueTests
{
    [Fact]
    public void ProgramToleratesMissingScoresAndDeduplicatesIds()
    {
        using var json = JsonDocument.Parse("""
        {"program":[{"id":43,"title":"Tenis","matchesByTimespans":[[
            {"id":123,"name":"A - B","live":true,"competition":"ATP Test"},
            {"id":124,"name":"C - D","score":null,"live":false},
            {"id":123,"name":"A - B","live":true},
            {"id":"../../file","name":"Bad","live":true}
        ]]}]}
        """);
        var matches = Catalogue.Parse(json.RootElement);
        Assert.Equal(2, matches.Count);
        Assert.Equal("123", matches[0].Id);
        Assert.Equal("Tenis", matches[0].Sport);
        Assert.Null(matches[0].Start);
    }
    [Fact]
    public void FavouriteNeverFallsBackToUnrelatedOrOfflineGame()
    {
        SportMatch[] matches = [new("1", "Kometa - A", "1", "Hokej", "Test", false, null), new("2", "C - D", "1", "Hokej", "Test", true, null)];
        Assert.Throws<TipsportException>(() => Catalogue.Select(matches, "favourite", "Kometa"));
        Assert.Throws<TipsportException>(() => Catalogue.Select(matches, "1", "Kometa"));
        Assert.Equal("2", Catalogue.Select(matches, "2", "Kometa").Id);
    }
    [Fact]
    public void TeamAliasAndEventIdResolveToSameAcquisitionKey()
    {
        SportMatch[] matches = [new("12", "A - KOMETA Brno", "1", "Hokej", "Test", true, null)];
        Assert.Equal(Catalogue.Select(matches, "12", "Kometa"), Catalogue.Select(matches, "favourite", "Kometa"));
    }
    [Theory]
    [InlineData("https://127.0.0.1/video")]
    [InlineData("https://192.168.1.2/video")]
    [InlineData("https://[::1]/video")]
    [InlineData("https://[fc00::1]/video")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://example.test/video")]
    [InlineData("https://user:secret@example.test/video")]
    [InlineData("https://example.test:8443/video")]
    public void UnsafeMediaUrlsAreRejected(string url) => Assert.Throws<TipsportException>(() => TipsportClient.CheckedUri(url));
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.1.2")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    public void NonPublicAddressesAreRejected(string ip) => Assert.False(TipsportClient.IsPublicAddress(IPAddress.Parse(ip)));
    [Theory]
    [InlineData("abc\r\nHeader: injected")]
    [InlineData("cookie; other=value")]
    [InlineData(" cookie-with-space")]
    [InlineData("short")]
    public void CookieCannotInjectAdditionalHeaders(string value) => Assert.Throws<TipsportException>(() => SessionStore.Validate(value));
    [Fact]
    public void PlaylistNamesCannotInjectNewEntries() => Assert.Equal("Bad' name next", TipsportController.Clean("Bad\" name\nnext"));
}
