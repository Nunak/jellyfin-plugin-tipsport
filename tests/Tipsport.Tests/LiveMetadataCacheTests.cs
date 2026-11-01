using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Model.Entities;
using Xunit;
namespace Tipsport.Tests;
public sealed class LiveMetadataCacheTests
{
    private static List<MediaStream> Streams => [new() { Type = MediaStreamType.Video, Codec = "h264", Width = 854, Height = 480, BitRate = 2_000_000, Index = 0 }, new() { Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, SampleRate = 48000, Index = 1 }];
    [Fact] public async Task DifferentAliasesAndConcurrentRequestsShareAnalysisButNeverMutableStreams()
    {
        var cache = new LiveMetadataCache(); var calls = 0;
        var status = new RelayStatus(true, "123", 1, 1);
        async Task<List<MediaStream>> Probe(CancellationToken ct) { Interlocked.Increment(ref calls); await Task.Yield(); return Streams; }
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => cache.Get("123", () => status, Probe, default)));
        Assert.Equal(1, calls); Assert.Equal(11, cache.Hits);
        results[0][0].Codec = "changed-by-client"; results[0][1].Channels = 99;
        Assert.Equal("h264", results[1][0].Codec); Assert.Equal(2, results[1][1].Channels);
        Assert.Equal(854, results[1][0].Width); Assert.Equal(1, results[1][1].Index);
        Assert.NotSame(results[0][0], results[1][0]);
    }
    [Fact] public async Task InitialProbeMayStartInputButRestartMustInvalidate()
    {
        var cache = new LiveMetadataCache(); var calls = 0; var status = new RelayStatus(false, null, 0, 0);
        Task<List<MediaStream>> Probe(CancellationToken _) { calls++; status = new(true, "123", 1, status.Acquisitions + 1); return Task.FromResult(Streams); }
        await cache.Get("123", () => status, Probe, default);
        await cache.Get("123", () => status, Probe, default); Assert.Equal(1, calls);
        status = new(true, "123", 1, 2);
        await cache.Get("123", () => status, Probe, default); Assert.Equal(2, calls);
    }
    [Fact] public async Task InactiveDifferentOrRecoveringInputsCannotReuseMetadata()
    {
        foreach (var next in new[] { new RelayStatus(false, null, 0, 1), new(true, "456", 1, 1), new(true, "123", 1, 1, true) })
        {
            var cache = new LiveMetadataCache(); var calls = 0; var status = new RelayStatus(true, "123", 1, 1);
            Task<List<MediaStream>> Probe(CancellationToken _) { calls++; return Task.FromResult(Streams); }
            await cache.Get("123", () => status, Probe, default); status = next;
            await cache.Get("123", () => status, Probe, default); Assert.Equal(2, calls);
        }
    }
    [Fact] public async Task FailedProbeDoesNotPoisonTheNextRequest()
    {
        var cache = new LiveMetadataCache(); var status = new RelayStatus(true, "123", 1, 1);
        await Assert.ThrowsAsync<IOException>(() => cache.Get("123", () => status, _ => throw new IOException(), default));
        var result = await cache.Get("123", () => status, _ => Task.FromResult(Streams), default);
        Assert.Equal("h264", result[0].Codec); Assert.Equal(0, cache.Hits);
    }
    [Fact] public async Task SnapshotExpiresEvenWhenInputDoesNotRestart()
    {
        var clock = new Clock(); var cache = new LiveMetadataCache(clock); var status = new RelayStatus(true, "123", 1, 1); var calls = 0;
        Task<List<MediaStream>> Probe(CancellationToken _) { calls++; return Task.FromResult(Streams); }
        await cache.Get("123", () => status, Probe, default); clock.Now = clock.Now.AddMinutes(3);
        await cache.Get("123", () => status, Probe, default); Assert.Equal(2, calls);
    }
    [Fact] public async Task CancellationAndARecoveryDuringProbeCannotPublishAReusableSnapshot()
    {
        var cache = new LiveMetadataCache(); var status = new RelayStatus(true, "123", 1, 1);
        await cache.Get("123", () => status, _ => { status = new(true, "123", 1, 2); return Task.FromResult(Streams); }, default);
        await cache.Get("123", () => status, _ => Task.FromResult(Streams), default);
        Assert.Equal(2, cache.Misses);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.Get("123", () => status, _ => throw new Exception("Must not run"), cts.Token));
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
