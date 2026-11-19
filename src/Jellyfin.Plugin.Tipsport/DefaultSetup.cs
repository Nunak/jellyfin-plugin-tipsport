using System.Net;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Keeps this plugin's local tuner and channel access consistent with its shared input.</summary>
public sealed class DefaultSetup(IUserManager users, ILibraryManager library,
    IServerConfigurationManager configuration, IServerApplicationHost host, ILogger<DefaultSetup> logger) : BackgroundService
{
    internal Func<PluginConfiguration> Settings { get; set; } = () => Plugin.Instance?.Configuration ?? new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Discovery can finish after hosted services start. Reconciliation also catches
        // users created through clients which submit their access policy after creation.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Reconcile(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Do not put user records, paths or configured URLs in the log.
                logger.LogWarning("Tipsport default setup could not finish ({FailureType}); it will retry", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task Reconcile(CancellationToken token)
    {
        var settings = Settings();
        if (!settings.Enabled) return;
        if (settings.ManageOwnTvTuners && configuration.GetConfiguration("livetv") is LiveTvOptions options)
        {
            var before = options.TunerHosts.Select(t => (Tuner: t, Count: t.TunerCount, Sharing: t.AllowStreamSharing)).ToArray();
            var changed = false;
            foreach (var tuner in options.TunerHosts)
                changed |= ApplyTuner(tuner, TipsportChannel.LocalBase(host));
            if (changed)
            {
                try { configuration.SaveConfiguration("livetv", options); }
                catch
                {
                    foreach (var previous in before)
                    { previous.Tuner.TunerCount = previous.Count; previous.Tuner.AllowStreamSharing = previous.Sharing; }
                    throw;
                }
                logger.LogInformation("Tipsport local TV tuner configured for multiple viewers through one shared input");
            }
        }
        if (!settings.ShareWithAllTvUsers) return;
        var channel = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Channel], Recursive = true })
            .OfType<Channel>().SingleOrDefault(c => c.Name == "Tipsport");
        if (channel == null || channel.Id == Guid.Empty) return;
        foreach (var user in users.GetUsers())
        {
            token.ThrowIfCancellationRequested();
            var before = user.GetPreference(PreferenceKind.EnabledChannels);
            if (!ApplyUserAccess(user, channel.Id)) continue;
            try { await users.UpdateUserAsync(user).ConfigureAwait(false); }
            catch
            {
                user.SetPreference(PreferenceKind.EnabledChannels, before);
                throw;
            }
        }
    }

    internal static bool ApplyUserAccess(User user, Guid channelId)
    {
        if (channelId == Guid.Empty || user.HasPermission(PermissionKind.IsDisabled)
            || !user.HasPermission(PermissionKind.EnableLiveTvAccess)
            || !user.HasPermission(PermissionKind.EnableMediaPlayback)
            || user.HasPermission(PermissionKind.EnableAllChannels)) return false;
        var enabled = user.GetPreference(PreferenceKind.EnabledChannels);
        if (enabled.Any(value => Guid.TryParse(value, out var id) && id == channelId)) return false;
        // Add only this channel. Library, parental, remote, administration and all
        // other channel permissions remain under Jellyfin's normal authorization.
        user.SetPreference(PreferenceKind.EnabledChannels, [.. enabled, channelId.ToString("N")]);
        return true;
    }

    internal static bool ApplyTuner(TunerHostInfo tuner, string localBase)
    {
        if (!string.Equals(tuner.Type, "m3u", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(tuner.Url, UriKind.Absolute, out var address)
            || !Uri.TryCreate(localBase.TrimEnd('/') + "/Plugins/Tipsport/channels.m3u", UriKind.Absolute, out var expected)
            || address.Scheme != expected.Scheme || address.Port != expected.Port
            || !IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip)
            || address.AbsolutePath != expected.AbsolutePath
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0) return false;
        if (tuner.TunerCount == 0 && tuner.AllowStreamSharing) return false;
        // Jellyfin's M3U limit counts local tuner handles, not provider inputs.
        // SharedRelay independently enforces one provider input for every viewer.
        tuner.TunerCount = 0;
        tuner.AllowStreamSharing = true;
        return true;
    }
}
