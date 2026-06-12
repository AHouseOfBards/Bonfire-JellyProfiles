using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// Registers the plugin's hosted services with Jellyfin's DI container.
    /// Jellyfin discovers this class automatically from the plugin assembly.
    ///
    /// NOTE: IPluginServiceRegistrator requires a parameterless constructor.
    /// Do not add constructor parameters to this class.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(
            IServiceCollection serviceCollection,
            IServerApplicationHost applicationHost)
        {
            // ProfilesBootstrapTask runs at every server startup.
            // It patches index.html so the client script loads automatically.
            serviceCollection.AddHostedService<ProfilesBootstrapTask>();
        }
    }
}
