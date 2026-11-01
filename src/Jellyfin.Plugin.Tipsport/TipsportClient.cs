using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jellyfin.Plugin.Tipsport;

public sealed class TipsportClient : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0";
    private readonly SessionStore _sessions;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SportMatch>? _cached;
    private DateTimeOffset _cacheUntil;
    private string _cachedRegion = "";
    private bool _streamWarning;
    public string? KnownMatchName(string? id) => _cached?.FirstOrDefault(m => m.Id == id)?.Name;
    public string? QualityMatchId { get; private set; }
    public string? LastWarning { get; private set; }
    public DateTimeOffset? LastSuccessfulCatalogue { get; private set; }
    public DateTimeOffset? LastFailure { get; private set; }
    public string? LastQuality { get; private set; }
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new();
    public TipsportClient(SessionStore sessions)
    {
        _sessions = sessions;
        // Avoid HttpClientFactory URL logging: media URLs can contain signed credentials.
        _http = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = ConnectPublic
        }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public void Invalidate() { _cacheUntil = DateTimeOffset.MinValue; LastWarning = null; _streamWarning = false; }
    public async Task<IReadOnlyList<SportMatch>> GetMatches(CancellationToken token)
    {
        if (!Config.Enabled) throw new TipsportException("Plugin není zapnutý.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cached != null && DateTimeOffset.UtcNow < _cacheUntil && _cachedRegion == Config.Region) return Filter(_cached);
            var origin = Origin(Config.Region);
            using var result = await Fetch(new(origin + "/rest/articles/v1/tv/program?day=0&articleId="), true, token).ConfigureAwait(false);
            _cached = Catalogue.Parse(result.RootElement);
            LastSuccessfulCatalogue = DateTimeOffset.UtcNow;
            if (!_streamWarning) LastWarning = null;
            _cachedRegion = Config.Region;
            _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(15);
            return Filter(_cached);
        }
        catch (TipsportException) { Warn(); throw; }
        finally { _gate.Release(); }
    }
    private void Warn()
    {
        LastFailure = DateTimeOffset.UtcNow;
        LastWarning = "Tipsport odmítl požadavek nebo není dostupný. Ověřte přihlášení, oprávnění ke sledování a dostupnost služby. Po vypršení relace vložte novou cookie.";
    }
    private static IReadOnlyList<SportMatch> Filter(IReadOnlyList<SportMatch> matches)
    {
        var selected = Config.SportIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return selected.Count == 0 ? matches : matches.Where(m => selected.Contains(m.SportId)).ToArray();
    }
    public async Task<Uri> Resolve(string matchId, CancellationToken token)
    {
        try
        {
            var uri = await ResolveCore(matchId, token).ConfigureAwait(false);
            QualityMatchId = matchId;
            LastWarning = null; _streamWarning = false;
            return uri;
        }
        catch (TipsportException) { _streamWarning = true; Warn(); throw; }
    }
    private async Task<Uri> ResolveCore(string matchId, CancellationToken token)
    {
        if (!Catalogue.IsMatchId(matchId)) throw new TipsportException("Neplatný zápas.");
        using var document = await Fetch(new(Origin(Config.Region) + "/rest/offer/v2/live/matches/" + matchId + "/stream?deviceType=DESKTOP"), true, token).ConfigureAwait(false);
        var root = document.RootElement;
        var type = Catalogue.Text(root, "type");
        var target = CheckedUri(Catalogue.Text(root, "data"));
        if (type == "URL_IMG")
        {
            using var external = await Fetch(target, false, token).ConfigureAwait(false);
            return await BestQuality(CheckedUri(Catalogue.Text(external.RootElement, "hlsUrl")), token).ConfigureAwait(false);
        }
        if (type == "URL_TVCOM")
        {
            using var external = await Fetch(target, false, token).ConfigureAwait(false);
            if (external.RootElement.TryGetProperty("url", out var url) && url.TryGetProperty("hls", out var hls)) return await BestQuality(CheckedUri(Catalogue.Text(hls, "url")), token).ConfigureAwait(false);
            throw new TipsportException("Poskytovatel neposkytl HLS přenos.");
        }
        if (type.Contains("URL", StringComparison.Ordinal)) throw new TipsportException("Tento typ přenosu plugin nepodporuje.");
        return await BestQuality(target, token).ConfigureAwait(false);
    }
    private async Task<Uri> BestQuality(Uri uri, CancellationToken token)
    {
        LastQuality = null;
        if (ProviderHttp.CurlPath is not { } curl) { LastQuality = "Systémový curl není dostupný; výběr kvality provádí FFmpeg."; return uri; }
        try
        {
            var text = await ProviderHttp.FetchText(curl, uri, null, token).ConfigureAwait(false);
            var choice = HlsQuality.Select(uri, text);
            LastQuality = choice.Description;
            return choice.Uri;
        }
        catch (Exception e) when (e is TipsportException or HttpRequestException or SocketException or System.ComponentModel.Win32Exception)
        {
            // FFmpeg also supports redirects and non-HLS sources. Never expose signed URLs.
            LastQuality = "Playlist nelze předem vyhodnotit; výběr kvality provádí FFmpeg.";
            return uri;
        }
    }
    private async Task<JsonDocument> Fetch(Uri uri, bool authenticated, CancellationToken token)
    {
        if (ProviderHttp.CurlPath is { } curl)
        {
            try { return await ProviderHttp.Fetch(curl, uri, authenticated ? _sessions.Load() : null, token).ConfigureAwait(false); }
            catch (SocketException) { throw new TipsportException("Poskytovatele se nepodařilo kontaktovat."); }
            catch (System.ComponentModel.Win32Exception) { throw new TipsportException("Systémový HTTP klient není dostupný."); }
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.ConnectionClose = true;
        if (authenticated)
        {
            if (uri.Host != "www.tipsport.cz" && uri.Host != "www.tipsport.sk") throw new TipsportException("Přihlašovací relace smí být poslána pouze Tipsportu.");
            request.Headers.Add("Cookie", "JSESSIONID=" + _sessions.Load());
        }
        try
        {
            await CheckPublicHost(uri, token).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge", StringComparer.OrdinalIgnoreCase))
                throw new TipsportException("Webová ochrana Tipsportu vyžaduje ověření v prohlížeči. Relace samotná pro tento HTTP klient nestačí.");
            if (!response.IsSuccessStatusCode) throw new TipsportException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Tipsport odmítl požadavek (HTTP " + (int)response.StatusCode + ", " + (response.Content.Headers.ContentType?.MediaType == "application/json" ? "API" : "webová odpověď") + "). Zkontrolujte platnost relace a dostupnost přenosu ve svém účtu."
                : "Poskytovatel vrátil HTTP " + (int)response.StatusCode + ".");
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new TipsportException("Odpověď poskytovatele je příliš velká.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (body.Length + count > 8 * 1024 * 1024) throw new TipsportException("Odpověď poskytovatele je příliš velká.");
                body.Write(buffer, 0, count);
            }
            return JsonDocument.Parse(body.ToArray());
        }
        catch (HttpRequestException) { throw new TipsportException("Poskytovatele se nepodařilo kontaktovat."); }
        catch (SocketException) { throw new TipsportException("Poskytovatele se nepodařilo kontaktovat."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TipsportException("Poskytovatel neodpověděl včas. Zkuste to znovu."); }
        catch (JsonException) { throw new TipsportException("Poskytovatel nevrátil rozpoznanou odpověď."); }
    }
    public static string Origin(string region) => region == "SK" ? "https://www.tipsport.sk" : "https://www.tipsport.cz";
    public static Uri CheckedUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 || string.IsNullOrEmpty(uri.Host))
            throw new TipsportException("Nepodporovaná adresa přenosu.");
        if (IPAddress.TryParse(uri.Host, out var ip) && !IsPublicAddress(ip)) throw new TipsportException("Interní adresy přenosů nejsou povolené.");
        return uri;
    }
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        return b.Length == 4 ? b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            : (b[0] & 0xe0) == 0x20;
    }
    private static async Task CheckPublicHost(Uri uri, CancellationToken token)
    {
        CheckedUri(uri.AbsoluteUri);
        var addresses = await Dns.GetHostAddressesAsync(uri.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip))) throw new TipsportException("Interní adresy poskytovatelů nejsou povolené.");
    }
    private static async ValueTask<Stream> ConnectPublic(SocketsHttpConnectionContext context, CancellationToken token)
    {
        // Pin the connection to the public addresses we validated, preventing DNS rebinding.
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip))) throw new HttpRequestException("Provider address is not public.");
        // Prefer IPv4 while retaining a validated IPv6 fallback.
        foreach (var address in addresses.OrderBy(ip => ip.AddressFamily != AddressFamily.InterNetwork).Take(8))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Provider connection failed.");
    }
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
