using System.Text.Json;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>A single bounded snapshot for the active input; never share mutable DTOs with Jellyfin.</summary>
public sealed class LiveMetadataCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private string? _matchId;
    private long _acquisition;
    private byte[]? _snapshot;
    private DateTimeOffset _expires;
    private long _hits;
    private long _misses;
    public LiveMetadataCache() : this(TimeProvider.System) { }
    internal LiveMetadataCache(TimeProvider clock) => _clock = clock;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    internal async Task<List<MediaStream>> Get(string matchId, Func<RelayStatus> status,
        Func<CancellationToken, Task<List<MediaStream>>> probe, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var before = status();
            if (_snapshot != null && before.Active && !before.Reconnecting && before.MatchId == matchId
                && _matchId == matchId && before.Acquisitions == _acquisition && _clock.GetUtcNow() < _expires)
            {
                Interlocked.Increment(ref _hits);
                return Copy(_snapshot);
            }
            Interlocked.Increment(ref _misses);
            var streams = await probe(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var after = status();
            // The first probe may start the relay. Never retain a snapshot across an input recovery.
            if (after.Active && !after.Reconnecting && after.MatchId == matchId
                && (!before.Active ? after.Acquisitions == before.Acquisitions + 1 : before.MatchId == matchId && before.Acquisitions == after.Acquisitions))
            {
                _snapshot = JsonSerializer.SerializeToUtf8Bytes(streams);
                _matchId = matchId; _acquisition = after.Acquisitions; _expires = _clock.GetUtcNow().AddMinutes(2);
                return Copy(_snapshot);
            }
            return streams;
        }
        finally { _gate.Release(); }
    }
    private static List<MediaStream> Copy(byte[] snapshot) => JsonSerializer.Deserialize<List<MediaStream>>(snapshot)!;
}
