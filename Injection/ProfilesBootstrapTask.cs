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
    /// Runs once each time the Jellyfin server starts (IHostedService). The client script is served by
    /// <see cref="ProfilesIndexMiddleware"/>, so index.html stays as Jellyfin shipped it. This removes
    /// the tags that releases up to 1.4.x (and the "file" and "both" modes after them) wrote into it,
    /// once the middleware has served a page, and reports on the dashboard whether the middleware
    /// works.
    /// </summary>
    public class ProfilesBootstrapTask : IHostedService
    {
        // The script fragments and injection logic live in WebInjection, shared with the middleware.
        // These forwarders keep the rest of this file and the dashboard status reading as before.
        internal static string ScriptVersion => WebInjection.ScriptVersion;

        /// <summary>
        /// The OS account the Jellyfin process runs under. Without it the permission instructions have to
        /// guess: on Windows Jellyfin may be a service (NT AUTHORITY\NetworkService) or the logged-in
        /// user, and on Linux the account is conventionally "jellyfin" but often is not. Knowing it lets
        /// the dashboard give one exact command for that account.
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
                    // Best-effort: the UI falls back to generic guidance.
                    return null;
                }
            }
        }

        // Exposed so the dashboard page JS can check whether setup is complete.
        internal static bool InjectionSucceeded { get; private set; }
        internal static bool IsVersionStale { get; private set; }
        internal static string? IndexPath { get; private set; }

        /// <summary>
        /// Why the last injection attempt could not complete, or null when all is well. Shown on the
        /// dashboard so the banner says what is wrong instead of listing every possible fix.
        /// </summary>
        internal static string? LastFailureReason { get; private set; }

        // These flags used to be computed once, in StartAsync, so the dashboard banner was a snapshot
        // of boot state: a user could run the documented chmod/icacls command, reload, and still see
        // the failure until the next full restart. The static self-reference lets the admin endpoints
        // re-evaluate and retry on demand.
        /// <summary>
        /// True when this copy of the plugin was loaded after Jellyfin had started, which happens when a
        /// plugin is installed or updated on a running server.
        /// <para>
        /// Jellyfin calls <c>RegisterServices</c> on every plugin during host startup, the only place the
        /// pipeline hook can be added (an <see cref="Microsoft.AspNetCore.Hosting.IStartupFilter"/>
        /// registered later has nothing to filter). So an assembly answering requests while its own
        /// <see cref="ProfilesIndexMiddleware.IsRegistered"/> is false was loaded too late, and its
        /// middleware serves nothing until the server restarts.
        /// </para>
        /// <para>
        /// Issue #25: this looked exactly like a permissions failure. The old build's middleware is usually
        /// still in the pipeline, so the switcher keeps working while the settings page says injection
        /// failed and tells the administrator to chmod a file that has nothing to do with it. The fix is a
        /// real restart; Jellyfin's own Restart button does not restart the process on most container
        /// images.
        /// </para>
        /// </summary>
        internal static bool RestartRequired => !ProfilesIndexMiddleware.IsRegistered;

        /// <summary>
        /// The version of this plugin that is <b>running</b>, and the newest version on disk. They differ
        /// exactly when Bonfire was installed or updated on a server that has not restarted since.
        /// <para>
        /// Issue #25: the dashboard said a restart was required and left it there. An administrator who
        /// already restarted (or pressed Docker's Restart button, which does nothing on most images) could
        /// not tell a stale message from a real one. Two version numbers are checkable: if the running
        /// version is already the newest installed, the message is wrong and the reader can see it.
        /// </para>
        /// <para>
        /// Read off the folder names: Jellyfin installs each version into its own <c>{Name}_{Version}</c>
        /// directory and loads one. The prefix comes from the folder this assembly loaded from, not a
        /// hardcoded name (same reasoning as CleanupOldDlls).
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
        /// The highest version among the sibling plugin folders of <paramref name="ownDirectory"/>, or
        /// null when there is nothing to compare, including when loaded from somewhere that is not a plugin
        /// folder (a test harness, a single-file publish). That is not an error.
        /// <para>
        /// Takes the directory instead of reading its own assembly location so a test can use a directory
        /// laid out on purpose: a folder that is not ours, a version suffix that will not parse, several
        /// versions side by side.
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

                // "Bonfire_", taken from the folder we are in, as in CleanupOldDlls: a literal "Bonfire"
                // would silently stop matching if the plugin were renamed.
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
        /// True when a newer version than the running one is on disk waiting for a restart. Null when the
        /// versions cannot be compared, which is not the same as false.
        /// </summary>
        internal static bool? NewerVersionInstalled()
        {
            var (running, newest) = FindInstalledVersions();
            if (newest == null) return null;

            // The running version may carry a pre-release label ("1.5.6-beta"); the folder name never
            // does. Compare the numeric part only.
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

        // Client PIN login.
        //
        // A sub-profile is created with Jellyfin's default authentication provider and a random
        // 64-character password nobody knows, so it can only be entered through Bonfire's own switch.
        // For a television to offer it too, the profile has to reach BonfirePinAuthenticationProvider.
        // Jellyfin picks a user's provider by matching the stored id against each provider's type name.
        //
        // 1.6.1.2 cleared the id instead of setting it, so every provider would be tried and ours
        // would be one way in among others. That broke the server: the id is read back from the
        // database on every load, and the User constructor does
        // ArgumentException.ThrowIfNullOrEmpty(authenticationProviderId). EF Core cannot materialize
        // a row with an empty id, and UserManager.GetUsers() enumerates every user, so two blanked
        // sub-profiles took down user lookup for a 40-user server. Every client got HTTP 400 on every
        // endpoint that lists users, and turning the feature off did not help because the damage was
        // in the database. Repair needed manual SQL. tests/cs/clientpin proves the constructor
        // rejects it.
        //
        // So the id is set, never cleared, and the value to restore comes from the profile's master,
        // an ordinary Jellyfin user whose provider is whatever this server uses, rather than a
        // hardcoded type name.
        private const string DefaultProviderIdFallback =
            "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

        /// <summary>
        /// Re-points accounts after something changes which provider they belong on: the client PIN
        /// setting, a master setting or clearing its PIN, a profile deleted or imported. Waiting for the
        /// next restart would leave an account bound to a provider that no longer wants it, and Jellyfin
        /// offers a bound account no other.
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
        /// <item><description>A sub-profile: Bonfire's when client PIN login is on, its master's own
        /// provider when off.</description></item>
        /// <item><description>A master that has a PIN and owns profiles: Bonfire's while the feature is
        /// on, so its PIN can be reached.</description></item>
        /// <item><description>Any other master still bound to Bonfire's: back to Jellyfin's own. This is
        /// why every master is checked, not only those with a PIN: one that cleared its PIN or deleted its
        /// last profile used to stay bound, and the provider then refused its real
        /// password.</description></item>
        /// </list>
        /// <para>
        /// Runs at startup and after each of those changes.
        /// </para>
        /// </summary>
        internal void ReconcileAuthProviders()
        {
            // One at a time (r2-16-03): startup and a settings save both come here, and a run that read
            // the configuration earlier but finished later would overwrite the newer run's answer. Each
            // run reads the configuration only once it holds the gate. A SemaphoreSlim, not a lock: it is
            // not the configuration object, and the work inside waits on the user manager.
            ReconcileGate.Wait();
            try
            {
                ReconcileAuthProvidersCore();
            }
            finally
            {
                ReconcileGate.Release();
            }
        }

        private static readonly System.Threading.SemaphoreSlim ReconcileGate = new(1, 1);

        /// <summary>
        /// Plugin uninstall: puts Jellyfin's own provider back on every account Bonfire bound. An account
        /// left bound to a provider that is no longer loaded is refused its real password everywhere.
        /// Never throws; an uninstall must not be stopped by this.
        /// </summary>
        internal static void RestoreProvidersForUninstall()
        {
            var self = _current;
            if (self == null) return;

            try
            {
                ReconcileGate.Wait();
                try { self.ReconcileAuthProvidersCore(forceOff: true); }
                finally { ReconcileGate.Release(); }
            }
            catch (Exception ex)
            {
                self._logger.LogError(ex, "ProfilesPlugin: could not put Jellyfin's authentication provider back before uninstall.");
            }
        }

        private void ReconcileAuthProvidersCore(bool forceOff = false)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.Mappings == null) return;

            var ours = typeof(BonfirePinAuthenticationProvider).FullName!;
            var enabled = config.EnableClientPinLogin && !forceOff;

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

                    // Never write an empty or null id: it makes the row unreadable, not merely unusable (see
                    // the Client PIN login note above).
                    if (string.IsNullOrEmpty(target)) return;
                    if (string.Equals(user.AuthenticationProviderId, target, StringComparison.Ordinal)) return;

                    user.AuthenticationProviderId = target;
                    _userManager.UpdateUserAsync(user).GetAwaiter().GetResult();
                    changed++;
                }
                catch (Exception ex)
                {
                    // One bad account must not stop the rest or startup.
                    _logger.LogError(ex,
                        "ProfilesPlugin: could not set the authentication provider for account {UserId}.",
                        userId);
                }
            }

            // Sub-profiles. Restored from the master, not a hardcoded id, when the feature is off.
            // MasterProviderId never answers with ours, so a master that is itself still bound cannot be
            // copied onto its profiles.
            foreach (var m in rows.Where(m => m.MasterUserId != m.ProfileUserId && m.MasterUserId != Guid.Empty))
            {
                var masterId = m.MasterUserId;
                Point(m.ProfileUserId, _ => enabled ? ours : MasterProviderId(masterId));
            }

            // Masters. A master with a PIN is bound so it can be entered on a television without its full
            // password; the provider keeps the password working by delegating to Jellyfin's own
            // (BonfirePinAuthenticationProvider.DelegateToJellyfin).
            //
            // Only a master on Jellyfin's own provider is bound. Delegation goes to that provider, so
            // binding an LDAP or SSO account would swap its real password check for one that cannot
            // succeed.
            //
            // Restoring uses the literal default provider, not MasterProviderId, which for a master reads
            // back its current binding (this provider) and would pin it there.
            var belongsHere = new HashSet<Guid>();
            if (enabled)
            {
                foreach (var m in rows.Where(m => m.MasterUserId != m.ProfileUserId && m.MasterUserId != Guid.Empty))
                    belongsHere.Add(m.ProfileUserId);
            }

            foreach (var masterId in rows.Select(m => m.MasterUserId).Where(id => id != Guid.Empty).Distinct())
            {
                // Never an administrator: if Bonfire is removed or fails to load, a bound account's real
                // password stops working, and an administrator locked out that way cannot repair anyone
                // else. An administrator already bound is put back.
                var isAdmin = IsAdministrator(masterId);
                var qualifies = enabled
                    && !isAdmin
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

                    // An administrator moved back loses the main PIN on a TV's sign-in screen; say so,
                    // once, as it happens (r3-04-02).
                    if (boundHere && isAdmin)
                        _logger.LogInformation("ProfilesPlugin: {User} is an administrator, so it signs in with its password again, not its PIN.", masterId);
                    return boundHere ? DefaultProviderIdFallback : null;
                });
            }

            // Every other account still bound here (R-1318). The loops above only visit what the
            // configuration lists, and the configuration can forget: Jellyfin replaces an unreadable file
            // with defaults, and a rollback drops what an older build can't read. Such an account is
            // offered no provider but this one, which refuses it (with the feature off, nothing at all),
            // so a main account's real password stopped working. Jellyfin's own provider is right for all
            // of them: a profile's password is random, so it stays closed until it is a profile again.
            List<Jellyfin.Database.Implementations.Entities.User> everyone;
            try
            {
                // By name, never `_userManager.Users`: it became GetUsers() in 10.11.7 (gone in 12.0), and a
                // direct reference compiles against 10.11.5 and throws MissingMethodException on anything
                // newer. This catch swallowed that, so the safety net below silently never ran. tests/binchk.sh
                // found it.
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

        private bool IsAdministrator(Guid userId)
        {
            try
            {
                var user = _userManager.GetUserById(userId);
                return user != null
                    && Jellyfin.Data.UserEntityExtensions.HasPermission(user,
                        Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator);
            }
            catch (Exception ex)
            {
                // Cannot tell: leave it unbound, the safe side. Said, because it stops the account's
                // PIN sign-in on TVs and nothing else would show why (r3-04-02).
                _logger.LogWarning(ex, "ProfilesPlugin: could not tell whether {User} is an administrator, so its PIN sign-in is off until the next start.", userId);
                return true;
            }
        }

        /// <summary>
        /// The provider a master account uses, which is whatever this server's ordinary users use.
        /// Preferred over a hardcoded type name so nothing has to track upstream renames; the literal is a
        /// last resort for a master we cannot read.
        /// <para>
        /// <b>Never this provider.</b> Masters with a PIN are bound here too, so a master retired in the
        /// same pass is still bound when a sub-profile reads it, and the sub-profile would copy the id
        /// being switched off. A disabled provider is filtered out of <c>GetAuthenticationProviders</c>,
        /// so the account would match nothing, fall through to Jellyfin's <c>InvalidAuthProvider</c> and
        /// be unable to authenticate. Guarded here rather than by loop ordering, which the next person
        /// to touch the loop should not have to know about.
        /// </para>
        /// </summary>
        private string MasterProviderId(Guid masterId)
        {
            try
            {
                return StandaloneProvider(_userManager.GetUserById(masterId)?.AuthenticationProviderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ProfilesPlugin: could not read master {MasterId} to find its authentication provider.",
                    masterId);
            }

            return DefaultProviderIdFallback;
        }

        /// <summary>
        /// The provider an account can sign in with on its own, given its master's: the master's when that
        /// is Jellyfin's own provider, Jellyfin's own otherwise. Never an LDAP or SSO provider, which has
        /// never heard of a sub-profile ("could never sign in"), and never this provider. The one rule for
        /// both the reconciliation here and the controller's made-standalone path (R-1102).
        /// </summary>
        internal static string StandaloneProvider(string? masterProviderId)
            => !string.IsNullOrEmpty(masterProviderId)
               && masterProviderId.EndsWith(".DefaultAuthenticationProvider", StringComparison.Ordinal)
                ? masterProviderId
                : DefaultProviderIdFallback;


        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            CleanupOldDlls();

            // Staged files from writes the last run never finished. An hour, though nothing writes
            // before start-up is done, so a clock that jumped cannot take one in flight.
            try
            {
                var swept = Controllers.ProfilesBaseController.SweepStagedFiles(
                    Path.Combine(_appPaths.DataPath, "plugins", "ProfilesManagement"), TimeSpan.FromHours(1));
                if (swept > 0) _logger.LogInformation("ProfilesPlugin: removed {Count} unfinished file writes left by the last run.", swept);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: could not look for unfinished file writes.");
            }

            // Before the reconciliation, which binds accounts by what the configuration lists: a restored
            // configuration puts them back where they were (R-1127, R-1318).
            _ = Plugin.Instance?.Configuration;
            if (Plugin.RestoredFrom != null)
            {
                if (Plugin.Restored)
                    _logger.LogWarning("ProfilesPlugin: the configuration file would not read, so the last copy that saved cleanly was restored. The unreadable file is kept at {Path}.", Plugin.RestoredFrom);
                else
                    _logger.LogError("ProfilesPlugin: the configuration file would not read and there was no earlier copy to restore, so Bonfire started empty. The unreadable file is kept at {Path}.", Plugin.RestoredFrom);
            }

            // An exception out of a host's StartAsync stops Jellyfin starting. The same call after a
            // settings change is already guarded in ReconcileAuthProvidersNow; a bad row must not take the
            // server down here. (It still waits on UpdateUserAsync for each account it moves, once, for
            // the accounts that are wrong, and never on a server already settled.)
            try
            {
                ReconcileAuthProviders();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: could not reconcile authentication providers at startup; the server starts anyway.");
            }

            lock (PatchLock)
            {
                TryPatchIndex();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Re-reads index.html and recomputes the status flags without writing anything. Called when the
        /// dashboard loads so the banner reflects the file as it is now, not as at boot.
        /// </summary>
        internal static void RefreshInjectionStatus()
        {
            // Loaded after startup: nothing this copy reports about the pipeline can be true yet, and the
            // flags would keep whatever they held (for a fresh assembly, the false a static bool starts
            // with). That false raised a permissions warning on servers whose only problem was a pending
            // restart.
            if (RestartRequired)
            {
                InjectionSucceeded = false;
                IsVersionStale = false;

                // Named versions, not just "a restart is required" (issue #25). An administrator who already
                // restarted, or pressed Docker's Restart button where it does not restart the process, could
                // not tell a stale message from a live one. "Running 1.5.3, 1.5.4 is installed" is checkable.
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
                    // index.html is supposed to be clean, so reading it says nothing about whether the switcher
                    // will load. What matters is whether the pipeline hook is live.
                    IndexPath = IndexPath ?? self.FindIndexHtml();

                    // Not IsRegistered: PluginServiceRegistrator sets it unconditionally, so it is true wherever
                    // the plugin runs and says nothing about the pipeline. Reporting it as success showed a green
                    // "installed and up to date" banner on a server where no script reached the page. The honest
                    // signal is a request actually handled, and anyone reading this page has loaded the web
                    // client, so one has been.
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
        /// Re-runs the full injection, so an administrator can apply a permission fix and click "Retry"
        /// instead of restarting Jellyfin. Returns true when the client script is present afterwards.
        /// </summary>
        internal static bool RunInjectionNow()
        {
            var self = _current;
            if (self == null) return InjectionSucceeded;

            lock (PatchLock)
            {
                // Re-resolve from scratch: the previous run may have cached a path that has since changed
                // (e.g. a Jellyfin web-client update).
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
        /// Removes the ".old" DLLs Jellyfin leaves behind when it cannot delete an in-use assembly during
        /// a plugin update (common on Windows, where the file stays locked until the process exits).
        /// </summary>
        private void CleanupOldDlls()
        {
            // Derive the directory from this assembly, not a name under PluginsPath: Jellyfin names plugin
            // folders "{Name}_{Version}", so a hardcoded "Bonfire" never matches and cleanup silently does
            // nothing.
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
                        // Still locked by another process; retried next startup.
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
            // The middleware is the only way the script is served. index.html is not ours to write: take
            // the tags back out so the file returns to how Jellyfin shipped it. A server saved in the old
            // "file" or "both" mode lands here after an upgrade.
            TryUnpatchIndex();
        }

        /// <summary>
        /// Cleans index.html once the middleware has proved it is in the pipeline.
        /// <para>
        /// Called by <see cref="ProfilesIndexMiddleware"/> the first time it handles a request. Only then
        /// do we know the hook is live (being registered in DI does not mean the pipeline applies it), so
        /// only then is it safe to remove tags that are currently making the switcher work.
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

            // Off the request thread: this reads and rewrites a file, and the browser is waiting on the
            // response that triggered it.
            _ = BackgroundWork.Run(_ =>
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
        /// Takes the script tags back out of index.html, restoring the file to how Jellyfin shipped it.
        /// Runs when injection is middleware-only, so changing the setting cleans the file instead of
        /// merely stopping further writes.
        /// <para>
        /// Failing here is harmless: the tags stay, the middleware sees a document already injected and
        /// steps aside, and the switcher loads from the file as before.
        /// </para>
        /// </summary>
        private void TryUnpatchIndex()
        {
            IndexPath = FindIndexHtml();
            // Not IsRegistered: RegisterServices sets it unconditionally, so it says nothing about whether
            // the hook reached the pipeline. RefreshInjectionStatus stopped using it in 1.4.8; this was
            // left behind. At startup nothing has been served, so this is false until the dashboard
            // recomputes, which it always does before reading it.
            InjectionSucceeded = ProfilesIndexMiddleware.HasSeenIndexRequest
                && ProfilesIndexMiddleware.LastError == null;
            IsVersionStale = false;
            LastFailureReason = null;

            if (IndexPath is null)
            {
                return;
            }

            // Registration proves nothing: IsRegistered is always true here. The middleware must have
            // handled a request before the tags come out, and at startup it has not. So the file stays
            // patched until the middleware says otherwise via CleanIndexOnceMiddlewareIsLive below.
            // Meanwhile it costs nothing: the middleware sees the tags present and steps aside.
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
        /// Writes via a temp file in the same directory, then replaces the original, so a crash or full
        /// disk mid-write cannot leave Jellyfin a truncated index.html (which would break the whole web
        /// client).
        ///
        /// There is no fallback to writing in place. It existed for mounts that allow writing a file but
        /// not creating a sibling, but an interrupted truncate-then-write leaves a broken index.html for
        /// everyone. The only caller, the one-time cleanup of old tags, is harmless when it fails (the
        /// middleware sees the tags present and steps aside), and gets the exception.
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
                // Not fatal: the write below may still succeed, and if not the caller reports the real
                // permission error.
                _logger.LogDebug(ex, "ProfilesPlugin: Could not clear the read-only attribute on {Path}.", path);
            }

            // The temp file is created beside index.html, so this needs write permission on the
            // directory, not just the file. Most Linux/Docker installs do not grant it, and the cleanup
            // then does not happen.
            var tempPath = path + ".bonfire.tmp";
            try
            {
                File.WriteAllText(tempPath, contents);
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
                throw;
            }
        }

        // Path discovery.

        /// <summary>
        /// A key that is the same for two paths pointing at the same file. The Linux packages ship several
        /// candidate directories as symlinks to one another, so comparing normalized path strings would
        /// report a duplicate jellyfin-web on an ordinary install and send the administrator looking for a
        /// bind mount that does not exist.
        /// </summary>
        private static string FileIdentity(string path)
        {
            try
            {
                // The link is usually on the directory (/usr/lib/jellyfin/web -> /usr/share/jellyfin/web),
                // not on index.html, so resolve both.
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
                // Not a link, or the target cannot be read: the path itself will do.
            }
            return path;
        }

        /// <summary>
        /// Searches every location Jellyfin is known to put its web client (Windows installer, Linux
        /// packages, Docker images, portable/Scoop). Returns the full path to index.html or <c>null</c>.
        /// </summary>
        private string? FindIndexHtml()
        {
            var candidates = new List<string?>();

            // 1. Jellyfin's own reported WebPath (highest confidence). IApplicationPaths.WebPath comes
            //    from its own config, so it is right on any properly configured install.
            candidates.Add(_appPaths.WebPath);

            // 2. Relative to the running executable: Windows portable, Scoop, and Docker images with
            //    jellyfin-web next to the server binary.
            var baseDir = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDir, "jellyfin-web"));
            candidates.Add(Path.Combine(baseDir, "..", "jellyfin-web"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "jellyfin-web"));

            // 3. Windows standard installer path.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                candidates.Add(Path.Combine(pf, "Jellyfin", "Server", "jellyfin-web"));
                candidates.Add(Path.Combine(pf, "Jellyfin", "jellyfin-web"));
            }

            // 4. Linux package manager installs (apt/rpm/AUR).
            candidates.Add("/usr/share/jellyfin/web");
            candidates.Add("/usr/lib/jellyfin/web");
            candidates.Add("/usr/local/share/jellyfin/web");
            candidates.Add("/opt/jellyfin/web");

            // 5. Docker common image layouts.
            candidates.Add("/jellyfin/jellyfin-web");
            candidates.Add("/jellyfin/web");
            candidates.Add("/app/jellyfin-web");
            candidates.Add("/config/jellyfin-web");
            candidates.Add("/data/jellyfin-web");

            // Every match is collected, not just the first. A second copy of jellyfin-web is the likeliest
            // reason a tag writes cleanly and is not there when the browser loads (issue #17), and the
            // administrator cannot check for one they were never told about.
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
                    // Path may be syntactically invalid on this OS: skip it.
                    _logger.LogDebug(ex, "ProfilesPlugin: Candidate path '{Dir}' is invalid or inaccessible.", dir);
                }
            }

            if (found.Count == 0) return null;

            var others = found.Skip(1).ToList();
            if (others.Count > 0)
            {
                // The file is only read to take old script tags out; nothing is added.
                _logger.LogWarning(
                    "ProfilesPlugin: More than one jellyfin-web index.html exists on this system. " +
                    "Checking {Path} for old script tags; also found {Others}. Old tags in the others " +
                    "are not removed.",
                    found[0], string.Join(", ", others));
            }
            else
            {
                _logger.LogDebug("ProfilesPlugin: Found index.html at {Path}.", found[0]);
            }

            return found[0];
        }
    }
}
