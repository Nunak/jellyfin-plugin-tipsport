using System.Net;
using System.Text;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Tipsport;

[ApiController]
[Route("Plugins/Tipsport")]
public sealed class TipsportController(TipsportClient client, SessionStore sessions, SharedRelay relay, IServerApplicationHost host, MatchArtwork artwork, LiveMetadataCache metadataCache) : ControllerBase
{
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new();

    [HttpGet("status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public object Status() => new { HasSession = sessions.HasSession, client.AuthenticationRequired, Config.Enabled, Relay = relay.Status, MetadataCache = new { metadataCache.Hits, metadataCache.Misses }, ProbeMs = client.KnownProbeMs(relay.Status.MatchId), CurrentMatchName = client.KnownMatchName(relay.Status.MatchId), Quality = client.LastQuality, Warning = client.LastWarning, client.LastSuccessfulCatalogue, client.LastFailure, QualityMatchId = client.QualityMatchId, PlaylistUrl = TipsportChannel.LocalBase(host) + "/Plugins/Tipsport/channels.m3u", GuideUrl = TipsportChannel.LocalBase(host) + "/Plugins/Tipsport/guide.xml" };

    [HttpGet("diagnostics")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult DiagnosticExport()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(Diagnostics.Create(Config, sessions.HasSession, relay.Status, client.LastQuality,
            client.LastWarning != null, client.LastSuccessfulCatalogue, client.LastFailure, client.KnownProbeMs(relay.Status.MatchId), metadataCache.Hits, metadataCache.Misses));
    }

    public sealed record SessionRequest(string Session);
    [HttpPost("session")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult SetSession(SessionRequest request)
    {
        try { sessions.Save(request.Session ?? ""); client.Invalidate(); return Ok(new { Saved = true }); }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
    }
    [HttpPost("session/import-kodi")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult ImportKodi()
    {
        try { sessions.ImportKodi(); client.Invalidate(); return Ok(new { Saved = true }); }
        catch (Exception e) when (e is TipsportException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        { return BadRequest(new { Message = e is TipsportException ? e.Message : "Soubor relace Kodi se nepodařilo načíst." }); }
    }
    [HttpGet("catalogue")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> TestCatalogue(CancellationToken token)
    {
        try
        {
            var matches = await client.GetMatches(token).ConfigureAwait(false);
            return Ok(new { Count = matches.Count, Live = matches.Count(m => m.Live && !m.Ended), CurrentMatch = matches.FirstOrDefault(m => m.Id == relay.Status.MatchId), Matches = matches, Sports = matches.Select(m => m.Sport).Distinct().Order().ToArray() });
        }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
    }
    [HttpPost("stop")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Stop() { await relay.StopCurrent().ConfigureAwait(false); return NoContent(); }

    // Internal media endpoints are anonymous ONLY over loopback. Remote clients use
    // Jellyfin's normal authenticated playback endpoints and user permissions.
    private bool IsLoopback() => HttpContext.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    [HttpGet("quality/{key}.m3u8")]
    [AllowAnonymous]
    public IActionResult QualityPlaylist(string key)
    {
        if (!IsLoopback()) return StatusCode(403);
        if (!Guid.TryParseExact(key, "N", out _)) return NotFound();
        var playlist = client.SelectedPlaylist(key);
        if (playlist == null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Content(playlist, "application/vnd.apple.mpegurl");
    }

    [HttpGet("channels.m3u")]
    [AllowAnonymous]
    public async Task<IActionResult> Playlist(CancellationToken token)
    {
        if (!IsLoopback()) return StatusCode(403);
        try
        {
            var matches = await client.GetMatches(token).ConfigureAwait(false);
            var rows = new StringBuilder("#EXTM3U url-tvg=\"").Append(TipsportChannel.LocalBase(host)).Append("/Plugins/Tipsport/guide.xml\"\n");
            var origin = TipsportChannel.LocalBase(host) + "/Plugins/Tipsport/stream/";
            void Add(string id, string name, string group, string? logo = null)
            {
                rows.Append("#EXTINF:-1 tvg-id=\"tipsport.").Append(id).Append('"');
                if (logo != null) rows.Append(" tvg-logo=\"").Append(logo).Append('"');
                rows.Append(" group-title=\"").Append(Clean(group)).Append("\",").Append(Clean(name)).Append('\n');
                rows.Append(origin).Append(id).Append(".ts\n");
            }
            foreach (var team in Favourites.List(Config.FavouriteTeam, Config.AdditionalFavouriteTeams)) Add(team.Id, "Tipsport – " + team.Name, "Tipsport", TeamArtwork.ForTeam(team.Name));
            if (Config.IncludeTennisTest) Add("test-tennis", "Tipsport – test tenisu", "Tipsport");
            if (Config.IncludeMatchesInPlaylist)
                foreach (var match in matches.Where(m => m.Live && !m.Ended)) Add(match.Id, "ŽIVĚ · " + match.Name, match.SportGroup);
            Response.Headers.CacheControl = "no-store";
            return Content(rows.ToString(), "audio/x-mpegurl; charset=utf-8");
        }
        catch (TipsportException e) { return StatusCode(503, e.Message); }
    }
    [HttpGet("guide.xml")]
    [AllowAnonymous]
    public async Task<IActionResult> Guide(CancellationToken token)
    {
        if (!IsLoopback()) return StatusCode(403);
        try
        {
            Response.Headers.CacheControl = "no-store";
            return Content(Programme.Xml(await client.GetMatches(token).ConfigureAwait(false), Config), "application/xml; charset=utf-8");
        }
        catch (TipsportException e) { return StatusCode(503, e.Message); }
    }

    [HttpGet("artwork/match/{selector}.png")]
    [AllowAnonymous]
    public async Task<IActionResult> MatchImage(string selector, CancellationToken token)
    {
        if (!IsLoopback()) return StatusCode(403);
        var id = Favourites.MatchId(selector);
        var team = Favourites.List(Config.FavouriteTeam, Config.AdditionalFavouriteTeams).FirstOrDefault(t => t.Id == selector);
        if (id == null && team == null) return NotFound();
        try
        {
            var matches = await client.GetMatches(token).ConfigureAwait(false);
            var match = id != null ? matches.FirstOrDefault(m => m.Id == id)
                : matches.FirstOrDefault(m => m.Name.Contains(team!.Name, StringComparison.OrdinalIgnoreCase));
            if (match == null) return Artwork("sport");
            Response.Headers.CacheControl = "public, max-age=300";
            return File(await artwork.Get(match).ConfigureAwait(false), "image/png");
        }
        catch (TipsportException) { return Artwork("sport"); }
    }

    [HttpGet("artwork/{kind}.png")]
    [AllowAnonymous]
    public IActionResult Artwork(string kind)
    {
        if (!IsLoopback()) return StatusCode(403);
        if (kind is not ("sport" or "live" or "upcoming")) return NotFound();
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Tipsport.Artwork." + kind + ".png");
        return stream == null ? NotFound() : File(stream, "image/png");
    }

    internal static string Clean(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    [HttpGet("stream/{selector}.ts")]
    [AllowAnonymous]
    public async Task Stream(string selector, CancellationToken token)
    {
        if (!IsLoopback()) { Response.StatusCode = 403; return; }
        try
        {
            var currentId = Favourites.MatchId(selector) ?? "";
            var match = Catalogue.IsMatchId(currentId) && relay.Status is { Active: true } playing && playing.MatchId == currentId
                ? new SportMatch(currentId, client.KnownMatchName(currentId) ?? currentId, "", "", "", true, null)
                : Favourites.Select(await client.GetMatches(token).ConfigureAwait(false), selector, Config);
            using var lease = await relay.Subscribe(match.Id, ct => client.Resolve(match.Id, ct), token).ConfigureAwait(false);
            var first = await lease.Reader.ReadAsync(token).ConfigureAwait(false);
            Response.ContentType = "video/MP2T";
            Response.Headers.CacheControl = "no-store";
            await Response.Body.WriteAsync(first, token).ConfigureAwait(false);
            await Response.Body.FlushAsync(token).ConfigureAwait(false);
            await foreach (var bytes in lease.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await Response.Body.WriteAsync(bytes, token).ConfigureAwait(false);
                await Response.Body.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is TipsportException or System.Threading.Channels.ChannelClosedException or IOException)
        {
            if (Response.HasStarted) { HttpContext.Abort(); return; }
            Response.StatusCode = 503;
            await Response.WriteAsync(e is TipsportException ? e.Message : "Přenos skončil nebo není dostupný.", token).ConfigureAwait(false);
        }
    }
}
