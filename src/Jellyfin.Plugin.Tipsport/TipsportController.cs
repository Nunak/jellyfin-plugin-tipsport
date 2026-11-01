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
public sealed class TipsportController(TipsportClient client, SessionStore sessions, SharedRelay relay, IServerApplicationHost host) : ControllerBase
{
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new();

    [HttpGet("status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public object Status() => new { HasSession = sessions.HasSession, Config.Enabled, Relay = relay.Status, Quality = client.LastQuality, PlaylistUrl = TipsportChannel.LocalBase(host) + "/Plugins/Tipsport/channels.m3u" };

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
            return Ok(new { Count = matches.Count, Live = matches.Count(m => m.Live), Sports = matches.Select(m => m.Sport).Distinct().Order().ToArray() });
        }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
    }
    [HttpPost("stop")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Stop() { await relay.StopCurrent().ConfigureAwait(false); return NoContent(); }

    // Internal media endpoints are anonymous ONLY over loopback. Remote clients use
    // Jellyfin's normal authenticated playback endpoints and user permissions.
    private bool IsLoopback() => HttpContext.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    [HttpGet("channels.m3u")]
    [AllowAnonymous]
    public async Task<IActionResult> Playlist(CancellationToken token)
    {
        if (!IsLoopback()) return StatusCode(403);
        try
        {
            var matches = await client.GetMatches(token).ConfigureAwait(false);
            var rows = new StringBuilder("#EXTM3U\n");
            var origin = TipsportChannel.LocalBase(host) + "/Plugins/Tipsport/stream/";
            void Add(string id, string name, string group, string? logo = null)
            {
                rows.Append("#EXTINF:-1 tvg-id=\"tipsport.").Append(id).Append('"');
                if (logo != null) rows.Append(" tvg-logo=\"").Append(logo).Append('"');
                rows.Append(" group-title=\"").Append(Clean(group)).Append("\",").Append(Clean(name)).Append('\n');
                rows.Append(origin).Append(id).Append(".ts\n");
            }
            if (!string.IsNullOrWhiteSpace(Config.FavouriteTeam)) Add("favourite", "Tipsport – " + Config.FavouriteTeam, "Tipsport", TeamArtwork.ForTeam(Config.FavouriteTeam));
            if (Config.IncludeTennisTest) Add("test-tennis", "Tipsport – test tenisu", "Tipsport");
            if (Config.IncludeMatchesInPlaylist)
                foreach (var match in matches.Where(m => m.Live)) Add(match.Id, "ŽIVĚ · " + match.Name, match.SportGroup);
            Response.Headers.CacheControl = "no-store";
            return Content(rows.ToString(), "audio/x-mpegurl; charset=utf-8");
        }
        catch (TipsportException e) { return StatusCode(503, e.Message); }
    }
    internal static string Clean(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    [HttpGet("stream/{selector}.ts")]
    [AllowAnonymous]
    public async Task Stream(string selector, CancellationToken token)
    {
        if (!IsLoopback()) { Response.StatusCode = 403; return; }
        try
        {
            var match = Catalogue.Select(await client.GetMatches(token).ConfigureAwait(false), selector, Config.FavouriteTeam);
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
