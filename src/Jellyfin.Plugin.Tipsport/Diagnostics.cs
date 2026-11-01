using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Explicit allowlist: never serialize configuration, sessions, URLs or raw errors.</summary>
internal static partial class Diagnostics
{
    [GeneratedRegex(@"\b[0-9]{2,5}x[0-9]{2,5}\b")]
    private static partial Regex Resolutions();
    internal static object Create(PluginConfiguration config, bool hasSession, RelayStatus relay, string? quality,
        bool warning, DateTimeOffset? catalogueTime, DateTimeOffset? failureTime, long? probeMs = null, long cacheHits = 0, long cacheMisses = 0) => new
    {
        PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
        Runtime = RuntimeInformation.FrameworkDescription,
        Platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
        Created = DateTimeOffset.UtcNow,
        config.Enabled,
        Region = config.Region == "SK" ? "SK" : "CZ",
        HasSession = hasSession,
        config.ShowUpcoming,
        config.MaximumHeight,
        config.LowLatencyStart,
        config.ShortHlsSegments,
        config.IosTransportStream,
        MetadataCache = new { Hits = cacheHits, Misses = cacheMisses },
        FavouriteTeamCount = Favourites.List(config.FavouriteTeam, config.AdditionalFavouriteTeams).Count,
        SportFilterCount = config.SportIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length,
        Relay = new { relay.Active, relay.Readers, relay.Acquisitions, relay.Reconnecting, relay.Reconnects, relay.ResolveMs, relay.FirstByteMs, relay.LastDataAgeMs, relay.ReplayBytes },
        ProbeMs = probeMs,
        Resolutions = Resolutions().Matches(quality ?? "").Select(m => m.Value).Distinct().ToArray(),
        HasWarning = warning,
        LastSuccessfulCatalogue = catalogueTime,
        LastFailure = failureTime
    };
}
