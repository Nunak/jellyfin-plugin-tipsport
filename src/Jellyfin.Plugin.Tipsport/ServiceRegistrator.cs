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
        services.AddSingleton<SessionStore>();
        services.AddSingleton<TipsportClient>();
        services.AddSingleton<IRelaySourceFactory, FfmpegSourceFactory>();
        services.AddSingleton<SharedRelay>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SharedRelay>());
        services.AddSingleton<IChannel, TipsportChannel>();
    }
}
