using Jellyfin.Plugin.Tipsport;
using Xunit;

namespace Tipsport.Tests;

public class HlsQualityTests
{
    private static readonly Uri Master = new("https://media.example.test/live/master.m3u8?private=secret");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighestResolutionWinsRegardlessOfPlaylistOrder(bool reverse)
    {
        string[] variants = ["#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=512x288\nlow.m3u8", "#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720\nhigh.m3u8"];
        if (reverse) Array.Reverse(variants);
        var choice = HlsQuality.Select(Master, "#EXTM3U\n" + string.Join('\n', variants));
        Assert.Equal("https://media.example.test/live/high.m3u8", choice.Uri.AbsoluteUri);
        Assert.Contains("Vybráno 1280x720", choice.Description);
        Assert.DoesNotContain("secret", choice.Description);
        Assert.DoesNotContain("example.test", choice.Description);
    }
    [Fact] public void TiedResolutionUsesAverageBandwidthAndHandlesQuotedCodecs()
    {
        var choice = HlsQuality.Select(Master, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,AVERAGE-BANDWIDTH=1000000,RESOLUTION=960x540,CODECS=\"avc1.64001f,mp4a.40.2\"\na.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2500000,AVERAGE-BANDWIDTH=2000000,RESOLUTION=960x540\nb.m3u8");
        Assert.EndsWith("b.m3u8", choice.Uri.AbsoluteUri);
    }
    [Fact] public void SeparateAudioDoesNotGetLostBySelectingVideoOnlyPlaylist()
    {
        var choice = HlsQuality.Select(Master, "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,AUDIO=\"audio\"\nvideo.m3u8");
        Assert.Equal(Master, choice.Uri);
        Assert.Contains("Oddělený zvuk", choice.Description);
    }
    [Fact] public void MediaPlaylistIsPreservedWithoutInventingOtherQualities()
    {
        var choice = HlsQuality.Select(Master, "\uFEFF#EXTM3U\r\n#EXTINF:3,\r\nsegment.ts\r\n");
        Assert.Equal(Master, choice.Uri);
        Assert.Contains("jediný mediální playlist", choice.Description);
    }
    [Theory]
    [InlineData("http://media.example.test/high.m3u8")]
    [InlineData("https://127.0.0.1/high.m3u8")]
    [InlineData("file:///etc/passwd")]
    public void SelectedVariantsMustBePublicHttps(string uri) => Assert.Throws<TipsportException>(() => HlsQuality.Select(Master, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720\n" + uri));
    [Fact] public void NonPlaylistResponseIsRejectedWithoutLeakingBody()
    {
        var error = Assert.Throws<TipsportException>(() => HlsQuality.Select(Master, "<html>private-content</html>"));
        Assert.DoesNotContain("private-content", error.Message);
    }
}
