using System;
using Jellyfin.Plugin.PlexBridge.Channels;
using Jellyfin.Plugin.PlexBridge.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PlexBridge;

/// <summary>
/// Registers Plex Bridge services with Jellyfin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHttpClient<PlexClient>()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        serviceCollection.AddSingleton<ProxyTokenService>();
        serviceCollection.AddSingleton<IChannel, PlexChannel>();
    }
}
