using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Tipsport.Tests;

public sealed class StableCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tipsport-links-" + Guid.NewGuid());
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<IApplicationPaths> _paths = new();
    private readonly MatchArtwork _artwork = new();
    private readonly Channel _channel = new() { Id = Guid.NewGuid(), Name = "Tipsport" };
    private readonly List<BaseItem> _existing = [];
    public StableCatalogueTests()
    {
        _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_root);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
            q.ChannelIds?.Count > 0 ? _existing : [_channel]);
        _library.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _providers.Setup(p => p.SaveImage(It.IsAny<BaseItem>(), It.IsAny<Stream>(), "image/png", ImageType.Primary, null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }
    private StableCatalogueItems Create() => new(_library.Object, _providers.Object, _artwork, _paths.Object);
    private static SportMatch Match(bool live = false, string name = "Brno - Olomouc") => new("123", name, "hockey", "Hokej", "Liga", live, DateTimeOffset.Parse("2026-11-01T16:00:00Z"));
    private static ChannelItemInfo Info(bool live = false) => new() { Id = "match-123", Name = live ? "ŽIVĚ · Brno - Olomouc" : "ČEKÁME · Brno - Olomouc", Overview = "Program", Type = ChannelItemType.Media };
    private async Task Prepare(StableCatalogueItems sync, ChannelItemInfo info, SportMatch match) => await sync.Prepare([info], [match], CancellationToken.None);

    [Fact]
    public async Task NewMatchesKeepSameProviderIdAcrossStateAndArtworkChanges()
    {
        var sync = Create(); var before = Info(); var after = Info(true);
        await Prepare(sync, before, Match());
        await Prepare(sync, after, Match(true, "Brno - Olomouc (oprava)"));
        Assert.Equal("match-123", before.Id);
        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(before.ProviderIds.Values.Single(), after.ProviderIds.Values.Single());
        Assert.Equal("123", Favourites.MatchId(after.Id));
    }
    [Fact]
    public async Task PublishedUpcomingLinkSurvivesLiveTransitionAndRestartEvenIfDatabaseItemIsMissing()
    {
        _existing.Add(new Video { ExternalId = "event-upcoming-123", Name = "old" });
        var before = Info(); await Prepare(Create(), before, Match());
        _existing.Clear();
        var after = Info(true); await Prepare(Create(), after, Match(true));
        Assert.Equal("event-upcoming-123", after.Id);
        Assert.Equal(before.Id, after.Id);
    }
    [Fact]
    public async Task MetadataAndImageAreUpdatedOnExistingItemWithoutChangingItsId()
    {
        var video = new Video { Id = Guid.NewGuid(), ExternalId = "event-upcoming-123", Name = "ČEKÁME", Overview = "old" };
        var id = video.Id; _existing.Add(video);
        var info = Info(true); var sync = Create();
        await Prepare(sync, info, Match(true));
        Assert.Equal(id, video.Id);
        Assert.Equal("event-upcoming-123", info.Id);
        Assert.Equal(info.Name, video.Name); Assert.Equal(info.Overview, video.Overview);
        _providers.Verify(p => p.SaveImage(video, It.IsAny<Stream>(), "image/png", ImageType.Primary, null, It.IsAny<CancellationToken>()), Times.Once);
        await Prepare(sync, Info(true), Match(true));
        _library.Verify(l => l.UpdateItemAsync(video, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Once);
        _providers.Verify(p => p.SaveImage(video, It.IsAny<Stream>(), "image/png", ImageType.Primary, null, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task ExplicitlyLockedItemIsPreserved()
    {
        var video = new Video { ExternalId = "event-upcoming-123", Name = "Vlastní název", IsLocked = true };
        _existing.Add(video); await Prepare(Create(), Info(true), Match(true));
        Assert.Equal("Vlastní název", video.Name);
        Assert.Empty(_providers.Invocations);
        _library.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task LockedNameAndOverviewRemainWhileGeneratedImageCanRefresh()
    {
        var video = new Video { ExternalId = "match-123", Name = "Vlastní", Overview = "Popis", LockedFields = [MetadataField.Name, MetadataField.Overview] };
        _existing.Add(video); await Prepare(Create(), Info(true), Match(true));
        Assert.Equal("Vlastní", video.Name); Assert.Equal("Popis", video.Overview);
        _providers.Verify(p => p.SaveImage(video, It.IsAny<Stream>(), "image/png", ImageType.Primary, null, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task TemporaryNowWatchingEntryIsNeverAdoptedAsMatchLink()
    {
        _existing.Add(new Video { ExternalId = "current-123-4" });
        var info = Info(); await Prepare(Create(), info, Match());
        Assert.Equal("match-123", info.Id);
    }
    [Fact]
    public async Task ExistingFavouriteShortcutGetsOrderingWithoutReplacingItsArtworkOrId()
    {
        var item = new Video { Id = Guid.NewGuid(), ExternalId = "favourite", Name = "Kometa – živý přenos" };
        _existing.Add(item);
        var info = new ChannelItemInfo { Id = "favourite", Name = item.Name, Type = ChannelItemType.Media };
        info.ProviderIds["TipsportSortKey"] = "0000";
        await Create().Prepare([info], [], CancellationToken.None);
        Assert.Equal("favourite", info.Id); Assert.Equal("0000", item.ForcedSortName);
        Assert.Empty(_providers.Invocations);
    }
    public void Dispose() { _artwork.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
