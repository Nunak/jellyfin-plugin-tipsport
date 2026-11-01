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
