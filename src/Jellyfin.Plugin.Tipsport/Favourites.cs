using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Tipsport;

public sealed record Favourite(string Id, string Name);
public static class Favourites
{
    public static IReadOnlyList<Favourite> List(string primary, string additional = "")
    {
        var names = new[] { primary }.Concat(additional.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20);
        return names.Select(n => new Favourite(n.Equals(primary.Trim(), StringComparison.OrdinalIgnoreCase) ? "favourite" :
            "team-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n.ToUpperInvariant())))[..16].ToLowerInvariant(), n)).ToArray();
    }
    internal static string? MatchId(string selector)
    {
        var value = selector.StartsWith("event-live-", StringComparison.Ordinal) ? selector[11..]
            : selector.StartsWith("event-upcoming-", StringComparison.Ordinal) ? selector[15..]
            : selector.StartsWith("current-", StringComparison.Ordinal) ? selector[8..].Split('-')[0]
            : selector.StartsWith("live-", StringComparison.Ordinal) ? selector[5..]
            : selector.StartsWith("upcoming-", StringComparison.Ordinal) ? selector[9..] : selector;
        return Catalogue.IsMatchId(value) ? value : null;
    }
    public static SportMatch Select(IReadOnlyList<SportMatch> matches, string selector, PluginConfiguration config)
    {
        selector = MatchId(selector) ?? selector;
        var favourite = List(config.FavouriteTeam, config.AdditionalFavouriteTeams).FirstOrDefault(t => t.Id == selector);
        return Catalogue.Select(matches, favourite != null ? "favourite" : selector, favourite?.Name ?? config.FavouriteTeam);
    }
}
