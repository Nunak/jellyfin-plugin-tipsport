using System.Net;
using System.Xml.Serialization;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Tipsport.Tests;

public sealed class DefaultSetupTests
{
    private const string LocalBase = "http://127.0.0.1:8096";
    private static readonly Guid ChannelId = Guid.Parse("cc19d11d-7268-42af-834b-d070e91471ca");
    private static User Viewer()
    {
        var user = new User("viewer", "auth", "reset");
        user.SetPermission(PermissionKind.IsDisabled, false);
        user.SetPermission(PermissionKind.EnableLiveTvAccess, true);
        user.SetPermission(PermissionKind.EnableMediaPlayback, true);
        user.SetPermission(PermissionKind.EnableAllChannels, false);
        return user;
    }
    private static TunerHostInfo Tuner(string? url = null) => new()
    {
        Type = "m3u", Url = url ?? LocalBase + "/Plugins/Tipsport/channels.m3u",
        TunerCount = 1, AllowStreamSharing = false,
        AllowFmp4TranscodingContainer = false, ReadAtNativeFramerate = true,
        IgnoreDts = true, FallbackMaxStreamingBitrate = 30_000_000, UserAgent = "existing-agent"
    };

    [Fact]
    public void DefaultsEnableSharedViewingAndIosWithoutExperimentalLatencyChanges()
    {
        var settings = new PluginConfiguration();
        Assert.True(settings.ShareWithAllTvUsers);
        Assert.True(settings.ManageOwnTvTuners);
        Assert.True(settings.IosTransportStream);
        Assert.False(settings.Enabled);
        Assert.False(settings.ShortHlsSegments);
        Assert.False(settings.LowLatencyStart);
        Assert.False(settings.IncludeTennisTest);
        Assert.Equal(0, settings.MaximumHeight);
    }

