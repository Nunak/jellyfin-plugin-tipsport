using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Tipsport;

internal sealed record HlsChoice(Uri Uri, string Description, string? Playlist = null, int? Bitrate = null);

internal static partial class HlsQuality
{
    [GeneratedRegex("([A-Z0-9-]+)=(?:\"([^\"]*)\"|([^,]*))")]
    private static partial Regex Attributes();

    internal static HlsChoice Select(Uri master, string text, int maximumHeight = 0)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw new TipsportException("Zdroj nevrátil HLS playlist.");
        var candidates = new List<(Uri Uri, long Area, long Bandwidth, string Resolution, string? Audio, int Height, string Header)>();
        var externalAudio = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> Read(string value) => Attributes().Matches(value).GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.Last().Groups[2].Success ? g.Last().Groups[2].Value : g.Last().Groups[3].Value);
        foreach (var line in lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)))
        {
            var a = Read(line);
            if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.ContainsKey("URI") && a.TryGetValue("GROUP-ID", out var group)) externalAudio.Add(group);
        }
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) || lines[i + 1].StartsWith('#')) continue;
            var a = Read(lines[i]);
            var resolution = a.GetValueOrDefault("RESOLUTION", "");
            var dimensions = resolution.Split('x');
            long area = dimensions.Length == 2 && int.TryParse(dimensions[0], out var width) && int.TryParse(dimensions[1], out var height)
                && width is > 0 and <= 32768 && height is > 0 and <= 32768 ? (long)width * height : 0;
            long.TryParse(a.GetValueOrDefault("AVERAGE-BANDWIDTH", a.GetValueOrDefault("BANDWIDTH", "0")), out var bandwidth);
            if (!Uri.TryCreate(master, lines[i + 1], out var uri)) continue;
            candidates.Add((uri, area, bandwidth, area > 0 ? resolution : "neuvedené rozlišení", a.GetValueOrDefault("AUDIO"), dimensions.Length == 2 && int.TryParse(dimensions[1], out var h) ? h : 0, lines[i]));
        }
        if (candidates.Count == 0 && maximumHeight > 0) throw new TipsportException("Zdroj nenabízí volitelné kvality. Pro tento přenos zvolte automatickou kvalitu.");
        if (candidates.Count == 0) return new(master, "Zdroj obsahuje jediný mediální playlist; další kvality nejsou v tomto playlistu uvedené.");
        var eligible = maximumHeight > 0 ? candidates.Where(v => v.Area > 0 && v.Height > 0 && v.Height <= maximumHeight) : candidates;
        if (!eligible.Any()) throw new TipsportException("Zdroj nenabízí kvalitu v nastaveném limitu. Zvyšte limit nebo zvolte automatickou kvalitu.");
        var best = eligible.OrderByDescending(v => v.Area).ThenByDescending(v => v.Bandwidth).First();
        var bitrate = best.Bandwidth is >= 64_000 and <= 200_000_000 ? (int?)best.Bandwidth : null;
        var offered = string.Join(", ", candidates.Select(v => v.Resolution).Distinct());
        if (best.Audio != null && externalAudio.Contains(best.Audio))
        {
            if (maximumHeight == 0) return new(master, "HLS varianty: " + offered + ". Oddělený zvuk; výběr nejlepší stopy provádí FFmpeg.", Bitrate: bitrate);
            var groups = Read(best.Header).Where(p => p.Key is "AUDIO" or "VIDEO" or "SUBTITLES").Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
            var media = lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && groups.Contains(Read(l).GetValueOrDefault("GROUP-ID", "")))
                .Select(l => RewriteUris(master, l));
            var global = lines.Where(l => l.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) || l == "#EXT-X-INDEPENDENT-SEGMENTS" ||
                l.StartsWith("#EXT-X-START:", StringComparison.Ordinal) || l.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)).Select(l => RewriteUris(master, l));
            var playlist = "#EXTM3U\n" + string.Join("\n", global.Concat(media)) + "\n" + best.Header + "\n" + TipsportClient.CheckedUri(best.Uri.AbsoluteUri).AbsoluteUri + "\n";
            return new(master, "HLS varianty: " + offered + ". Vybráno " + best.Resolution + " / " + best.Bandwidth / 1000 + " kbit/s, včetně odděleného zvuku.", playlist, bitrate);
        }
        return new(TipsportClient.CheckedUri(best.Uri.AbsoluteUri), "HLS varianty: " + offered + ". Vybráno " + best.Resolution + " / " + best.Bandwidth / 1000 + " kbit/s.", Bitrate: bitrate);
    }
    private static string RewriteUris(Uri master, string line) => Attributes().Replace(line, m => m.Groups[1].Value == "URI"
        ? "URI=\"" + TipsportClient.CheckedUri(new Uri(master, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).AbsoluteUri).AbsoluteUri + "\"" : m.Value);
}
