using System.Diagnostics;
using System.Threading.Channels;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Tipsport;

public interface IRelaySource : IAsyncDisposable { Stream Output { get; } }
public interface IRelaySourceFactory { Task<IRelaySource> Open(Uri uri, CancellationToken token); }

public sealed class FfmpegSourceFactory(IMediaEncoder encoder) : IRelaySourceFactory
{
    public Task<IRelaySource> Open(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = ResolveExecutable(encoder.EncoderPath, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-rw_timeout", "15000000", "-user_agent", TipsportClient.UserAgent,
            // Automatic selection chooses the highest-resolution video, rather than
            // the first (potentially low-quality) rendition of an HLS master playlist.
            "-i", uri.AbsoluteUri, "-sn", "-dn", "-c", "copy", "-mpegts_flags", "+resend_headers", "-f", "mpegts", "pipe:1" }) start.ArgumentList.Add(arg);
        // No shell, command logging, stderr logging, or downloaded executable.
        var process = Process.Start(start) ?? throw new TipsportException("FFmpeg se nepodařilo spustit.");
        process.StandardInput.Close();
        return Task.FromResult<IRelaySource>(new FfmpegSource(process));
    }
    internal static string ResolveExecutable(string? configured, string baseDirectory, string? searchPath, bool windows)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            if (!Path.IsPathRooted(configured))
            {
                var filename = windows && !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured + ".exe" : configured;
                foreach (var directory in new[] { baseDirectory }.Concat((searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)))
                {
                    var candidate = Path.Combine(directory.Trim('"'), filename);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
        }
        throw new TipsportException("FFmpeg serveru Jellyfin není dostupný.");
    }
    private sealed class FfmpegSource(Process process) : IRelaySource
    {
        private readonly Task _stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        private int _disposed;
        public Stream Output => process.StandardOutput.BaseStream;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await _stderr.ConfigureAwait(false); } catch (IOException) { }
            process.Dispose();
        }
    }
}

public sealed record RelayStatus(bool Active, string? MatchId, int Readers, long Acquisitions, bool Reconnecting = false, int Reconnects = 0);

/// <summary>One provider acquisition; multiple bounded local readers of the same match.</summary>
public sealed class SharedRelay(IRelaySourceFactory factory) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private RelaySession? _current;
    private long _starts;
    private int _disposed;
    internal TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public RelayStatus Status => _current is { Ended: false } current
        ? new(true, current.MatchId, current.Readers, Interlocked.Read(ref _starts), current.Reconnecting, current.Reconnects)
        : new(false, null, 0, Interlocked.Read(ref _starts));

    public async Task<RelayLease> Subscribe(string matchId, Func<CancellationToken, Task<Uri>> resolve, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            if (_current is { Ended: false })
            {
                if (_current.MatchId != matchId) throw new TipsportException("Jiný zápas už sleduje některý divák. Připojte se ke stejnému přenosu, nebo nejprve ukončete jeho sledování.");
                return _current.Subscribe();
            }
            if (_current != null) await _current.DisposeAsync().ConfigureAwait(false);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            var uri = await resolve(operation.Token).ConfigureAwait(false);
            var source = await factory.Open(uri, operation.Token).ConfigureAwait(false);
            var session = new RelaySession(matchId, source, IdleTimeout, _shutdown.Token, async ct =>
            {
                var refreshed = await resolve(ct).ConfigureAwait(false);
                var reopened = await factory.Open(refreshed, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _starts);
                return reopened;
            }, ReconnectDelay);
            _current = session;
            var lease = session.Subscribe();
            Interlocked.Increment(ref _starts);
            session.Start();
            return lease;
        }
        finally { _gate.Release(); }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopCurrent()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_current != null) await _current.DisposeAsync().ConfigureAwait(false); _current = null; }
        finally { _gate.Release(); }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await StopCurrent().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

public sealed class RelayLease(ChannelReader<byte[]> reader, Action release) : IDisposable
{
    private int _disposed;
    public ChannelReader<byte[]> Reader { get; } = reader;
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release(); }
}

