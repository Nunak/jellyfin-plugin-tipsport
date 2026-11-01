using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Tipsport;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = new("7c761c82-7b32-4b82-bb8f-49a6b0f319e7");
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "Tipsport";
    public override string Description => "Unofficial sports catalogue and single-input live streaming. Requires a valid Tipsport session.";
    public override Guid Id => PluginId;
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "Tipsport", EmbeddedResourcePath = "Jellyfin.Plugin.Tipsport.Configuration.configPage.html" }];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string Region { get; set; } = "CZ";
    public string FavouriteTeam { get; set; } = "";
    public bool ShowUpcoming { get; set; } = true;
    public bool IncludeTennisTest { get; set; }
    public bool IncludeMatchesInPlaylist { get; set; }
    public string SportIds { get; set; } = "";
}
