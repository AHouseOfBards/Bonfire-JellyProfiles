using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
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

            // Serves index.html with the script tags already in it, so the file on disk does
            // not have to be modified at all. This call site is the reason the whole approach
            // works: Jellyfin invokes RegisterServices from ApplicationHost.Init, which runs
            // inside ConfigureServices, before the request pipeline is built — so an
            // IStartupFilter registered here is picked up like any other.
            serviceCollection.AddTransient<IStartupFilter, ProfilesStartupFilter>();
            ProfilesIndexMiddleware.IsRegistered = true;
        }
    }
}