internal sealed class RelaySession(string matchId, IRelaySource source, TimeSpan idleTimeout, CancellationToken shutdown, Func<CancellationToken, Task<IRelaySource>> reopen, TimeSpan reconnectDelay) : IAsyncDisposable
{
    private IRelaySource _source = source;
    private int _reconnecting;
    private int _reconnects;
    public bool Reconnecting => Volatile.Read(ref _reconnecting) != 0;
    public int Reconnects => Volatile.Read(ref _reconnects);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Channel<byte[]>> _readers = [];
    private readonly Queue<byte[]> _ring = new();
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    private CancellationTokenSource? _idle;
    private Task _pump = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _ended;
    private int _disposed;
    public string MatchId { get; } = matchId;
    public bool Ended { get { lock (_sync) return _ended || _disposed != 0; } }
    public int Readers { get { lock (_sync) return _readers.Count; } }
    public void Start() => _pump = Task.Run(Pump);

    public RelayLease Subscribe()
    {
        lock (_sync)
        {
            if (_ended || _disposed != 0) throw new TipsportException("Přenos právě skončil. Spusťte jej znovu.");
            _idle?.Cancel(); _idle?.Dispose(); _idle = null;
            var id = Guid.NewGuid();
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
            foreach (var packet in _ring) queue.Writer.TryWrite(packet);
            _readers[id] = queue;
            return new(queue.Reader, () => Release(id));
        }
    }
    private void Release(Guid id)
    {
        lock (_sync)
        {
            if (_readers.Remove(id, out var reader)) reader.Writer.TryComplete();
            ScheduleIdle();
        }
    }
    private void ScheduleIdle()
    {
        if (_readers.Count != 0 || _ended || _disposed != 0 || _idle != null) return;
        _idle = new();
        var token = _idle.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(idleTimeout, token).ConfigureAwait(false);
                Task? disposal;
                lock (_sync)
                {
                    if (_readers.Count != 0 || token.IsCancellationRequested) return;
                    disposal = BeginDispose();
                }
                await disposal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }
    private async Task Pump()
    {
        try
        {
            var buffer = new byte[188 * 128];
            while (!_lifetime.IsCancellationRequested)
            {
                int read;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    read = await _source.Output.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) throw new IOException("End of input");
                    Volatile.Write(ref _reconnecting, 0);
                }
                catch (Exception) when (!_lifetime.IsCancellationRequested)
                {
                    Volatile.Write(ref _reconnecting, 1);
                    // Always finish the old provider process before acquiring a new one.
                    await _source.DisposeAsync().ConfigureAwait(false);
                    var recovered = false;
                    while (_reconnects < 3 && !_lifetime.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _reconnects);
                        await Task.Delay(reconnectDelay, _lifetime.Token).ConfigureAwait(false);
                        try
                        {
                            _source = await reopen(_lifetime.Token).ConfigureAwait(false);
                            lock (_sync) _ring.Clear();
                            recovered = true;
                            break;
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                        catch (Exception) { /* Only bounded retries; no provider details in responses. */ }
                    }
                    if (!recovered) break;
                    continue;
                }
                var packet = buffer.AsSpan(0, read).ToArray();
                lock (_sync)
                {
                    _ring.Enqueue(packet);
                    while (_ring.Count > 64) _ring.Dequeue();
                    foreach (var reader in _readers.ToArray())
                    {
                        if (reader.Value.Writer.TryWrite(packet)) continue;
                        reader.Value.Writer.TryComplete(new TipsportException("Přehrávač nestíhá přijímat data. Spusťte přenos znovu."));
                        _readers.Remove(reader.Key);
                    }
                    ScheduleIdle();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Do not propagate provider URLs or process exceptions into public responses. */ }
        finally
        {
            lock (_sync)
            {
                _ended = true;
                foreach (var reader in _readers.Values) reader.Writer.TryComplete();
                _readers.Clear();
                _ring.Clear();
            }
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(BeginDispose());
    }
    private Task BeginDispose()
    {
        if (_disposeTask != null) return _disposeTask;
        _disposed = 1;
        return _disposeTask = DisposeCore();
    }
    private async Task DisposeCore()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        lock (_sync) { _idle?.Cancel(); _idle?.Dispose(); _idle = null; }
        _lifetime.Dispose();
    }
}
