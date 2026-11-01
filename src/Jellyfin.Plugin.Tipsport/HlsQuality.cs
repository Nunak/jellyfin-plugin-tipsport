using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Tipsport;

internal sealed record HlsChoice(Uri Uri, string Description);

internal static partial class HlsQuality
{
    [GeneratedRegex("([A-Z0-9-]+)=(?:\"([^\"]*)\"|([^,]*))")]
    private static partial Regex Attributes();

    internal static HlsChoice Select(Uri master, string text)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw new TipsportException("Zdroj nevrátil HLS playlist.");
        var candidates = new List<(Uri Uri, long Area, long Bandwidth, string Resolution, string? Audio)>();
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
            candidates.Add((uri, area, bandwidth, area > 0 ? resolution : "neuvedené rozlišení", a.GetValueOrDefault("AUDIO")));
        }
        if (candidates.Count == 0) return new(master, "Zdroj obsahuje jediný mediální playlist; další kvality nejsou v tomto playlistu uvedené.");
        var best = candidates.OrderByDescending(v => v.Area).ThenByDescending(v => v.Bandwidth).First();
        var offered = string.Join(", ", candidates.Select(v => v.Resolution).Distinct());
        if (best.Audio != null && externalAudio.Contains(best.Audio))
            return new(master, "HLS varianty: " + offered + ". Oddělený zvuk; výběr nejlepší stopy provádí FFmpeg.");
        return new(TipsportClient.CheckedUri(best.Uri.AbsoluteUri), "HLS varianty: " + offered + ". Vybráno " + best.Resolution + " / " + best.Bandwidth / 1000 + " kbit/s.");
    }
}
