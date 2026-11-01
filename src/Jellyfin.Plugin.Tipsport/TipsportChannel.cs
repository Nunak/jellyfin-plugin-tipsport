using System.Globalization;
using System.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Tipsport;

public sealed class TipsportChannel(TipsportClient client, SessionStore sessions, IServerApplicationHost host, IUserManager users, IMediaEncoder encoder, SharedRelay relay, StableCatalogueItems catalogueItems)
    : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    public string Name => "Tipsport";
    public string Description => "Živé sportovní přenosy z vlastního účtu Tipsport.";
    public string DataVersion => "5";
    public string HomePageUrl => TipsportClient.Origin(Config.Region);
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new();
    public string GetCacheKey(string? userId)
    {
        var status = relay.Status;
        return (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30).ToString(CultureInfo.InvariantCulture) + ":" + status.MatchId + ":" + status.Acquisitions + ":" + status.Reconnecting;
    }
    public bool IsEnabledFor(string userId)
    {
        if (!Config.Enabled) return false;
        if (string.IsNullOrEmpty(userId)) return true; // Background catalogue discovery; playback still uses Jellyfin authorization.
        if (!Guid.TryParse(userId, out var id)) return false;
        var user = users.GetUserById(id);
        return user != null && users.GetUserDto(user).Policy?.EnableLiveTvAccess == true;
    }
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = [ChannelMediaContentType.Clip], MediaTypes = [ChannelMediaType.Video],
        SupportsContentDownloading = false, AutoRefreshLevels = 2
    };
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken token) => Task.FromResult(new DynamicImageResponse { HasImage = false });
    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken token)
    {
        if (query.UserId != Guid.Empty && !IsEnabledFor(query.UserId.ToString())) throw new UnauthorizedAccessException("Účet nemá povolený přístup k TV.");
        var items = new List<ChannelItemInfo>();
        if (!Config.Enabled || !sessions.HasSession) return new() { Items = items, TotalRecordCount = 0 };
        var status = relay.Status;
        var activeName = client.KnownMatchName(status.MatchId) ?? status.MatchId;
        if (query.FolderId != "watching")
            items.Add(Folder("watching", status.Active ? "00 · PRÁVĚ SE SLEDUJE · " + activeName : "00 · PRÁVĚ SE SLEDUJE · vstup je volný"));
        if (query.FolderId == "watching")
        {
            if (status.Active)
                items.Add(Media("current-" + status.MatchId + "-" + status.Acquisitions, "PRÁVĚ SE SLEDUJE · " + activeName, ActiveOverview(status.MatchId!), "match/current-" + status.MatchId));
            return new() { Items = items, TotalRecordCount = items.Count };
        }
        IReadOnlyList<SportMatch> matches;
        try { matches = await client.GetMatches(token).ConfigureAwait(false); }
        catch (TipsportException)
        {
            items.Add(Folder("unavailable", "Tipsport není dostupný – požádejte správce o kontrolu přihlášení"));
            return new() { Items = items, TotalRecordCount = items.Count };
        }
        if (string.IsNullOrEmpty(query.FolderId))
        {
            if (client.LastWarning != null) items.Add(Folder("warning", "Tipsport vyžaduje kontrolu správcem – přihlášení nebo dostupnost"));
            foreach (var team in Favourites.List(Config.FavouriteTeam, Config.AdditionalFavouriteTeams))
            {
                var favourite = Media(team.Id, team.Name + " – živý přenos",
                    "Přehraje právě živý zápas týmu " + team.Name + ". Mimo zápas přenos neběží. Aktuálně sledovaný přenos a jeho kvalitu najdete ve složce PRÁVĚ SE SLEDUJE; program ve složkách jednotlivých sportů.");
                favourite.ImageUrl = TeamArtwork.ForTeam(team.Name) ?? Artwork("sport");
                items.Add(favourite);
            }
            items.Add(Folder("live", "Právě živě"));
            if (Config.ShowUpcoming) items.Add(Folder("upcoming", "Připravované zápasy"));
            foreach (var sport in matches.GroupBy(m => m.SportId).OrderBy(g => g.First().SportGroup))
                items.Add(Folder("sport:" + sport.Key, (string.IsNullOrEmpty(sport.First().SportGroup) ? sport.First().Sport : sport.First().SportGroup) + " (" + sport.Count(m => m.Live) + " živě)"));
        }
        else
        {
            var selected = query.FolderId switch
            {
                "live" => matches.Where(m => m.Live),
                "upcoming" => matches.Where(m => !m.Live),
                _ when query.FolderId.StartsWith("sport:", StringComparison.Ordinal) => matches.Where(m => m.SportId == query.FolderId[6..] && (Config.ShowUpcoming || m.Live)),
                _ => []
            };
            foreach (var match in selected)
                items.Add(Media("match-" + match.Id, (match.Live ? "ŽIVĚ · " : "ČEKÁME · ") + match.Name,
                    match.Sport + " / " + match.Competition + StartText(match) + "\nAktuálně obsazený vstup a kvalitu najdete ve složce PRÁVĚ SE SLEDUJE.", "match/match-" + match.Id));
        }
        var paged = items.Skip(query.StartIndex ?? 0).Take(query.Limit ?? items.Count).ToArray();
        await catalogueItems.Prepare(paged, matches, token).ConfigureAwait(false);
        return new() { Items = paged, TotalRecordCount = items.Count };
    }
    private string Artwork(string kind) => LocalBase(host) + "/Plugins/Tipsport/artwork/" + kind + ".png";
    private static string StartText(SportMatch match) => match.Start.HasValue ? "\nZačátek: " + match.Start.Value.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("cs-CZ")) : "";
    private string ActiveOverview(string id)
    {
        var status = relay.Status;
        if (!status.Active) return "Momentálně se žádný přenos nesleduje.";
        if (status.MatchId != id) return "Právě se sleduje: " + (client.KnownMatchName(status.MatchId) ?? "jiný zápas") + ". Běžící přenos najdete v knihovně Tipsport pod PRÁVĚ SE SLEDUJE.";
        return "Tento zápas se právě sleduje. Připojení Jellyfinu: " + status.Readers + "." +
            (status.Reconnecting ? " Obnovuji spojení…" : "") + (client.QualityMatchId == id ? "\n" + client.LastQuality : "");
    }
    private ChannelItemInfo Folder(string id, string name) => new() { Id = id, Name = name, ImageUrl = Artwork("sport"), Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container };
    private ChannelItemInfo Media(string id, string name, string overview, string artwork = "live") => new()
    {
        Id = id, Name = name, Overview = overview, ImageUrl = Artwork(artwork), Type = ChannelItemType.Media,
        MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Clip, IsLiveStream = true
    };
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken token)
    {
        SportMatch match;
        var currentId = Favourites.MatchId(id) ?? "";
        if (Catalogue.IsMatchId(currentId) && relay.Status is { Active: true } playing && playing.MatchId == currentId)
            match = new(currentId, client.KnownMatchName(currentId) ?? currentId, "", "", "", true, null);
        else match = Favourites.Select(await client.GetMatches(token).ConfigureAwait(false), id, Config);
        if (relay.Status is { Active: true } busy && busy.MatchId != match.Id)
            throw new TipsportException("Právě se sleduje " + (client.KnownMatchName(busy.MatchId) ?? "jiný zápas") + ". Připojte se ke stejnému přenosu v položce PRÁVĚ SE SLEDUJE.");
        var source = new MediaSourceInfo
        {
            Id = match.Id, Name = match.Name, Protocol = MediaProtocol.Http,
            Path = LocalBase(host) + "/Plugins/Tipsport/stream/" + match.Id + ".ts",
            Container = "ts", IsInfiniteStream = true, IsRemote = false,
            SupportsDirectPlay = false, SupportsDirectStream = false, SupportsTranscoding = true,
            SupportsProbing = true, IgnoreDts = true, ReadAtNativeFramerate = true,
            AnalyzeDurationMs = 3000, MediaStreams = []
        };
        // Channel providers must supply stream metadata; otherwise Jellyfin can spend
        // minutes analyzing a live input and build an unsuitable transcoding command.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
        probe.CancelAfter(TimeSpan.FromSeconds(30));
        var media = await encoder.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = source, MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video, ExtractChapters = false
        }, probe.Token).ConfigureAwait(false);
        source.MediaStreams = media.MediaStreams;
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        return [source];
    }
    public static string LocalBase(IServerApplicationHost host) => host.GetApiUrlForLocalAccess(IPAddress.Loopback, false).TrimEnd('/');
}