    [Fact]
    public void UpgradeAddsNewDefaultsAndPreservesExplicitStoredChoices()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var oldXml = new StringReader("<PluginConfiguration><Enabled>true</Enabled><FavouriteTeam>Kometa</FavouriteTeam><MaximumHeight>720</MaximumHeight><IosTransportStream>false</IosTransportStream></PluginConfiguration>");
        var upgraded = Assert.IsType<PluginConfiguration>(serializer.Deserialize(oldXml));
        Assert.True(upgraded.ShareWithAllTvUsers); Assert.True(upgraded.ManageOwnTvTuners);
        Assert.False(upgraded.IosTransportStream);
        Assert.True(upgraded.Enabled); Assert.Equal("Kometa", upgraded.FavouriteTeam); Assert.Equal(720, upgraded.MaximumHeight);
        using var optedOutXml = new StringReader("<PluginConfiguration><ShareWithAllTvUsers>false</ShareWithAllTvUsers><ManageOwnTvTuners>false</ManageOwnTvTuners></PluginConfiguration>");
        var optedOut = Assert.IsType<PluginConfiguration>(serializer.Deserialize(optedOutXml));
        Assert.False(optedOut.ShareWithAllTvUsers); Assert.False(optedOut.ManageOwnTvTuners);
    }

    [Fact]
    public void GrantsOnlyTipsportAndPreservesOtherAccessRestrictions()
    {
        var user = Viewer();
        var other = Guid.NewGuid().ToString("N");
        user.SetPreference(PreferenceKind.EnabledChannels, [other]);
        user.SetPermission(PermissionKind.IsAdministrator, false);
        user.SetPermission(PermissionKind.EnableRemoteAccess, false);
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.MaxParentalRatingScore = 100;
        user.RemoteClientBitrateLimit = 3_000_000;
        var permissions = user.Permissions.Select(p => (p.Kind, p.Value)).ToArray();
        Assert.True(DefaultSetup.ApplyUserAccess(user, ChannelId));
        Assert.Equal(new[] { other, ChannelId.ToString("N") }, user.GetPreference(PreferenceKind.EnabledChannels));
        Assert.Equal(permissions, user.Permissions.Select(p => (p.Kind, p.Value)).ToArray());
        Assert.Equal(100, user.MaxParentalRatingScore);
        Assert.Equal(3_000_000, user.RemoteClientBitrateLimit);
        Assert.False(DefaultSetup.ApplyUserAccess(user, ChannelId));
    }

    [Theory]
    [InlineData(PermissionKind.IsDisabled, true)]
    [InlineData(PermissionKind.EnableLiveTvAccess, false)]
    [InlineData(PermissionKind.EnableMediaPlayback, false)]
    [InlineData(PermissionKind.EnableAllChannels, true)]
    public void DoesNotOverrideDisabledAccountsOrMissingTvAndPlaybackRights(PermissionKind kind, bool value)
    {
        var user = Viewer(); user.SetPermission(kind, value);
        Assert.False(DefaultSetup.ApplyUserAccess(user, ChannelId));
        Assert.Empty(user.GetPreference(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public void RecognizesExistingHyphenatedChannelIdAndRejectsEmptyId()
    {
        var user = Viewer(); user.SetPreference(PreferenceKind.EnabledChannels, [ChannelId.ToString("D")]);
        Assert.False(DefaultSetup.ApplyUserAccess(user, ChannelId));
        Assert.False(DefaultSetup.ApplyUserAccess(user, Guid.Empty));
        Assert.Single(user.GetPreference(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public void LocalTunerAllowsMoreThanOneViewerAndPreservesMediaOptions()
    {
        var tuner = Tuner();
        Assert.True(DefaultSetup.ApplyTuner(tuner, LocalBase));
        Assert.Equal(0, tuner.TunerCount);
        Assert.True(tuner.AllowStreamSharing);
        Assert.False(tuner.AllowFmp4TranscodingContainer);
        Assert.True(tuner.ReadAtNativeFramerate);
        Assert.True(tuner.IgnoreDts);
        Assert.Equal(30_000_000, tuner.FallbackMaxStreamingBitrate);
        Assert.Equal("existing-agent", tuner.UserAgent);
        Assert.False(DefaultSetup.ApplyTuner(tuner, LocalBase));
    }

    [Theory]
    [InlineData("http://example.org:8096/Plugins/Tipsport/channels.m3u")]
    [InlineData("http://127.0.0.1:8097/Plugins/Tipsport/channels.m3u")]
    [InlineData("http://127.0.0.1:8096/other/channels.m3u")]
    [InlineData("http://127.0.0.1:8096/Plugins/Tipsport/channels.m3u?account=other")]
    [InlineData("http://127.0.0.1:8096/Plugins/Tipsport/channels.m3u#other")]
    [InlineData("http://user@127.0.0.1:8096/Plugins/Tipsport/channels.m3u")]
    [InlineData("file:///tmp/playlist.m3u")]
    public void NeverChangesOtherSources(string url)
    {
        var tuner = Tuner(url);
        Assert.False(DefaultSetup.ApplyTuner(tuner, LocalBase));
        Assert.Equal(1, tuner.TunerCount);
        Assert.False(tuner.AllowStreamSharing);
    }

    [Fact]
    public void SupportsServerBasePathAndLoopbackIpv6()
    {
        var tuner = Tuner("http://[::1]:8096/jellyfin/Plugins/Tipsport/channels.m3u");
        Assert.True(DefaultSetup.ApplyTuner(tuner, LocalBase + "/jellyfin/"));
        tuner = Tuner(); tuner.Type = "hdhomerun";
        Assert.False(DefaultSetup.ApplyTuner(tuner, LocalBase));
    }

    private static (DefaultSetup Service, Mock<IUserManager> Users, Mock<IServerConfigurationManager> Configuration,
        List<User> Viewers, LiveTvOptions Options) Setup(PluginConfiguration? settings = null)
    {
        var viewers = new List<User> { Viewer() };
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUsers()).Returns(() => viewers);
        users.Setup(u => u.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([new Channel { Id = ChannelId, Name = "Tipsport" }]);
        var configuration = new Mock<IServerConfigurationManager>();
        var options = new LiveTvOptions { TunerHosts = [Tuner()] };
        configuration.Setup(c => c.GetConfiguration("livetv")).Returns(options);
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(IPAddress.Loopback, false)).Returns(LocalBase);
        var service = new DefaultSetup(users.Object, library.Object, configuration.Object, host.Object, NullLogger<DefaultSetup>.Instance)
        { Settings = () => settings ?? new PluginConfiguration { Enabled = true } };
        return (service, users, configuration, viewers, options);
    }

    [Fact]
    public async Task ExistingAndLaterCreatedUsersReceiveAccessWithoutRepeatedWrites()
    {
        var setup = Setup();
        await setup.Service.Reconcile(CancellationToken.None);
        await setup.Service.Reconcile(CancellationToken.None);
        setup.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Once);
        setup.Configuration.Verify(c => c.SaveConfiguration("livetv", setup.Options), Times.Once);
        var later = Viewer(); setup.Viewers.Add(later);
        await setup.Service.Reconcile(CancellationToken.None);
        Assert.Contains(ChannelId.ToString("N"), later.GetPreference(PreferenceKind.EnabledChannels));
        setup.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OptOutLeavesIndividualAccessAndTunerSettingsUntouched()
    {
        var setup = Setup(new() { Enabled = true, ShareWithAllTvUsers = false, ManageOwnTvTuners = false });
        await setup.Service.Reconcile(CancellationToken.None);
        setup.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Never);
        setup.Configuration.Verify(c => c.SaveConfiguration(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        Assert.Equal(1, setup.Options.TunerHosts[0].TunerCount);
        Assert.Empty(setup.Viewers[0].GetPreference(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public async Task DisabledPluginDoesNotChangeAccessOrTuners()
    {
        var setup = Setup(new());
        await setup.Service.Reconcile(CancellationToken.None);
        setup.Users.VerifyNoOtherCalls(); setup.Configuration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedUserSaveRemainsRetryable()
    {
        var setup = Setup(new() { Enabled = true, ManageOwnTvTuners = false });
        setup.Users.SetupSequence(u => u.UpdateUserAsync(It.IsAny<User>())).ThrowsAsync(new IOException()).Returns(Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => setup.Service.Reconcile(CancellationToken.None));
        Assert.Empty(setup.Viewers[0].GetPreference(PreferenceKind.EnabledChannels));
        await setup.Service.Reconcile(CancellationToken.None);
        Assert.Contains(ChannelId.ToString("N"), setup.Viewers[0].GetPreference(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public async Task FailedTunerSaveRemainsRetryable()
    {
        var setup = Setup(new() { Enabled = true, ShareWithAllTvUsers = false });
        setup.Configuration.SetupSequence(c => c.SaveConfiguration("livetv", setup.Options)).Throws(new IOException()).Pass();
        await Assert.ThrowsAsync<IOException>(() => setup.Service.Reconcile(CancellationToken.None));
        Assert.Equal(1, setup.Options.TunerHosts[0].TunerCount);
        Assert.False(setup.Options.TunerHosts[0].AllowStreamSharing);
        await setup.Service.Reconcile(CancellationToken.None);
        Assert.Equal(0, setup.Options.TunerHosts[0].TunerCount);
        Assert.True(setup.Options.TunerHosts[0].AllowStreamSharing);
    }
}
