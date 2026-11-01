using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tipsport;

internal static class LiveProbe
{
    internal static async Task<List<MediaStream>> Read(Func<int, CancellationToken, Task<List<MediaStream>>> probe, CancellationToken token)
    {
        try
        {
            var streams = await probe(1000, token).ConfigureAwait(false);
            if (Complete(streams)) return streams;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // A short analysis may miss codec headers; retry with the original window.
            // Do not log provider exceptions, which can contain signed media URLs.
        }
        token.ThrowIfCancellationRequested();
        return await probe(3000, token).ConfigureAwait(false);
    }

    private static bool Complete(IEnumerable<MediaStream> streams) =>
        streams.Any(s => s.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.Codec) && s.Width > 0 && s.Height > 0 && s.AverageFrameRate > 0)
        && streams.Any(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Codec) && s.Channels > 0 && s.SampleRate > 0);
}
