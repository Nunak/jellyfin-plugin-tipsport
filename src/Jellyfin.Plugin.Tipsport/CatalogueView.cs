using System.Globalization;

namespace Jellyfin.Plugin.Tipsport;

internal static class CatalogueView
{
    internal static int FavouriteRank(SportMatch match, PluginConfiguration config)
    {
        var teams = Favourites.List(config.FavouriteTeam, config.AdditionalFavouriteTeams);
        for (var i = 0; i < teams.Count; i++) if (match.Name.Contains(teams[i].Name, StringComparison.OrdinalIgnoreCase)) return i;
        return 1000;
    }
    internal static bool Favourite(SportMatch match, PluginConfiguration config) => FavouriteRank(match, config) < 1000;
    internal static IEnumerable<SportMatch> Select(IEnumerable<SportMatch> matches, string folder, PluginConfiguration config, DateTimeOffset now)
    {
        var today = TimeZoneInfo.ConvertTime(now, Prague).Date;
        return matches.Where(m => !m.Ended && (folder switch
        {
            "live" => m.Live,
            "today" => m.Live || m.Start.HasValue && TimeZoneInfo.ConvertTime(m.Start.Value, Prague).Date == today,
            "favourites" => Favourite(m, config) && (config.ShowUpcoming || m.Live),
            "upcoming" => !m.Live,
            _ when folder.StartsWith("sport:", StringComparison.Ordinal) => m.SportId == folder[6..] && (config.ShowUpcoming || m.Live),
            _ => false
        })).OrderBy(m => FavouriteRank(m, config)).ThenBy(m => m.Start ?? DateTimeOffset.MaxValue).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }
    private static TimeZoneInfo Prague => TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    internal static string SortKey(SportMatch match, PluginConfiguration config) =>
        FavouriteRank(match, config).ToString("D4", CultureInfo.InvariantCulture) + " " + (match.Start?.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) ?? "99999999999999") + " " + match.Name;
}
