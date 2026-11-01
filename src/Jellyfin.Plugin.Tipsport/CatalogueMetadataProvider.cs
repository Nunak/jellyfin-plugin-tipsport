using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tipsport;

/// <summary>Initial channel import queues metadata refresh; use it to order newly created cards too.</summary>
public sealed class CatalogueMetadataProvider : ICustomMetadataProvider<Video>
{
    public string Name => "Tipsport catalogue ordering";
    public Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ApplySort(item, item.ProviderIds.GetValueOrDefault("TipsportSortKey")));
    }
    internal static ItemUpdateType ApplySort(BaseItem item, string? sort)
    {
        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Name) || string.IsNullOrEmpty(sort) || item.ForcedSortName == sort)
            return ItemUpdateType.None;
        item.ForcedSortName = sort;
        return ItemUpdateType.MetadataEdit;
    }
}
