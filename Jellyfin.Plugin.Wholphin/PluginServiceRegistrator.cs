// Fork: services for the personal rows and ratings-on-add (GooseFlix #1635).

using Jellyfin.Plugin.Wholphin.Ratings;
using Jellyfin.Plugin.Wholphin.Rows;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Wholphin;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RowsService>();
        serviceCollection.AddSingleton<MdbListBridge>();
        serviceCollection.AddHostedService<RowsWarmupService>();
        serviceCollection.AddHostedService<RatingsOnAddService>();

        // GooseFlix #1671: the plugin's own MDBList ratings. Registered like the other background
        // services (by type): 0.1.1.502's factory registration never started it. The controller
        // shares the store; the run state is static.
        serviceCollection.AddSingleton<TitleRatingsStore>();
        serviceCollection.AddHostedService<TitleRatingsService>();
    }
}
