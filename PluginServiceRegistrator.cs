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
            //
            // It does NOT patch index.html in the default configuration, whatever this
            // comment used to say. Since 1.4.1 the default mode is Middleware, and
            // TryPatchIndex returns without touching the file unless an administrator has
            // explicitly chosen a mode that writes to it. The old wording here was the
            // written source of the most common false support report we get — an index.html
            // with no plugin tags in it is a HEALTHY install, not a failed injection.
            //
            // What the task actually does on every start: clean up DLLs left by a previous
            // update, and evaluate whether the configured mechanism is in place so the
            // dashboard can report it.
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
