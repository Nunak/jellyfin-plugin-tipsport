using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tipsport;

internal static class StreamBitrates
{
    internal static void Apply(MediaSourceInfo source, int? advertised)
    {
        var video = source.MediaStreams.Where(s => s.Type == MediaStreamType.Video).ToArray();
        var height = video.Max(s => s.Height) ?? 0;
        // Live MPEG-TS has no container bitrate. Do not let an unlimited client request
        // become an 80–400 Mbit/s hardware encode of a 2 Mbit/s source.
        var total = advertised is >= 64_000 and <= 200_000_000 ? advertised.Value : height switch
        { > 2160 => 40_000_000, > 1080 => 25_000_000, > 720 => 12_000_000, > 480 => 6_000_000, _ => 3_000_000 };
        source.Bitrate = total;
        source.FallbackMaxStreamingBitrate = total;
        var audio = source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).Sum(s => (long)(s.BitRate is > 0 and <= 2_000_000 ? s.BitRate.Value : 128_000));
        var videoBudget = (int)Math.Max(32_000, total - Math.Min(audio, total / 2));
        foreach (var stream in video) stream.BitRate = videoBudget / video.Length;
    }
}
