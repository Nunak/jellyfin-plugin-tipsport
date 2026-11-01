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
    [Fact] public void HeightLimitSelectsHighestAllowedResolution()
    {
        var text = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720\nhigh.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8";
        Assert.EndsWith("low.m3u8", HlsQuality.Select(Master, text, 480).Uri.AbsoluteUri);
        Assert.EndsWith("high.m3u8", HlsQuality.Select(Master, text).Uri.AbsoluteUri);
        Assert.Throws<TipsportException>(() => HlsQuality.Select(Master, text, 240));
    }
    [Fact] public void LimitedSeparateAudioMasterPreservesAudioAndDropsHigherVideo()
    {
        var text = "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",URI=\"audio.m3u8?token=secret\"\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,AUDIO=\"audio\"\nhigh.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO=\"audio\"\nlow.m3u8";
        var choice = HlsQuality.Select(Master, text, 480);
        Assert.Contains("https://media.example.test/live/audio.m3u8?token=secret", choice.Playlist);
        Assert.Contains("https://media.example.test/live/low.m3u8", choice.Playlist);
        Assert.DoesNotContain("high.m3u8", choice.Playlist);
        Assert.DoesNotContain("secret", choice.Description);
    }
    [Fact] public void LimitedSeparateAudioRejectsInternalAudioUri()
    {
        var text = "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",URI=\"https://127.0.0.1/audio\"\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO=\"audio\"\nlow.m3u8";
        Assert.Throws<TipsportException>(() => HlsQuality.Select(Master, text, 480));
    }
    [Fact] public void LimitIsNotSilentlyIgnoredForAnUnknownSingleMediaPlaylist()
    {
        Assert.Throws<TipsportException>(() => HlsQuality.Select(Master, "#EXTM3U\n#EXTINF:3,\nsegment.ts", 480));
    }

    [Fact] public void FilteredMasterPreservesGlobalHlsTagsAndResolvesSessionKeyUri()
    {
        var text = "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO=\"a\"\nvideo.m3u8";
        var playlist = HlsQuality.Select(Master, text, 480).Playlist!;
        Assert.Contains("#EXT-X-VERSION:6", playlist); Assert.Contains("#EXT-X-INDEPENDENT-SEGMENTS", playlist);
        Assert.Contains("URI=\"https://media.example.test/live/key.bin\"", playlist);
    }

}
