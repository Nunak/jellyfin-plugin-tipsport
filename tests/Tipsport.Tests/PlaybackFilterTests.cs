using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
namespace Tipsport.Tests;
public sealed class PlaybackFilterTests
{
    [Fact] public void IosCompatibilityOnlyChangesTheContainerForOwnedH264AacSources()
    {
        const string url = "Videos/id/master.m3u8?SegmentContainer=mp4&SegmentLength=6&api_key=test%2Btoken&VideoBitrate=2051000&AudioBitrate=128000&AllowVideoStreamCopy=true&AllowAudioStreamCopy=true&MinSegments=2";
        var source = CompatibleSource(url);
        TipsportPlaybackFilter.Apply(new PlaybackInfoResponse { MediaSources = [source] }, "http://127.0.0.1:8096", false, true);
        var q = QueryHelpers.ParseQuery(source.TranscodingUrl![source.TranscodingUrl.IndexOf('?')..]);
        Assert.Equal("ts", q["SegmentContainer"]);
        Assert.Equal("6", q["SegmentLength"]); Assert.Equal("2", q["MinSegments"]);
        Assert.Equal("2051000", q["VideoBitrate"]); Assert.Equal("128000", q["AudioBitrate"]);
        Assert.Equal("true", q["AllowVideoStreamCopy"]); Assert.Equal("true", q["AllowAudioStreamCopy"]);
        Assert.Equal("test+token", q["api_key"]);
        Assert.Equal(854, source.MediaStreams[0].Width); Assert.Equal(480, source.MediaStreams[0].Height);
    }
    [Fact] public void OtherCodecsOtherSourcesAndDisabledSettingsRemainUnchanged()
    {
        const string url = "Videos/id/master.m3u8?SegmentContainer=mp4&SegmentLength=6";
        var hevc = CompatibleSource(url); hevc.MediaStreams[0].Codec = "hevc";
        var ac3 = CompatibleSource(url); ac3.MediaStreams[1].Codec = "ac3";
        var external = CompatibleSource(url); external.Path = "https://other.test/stream.ts";
        var nonHls = CompatibleSource(url); nonHls.TranscodingSubProtocol = MediaStreamProtocol.http;
        var sources = new[] { hevc, ac3, external, nonHls };
        TipsportPlaybackFilter.Apply(new PlaybackInfoResponse { MediaSources = sources }, "http://127.0.0.1:8096", false, true);
        Assert.All(sources, s => Assert.Equal(url, s.TranscodingUrl));
        var disabled = CompatibleSource(url);
        TipsportPlaybackFilter.Apply(new PlaybackInfoResponse { MediaSources = [disabled] }, "http://127.0.0.1:8096", false, false);
        Assert.Equal(url, disabled.TranscodingUrl);
    }
    [Theory]
    [InlineData("Mozilla iPhone OS 18", true)]
    [InlineData("Mozilla iPad OS", true)]
    [InlineData("Mozilla Android", false)]
    [InlineData("Mozilla Macintosh", false)]
    public void IosCompatibilityDoesNotChangeOtherDeviceFamilies(string userAgent, bool expected) => Assert.Equal(expected, TipsportPlaybackFilter.IsIos(userAgent));
    [Theory]
    [InlineData(null)]
    [InlineData("Videos/id/stream.mp4?SegmentContainer=mp4")]
    [InlineData("Videos/id/master.m3u8?SegmentContainer=mp4&SegmentContainer=ts")]
    [InlineData("Videos/id/master.m3u8?SegmentContainer=unknown")]
    [InlineData("Videos/id/master.m3u8?SegmentContainer=ts")]
    public void CompatibilityPreservesUnknownOrAlreadyCompatibleRequests(string? url) => Assert.Equal(url, TipsportPlaybackFilter.TransportStream(url));
    private static MediaSourceInfo CompatibleSource(string url) => new()
    {
        Path = "http://127.0.0.1:8096/Plugins/Tipsport/stream/123.ts", TranscodingSubProtocol = MediaStreamProtocol.hls, TranscodingUrl = url,
        MediaStreams = [new() { Type = MediaStreamType.Video, Codec = "h264", Width = 854, Height = 480 }, new() { Type = MediaStreamType.Audio, Codec = "aac" }]
    };
    [Fact] public void SegmentChangePreservesAuthenticationCodecQualityAndClientMinimum()
    {
        const string url = "Videos/id/master.m3u8?SegmentLength=6&api_key=test%2Btoken&VideoBitrate=2051000&MinSegments=2&SegmentContainer=mp4&AllowVideoStreamCopy=true";
        var rewritten = TipsportPlaybackFilter.Shorten(url)!;
        var q = QueryHelpers.ParseQuery(rewritten[(rewritten.IndexOf('?') + 1)..]);
        Assert.Equal("3", q["SegmentLength"]); Assert.Equal("test+token", q["api_key"]);
        Assert.Equal("2051000", q["VideoBitrate"]); Assert.Equal("2", q["MinSegments"]); Assert.Equal("mp4", q["SegmentContainer"]); Assert.Equal("true", q["AllowVideoStreamCopy"]);
    }
    [Theory]
    [InlineData("Videos/id/master.m3u8?SegmentLength=1")]
    [InlineData("Videos/id/master.m3u8?SegmentLength=bad")]
    [InlineData("Videos/id/master.m3u8?SegmentLength=6&SegmentLength=7")]
    [InlineData("Videos/id/stream.mp4?SegmentLength=6")]
    [InlineData(null)]
    public void ShorterOrUnrecognizedClientRequestsArePreserved(string? url) => Assert.Equal(url, TipsportPlaybackFilter.Shorten(url));
    [Fact] public void OnlyOwnedNumericRelaySourceWithHlsIsRewritten()
    {
        MediaSourceInfo Source(string path, MediaStreamProtocol protocol = MediaStreamProtocol.hls) => new() { Path = path, TranscodingSubProtocol = protocol, TranscodingUrl = "Videos/id/master.m3u8?SegmentLength=6" };
        MediaSourceInfo[] sources = [Source("http://127.0.0.1:8096/Plugins/Tipsport/stream/123.ts"), Source("https://other.test/Plugins/Tipsport/stream/123.ts"), Source("http://127.0.0.1:8096/Plugins/Tipsport/stream/favourite.ts"), Source("http://127.0.0.1:8096/Plugins/Tipsport/stream/123.ts", MediaStreamProtocol.http), Source("/movies/film.mkv")];
        TipsportPlaybackFilter.Apply(new PlaybackInfoResponse { MediaSources = sources }, "http://127.0.0.1:8096");
        Assert.Contains("SegmentLength=3", sources[0].TranscodingUrl);
        Assert.All(sources.Skip(1), s => Assert.Contains("SegmentLength=6", s.TranscodingUrl));
    }
    [Fact] public void DefaultLengthZeroCanUseShortSegments() => Assert.Contains("SegmentLength=3", TipsportPlaybackFilter.Shorten("Videos/id/master.m3u8?SegmentLength=0"));
    [Fact] public void AbsentLengthCanUseShortSegmentsWithoutRemovingOtherParameters() => Assert.Contains("SegmentLength=3", TipsportPlaybackFilter.Shorten("Videos/id/master.m3u8?MinSegments=2"));
}
