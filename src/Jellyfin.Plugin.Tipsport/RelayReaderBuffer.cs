using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Absorb HLS download bursts without blocking other viewers or growing without bound.</summary>
internal sealed class RelayReaderBuffer : ChannelReader<byte[]>
{
    internal const long MaximumBytes = 16 * 1024 * 1024;
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private long _bytes;
    public long QueuedBytes => Interlocked.Read(ref _bytes);

    public bool TryWrite(byte[] packet)
    {
        if (Interlocked.Add(ref _bytes, packet.Length) <= MaximumBytes && _channel.Writer.TryWrite(packet)) return true;
        Interlocked.Add(ref _bytes, -packet.Length);
        return false;
    }

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
    public override Task Completion => _channel.Reader.Completion;
    public override bool TryRead([MaybeNullWhen(false)] out byte[] item)
    {
        if (!_channel.Reader.TryRead(out item)) return false;
        Interlocked.Add(ref _bytes, -item.Length);
        return true;
    }
    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => _channel.Reader.WaitToReadAsync(cancellationToken);
    public override async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytes, -item.Length);
        return item;
    }
}
