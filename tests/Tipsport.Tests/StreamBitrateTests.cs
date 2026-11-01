using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;
namespace Tipsport.Tests;
public sealed class StreamBitrateTests
{
    [Fact] public void AdvertisedBitrateOverridesUnknownOrInflatedProbeValues()
    {
        var source = new MediaSourceInfo { MediaStreams = [new() { Type = MediaStreamType.Video, Height = 480, BitRate = 83_000_000 }, new() { Type = MediaStreamType.Audio, BitRate = 128_000 }] };
        StreamBitrates.Apply(source, 2_179_000);
        Assert.Equal(2_179_000, source.Bitrate); Assert.Equal(source.Bitrate, source.FallbackMaxStreamingBitrate);
        Assert.Equal(2_051_000, source.MediaStreams[0].BitRate); Assert.Equal(128_000, source.MediaStreams[1].BitRate);
    }
    [Fact] public void MissingHlsMeasurementHasResolutionBasedFallbackInsteadOfClientMaximum()
    {
        var source = new MediaSourceInfo { MediaStreams = [new() { Type = MediaStreamType.Video, Height = 480 }] };
        StreamBitrates.Apply(source, null);
        Assert.Equal(3_000_000, source.Bitrate); Assert.Equal(3_000_000, source.MediaStreams[0].BitRate);
    }
    [Fact] public void ExternalAudioMasterRetainsItsAdvertisedAggregateBitrate()
    {
        var choice = HlsQuality.Select(new Uri("https://media.example.test/master.m3u8"), "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,AUDIO=\"a\"\nvideo.m3u8");
        Assert.Equal(2_000_000, choice.Bitrate);
        Assert.Null(choice.Playlist);
    }
}
