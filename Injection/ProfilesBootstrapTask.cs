using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using System.Linq;
using MediaBrowser.Controller.Library;
using Jellyfin.Profiles.Auth;
using Jellyfin.Profiles.Controllers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// Runs once every time the Jellyfin server starts via the IHostedService lifecycle.
    ///
    /// The client script is served by <see cref="ProfilesIndexMiddleware"/>, so index.html
    /// is left as Jellyfin shipped it. What this does is remove the tags that releases up
    /// to 1.4.x (and the "file" and "both" modes after them) wrote into it, once the
    /// middleware has served a page, and report on the dashboard whether the middleware
    /// is working.
    /// </summary>
    public class ProfilesBootstrapTask : IHostedService
    {
        // The script fragments and the injection logic now live in WebInjection, shared
        // with the middleware that serves index.html straight from the request pipeline.
        // These forwarders keep the rest of this file — and the dashboard status it feeds —
        // reading exactly as before.
        internal static string ScriptVersion => WebInjection.ScriptVersion;

        /// <summary>
        /// The OS account the Jellyfin process is running under.
        ///
        /// Permission instructions otherwise have to guess: on Windows, Jellyfin may run as a
        /// service (NT AUTHORITY\NetworkService) or as the logged-in user (tray/desktop mode),
        /// and on Linux the service account is conventionally "jellyfin" but frequently is not.
        /// Guessing forces a blunderbuss grant to every local user; knowing the account lets the
        /// dashboard emit one exact command that grants access to precisely that account.
        /// </summary>
        internal static string? RunningAccount
        {
            get
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var name = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                    var user = Environment.UserName;
                    return string.IsNullOrWhiteSpace(user) ? null : user;
                }
                catch
                {
                    // Identity lookup is best-effort — the UI falls back to generic guidance.
                    return null;
                }
            }
        }

        // Exposed so the dashboard page JS can check whether setup is complete.
        internal static bool InjectionSucceeded { get; private set; }
        internal static bool IsVersionStale { get; private set; }
        internal static string? IndexPath { get; private set; }

        /// <summary>
        /// Other jellyfin-web index.html files found on this system, beyond the one being
        /// patched. Normally empty; anything here is a candidate for what is really being
        /// served.
        /// </summary>
        internal static List<string> OtherIndexPaths { get; private set; } = new();

        /// <summary>
        /// Human-readable reason the last injection attempt could not complete, or null when
        /// everything is fine. Surfaced on the dashboard so the banner can say what is actually
        /// wrong instead of listing every possible fix.
        /// </summary>
        internal static string? LastFailureReason { get; private set; }

        // These flags used to be computed exactly once, during StartAsync. That made the
        // dashboard banner a snapshot of server-boot state: a user could run the documented
        // chmod/icacls command, reload the page, and still see the failure warning, because
        // nothing re-read index.html until the next full Jellyfin restart. The static
        // self-reference lets the admin endpoints re-evaluate (and retry) on demand.
        /// <summary>
        /// True when this copy of the plugin was loaded after Jellyfin had already started,
        /// which is what happens when a plugin is installed or updated on a running server.
        /// <para>
        /// Jellyfin calls <c>RegisterServices</c> on every plugin during host startup, and
        /// that is the only place the pipeline hook can be added — an
        /// <see cref="Microsoft.AspNetCore.Hosting.IStartupFilter"/> registered later has
        /// nothing left to filter. So an assembly that is answering requests while its own
        /// <see cref="ProfilesIndexMiddleware.IsRegistered"/> is still false was loaded too
        /// late, and its middleware will not serve anything until the server restarts.
        /// </para>
        /// <para>
        /// Issue #25: this state looked exactly like a permissions failure. The old build's
        /// middleware is usually still in the pipeline, so the switcher keeps working while
        /// the settings page reports that injection failed and tells the administrator to
        /// chmod a file that has nothing to do with it. The fix is a restart, and only a
        /// real one — Jellyfin's own Restart button does not restart the process on most
        /// container images.
        /// </para>
        /// </summary>
        internal static bool RestartRequired => !ProfilesIndexMiddleware.IsRegistered;

        /// <summary>
        /// The version of this plugin that is <b>running</b>, and the newest version present
        /// on disk. They differ exactly when Bonfire has been installed or updated on a
        /// server that has not restarted since.
        /// <para>
        /// Issue #25: the dashboard said a restart was required and left it there. An
        /// administrator who has already restarted — or who restarted the wrong thing, which
        /// on Docker is Jellyfin's own Restart button on most images — has no way to tell a
        /// stale message from a real one. Two version numbers are checkable: if the running
        /// version is already the newest installed, the message is wrong and the reader can
        /// see that for themselves.
        /// </para>
        /// <para>
        /// Read off the folder names, because that is where the answer is. Jellyfin installs
        /// each version into its own <c>{Name}_{Version}</c> directory and loads exactly one
        /// of them; the rest sit there until it restarts. The prefix is taken from the folder
        /// this assembly was loaded from rather than hardcoded — the same reasoning as
        /// CleanupOldDlls, where a hardcoded "Bonfire" would never have matched.
        /// </para>
        /// </summary>
        internal static (string Running, string? NewestInstalled) FindInstalledVersions()
        {
            var running = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(ProfilesBootstrapTask).Assembly)
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(running))
            {
                var plus = running.IndexOf('+');
                if (plus > 0) running = running.Substring(0, plus);
            }
            else
            {
                running = typeof(ProfilesBootstrapTask).Assembly.GetName().Version?.ToString();
            }
            running ??= "unknown";

            var dir = Path.GetDirectoryName(typeof(ProfilesBootstrapTask).Assembly.Location);
            return (running, NewestVersionBeside(dir));
        }

        /// <summary>
        /// The highest version among the sibling plugin folders of <paramref name="ownDirectory"/>,
        /// or null when there is nothing to compare against — which includes being loaded from
        /// somewhere that is not a plugin folder at all, such as a test harness or a
        /// single-file publish. That is not an error.
        /// <para>
        /// Takes the directory rather than reading its own assembly location, so it can be
        /// driven against a directory laid out on purpose. The interesting cases here — a
        /// folder that is not ours, a version suffix that will not parse, several versions
        /// side by side — never occur in the one directory the harness happens to run from.
        /// </para>
        /// </summary>
        internal static string? NewestVersionBeside(string? ownDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(ownDirectory) || !Directory.Exists(ownDirectory)) return null;

                var here = new DirectoryInfo(ownDirectory);
                var parent = here.Parent;
                var underscore = here.Name.LastIndexOf('_');
                if (parent == null || underscore <= 0) return null;

                // "Bonfire_" — taken from the folder we are in rather than hardcoded, the
                // same reasoning as CleanupOldDlls: a literal "Bonfire" would stop matching
                // the day the plugin is renamed, and stop matching silently.
                var prefix = here.Name.Substring(0, underscore + 1);

                Version? newest = null;
                foreach (var sibling in parent.GetDirectories(prefix + "*"))
                {
                    var suffix = sibling.Name.Substring(prefix.Length);
                    if (Version.TryParse(suffix, out var v) && (newest == null || v > newest))
                        newest = v;
                }

                return newest?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when a newer version than the running one is sitting on disk waiting for a
        /// restart. Null when the versions cannot be compared, which is not the same as false.
        /// </summary>
        internal static bool? NewerVersionInstalled()
        {
            var (running, newest) = FindInstalledVersions();
            if (newest == null) return null;

            // The running version may carry a pre-release label ("1.5.6-beta") while the
            // folder name never does, so compare on the numeric part only.
            var dash = running.IndexOf('-');
            var numeric = dash > 0 ? running.Substring(0, dash) : running;
            if (!Version.TryParse(numeric, out var mine) || !Version.TryParse(newest, out var theirs))
                return null;

            return theirs > mine;
        }

        private static ProfilesBootstrapTask? _current;
        private static readonly object PatchLock = new();

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<ProfilesBootstrapTask> _logger;

        private readonly IUserManager _userManager;

        public ProfilesBootstrapTask(
            IApplicationPaths appPaths,
            IUserManager userManager,
            ILogger<ProfilesBootstrapTask> logger)
        {
            _appPaths = appPaths;
            _userManager = userManager;
            _logger = logger;
            _current = this;
        }

        // ── Client PIN login ────────────────────────────────────────────────────────
        //
        // A sub-profile is created with Jellyfin's default authentication provider and a
        // 64-character random password nobody knows, which is why it can only be entered
        // through Bonfire's own switch. To let a television offer it too, the profile has
        // to reach BonfirePinAuthenticationProvider, and Jellyfin picks a user's provider
        // by matching the stored id against each provider's type name:
        //
        //     var providers = _authenticationProviders.Where(i => i.IsEnabled).ToList();
        //     if (!string.IsNullOrEmpty(authenticationProviderId))
        //         providers = providers.Where(i => /* match on type name */).ToList();
        //
        // 1.6.1.2 CLEARED the id instead of setting it, so that every provider would be
        // tried and ours became an additional way in rather than the only one. That
        // reasoning was sound and the implementation was catastrophic, because the id is
        // also read back out of the database on every load:
        //
        //     public User(string username, string authenticationProviderId, string passwordResetProviderId)
        //     {
        //         ArgumentException.ThrowIfNullOrEmpty(authenticationProviderId);
        //
        // An empty string means EF Core cannot materialise that row at all — and
        // UserManager.GetUsers() enumerates EVERY user, so two blanked sub-profiles took
        // down user lookup for a whole 40-user server. Every client got HTTP 400 on every
        // endpoint that lists users, and turning the feature off did not help, because the
        // damage was in the database rather than in the running code. It needed manual SQL
        // to repair. See tests/cs/clientpin, which now proves the constructor rejects it.
        //
        // So the id is SET, never cleared, and the value to restore is taken from the
        // profile's own master rather than from a hardcoded type name — the master is an
        // ordinary Jellyfin user whose provider is whatever this server actually uses, so
        // there is nothing to keep in step with upstream.
        private const string DefaultProviderIdFallback =
            "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

        /// <summary>
        /// Re-points accounts after something that changes which provider they belong on:
        /// the client PIN setting, a master setting or clearing its PIN, a profile being
        /// deleted or imported. Waiting for the next restart would leave an account bound to
        /// a provider that no longer wants it, and Jellyfin offers a bound account no other.
        /// </summary>
        internal static void ReconcileAuthProvidersNow()
        {
            var self = _current;
            if (self == null) return;

            try
            {
                self.ReconcileAuthProviders();
            }
            catch (Exception ex)
            {
                self._logger.LogError(ex, "ProfilesPlugin: could not reconcile authentication providers after a settings change.");
            }
        }

        /// <summary>
        /// Points each account at the provider the current settings call for.
        /// <list type="bullet">
        /// <item><description>A sub-profile: Bonfire's when client PIN login is on, its
        /// master's own provider when it is off.</description></item>
        /// <item><description>A master that has a PIN and owns profiles: Bonfire's while the
        /// feature is on, so its PIN can be reached.</description></item>
        /// <item><description>Any other master still bound to Bonfire's: back to Jellyfin's
        /// own. This case is why the method looks at every master rather than only the ones
        /// with a PIN — a master that cleared its PIN, or deleted its last profile, used to
        /// stay bound for good, and the provider then refused its real password.</description></item>
        /// </list>
        /// <para>
        /// Runs at startup and after each of those changes.
        /// </para>
        /// </summary>
        internal void ReconcileAuthProviders()
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.Mappings == null) return;

            var ours = typeof(BonfirePinAuthenticationProvider).FullName!;
            var enabled = config.EnableClientPinLogin;

            List<ProfileMapping> rows;
            lock (ProfilesBaseController.ConfigLock)
            {
                rows = config.Mappings.ToList();
            }

            int changed = 0;

            void Point(Guid userId, Func<string?, string?> choose)
            {
                try
                {
                    var user = _userManager.GetUserById(userId);
                    if (user == null) return;

                    var target = choose(user.AuthenticationProviderId);

                    // The whole point of this method's history. Never write an empty or
                    // null id: it makes the row unreadable rather than merely unusable.
                    if (string.IsNullOrEmpty(target)) return;
                    if (string.Equals(user.AuthenticationProviderId, target, StringComparison.Ordinal)) return;

                    user.AuthenticationProviderId = target;
                    _userManager.UpdateUserAsync(user).GetAwaiter().GetResult();
                    changed++;
                }
                catch (Exception ex)
                {
                    // One bad account must not stop the rest, and must not stop startup.
                    _logger.LogError(ex,
                        "ProfilesPlugin: could not set the authentication provider for account {UserId}.",
                        userId);
                }
            }

            // Sub-profiles. Restored from the master rather than a hardcoded id when the
            // feature is off; MasterProviderId never answers with ours, so a master that is
            // itself still bound cannot be copied onto its profiles.
            foreach (var m in rows.Where(m => m.MasterUserId != m.ProfileUserId && m.MasterUserId != Guid.Empty))
            {
                var masterId = m.MasterUserId;
                Point(m.ProfileUserId, _ => enabled ? ours : MasterProviderId(masterId));
            }

            // Masters. A master that has set a PIN is bound so it can be entered on a
            // television without its full password; the provider keeps the password working
            // by delegating to Jellyfin's own (BonfirePinAuthenticationProvider.DelegateToJellyfin).
            //
            // Only a master on Jellyfin's own provider is ever bound. Delegation goes to that
            // provider, so binding an account that signs in through LDAP or SSO would swap
            // its real password check for one that cannot succeed.
            //
            // Restoring is the literal default provider rather than MasterProviderId, which
            // for a master would read back whatever it is currently bound to — this provider
            // — and pin it there permanently.
            var belongsHere = new HashSet<Guid>();
            if (enabled)
            {
                foreach (var m in rows.Where(m => m.MasterUserId != m.ProfileUserId && m.MasterUserId != Guid.Empty))
                    belongsHere.Add(m.ProfileUserId);
            }

            foreach (var masterId in rows.Select(m => m.MasterUserId).Where(id => id != Guid.Empty).Distinct())
            {
                var qualifies = enabled
                    && rows.Any(m => m.ProfileUserId == masterId && m.MasterUserId == masterId
                                     && !string.IsNullOrEmpty(m.PinHash))
                    && rows.Any(m => m.MasterUserId == masterId && m.ProfileUserId != masterId);
                if (qualifies) belongsHere.Add(masterId);

                Point(masterId, current =>
                {
                    var boundHere = string.Equals(current, ours, StringComparison.OrdinalIgnoreCase);
                    if (qualifies)
                    {
                        return boundHere || string.Equals(current, DefaultProviderIdFallback, StringComparison.OrdinalIgnoreCase)
                            ? ours
                            : null;
                    }

                    return boundHere ? DefaultProviderIdFallback : null;
                });
            }

            // Every other account still bound here (R-1318). The loops above only visit what
            // the configuration lists, and the configuration can forget: Jellyfin replaces an
            // unreadable file with defaults, and a rollback drops what an older build can't
            // read. Such an account is offered no provider but this one, which refuses it -
            // and with the feature off, offers it nothing at all - so a main account's real
            // password stopped working. Jellyfin's own provider is right for all of them: a
            // profile's password is random, so it is simply closed until it is a profile again.
            List<Jellyfin.Database.Implementations.Entities.User> everyone;
            try
            {
                // By name, never `_userManager.Users`: that property became GetUsers() in
                // 10.11.7 (and is gone in 12.0), and a direct reference compiles against 10.11.5
                // and throws MissingMethodException everywhere newer - which this catch then
                // swallowed, so the safety net below silently never ran. tests/binchk.sh found it.
                var type = _userManager.GetType();
                var method = type.GetMethod("GetUsers", System.Type.EmptyTypes);
                var listed = method != null
                    ? method.Invoke(_userManager, null)
                    : type.GetProperty("Users")?.GetValue(_userManager);
                everyone = ((IEnumerable<Jellyfin.Database.Implementations.Entities.User>?)listed
                            ?? Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>()).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: could not list accounts to find any left on Bonfire's provider.");
                everyone = new List<Jellyfin.Database.Implementations.Entities.User>();
            }

            foreach (var user in everyone)
            {
                if (belongsHere.Contains(user.Id)) continue;
                if (!string.Equals(user.AuthenticationProviderId, ours, StringComparison.OrdinalIgnoreCase)) continue;
                Point(user.Id, _ => DefaultProviderIdFallback);
            }

            if (changed > 0)
            {
                _logger.LogInformation(
                    "ProfilesPlugin: re-pointed {Count} account(s) at the authentication provider the "
                    + "TVs & Apps settings call for.",
                    changed);
            }
        }

        /// <summary>
        /// The provider a master account uses, which is whatever this server's ordinary
        /// users use. Preferred over a hardcoded type name so nothing has to be kept in
        /// step with upstream renames; the literal is only a last resort for a master we
        /// cannot read.
        /// <para>
        /// <b>Never this provider.</b> Since masters with a PIN are bound here too, a master
        /// being retired in the same pass is still bound when a sub-profile reads it — and
        /// the sub-profile would copy the very id being switched off. A disabled provider is
        /// filtered out of <c>GetAuthenticationProviders</c>, so the account would match
        /// nothing, fall through to Jellyfin's <c>InvalidAuthProvider</c>, and be unable to
        /// authenticate at all. Guarded here rather than by ordering the loop, because the
        /// next person to touch the loop should not have to know.
        /// </para>
        /// </summary>
        private string MasterProviderId(Guid masterId)
        {
            try
            {
                var master = _userManager.GetUserById(masterId);
                if (master != null
                    && !string.IsNullOrEmpty(master.AuthenticationProviderId)
                    && !string.Equals(
                        master.AuthenticationProviderId,
                        typeof(BonfirePinAuthenticationProvider).FullName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return master.AuthenticationProviderId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ProfilesPlugin: could not read master {MasterId} to find its authentication provider.",
                    masterId);
            }

            return DefaultProviderIdFallback;
        }


        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            CleanupOldDlls();

            // Before the reconciliation, which binds accounts by what the configuration lists:
            // a restored configuration puts them back where they were (R-1127, R-1318).
            _ = Plugin.Instance?.Configuration;
            if (Plugin.RestoredFrom != null)
            {
                if (Plugin.Restored)
                    _logger.LogWarning("ProfilesPlugin: the configuration file would not read, so the last copy that saved cleanly was restored. The unreadable file is kept at {Path}.", Plugin.RestoredFrom);
                else
                    _logger.LogError("ProfilesPlugin: the configuration file would not read and there was no earlier copy to restore, so Bonfire started empty. The unreadable file is kept at {Path}.", Plugin.RestoredFrom);
            }

            ReconcileAuthProviders();
            lock (PatchLock)
            {
                TryPatchIndex();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Re-reads index.html and recomputes the status flags without writing anything.
        /// Called when the dashboard loads so the banner always reflects the file as it is
        /// right now, not as it was when the server booted.
        /// </summary>
        internal static void RefreshInjectionStatus()
        {
            // Loaded after startup: nothing this copy reports about the pipeline can be
            // true yet, and the flags below would otherwise keep whatever they happened to
            // hold — for a fresh assembly, the `false` a static bool starts life with.
            // That false is what raised a permissions warning on servers whose only
            // problem was a pending restart.
            if (RestartRequired)
            {
                InjectionSucceeded = false;
                IsVersionStale = false;

                // Named versions rather than "a restart is required" on its own (issue #25).
                // An administrator who has already restarted, or who pressed Jellyfin's own
                // Restart button on a Docker image where it does not restart the process,
                // could not tell a stale message from a live one. "Running 1.5.3, 1.5.4 is
                // installed" is checkable against what they just did.
                var (running, newest) = FindInstalledVersions();
                var versions = newest != null && newest != running
                    ? $"Running {running}. Version {newest} is installed and starts once the "
                      + "server process restarts. "
                    : $"Running {running}. ";

                LastFailureReason =
                    versions
                    + "Bonfire has been installed or updated since Jellyfin started, so this "
                    + "version is not serving the client script yet. Restart Jellyfin to "
                    + "finish. On Docker restart the container — Jellyfin's own Restart "
                    + "button does not restart the process on most images.";
                return;
            }

            var self = _current;
            if (self == null) return;

            lock (PatchLock)
            {
                try
                {
                    // index.html is *supposed* to be clean, so reading it says nothing about
                    // whether the switcher will load. What matters is whether the pipeline
                    // hook is live.
                    IndexPath = IndexPath ?? self.FindIndexHtml();

                    // Not IsRegistered: PluginServiceRegistrator sets that unconditionally,
                    // so it is true wherever the plugin runs at all and says nothing about
                    // whether the hook reached the pipeline. Reporting it as success meant a
                    // green "installed and up to date" banner on a server where no script was
                    // reaching the page. The honest signal is a request actually handled —
                    // and anyone reading this page has already loaded the web client, so one
                    // has been.
                    var seen = ProfilesIndexMiddleware.HasSeenIndexRequest;
                    var lastError = ProfilesIndexMiddleware.LastError;

                    InjectionSucceeded = seen && lastError == null;
                    IsVersionStale = false;
                    LastFailureReason = InjectionSucceeded
                        ? null
                        : seen
                            ? "Bonfire is in the request pipeline but could not add its script "
                              + "to the last page it served: " + lastError
                            : "Bonfire has not handled a request for Jellyfin's web page, so "
                              + "nothing is adding the client script. Restart Jellyfin; if that "
                              + "does not help, a proxy may be serving its own copy of the page.";
                }
                catch (Exception ex)
                {
                    self._logger.LogDebug(ex, "ProfilesPlugin: Could not refresh injection status.");
                    LastFailureReason = "Could not read index.html to check status: " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Re-runs the full injection. Lets an administrator apply a permission fix and click
        /// "Retry" on the dashboard instead of having to restart the whole Jellyfin server.
        /// Returns true when the client script is present afterwards.
        /// </summary>
        internal static bool RunInjectionNow()
        {
            var self = _current;
            if (self == null) return InjectionSucceeded;

            lock (PatchLock)
            {
                // Re-resolve from scratch: the previous run may have cached a path from a
                // location that has since changed (e.g. a Jellyfin web-client update).
                IndexPath = null;
                self.TryPatchIndex();
            }

            RefreshInjectionStatus();
            return InjectionSucceeded;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>
        /// Removes the ".old" DLLs Jellyfin leaves behind when it cannot delete an in-use
        /// assembly during a plugin update (common on Windows, where the file stays locked
        /// until the process exits).
        /// </summary>
        private void CleanupOldDlls()
        {
            // Derive the directory from this assembly rather than guessing a name under
            // PluginsPath: Jellyfin names plugin folders "{Name}_{Version}", so a hardcoded
            // "Bonfire" never matches and the cleanup silently does nothing.
            string? pluginDir;
            try
            {
                pluginDir = Path.GetDirectoryName(typeof(ProfilesBootstrapTask).Assembly.Location);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: Could not resolve the plugin directory for cleanup.");
                return;
            }

            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
            {
                _logger.LogDebug("ProfilesPlugin: Plugin directory '{Dir}' unavailable; skipping .old cleanup.", pluginDir);
                return;
            }

            try
            {
                foreach (var file in Directory.GetFiles(pluginDir, "*.old"))
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogDebug("ProfilesPlugin: Removed stale plugin file {File}.", file);
                    }
                    catch (Exception ex)
                    {
                        // Still locked by another process — it will be retried next startup.
                        _logger.LogDebug(ex, "ProfilesPlugin: Could not delete stale file {File}.", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: Failed to enumerate stale plugin files in {Dir}.", pluginDir);
            }
        }

        private void TryPatchIndex()
        {
            // The middleware is the only way the script is served. index.html is not ours to
            // write: take the tags back out so the file returns to how Jellyfin shipped it. A
            // server saved in the old "file" or "both" mode lands here after an upgrade.
            TryUnpatchIndex();
        }

        /// <summary>
        /// Cleans index.html once the middleware has proved it is in the pipeline.
        ///
        /// <para>
        /// Called by <see cref="ProfilesIndexMiddleware"/> the first time it handles a
        /// request. That is the only moment we know the hook is genuinely live — being
        /// registered in DI does not mean the pipeline ever applies it — and it is
        /// therefore the only safe moment to remove tags that are currently the thing
        /// making the switcher work.
        /// </para>
        /// </summary>
        internal static void CleanIndexOnceMiddlewareIsLive()
        {
            if (System.Threading.Interlocked.Exchange(ref _cleanedForMiddleware, 1) != 0)
            {
                return;
            }

            var self = _current;
            if (self == null) return;

            // Off the request thread: this reads and rewrites a file, and the browser is
            // waiting on the response that triggered it.
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (PatchLock)
                {
                    try
                    {
                        self.TryUnpatchIndex();
                    }
                    catch (Exception ex)
                    {
                        self._logger.LogDebug(ex, "ProfilesPlugin: deferred index cleanup failed.");
                    }
                }
            });
        }

        private static int _cleanedForMiddleware;

        /// <summary>
        /// Takes the script tags back out of index.html, restoring the file to how Jellyfin
        /// shipped it. Runs when injection is set to middleware only, so that changing the
        /// setting actually cleans the file up rather than merely stopping further writes.
        /// <para>
        /// Failing here is harmless. The tags stay where they are, the middleware sees a
        /// document that is already injected and steps aside, and the switcher loads from
        /// the file exactly as it did before.
        /// </para>
        /// </summary>
        private void TryUnpatchIndex()
        {
            IndexPath = FindIndexHtml();
            // Not IsRegistered — RegisterServices sets that unconditionally, so it is true
            // wherever the plugin loads and says nothing about whether the hook reached the
            // pipeline. This is the same dishonest signal RefreshInjectionStatus stopped
            // using in 1.4.8; it was left behind here. At startup nothing has been served
            // yet, so this is false until the dashboard recomputes — which it always does
            // before reading it.
            InjectionSucceeded = ProfilesIndexMiddleware.HasSeenIndexRequest
                && ProfilesIndexMiddleware.LastError == null;
            IsVersionStale = false;
            LastFailureReason = null;

            if (IndexPath is null)
            {
                return;
            }

            // Registration proves nothing: IsRegistered is set unconditionally by
            // RegisterServices, so it is always true here. What has to be true before the
            // tags come out is that the middleware has actually handled a request — and at
            // startup nothing has. So the file stays patched until the middleware itself
            // says otherwise, via CleanIndexOnceMiddlewareIsLive below.
            //
            // Leaving it patched costs nothing in the meantime: the middleware sees the
            // tags already present and steps aside.
            if (!ProfilesIndexMiddleware.HasSeenIndexRequest)
            {
                _logger.LogDebug(
                    "ProfilesPlugin: middleware-only injection, but no page request has reached "
                    + "the hook yet. Leaving {Path} as it is until one does.", IndexPath);
                return;
            }

            try
            {
                var html = File.ReadAllText(IndexPath);
                if (!WebInjection.Remove(html, out var cleaned))
                {
                    return;
                }

                WriteFileAtomic(IndexPath, cleaned);
                _logger.LogInformation(
                    "ProfilesPlugin: Removed the client script tags from {Path}. index.html is "
                    + "now served from the request pipeline instead.", IndexPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "ProfilesPlugin: Could not clean {Path}. Harmless — the middleware will "
                    + "see the existing tags and step aside.", IndexPath);
            }
        }

        /// <summary>
        /// Writes via a temp file in the same directory then replaces the original, so a
        /// crash or a full disk mid-write cannot leave Jellyfin with a truncated index.html
        /// (which would break the entire web client, not just this plugin).
        ///
        /// Falls back to a direct write if the atomic replace isn't permitted — some
        /// container mounts allow writing a file but not creating siblings.
        /// </summary>
        private void WriteFileAtomic(string path, string contents)
        {
            try
            {
                if (File.Exists(path))
                {
                    var attr = File.GetAttributes(path);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                    }
                }
            }
            catch (Exception ex)
            {
                // Not fatal: the write below may still succeed, and if it doesn't the caller
                // reports the real permission error.
                _logger.LogDebug(ex, "ProfilesPlugin: Could not clear the read-only attribute on {Path}.", path);
            }

            // NOTE: the temp file is created alongside index.html, so this path needs write
            // permission on the *directory*, not just the file. The documented fix commands
            // only grant permission on index.html itself, so on most Linux/Docker installs the
            // atomic path fails and the in-place fallback below is what actually runs.
            var tempPath = path + ".bonfire.tmp";
            try
            {
                File.WriteAllText(tempPath, contents);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex,
                    "ProfilesPlugin: Atomic replace unavailable for {Path}; writing in place.", path);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
                File.WriteAllText(path, contents);
            }
        }

        // ── Path discovery ───────────────────────────────────────────────────────

        /// <summary>
        /// A key that is the same for two paths pointing at the same file.
        ///
        /// The Linux packages ship several of the candidate directories as symlinks to one
        /// another, so comparing normalised path strings would report a duplicate copy of
        /// jellyfin-web on a perfectly ordinary install and send the administrator looking
        /// for a bind mount that does not exist.
        /// </summary>
        private static string FileIdentity(string path)
        {
            try
            {
                // The link is usually on the directory (/usr/lib/jellyfin/web →
                // /usr/share/jellyfin/web), not on index.html, so resolve both.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dirTarget = Directory.ResolveLinkTarget(dir, returnFinalTarget: true);
                    if (dirTarget != null) path = Path.Combine(dirTarget.FullName, Path.GetFileName(path));
                }

                var fileTarget = File.ResolveLinkTarget(path, returnFinalTarget: true);
                if (fileTarget != null) return fileTarget.FullName;
            }
            catch
            {
                // Not a link, or the target cannot be read — the path itself will do.
            }
            return path;
        }

        /// <summary>
        /// Searches all locations Jellyfin is known to place its web client on every
        /// supported platform (Windows installer, Linux packages, Docker images,
        /// portable/Scoop). Returns the full path to index.html or <c>null</c>.
        /// </summary>
        private string? FindIndexHtml()
        {
            var candidates = new List<string?>();

            // ── 1. Jellyfin's own reported WebPath (highest confidence) ──────────
            //    IApplicationPaths.WebPath is set by Jellyfin at startup from its
            //    own config, so this is correct on any properly configured install.
            candidates.Add(_appPaths.WebPath);

            // ── 2. Relative to the running executable ────────────────────────────
            //    Works for Windows portable, Scoop, and some Docker images where
            //    jellyfin-web is placed next to / near the server binary.
            var baseDir = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDir, "jellyfin-web"));
            candidates.Add(Path.Combine(baseDir, "..", "jellyfin-web"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "jellyfin-web"));

            // ── 3. Windows — standard installer path ─────────────────────────────
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                candidates.Add(Path.Combine(pf, "Jellyfin", "Server", "jellyfin-web"));
                candidates.Add(Path.Combine(pf, "Jellyfin", "jellyfin-web"));
            }

            // ── 4. Linux — package manager installs (apt/rpm/AUR) ───────────────
            candidates.Add("/usr/share/jellyfin/web");
            candidates.Add("/usr/lib/jellyfin/web");
            candidates.Add("/usr/local/share/jellyfin/web");
            candidates.Add("/opt/jellyfin/web");

            // ── 5. Docker — common image layouts ────────────────────────────────
            candidates.Add("/jellyfin/jellyfin-web");
            candidates.Add("/jellyfin/web");
            candidates.Add("/app/jellyfin-web");
            candidates.Add("/config/jellyfin-web");
            candidates.Add("/data/jellyfin-web");

            // Every match is collected, not just the first. A second copy of jellyfin-web
            // is the likeliest explanation for a tag that writes cleanly and then is not
            // there when the browser loads the page (issue #17), and the administrator
            // cannot check for one they were never told about.
            var found = new List<string>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in candidates)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                try
                {
                    var fullDir = Path.GetFullPath(dir);
                    var candidate = Path.Combine(fullDir, "index.html");
                    if (File.Exists(candidate) && identities.Add(FileIdentity(candidate)))
                    {
                        found.Add(candidate);
                    }
                }
                catch (Exception ex)
                {
                    // Path may be syntactically invalid on this OS — skip it.
                    _logger.LogDebug(ex, "ProfilesPlugin: Candidate path '{Dir}' is invalid or inaccessible.", dir);
                }
            }

            if (found.Count == 0) return null;

            OtherIndexPaths = found.Skip(1).ToList();
            if (OtherIndexPaths.Count > 0)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: More than one jellyfin-web index.html exists on this system. " +
                    "Patching {Path}; also found {Others}. If the switcher does not appear, Jellyfin " +
                    "is serving one of the others.",
                    found[0], string.Join(", ", OtherIndexPaths));
            }
            else
            {
                _logger.LogDebug("ProfilesPlugin: Found index.html at {Path}.", found[0]);
            }

            return found[0];
        }

        private static bool IsRunningInDocker() =>
            File.Exists("/.dockerenv") ||
            string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase);
    }
}
