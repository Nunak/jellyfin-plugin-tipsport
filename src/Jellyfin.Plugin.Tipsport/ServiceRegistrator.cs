using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Tipsport;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddTransient<TipsportPlaybackFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<TipsportPlaybackFilter>());
        services.AddSingleton<LiveMetadataCache>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<ILoginBrowserFactory, ChromiumLoginBrowserFactory>();
        services.AddSingleton<ISessionVerifier, ProviderSessionVerifier>();
        services.AddSingleton<BrowserLogin>(sp => new BrowserLogin(sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<ILoginBrowserFactory>(), sp.GetRequiredService<ISessionVerifier>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BrowserLogin>());
        services.AddSingleton<TipsportClient>();
        services.AddSingleton<MatchArtwork>();
        services.AddSingleton<StableCatalogueItems>();
        services.AddSingleton<IRelaySourceFactory, FfmpegSourceFactory>();
        services.AddSingleton<SharedRelay>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SharedRelay>());
        services.AddSingleton<IChannel, TipsportChannel>();
    }
}
