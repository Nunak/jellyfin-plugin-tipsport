using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Model.Entities;
using Xunit;
namespace Tipsport.Tests;
public sealed class LiveProbeTests
{
    private static List<MediaStream> Complete => [new() { Type = MediaStreamType.Video, Codec = "h264", Width = 854, Height = 480, AverageFrameRate = 50 }, new() { Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 }];
    [Fact] public async Task CompleteMetadataNeedsOnlyShortProbe()
    {
        var durations = new List<int>();
        var result = await LiveProbe.Read((ms, _) => { durations.Add(ms); return Task.FromResult(Complete); }, default);
        Assert.Equal(new[] { 1000 }, durations); Assert.Equal(854, result[0].Width);
    }
    [Fact] public async Task MissingAudioFallsBackToOriginalWindow()
    {
        var durations = new List<int>();
        var result = await LiveProbe.Read((ms, _) => { durations.Add(ms); return Task.FromResult(ms == 1000 ? Complete.Take(1).ToList() : Complete); }, default);
        Assert.Equal(new[] { 1000, 3000 }, durations); Assert.Equal(2, result.Count);
    }
    [Fact] public async Task FailedShortProbeRetriesWithoutChangingCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await LiveProbe.Read((ms, ct) => { Assert.Equal(cancellation.Token, ct); return ms == 1000 ? Task.FromException<List<MediaStream>>(new IOException()) : Task.FromResult(Complete); }, cancellation.Token);
        Assert.Equal(2, result.Count);
    }
    [Fact] public async Task CancellationDoesNotStartFallback()
    {
        using var cancellation = new CancellationTokenSource(); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveProbe.Read((ms, ct) => { calls++; cancellation.Cancel(); return Task.FromCanceled<List<MediaStream>>(ct); }, cancellation.Token));
        Assert.Equal(1, calls);
    }
}
