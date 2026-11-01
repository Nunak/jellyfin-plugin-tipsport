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

public sealed class TipsportChannel(TipsportClient client, SessionStore sessions, IServerApplicationHost host, IUserManager users, IMediaEncoder encoder)
    : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    public string Name => "Tipsport";
    public string Description => "Živé sportovní přenosy z vlastního účtu Tipsport.";
    public string DataVersion => "2";
    public string HomePageUrl => TipsportClient.Origin(Config.Region);
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new();
    public string GetCacheKey(string? userId) => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30).ToString(CultureInfo.InvariantCulture);
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
        var matches = await client.GetMatches(token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(query.FolderId))
        {
            if (!string.IsNullOrWhiteSpace(Config.FavouriteTeam))
            {
                var favourite = Media("favourite", Config.FavouriteTeam + " – živý přenos", "Přehraje právě živý zápas vybraného týmu. Mimo zápas se přenos nespustí.");
                favourite.ImageUrl = TeamArtwork.ForTeam(Config.FavouriteTeam);
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
                items.Add(Media(match.Id, (match.Live ? "ŽIVĚ · " : "ČEKÁME · ") + match.Name,
                    match.Sport + " / " + match.Competition + (match.Start.HasValue ? "\nZačátek: " + match.Start.Value.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("cs-CZ")) : "")));
        }
        var paged = items.Skip(query.StartIndex ?? 0).Take(query.Limit ?? items.Count).ToArray();
        return new() { Items = paged, TotalRecordCount = items.Count };
    }
    private static ChannelItemInfo Folder(string id, string name) => new() { Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container };
    private static ChannelItemInfo Media(string id, string name, string overview) => new()
    {
        Id = id, Name = name, Overview = overview, Type = ChannelItemType.Media,
        MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Clip, IsLiveStream = true
    };
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken token)
    {
        var match = Catalogue.Select(await client.GetMatches(token).ConfigureAwait(false), id, Config.FavouriteTeam);
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
