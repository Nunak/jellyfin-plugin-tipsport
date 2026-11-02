using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Tipsport;

public sealed class TipsportException(string message, bool authenticationRequired = false) : Exception(message)
{
    public bool AuthenticationRequired { get; } = authenticationRequired;
}
public sealed record SportMatch(string Id, string Name, string SportId, string Sport, string Competition, bool Live, DateTimeOffset? Start, string SportGroup = "", bool Ended = false);

public static class Catalogue
{
    public static IReadOnlyList<SportMatch> Parse(JsonElement root)
    {
        var result = new Dictionary<string, SportMatch>(StringComparer.Ordinal);
        if (!root.TryGetProperty("program", out var sports) || sports.ValueKind != JsonValueKind.Array)
            throw new TipsportException("Poskytovatel nevrátil rozpoznaný program. Zkontrolujte přihlášenou relaci.");
        foreach (var sport in sports.EnumerateArray())
        {
            var sportId = Text(sport, "id");
            var title = Text(sport, "title");
            if (!sport.TryGetProperty("matchesByTimespans", out var groups) || groups.ValueKind != JsonValueKind.Array) continue;
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array) continue;
                foreach (var match in group.EnumerateArray())
                {
                    var id = Text(match, "id");
                    var name = Text(match, "name");
                    if (!IsMatchId(id) || string.IsNullOrWhiteSpace(name)) continue;
                    var live = match.TryGetProperty("live", out var flag) && flag.ValueKind == JsonValueKind.True;
                    result[id] = new(id, name, sportId, Text(match, "sport") is { Length: > 0 } s ? s : title,
                        Text(match, "competition"), live, ReadStart(match), title, IsEnded(match));
                }
            }
        }
        return result.Values.OrderByDescending(m => m.Live).ThenBy(m => m.Start ?? DateTimeOffset.MaxValue).ThenBy(m => m.Name).ToArray();
    }

    public static SportMatch Select(IReadOnlyList<SportMatch> matches, string selector, string team)
    {
        SportMatch? match = selector switch
        {
            "favourite" or "kometa" when !string.IsNullOrWhiteSpace(team) => matches.FirstOrDefault(m => m.Live && !m.Ended && m.Name.Contains(team, StringComparison.OrdinalIgnoreCase)),
            "test-tennis" => matches.Where(m => m.Live && !m.Ended && m.SportId == "43").OrderByDescending(m => m.Competition.StartsWith("ATP ", StringComparison.OrdinalIgnoreCase)).FirstOrDefault(),
            _ when IsMatchId(selector) => matches.FirstOrDefault(m => m.Id == selector && m.Live && !m.Ended),
            _ => null
        };
        if (match != null) return match;
        var selected = matches.FirstOrDefault(m => m.Id == selector);
        if (selected?.Ended == true) throw new TipsportException("Tento přenos už skončil. Vyberte jiný živý zápas.");
        if (selected?.Start is { } start && start > DateTimeOffset.UtcNow)
            throw new TipsportException("Přenos ještě nezačal. Plánovaný začátek: " + start.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("cs-CZ")) + ".");
        throw new TipsportException(selected != null ? "Přenos nyní není živě dostupný. Zkuste to později." :
            selector is "favourite" or "kometa" ? "Oblíbený tým právě nemá živý přenos. Program najdete ve složce Oblíbené." :
            "Přenos už není v aktuální nabídce. Mohl skončit nebo být stažen poskytovatelem. Obnovte nabídku.");
    }

    private static bool IsEnded(JsonElement match) => new[] { "ended", "finished" }.Any(k =>
        match.TryGetProperty(k, out var value) && value.ValueKind == JsonValueKind.True);

    public static bool IsMatchId(string value) => value.Length is > 0 and <= 20 && value.All(c => c is >= '0' and <= '9');
    public static string Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static DateTimeOffset? ReadStart(JsonElement match)
    {
        // Do not invent start times when the provider omits or changes this field.
        foreach (var field in new[] { "startDate", "startTime", "start" })
        {
            var text = Text(match, field);
            if (long.TryParse(text, out var unix) && unix > 1_000_000_000)
            {
                try { return unix > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix); }
                catch (ArgumentOutOfRangeException) { }
            }
            if (text.Contains('T') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
        }
        return null;
    }
}
