using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Preserves published links while updating metadata that Jellyfin's channel importer leaves unchanged.</summary>
public sealed class StableCatalogueItems(ILibraryManager library, IProviderManager providers, MatchArtwork artwork, IApplicationPaths paths)
{
    private const string ArtworkKey = "TipsportArtworkRevision";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _aliasPath = Path.Combine(paths.PluginConfigurationsPath, "Tipsport.private", "links.json");
    private Dictionary<string, string>? _aliases;

    public async Task Prepare(IReadOnlyList<ChannelItemInfo> items, IReadOnlyList<SportMatch> matches, CancellationToken token, string? activeMatchId = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var channel = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Channel], Recursive = true })
                .OfType<Channel>().FirstOrDefault(c => c.Name == "Tipsport");
            if (channel == null) return; // Discovery before Jellyfin creates the channel entity.
            var existing = library.GetItemList(new InternalItemsQuery { ChannelIds = [channel.Id], IncludeItemTypes = [BaseItemKind.Video], Recursive = true });
            _aliases ??= File.Exists(_aliasPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_aliasPath, token).ConfigureAwait(false)) ?? new()
                : new();
            await RefreshUnavailable(existing, matches, activeMatchId, token).ConfigureAwait(false);
            var changed = false;
            var planned = new List<(ChannelItemInfo Info, SportMatch? Match)>();
            foreach (var info in items.Where(i => i.Type == ChannelItemType.Media))
            {
                var matchId = Favourites.MatchId(info.Id);
                var match = matches.FirstOrDefault(m => m.Id == matchId);
                if (match == null) { planned.Add((info, null)); continue; }
                if (!_aliases.TryGetValue(match.Id, out var alias))
                {
                    // Keep an already published 0.2 link, even when its old name contained the previous state.
                    alias = existing.Where(i => !(i.ExternalId ?? "").StartsWith("current-", StringComparison.Ordinal) && Favourites.MatchId(i.ExternalId ?? "") == match.Id)
                        .OrderBy(i => i.DateCreated).ThenBy(i => i.ExternalId, StringComparer.Ordinal).FirstOrDefault()?.ExternalId;
                    if (alias != null && alias != "match-" + match.Id) { _aliases[match.Id] = alias; changed = true; }
                }
                info.Id = alias ?? "match-" + match.Id;
                planned.Add((info, match));
            }
            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_aliasPath)!);
                var temp = _aliasPath + ".tmp";
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_aliases), token).ConfigureAwait(false);
                File.Move(temp, _aliasPath, true);
            }
            foreach (var (info, match) in planned)
            {
                var revision = match == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { match.Id, match.Name, match.Live, match.Start, match.Sport, match.SportGroup, Version = 1 }))));
                if (revision != null) info.ProviderIds[ArtworkKey] = revision;
                var item = existing.FirstOrDefault(i => i.ExternalId == info.Id);
                if (item == null || item.IsLocked) continue;
                var update = CatalogueMetadataProvider.ApplySort(item, info.ProviderIds.GetValueOrDefault("TipsportSortKey"));
                if (info.ProviderIds.TryGetValue("TipsportSortKey", out var sort) && item.ProviderIds.GetValueOrDefault("TipsportSortKey") != sort)
                { item.ProviderIds["TipsportSortKey"] = sort; update |= ItemUpdateType.MetadataEdit; }
                if (!item.LockedFields.Contains(MetadataField.Name) && item.Name != info.Name)
                { item.Name = info.Name; update |= ItemUpdateType.MetadataEdit; }
                if (!item.LockedFields.Contains(MetadataField.Overview) && item.Overview != info.Overview)
                { item.Overview = info.Overview; update |= ItemUpdateType.MetadataEdit; }
                if (match != null && item.ProviderIds.Remove("TipsportAvailability")) update |= ItemUpdateType.MetadataEdit;
                if (match != null && (!item.ProviderIds.TryGetValue(ArtworkKey, out var saved) || saved != revision))
                {
                    using var png = new MemoryStream(await artwork.Get(match).ConfigureAwait(false), false);
                    await providers.SaveImage(item, png, "image/png", ImageType.Primary, null, token).ConfigureAwait(false);
                    item.ProviderIds[ArtworkKey] = revision!;
                    update |= ItemUpdateType.ImageUpdate;
                }
                if (update != ItemUpdateType.None)
                {
                    item.OnMetadataChanged();
                    await library.UpdateItemAsync(item, item.GetParent(), update, token).ConfigureAwait(false);
                }
            }

        }
        finally { _gate.Release(); }
    }
    private async Task RefreshUnavailable(IReadOnlyList<BaseItem> existing, IReadOnlyList<SportMatch> matches, string? activeMatchId, CancellationToken token)
    {
        var offered = matches.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var item in existing)
        {
            if (item.IsLocked || (item.ExternalId ?? "").StartsWith("current-", StringComparison.Ordinal)) continue;
            var id = Favourites.MatchId(item.ExternalId ?? "");
            if (id == null || id == activeMatchId) continue;
            offered.TryGetValue(id, out var match);
            if (match is { Ended: false }) continue;
            var state = match?.Ended == true ? "SKONČENO" : "MIMO NABÍDKU";
            var revision = "availability:" + state;
            if (item.ProviderIds.GetValueOrDefault("TipsportAvailability") == revision) continue;
            var name = match?.Name ?? OriginalName(item.Name);
            var update = ItemUpdateType.None;
            if (!item.LockedFields.Contains(MetadataField.Name))
            { item.Name = state + " · " + name; update |= ItemUpdateType.MetadataEdit; }
            if (!item.LockedFields.Contains(MetadataField.Overview))
            {
                item.Overview = match?.Ended == true ? "Poskytovatel označil tento přenos jako skončený. Vyberte jiný živý zápas."
                    : "Tento přenos není v aktuální nabídce poskytovatele. Mohl skončit nebo být stažen. Obnovte nabídku a vyberte dostupný přenos.";
                update |= ItemUpdateType.MetadataEdit;
            }
            var picture = match ?? new SportMatch(id, name, "", "Sportovní přenos", "", false, null);
            using var png = new MemoryStream(await artwork.Get(picture, state).ConfigureAwait(false), false);
            await providers.SaveImage(item, png, "image/png", ImageType.Primary, null, token).ConfigureAwait(false);
            item.ProviderIds["TipsportAvailability"] = revision;
            item.ProviderIds[ArtworkKey] = revision;
            update |= ItemUpdateType.ImageUpdate;
            item.OnMetadataChanged();
            await library.UpdateItemAsync(item, item.GetParent(), update, token).ConfigureAwait(false);
        }
    }
    private static string OriginalName(string name)
    {
        foreach (var prefix in new[] { "ŽIVĚ · ", "ČEKÁME · ", "SKONČENO · ", "MIMO NABÍDKU · " })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..];
        return name;
    }

}
