using System.Globalization;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Tipsport;

public static class Programme
{
    public static string Xml(IReadOnlyList<SportMatch> matches, PluginConfiguration config)
    {
        var channels = Favourites.List(config.FavouriteTeam, config.AdditionalFavouriteTeams)
            .Select(f => (f.Id, f.Name, Matches: matches.Where(m => m.Name.Contains(f.Name, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (config.IncludeTennisTest) channels.Add(("test-tennis", "Test tenisu", matches.Where(m => m.SportId == "43")));
        if (config.IncludeMatchesInPlaylist)
            channels.AddRange(matches.Where(m => m.Live).Select(m => (m.Id, m.Name, Matches: (IEnumerable<SportMatch>)new[] { m })));
        var tv = new XElement("tv", new XAttribute("generator-info-name", "Jellyfin Tipsport"));
        foreach (var channel in channels)
        {
            var id = "tipsport." + channel.Id;
            tv.Add(new XElement("channel", new XAttribute("id", id), new XElement("display-name", channel.Name)));
            foreach (var match in channel.Matches.Where(m => m.Start.HasValue).OrderBy(m => m.Start))
            {
                // Jellyfin discards XMLTV without stop. Mark the configurable guide window as an estimate.
                var duration = Math.Clamp(config.GuideDurationMinutes, 30, 480);
                tv.Add(new XElement("programme", new XAttribute("start", match.Start!.Value.ToUniversalTime().ToString("yyyyMMddHHmmss '+0000'", CultureInfo.InvariantCulture)),
                    new XAttribute("stop", match.Start.Value.AddMinutes(duration).ToUniversalTime().ToString("yyyyMMddHHmmss '+0000'", CultureInfo.InvariantCulture)),
                    new XAttribute("channel", id), new XElement("title", match.Name),
                    new XElement("desc", match.Sport + " / " + match.Competition + "\nKonec v programu je orientační (" + duration + " minut od začátku). Skutečný přenos se řídí průběhem zápasu."), new XElement("category", "sports"), new XElement("category", match.Sport)));
            }
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), tv).ToString();
    }
}
