using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Wide match cards rendered with the SkiaSharp already shipped by Jellyfin.</summary>
public sealed class MatchArtwork : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly Lazy<Task<byte[]?>> _logo;
    public MatchArtwork() => _logo = new(LoadLogo);
    private async Task<byte[]?> LoadLogo()
    {
        try
        {
            // Fixed public club image, without any Tipsport cookies or configurable URL.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await _http.GetAsync(TeamArtwork.KometaLogo, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2 * 1024 * 1024) return null;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) return null;
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { return null; }
    }
    public async Task<byte[]> Get(SportMatch match)
    {
        var key = match.Id + "|" + match.Name + "|" + match.Live + "|" + match.Start + "|" + match.Sport;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        byte[]? logo = match.Name.Contains("Kometa", StringComparison.OrdinalIgnoreCase) ? await _logo.Value.ConfigureAwait(false) : null;
        var png = Render(match, logo);
        // Avoid an unbounded cache when catalogue IDs change over many days.
        if (_cache.Count >= 256) _cache.Clear();
        _cache[key] = png;
        return png;
    }
    internal static byte[] Render(SportMatch match, byte[]? logo = null)
    {
        using var bitmap = new SKBitmap(960, 540);
        using var canvas = new SKCanvas(bitmap);
        using var background = new SKPaint { Shader = SKShader.CreateLinearGradient(new(0, 0), new(960, 540),
            new[] { new SKColor(10, 25, 42), match.Live ? new SKColor(0, 94, 91) : new SKColor(14, 67, 106) }, null, SKShaderTileMode.Clamp) };
        canvas.DrawRect(0, 0, 960, 540, background);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 30);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var accent = new SKPaint { Color = match.Live ? new SKColor(37, 222, 175) : new SKColor(98, 191, 247), IsAntialias = true };
        using var border = new SKPaint { Color = new SKColor(255, 255, 255, 38), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(42, 35, 918, 505), 22, 22, border);
        canvas.DrawCircle(75, 79, 10, accent);
        canvas.DrawText(match.Live ? "ŽIVĚ" : "PŘIPRAVUJEME", 100, 90, font, accent);
        font.Size = 24;
        canvas.DrawText(Fit(match.SportGroup.Length > 0 ? match.SportGroup : match.Sport, font, 650), 65, 135, font, paint);
        using var logoBitmap = logo == null ? null : SKBitmap.Decode(logo);
        if (logoBitmap != null)
        {
            var scale = Math.Min(120f / logoBitmap.Width, 120f / logoBitmap.Height);
            canvas.DrawBitmap(logoBitmap, new SKRect(790, 50, 790 + logoBitmap.Width * scale, 50 + logoBitmap.Height * scale));
        }
        font.Size = 54;
        var lines = Wrap(match.Name, font, 825);
        while (lines.Count > 3 && font.Size > 32) { font.Size -= 2; lines = Wrap(match.Name, font, 825); }
        var top = lines.Count == 1 ? 295 : lines.Count == 2 ? 255 : 220;
        for (var i = 0; i < Math.Min(3, lines.Count); i++)
            canvas.DrawText(i == 2 && lines.Count > 3 ? Fit(string.Join(" ", lines.Skip(2)), font, 825) : lines[i], 65, top + i * (font.Size + 14), font, paint);
        font.Size = 26;
        var time = match.Start.HasValue ? "Začátek: " + match.Start.Value.ToLocalTime().ToString("d. M. HH:mm", CultureInfo.GetCultureInfo("cs-CZ"))
            : match.Live ? "Přenos právě probíhá" : "Čas začátku není uveden";
        canvas.DrawText(time, 65, 465, font, accent);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    internal static List<string> Wrap(string text, SKFont font, float width)
    {
        var result = new List<string>(); var line = "";
        // Add opportunities around the provider's team separator, while preserving it.
        foreach (var word in text.Replace("-", " - ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(next) > width && line.Length > 0) { result.Add(line); line = word; }
            else line = next;
            while (font.MeasureText(line) > width)
            {
                var length = line.Length;
                while (length > 1 && font.MeasureText(line[..length]) > width) length--;
                result.Add(line[..length]); line = line[length..];
            }
        }
        if (line.Length > 0) result.Add(line);
        return result.Count > 0 ? result : ["Sportovní přenos"];
    }
    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 0 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }
    public void Dispose() => _http.Dispose();
}
