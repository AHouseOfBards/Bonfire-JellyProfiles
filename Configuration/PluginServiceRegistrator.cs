using Campground.Shared.Injection;
using Campground.Shared.Registry;
using Jellyfin.Profiles.Auth;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
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
            // First, so the host stops it last: once every other service has unhooked its
            // events, it cancels Bonfire's background work and waits for it (BackgroundWork).
            serviceCollection.AddHostedService<Configuration.BackgroundWork.Stopper>();

            // ProfilesBootstrapTask runs at every server startup.
            //
            // It does NOT patch index.html, whatever this comment used to say. The "file"
            // and "both" modes that did are gone; TryPatchIndex only takes tags back out of
            // the file. The old wording here was the written source of the most common
            // false support report we get — an index.html with no plugin tags in it is a
            // HEALTHY install, not a failed injection.
            //
            // What the task actually does on every start: clean up DLLs left by a previous
            // update, and evaluate whether the middleware is in place so the dashboard can
            // report it.
            serviceCollection.AddHostedService<ProfilesBootstrapTask>();

            // Bonfire's middleware: the sign-in gates, the proxy switcher, the user-list edits,
            // and noting that index.html is being served (the tags themselves are added by the
            // coordinator below), so the file on disk is never modified. This call site is the
            // reason the whole approach
            // works: Jellyfin invokes RegisterServices from ApplicationHost.Init, which runs
            // inside ConfigureServices, before the request pipeline is built — so an
            // IStartupFilter registered here is picked up like any other.
            serviceCollection.AddTransient<IStartupFilter, ProfilesStartupFilter>();
            ProfilesIndexMiddleware.IsRegistered = true;

            // The Campground coordinator, which adds the script tags to index.html for every
            // Campground plugin on the server in one pass (Campground.Shared, §3.5). Every
            // plugin adds its copy and the newest one runs; the others pass the page through
            // untouched.
            //
            // AFTER ProfilesStartupFilter, deliberately. ASP.NET builds startup filters so
            // the first one registered is the outermost middleware, which keeps Bonfire's
            // where it has always been — in front of everything, where the sign-in gates,
            // the proxy switcher and the user-list edits must run — and puts the coordinator
            // inside it, buffering the document the rest of the pipeline produces.
            // tests/cs/pipeline builds the pipeline from these registrations, in this order.
            serviceCollection.AddCampgroundInjection(PluginIds.Bonfire, WebInjection.Registration);

            // Offers campground.limits.v1 to other Campground plugins. After the coordinator's
            // filter so the Campground generation is open when it runs (see LimitsStartupFilter).
            serviceCollection.AddSingleton<IStartupFilter, LimitsStartupFilter>();

            // Lets a sub-profile be entered with its PIN from a client that has no
            // switcher — a television, mostly. UserManager takes
            // IEnumerable<IAuthenticationProvider> in its constructor, so registering here
            // is all it takes to be offered one; the provider reports IsEnabled false
            // until an administrator turns the feature on, and Jellyfin filters disabled
            // providers out before it matches a user against one.
            serviceCollection.AddSingleton<IAuthenticationProvider, BonfirePinAuthenticationProvider>();

            // Notes which household a device belongs to, every time anyone signs in on it.
            //
            // Without this the sign-in-screen profile list cannot work on a television at
            // all: Jellyfin deletes its own Device row on logout, and Android TV logs out
            // before opening the picker. Bonfire's record is the only one that survives.
            serviceCollection.AddHostedService<BonfireSessionListener>();

            // Counts what limited profiles watch and stops it when their time is up. Does
            // nothing but read the session list while no profile has limits.
            serviceCollection.AddHostedService<ViewingTimer>();

            // Ends the profile sessions the picker made before the last restart.
            serviceCollection.AddHostedService<ProxyPickerStartup>();

            // For other plugins: which household a user belongs to. Read-only.
            serviceCollection.AddSingleton<IBonfireProfiles, BonfireProfiles>();

            // The same answers for the other Campground plugins: campground.household.v1 and
            // campground.device.v1 (see BonfireCampground).
            serviceCollection.AddHostedService<CampgroundProvidersTask>();
        }
    }
}
