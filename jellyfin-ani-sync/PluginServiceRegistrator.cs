using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace jellyfin_ani_sync;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = serviceCollection.AddHostedService<SessionServerEntry>();
        _ = serviceCollection.AddHostedService<UserDataServerEntry>();
    }
}
