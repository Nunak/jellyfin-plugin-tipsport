using System.Threading.Channels;
using Jellyfin.Plugin.Tipsport;
using Xunit;

namespace Tipsport.Tests;

public sealed class RelayTests
{
    private static readonly Uri Media = new("https://media.example.test/live.m3u8");
    [Fact]
    public async Task ConcurrentViewersShareExactlyOneAcquisitionAndReceiveSameBytes()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        var resolves = 0;
        Task<Uri> Resolve(CancellationToken _) { Interlocked.Increment(ref resolves); return Task.FromResult(Media); }
        var leases = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => relay.Subscribe("100", Resolve, CancellationToken.None)));
        try
        {
            factory.Last!.Input.Writer.TryWrite([0x47, 1, 2, 3]);
            foreach (var lease in leases) Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, await lease.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, factory.Starts);
            Assert.Equal(1, resolves);
            Assert.Equal(12, relay.Status.Readers);
            Assert.Equal(1, relay.Status.Acquisitions);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
    [Fact]
    public async Task DifferentMatchIsRejectedWithoutResolvingOrOpeningAnotherInput()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        using var first = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        var otherResolve = false;
        await Assert.ThrowsAsync<TipsportException>(() => relay.Subscribe("200", _ => { otherResolve = true; return Task.FromResult(Media); }, CancellationToken.None));
        Assert.False(otherResolve);
        Assert.Equal(1, factory.Starts);
        Assert.Equal("100", relay.Status.MatchId);
    }
    [Fact]
    public async Task IdleInputStopsAfterLastReaderButAnotherViewerKeepsItAlive()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory) { IdleTimeout = TimeSpan.FromMilliseconds(40) };
        using var first = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        var second = await relay.Subscribe("100", _ => throw new Exception("Must not resolve twice"), CancellationToken.None);
        second.Dispose();
        await Task.Delay(90);
        Assert.True(relay.Status.Active);
        first.Dispose();
        await factory.Last!.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(relay.Status.Active);
    }
    [Fact]
    public async Task ProviderFailureDoesNotPoisonNextAcquisition()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        await Assert.ThrowsAsync<TipsportException>(() => relay.Subscribe("100", _ => throw new TipsportException("offline"), CancellationToken.None));
        Assert.Equal(0, factory.Starts);
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        Assert.Equal(1, factory.Starts);
    }
    [Fact]
    public async Task NewReaderReceivesRecentTransportHeadersAndPackets()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        using var first = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        factory.Last!.Input.Writer.TryWrite([0x47, 0x40, 0x00]);
        await first.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        using var second = await relay.Subscribe("100", _ => throw new Exception(), CancellationToken.None);
        Assert.Equal(new byte[] { 0x47, 0x40, 0x00 }, await second.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, factory.Starts);
    }
    [Fact]
    public async Task ShutdownReleasesProcessAndCompletesReaders()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        await relay.StopAsync(CancellationToken.None);
        Assert.True(factory.Last!.Disposed.Task.IsCompleted);
        Assert.False(await lease.Reader.WaitToReadAsync());
        Assert.False(relay.Status.Active);
    }
    [Fact]
    public async Task NewMatchWaitsForOldProcessToFinishDisposing()
    {
        var slow = new SlowClosingSource();
        var firstOpen = true;
        var factory = new FakeFactory(() => { if (!firstOpen) return new(); firstOpen = false; return slow; });
        await using var relay = new SharedRelay(factory) { IdleTimeout = TimeSpan.FromMilliseconds(10) };
        var first = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        first.Dispose();
        await slow.Closing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = relay.Subscribe("200", _ => Task.FromResult(Media), CancellationToken.None);
        await Task.Delay(40);
        Assert.False(next.IsCompleted);
        Assert.Equal(1, factory.Starts);
        slow.AllowClose.TrySetResult();
        using var second = await next.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, factory.Starts);
    }
    [Fact]
    public async Task InterruptedSourceReconnectsAndExistingViewersKeepReceiving()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory) { ReconnectDelay = TimeSpan.FromMilliseconds(5) };
        var resolves = 0;
        using var first = await relay.Subscribe("100", _ => { resolves++; return Task.FromResult(Media); }, CancellationToken.None);
        using var second = await relay.Subscribe("100", _ => throw new Exception("Shared subscription must not resolve"), CancellationToken.None);
        var old = factory.Last!;
        old.Input.Writer.TryComplete(new IOException("Disconnected"));
        await WaitUntil(() => factory.Starts == 2);
        Assert.True(old.Disposed.Task.IsCompleted);
        Assert.Equal(2, resolves);
        factory.Last!.Input.Writer.TryWrite([0x47, 9, 8]);
        Assert.Equal(new byte[] { 0x47, 9, 8 }, await first.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new byte[] { 0x47, 9, 8 }, await second.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, relay.Status.Reconnects);
        Assert.Equal(2, relay.Status.Readers);
    }
    [Fact]
    public async Task ReconnectionStopsAfterThreeAttempts()
    {
        var factory = new FakeFactory(() => { var source = new FakeSource(); source.Input.Writer.TryComplete(); return source; });
        await using var relay = new SharedRelay(factory) { ReconnectDelay = TimeSpan.FromMilliseconds(1) };
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        Assert.False(await lease.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4, factory.Starts); // Initial input plus three bounded recoveries.
        Assert.False(relay.Status.Active);
    }
    [Fact]
    public async Task LastViewerLeavingDuringRecoveryCancelsFurtherAcquisitions()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory) { ReconnectDelay = TimeSpan.FromSeconds(1), IdleTimeout = TimeSpan.FromMilliseconds(10) };
        var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        factory.Last!.Input.Writer.TryComplete();
        await WaitUntil(() => relay.Status.Reconnecting);
        lease.Dispose();
        await WaitUntil(() => !relay.Status.Active);
        Assert.Equal(1, factory.Starts);
    }
    [Fact]
    public async Task ReconnectionWaitsUntilPreviousProcessHasCompletelyStopped()
    {
        var slow = new SlowClosingSource();
        var count = 0;
        var factory = new FakeFactory(() => ++count == 1 ? slow : new FakeSource());
        await using var relay = new SharedRelay(factory) { ReconnectDelay = TimeSpan.FromMilliseconds(1) };
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), CancellationToken.None);
        slow.Input.Writer.TryComplete();
        await slow.Closing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, factory.Starts);
        slow.AllowClose.TrySetResult();
        await WaitUntil(() => factory.Starts == 2);
        Assert.True(relay.Status.Active);
    }
    [Fact]
    public async Task TimingDistinguishesOpeningFromFirstDataAndIdleInput()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), default);
        Assert.NotNull(relay.Status.ResolveMs);
        Assert.Null(relay.Status.FirstByteMs);
        Assert.Null(relay.Status.LastDataAgeMs);
        factory.Last!.Input.Writer.TryWrite([0x47, 1, 2, 3]);
        await lease.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(relay.Status.FirstByteMs >= 0);
        Assert.True(relay.Status.LastDataAgeMs >= 0);
        Assert.Equal(4, relay.Status.ReplayBytes);
        await Task.Delay(25);
        Assert.True(relay.Status.LastDataAgeMs >= 15);
        await relay.StopCurrent();
        Assert.Null(relay.Status.FirstByteMs);
        Assert.Null(relay.Status.ResolveMs);
        Assert.Equal(0, relay.Status.ReplayBytes);
    }
    [Fact]
    public async Task ReplayMetricRemainsBoundedAndSharedJoinsDoNotRestartTiming()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory);
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), default);
        for (var i = 0; i < 100; i++)
        {
            factory.Last!.Input.Writer.TryWrite([0x47, 1, 2, 3]);
            await lease.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(64 * 4, relay.Status.ReplayBytes);
        var timing = relay.Status.FirstByteMs;
        using var other = await relay.Subscribe("100", _ => throw new Exception("Must reuse input"), default);
        Assert.Equal(timing, relay.Status.FirstByteMs);
        Assert.Equal(1, factory.Starts);
    }
    [Fact]
    public async Task RecoveryResetsFirstDataTimingAndReplayMetric()
    {
        var factory = new FakeFactory();
        await using var relay = new SharedRelay(factory) { ReconnectDelay = TimeSpan.FromMilliseconds(1) };
        using var lease = await relay.Subscribe("100", _ => Task.FromResult(Media), default);
        factory.Last!.Input.Writer.TryWrite([0x47, 1, 2, 3]);
        await lease.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        factory.Last!.Input.Writer.TryComplete();
        await WaitUntil(() => factory.Starts == 2 && relay.Status.FirstByteMs == null);
        Assert.Null(relay.Status.LastDataAgeMs);
        Assert.Equal(0, relay.Status.ReplayBytes);
        factory.Last!.Input.Writer.TryWrite([0x47, 5]);
        await lease.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(relay.Status.FirstByteMs >= 0);
        Assert.Equal(2, relay.Status.ReplayBytes);
    }
    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private sealed class FakeFactory(Func<FakeSource>? create = null) : IRelaySourceFactory
    {
        public int Starts;
        public FakeSource? Last;
        public Task<IRelaySource> Open(Uri uri, CancellationToken token) { Starts++; Last = create?.Invoke() ?? new(); return Task.FromResult<IRelaySource>(Last); }
    }
    private class FakeSource : Stream, IRelaySource
    {
        public Channel<byte[]> Input { get; } = Channel.CreateUnbounded<byte[]>();
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream Output => this;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var data = await Input.Reader.ReadAsync(token);
            data.CopyTo(buffer);
            return data.Length;
        }
        public override ValueTask DisposeAsync() { Input.Writer.TryComplete(); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class SlowClosingSource : FakeSource
    {
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DisposeAsync()
        {
            Closing.TrySetResult();
            await AllowClose.Task;
            await base.DisposeAsync();
        }
    }
}
