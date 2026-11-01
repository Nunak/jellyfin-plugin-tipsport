using System.Globalization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Optional playback compatibility settings for this channel's local relay only.</summary>
public sealed class TipsportPlaybackFilter(ILibraryManager library, IServerApplicationHost host) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var config = Plugin.Instance?.Configuration;
        var shortSegments = config?.ShortHlsSegments ?? false;
        var iosTransportStream = (config?.IosTransportStream ?? false) && IsIos(context.HttpContext.Request.Headers.UserAgent.ToString());
        if ((shortSegments || iosTransportStream)
            && context.Result is ObjectResult { Value: PlaybackInfoResponse response }
            && Guid.TryParse(context.RouteData.Values.GetValueOrDefault("itemId")?.ToString(), out var id)
            && library.GetItemById(id) is { } item
            && library.GetItemById(item.ChannelId) is Channel { Name: "Tipsport" })
        {
            Apply(response, TipsportChannel.LocalBase(host), shortSegments, iosTransportStream);
        }
        await next().ConfigureAwait(false);
    }

    internal static bool IsIos(string userAgent) => userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
        || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase);

    internal static void Apply(PlaybackInfoResponse response, string localBase, bool shortSegments = true, bool iosTransportStream = false)
    {
        var prefix = localBase.TrimEnd('/') + "/Plugins/Tipsport/stream/";
        foreach (var source in response.MediaSources)
        {
            if (source.Path is not { } path || !path.StartsWith(prefix, StringComparison.Ordinal)
                || !path.EndsWith(".ts", StringComparison.Ordinal) || !Catalogue.IsMatchId(path[prefix.Length..^3])
                || !string.Equals(source.TranscodingSubProtocol.ToString(), "hls", StringComparison.OrdinalIgnoreCase)) continue;
            if (iosTransportStream && CanUseTransportStream(source)) source.TranscodingUrl = TransportStream(source.TranscodingUrl);
            if (shortSegments) source.TranscodingUrl = Shorten(source.TranscodingUrl);
        }
    }

    private static bool CanUseTransportStream(MediaBrowser.Model.Dto.MediaSourceInfo source) =>
        source.MediaStreams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video && string.Equals(s.Codec, "h264", StringComparison.OrdinalIgnoreCase))
        && source.MediaStreams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio)
        && source.MediaStreams.Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio).All(s => string.Equals(s.Codec, "aac", StringComparison.OrdinalIgnoreCase));

    internal static string? TransportStream(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var separator = url.IndexOf('?');
        if (separator < 0 || !url[..separator].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return url;
        var query = QueryHelpers.ParseQuery(url[(separator + 1)..]);
        if (query.TryGetValue("SegmentContainer", out var value)
            && (value.Count != 1 || !string.Equals(value[0], "mp4", StringComparison.OrdinalIgnoreCase))) return url;
        query["SegmentContainer"] = "ts";
        return QueryHelpers.AddQueryString(url[..separator], query);
    }

    internal static string? Shorten(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var separator = url.IndexOf('?');
        if (separator < 0 || !url[..separator].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return url;
        var query = QueryHelpers.ParseQuery(url[(separator + 1)..]);
        // Unknown values are left alone; keep an already shorter client choice.
        if (query.TryGetValue("SegmentLength", out var value)
            && (value.Count != 1 || !int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is > 0 and <= 3)) return url;
        query["SegmentLength"] = "3";
        return QueryHelpers.AddQueryString(url[..separator], query);
    }
}
