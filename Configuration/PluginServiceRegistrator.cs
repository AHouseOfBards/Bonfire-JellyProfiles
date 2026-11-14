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
    /// Registers the plugin's hosted services with Jellyfin's DI container. Jellyfin finds this
    /// class in the plugin assembly. It needs a parameterless constructor, so add no parameters.
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

            // ProfilesBootstrapTask runs at every server startup. It does not patch index.html: the
            // "file" and "both" modes that did are gone, and TryPatchIndex only takes tags back out.
            // The old wording here was the source of the most common false support report: an
            // index.html with no plugin tags is a HEALTHY install. On every start the task cleans up
            // DLLs left by a previous update and checks whether the middleware is in place so the
            // dashboard can report it.
            serviceCollection.AddHostedService<ProfilesBootstrapTask>();

            // Bonfire's middleware: the sign-in gates, the proxy switcher, the user-list edits, and
            // noting that index.html is being served (the coordinator below adds the tags), so the
            // file on disk is never modified. Jellyfin calls RegisterServices from
            // ApplicationHost.Init, inside ConfigureServices and before the request pipeline is built,
            // so an IStartupFilter registered here is picked up like any other.
            serviceCollection.AddTransient<IStartupFilter, ProfilesStartupFilter>();
            ProfilesIndexMiddleware.IsRegistered = true;

            // The Campground coordinator, which adds the script tags to index.html for every Campground
            // plugin on the server in one pass (Campground.Shared, section 3.5). Every plugin adds its
            // copy and the newest one runs; the others pass the page through.
            //
            // After ProfilesStartupFilter, on purpose. The first filter registered is the outermost
            // middleware, which keeps Bonfire's in front of everything, where the sign-in gates, proxy
            // switcher and user-list edits must run, and puts the coordinator inside it, buffering what
            // the rest of the pipeline produces. tests/cs/pipeline builds the pipeline from these
            // registrations, in this order.
            serviceCollection.AddCampgroundInjection(PluginIds.Bonfire, WebInjection.Registration);

            // Offers campground.limits.v1 to other Campground plugins. After the coordinator's
            // filter so the Campground generation is open when it runs (see LimitsStartupFilter).
            serviceCollection.AddSingleton<IStartupFilter, LimitsStartupFilter>();

            // Lets a sub-profile be entered with its PIN from a client with no switcher, mostly a
            // television. UserManager takes IEnumerable<IAuthenticationProvider>, so registering here
            // is enough; the provider reports IsEnabled false until an administrator turns the feature
            // on, and Jellyfin filters disabled providers out before matching a user.
            serviceCollection.AddSingleton<IAuthenticationProvider, BonfirePinAuthenticationProvider>();

            // Notes which household a device belongs to, every time anyone signs in on it. Without
            // this the sign-in-screen profile list cannot work on a television: Jellyfin deletes its
            // own Device row on logout, and Android TV logs out before opening the picker.
            serviceCollection.AddHostedService<BonfireSessionListener>();

            // Passes a change made to a main account in Dashboard → Users on to its profiles
            // when it is made, not when one is next opened (R-1301).
            serviceCollection.AddHostedService<MasterUpdateListener>();

            // Counts what limited profiles watch and stops it when their time is up. Does
            // nothing but read the session list while no profile has limits.
            serviceCollection.AddHostedService<ViewingTimer>();

            // Ends the profile sessions the picker made before the last restart.
            serviceCollection.AddHostedService<ProxyPickerStartup>();

            // A user deleted in Dashboard → Users takes Bonfire's records about them along, and a
            // clean-up missed while Bonfire wasn't running is done at the next start
            // (Campground.Shared 1.0.1, UserDataCleanup).
            serviceCollection.AddHostedService<UserRecordsStartup>();
            serviceCollection.AddScoped<MediaBrowser.Controller.Events.IEventConsumer<Jellyfin.Data.Events.Users.UserDeletedEventArgs>, UserRecordsDeleted>();

            // For other plugins: which household a user belongs to. Read-only.
            serviceCollection.AddSingleton<IBonfireProfiles, BonfireProfiles>();

            // The same answers for the other Campground plugins: campground.household.v1 and
            // campground.device.v1 (see BonfireCampground).
            serviceCollection.AddHostedService<CampgroundProvidersTask>();
        }
    }
}
