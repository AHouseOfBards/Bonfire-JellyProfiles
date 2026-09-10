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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// Runs once every time the Jellyfin server starts via the IHostedService lifecycle.
    ///
    /// Ensures that the Profiles client script tag is present in Jellyfin's index.html
    /// so the profile gate and switch button load automatically for all users without
    /// any manual post-installation steps.
    ///
    /// The patch is idempotent — if the tag is already present the file is left untouched.
    /// Because Jellyfin replaces index.html when the web client is updated, running
    /// this check on every startup keeps the injection self-healing.
    ///
    /// If the file cannot be written (admin-locked directory, insufficient permissions)
    /// a clear, platform-specific warning is written to the Jellyfin log with
    /// copy-pasteable fix commands for Docker, Linux, and Windows.
    /// </summary>
    public class ProfilesBootstrapTask : IHostedService
    {
        // The script fragments and the injection logic now live in WebInjection, shared
        // with the middleware that serves index.html straight from the request pipeline.
        // These forwarders keep the rest of this file — and the dashboard status it feeds —
        // reading exactly as before.
        internal static string ScriptVersion => WebInjection.ScriptVersion;

        private static string BodyScriptTag => WebInjection.BodyScriptTag;

        /// <summary>Version recorded in index.html’s script tag, or null if absent/unparseable.</summary>
        private static string? GetInjectedScriptVersion(string html)
            => WebInjection.GetInjectedScriptVersion(html);

        /// <summary>True when index.html’s script tag already points at this build.</summary>
        private static bool IsScriptVersionCurrent(string html)
            => WebInjection.IsScriptVersionCurrent(html);

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

        /// <summary>
        /// Non-destructively probes whether Jellyfin can actually write index.html.
        ///
        /// Reporting "the version tag is old" is describing a symptom; the administrator needs
        /// to know the cause, and the cause is almost always that the file is not writable by
        /// the Jellyfin process. Opening for write and closing immediately answers that
        /// definitively instead of inferring it from a failed patch.
        /// </summary>
        internal static bool CanWriteIndexHtml(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Shared with the middleware — see WebInjection for what these contain and why.
        private const string BodyMarker = WebInjection.BodyMarker;
        private const string HeadScript = WebInjection.HeadScript;
        private const string HeadMarker = WebInjection.HeadMarker;

        // Exposed so the dashboard page JS can check whether setup is complete.
        internal static bool InjectionSucceeded { get; private set; }
        internal static bool IsVersionStale { get; private set; }
        internal static string? IndexPath { get; private set; }

        /// <summary>
        /// True once a write of the script tag has completed without throwing.
        ///
        /// Distinguishes the two ways the tag can be missing, which need opposite fixes:
        /// the write never happened (permissions), or it happened and the tag is still gone
        /// when the file is read back — which means the file being served is not the file
        /// being patched. Issue #17 was reported as the first and behaves like the second.
        /// </summary>
        private static bool _tagWriteReportedSuccess;

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
        /// Which mechanism is supposed to be adding the script tags. Falls back to
        /// <see cref="IndexInjectionModes.Middleware"/> when the plugin instance is not up
        /// yet, because the fallback must never be a mode that writes to index.html: an
        /// administrator who has said "do not touch my file" would otherwise have it
        /// patched during any window where the configuration is unreadable.
        /// </summary>
        private static string CurrentInjectionMode =>
            IndexInjectionModes.Normalize(Plugin.Instance?.Configuration?.IndexInjectionMode);

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
        /// Points each sub-profile at the provider the current settings call for: Bonfire's
        /// when client PIN login is on, and the master's own provider when it is off.
        /// <para>
        /// Runs at startup and again whenever the setting is saved, so switching the
        /// feature off takes effect immediately instead of at the next restart — a profile
        /// left pointing at a disabled provider has no provider at all, and the web
        /// switcher would stop being able to enter it.
        /// </para>
        /// </summary>
        /// <summary>
        /// Re-points sub-profiles after an administrator changes the client PIN setting, so
        /// turning it OFF takes effect at once. Waiting for the next restart would leave
        /// every profile pointing at a provider that is no longer enabled, and a profile
        /// with no enabled provider cannot be entered at all — including by the web
        /// switcher, which has nothing to do with this feature.
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

        internal void ReconcileAuthProviders()
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.Mappings == null) return;

            var wanted = config.EnableClientPinLogin
                ? typeof(BonfirePinAuthenticationProvider).FullName
                : null;   // null = restore whatever the master uses

            var subProfiles = config.Mappings
                .Where(m => m.MasterUserId != m.ProfileUserId)
                .Select(m => (Profile: m.ProfileUserId, Master: m.MasterUserId))
                .ToList();

            // A master that has set a PIN is bound too, so it can be entered on a television
            // without its full password. Only when it HAS a PIN: binding one without would
            // hand an account with a real password to a provider with nothing to check, and
            // the provider keeps the password working by delegating to Jellyfin's own — see
            // BonfirePinAuthenticationProvider.DelegateToJellyfin.
            //
            // Restoring is the literal default provider rather than MasterProviderId, which
            // for a master would read back whatever it is currently bound to — this provider
            // — and pin it there permanently.
            var mastersWithPins = config.Mappings
                .Where(m => m.MasterUserId == m.ProfileUserId
                            && !string.IsNullOrEmpty(m.PinHash)
                            && config.Mappings.Any(o => o.MasterUserId == m.MasterUserId
                                                        && o.ProfileUserId != m.MasterUserId))
                .Select(m => (Profile: m.ProfileUserId, Master: Guid.Empty))
                .ToList();

            subProfiles.AddRange(mastersWithPins);

            int changed = 0;
            foreach (var (profileId, masterId) in subProfiles)
            {
                try
                {
                    var user = _userManager.GetUserById(profileId);
                    if (user == null) continue;

                    // Guid.Empty marks a master: there is no owning account to read a
                    // provider from, so the restore target is Jellyfin's own.
                    var target = wanted ?? (masterId == Guid.Empty
                        ? "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider"
                        : MasterProviderId(masterId));

                    // The whole point of this method's history. Never write an empty or
                    // null id: it makes the row unreadable rather than merely unusable.
                    if (string.IsNullOrEmpty(target)) continue;
                    if (string.Equals(user.AuthenticationProviderId, target, StringComparison.Ordinal)) continue;

                    user.AuthenticationProviderId = target;
                    _userManager.UpdateUserAsync(user).GetAwaiter().GetResult();
                    changed++;
                }
                catch (Exception ex)
                {
                    // One bad profile must not stop the rest, and must not stop startup.
                    _logger.LogError(ex,
                        "ProfilesPlugin: could not set the authentication provider for profile {ProfileId}.",
                        profileId);
                }
            }

            if (changed > 0)
            {
                _logger.LogInformation(
                    config.EnableClientPinLogin
                        ? "ProfilesPlugin: {Count} profile(s) can now be entered by PIN from any client."
                        : "ProfilesPlugin: {Count} profile(s) returned to the default authentication provider.",
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
                    // Middleware only: index.html is *supposed* to be clean, so reading it
                    // says nothing about whether the switcher will load. What matters is
                    // whether the pipeline hook is live.
                    if (!IndexInjectionModes.PatchesFile(CurrentInjectionMode))
                    {
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
                                  + "nothing is adding the client script. Switch back to patching "
                                  + "index.html.";
                        return;
                    }

                    var indexPath = IndexPath ?? self.FindIndexHtml();
                    IndexPath = indexPath;

                    if (indexPath == null || !File.Exists(indexPath))
                    {
                        InjectionSucceeded = false;
                        IsVersionStale = false;
                        LastFailureReason = "Jellyfin's index.html could not be located on this system.";
                        return;
                    }

                    var html = File.ReadAllText(indexPath);
                    var hasBody = html.Contains(BodyMarker, StringComparison.Ordinal);
                    var hasHead = html.Contains(HeadMarker, StringComparison.Ordinal);
                    var versionCurrent = IsScriptVersionCurrent(html);

                    // In "both" mode the middleware picks up whatever the file patch could
                    // not do, so an unpatched or stale index.html stops being a failure and
                    // becomes the fallback working as designed. Gate that on having actually
                    // seen a request, not merely on being registered — otherwise a filter
                    // that never got installed would silently suppress the real warning.
                    bool middlewareCovers = IndexInjectionModes.UsesMiddleware(CurrentInjectionMode)
                        && ProfilesIndexMiddleware.HasSeenIndexRequest;

                    InjectionSucceeded = hasBody || middlewareCovers;
                    IsVersionStale = !middlewareCovers && hasBody && (!versionCurrent || !hasHead);

                    if (middlewareCovers)
                    {
                        LastFailureReason = null;
                        return;
                    }

                    if (!hasBody)
                    {
                        // Same symptom, three different fixes. Reporting only "the tag is not
                        // present" sent issue #17 chasing file permissions that were already
                        // correct.
                        if (!CanWriteIndexHtml(indexPath))
                        {
                            LastFailureReason =
                                $"Jellyfin cannot write {indexPath}. Grant write access with the "
                                + "command below, then click Re-check.";
                        }
                        else if (_tagWriteReportedSuccess)
                        {
                            LastFailureReason =
                                $"The tag was written to {indexPath} but is no longer there. "
                                + "Jellyfin is serving a different copy of jellyfin-web — look for "
                                + "a bind mount, a second install, or a proxy serving its own files."
                                + (OtherIndexPaths.Count > 0
                                    ? " Also found: " + string.Join(", ", OtherIndexPaths) + "."
                                    : string.Empty);
                        }
                        else
                        {
                            LastFailureReason =
                                $"{indexPath} is writable but has no plugin script tag yet. "
                                + "Click Re-check to add it.";
                        }
                    }
                    else if (!versionCurrent)
                    {
                        // Lead with the CAUSE, not the symptom. Saying only "the tag is old"
                        // gave the administrator nothing to act on — and looked like a
                        // contradiction next to a correct version badge, which reports the
                        // plugin's version and says nothing about index.html's contents.
                        var found = GetInjectedScriptVersion(html);
                        var describeFound = found == null
                            ? "no version marker at all (it predates the cache-buster)"
                            : $"version {found}";

                        LastFailureReason = CanWriteIndexHtml(indexPath)
                            ? $"index.html's script tag carries {describeFound}, but this build is "
                              + $"v{ScriptVersion}. The file is writable, so clicking Re-check "
                              + "should update it."
                            : $"Jellyfin cannot write {indexPath}, so its script tag still carries "
                              + $"{describeFound} instead of v{ScriptVersion}. "
                              + "This does not break anything: the switcher is running, and browsers "
                              + "pick up new client code by themselves within about five minutes. "
                              + "Granting write access below only makes updates instant.";
                    }
                    else if (!hasHead)
                    {
                        LastFailureReason =
                            "The anti-flicker script is missing from <head>. The switcher works, "
                            + "but you may see a brief flash of content when switching profiles.";
                    }
                    else
                    {
                        LastFailureReason = null;
                    }
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
            // Middleware only: index.html is not ours to write any more. Take the tags
            // back out so the file returns to how Jellyfin shipped it, and let the
            // request pipeline do the work from here.
            if (!IndexInjectionModes.PatchesFile(CurrentInjectionMode))
            {
                TryUnpatchIndex();
                return;
            }

            var indexPath = FindIndexHtml();
            IndexPath = indexPath;

            if (indexPath is null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Could not locate index.html in any known Jellyfin web path. " +
                    "The Profiles client script will not load automatically. " +
                    "Manually add the following line before </body> in your index.html: {Tag}",
                    BodyScriptTag);
                return;
            }

            try
            {
                // Create backup in the plugin data directory (always writable), NOT next to index.html.
                // Storing the backup beside index.html requires directory write permission on the web
                // root (e.g. /usr/share/jellyfin/web/) which the jellyfin service user typically does
                // not have — even after a `chmod 666 index.html` on the file itself.
                try
                {
                    var backupDir = Path.Combine(_appPaths.DataPath, "plugins", "ProfilesManagement");
                    Directory.CreateDirectory(backupDir);
                    var backupPath = Path.Combine(backupDir, "index.html.bonfire.bak");
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(indexPath, backupPath, false);
                        _logger.LogInformation("ProfilesPlugin: Created backup of index.html at {Path}.", backupPath);
                    }
                }
                catch (Exception backupEx)
                {
                    // A backup failure is non-fatal — log a warning and continue with the patch.
                    _logger.LogWarning(backupEx, "ProfilesPlugin: Could not create backup of index.html. Proceeding with injection anyway.");
                }

                var html = File.ReadAllText(indexPath);

                bool hasBody = html.Contains(BodyMarker, StringComparison.Ordinal);
                bool hasHead = html.Contains(HeadMarker, StringComparison.Ordinal);

                // Check that the script tag matches the *current* version, not just that
                // some version is present. After a plugin update the old ?v=1.1.x tag
                // stays in index.html and the browser keeps serving the cached old JS,
                // so new features (like tag filtering) silently never appear.
                bool bodyVersionCurrent = IsScriptVersionCurrent(html);

                if (hasBody && bodyVersionCurrent && hasHead)
                {
                    _logger.LogDebug(
                        "ProfilesPlugin: Scripts already correctly present in {Path} — no changes made.",
                        indexPath);
                    InjectionSucceeded = true;
                    IsVersionStale = false;
                    return;
                }

                // If body marker is present, the plugin script is injected and functional — the
                // profile gate and switcher load fine. Mark as succeeded NOW so the failure banner
                // never fires. Set IsVersionStale if the cache-buster version or head tag is missing.
                if (hasBody)
                {
                    InjectionSucceeded = true;
                    IsVersionStale = !bodyVersionCurrent || !hasHead;
                }
                else
                {
                    InjectionSucceeded = false;
                    IsVersionStale = false;
                }

                // Both fragments in one pass, shared with ProfilesIndexMiddleware.
                bool changed = WebInjection.Inject(html, out html);

                // The body script is what makes the switcher work at all; the head script only
                // prevents a flash of content. Track them separately so a missing <head> anchor
                // downgrades to the "stale" banner rather than either claiming full success or
                // firing the alarming "injection failed" one.
                bool bodyPresent = html.Contains(BodyMarker, StringComparison.Ordinal);
                bool headPresent = html.Contains(HeadMarker, StringComparison.Ordinal);

                if (!bodyPresent)
                {
                    // Only clear InjectionSucceeded if there was genuinely nothing before.
                    // (If an old working injection existed, InjectionSucceeded was already
                    // set true above and should stay that way.)
                    if (!InjectionSucceeded)
                    {
                        LastFailureReason =
                            $"No </body> tag was found in {indexPath}, so the client script "
                            + "could not be inserted.";
                        _logger.LogWarning(
                            "ProfilesPlugin: Could not find a </body> anchor in {Path}. " +
                            "The client script was NOT injected. Add the following line before </body> manually: {Tag}",
                            indexPath, BodyScriptTag);
                    }
                    return;
                }

                if (changed)
                {
                    // Throws on failure; the catch blocks below preserve any previously
                    // working state rather than reporting a hard failure.
                    WriteFileAtomic(indexPath, html);
                    _tagWriteReportedSuccess = true;
                    _logger.LogInformation(
                        "ProfilesPlugin: Client scripts injected successfully into {Path}.",
                        indexPath);
                }

                InjectionSucceeded = true;

                // Re-derive from the document we just wrote instead of assuming success.
                // Previously this cleared IsVersionStale unconditionally, which hid the case
                // where the body tag was inserted but the <head> anchor was missing entirely.
                IsVersionStale = !headPresent;
                LastFailureReason = headPresent
                    ? null
                    : "The anti-flicker script could not be added because index.html has no "
                      + "<head> tag. The switcher works, but you may see a brief flash of "
                      + "content when switching profiles.";
            }
            catch (UnauthorizedAccessException ex)
            {
                // If an older version of the injection is already working in index.html,
                // InjectionSucceeded was set true above. A write failure here only means
                // we couldn't update the cache-buster version — not that the switcher is
                // broken. Only log the full permission error when InjectionSucceeded is
                // still false (i.e. there was no prior injection at all).
                if (!InjectionSucceeded)
                {
                    LastFailureReason =
                        $"Jellyfin does not have permission to write {indexPath}. "
                        + "Run the command below for your platform, then click Re-check.";
                    LogPermissionError(indexPath, ex);
                }
                else
                {
                    IsVersionStale = true;
                    LastFailureReason =
                        $"Jellyfin cannot write {indexPath}, so its script tag still requests an "
                        + $"older client script than this build (v{ScriptVersion}). The switcher "
                        + "still works and browsers pick up new code on their next revalidation "
                        + "(within ~5 minutes) — granting write access just makes it immediate.";
                    _logger.LogDebug(ex,
                        "ProfilesPlugin: Could not update script version in {Path} (permission denied). " +
                        "The existing injection is still functional; users may need a browser hard-refresh " +
                        "to pick up the latest client script.", indexPath);
                }
            }
            catch (IOException ex)
            {
                if (!InjectionSucceeded)
                {
                    LastFailureReason =
                        $"Could not read or write {indexPath}: {ex.Message}";
                    _logger.LogWarning(
                        ex,
                        "ProfilesPlugin: IO error reading/writing {Path}. " +
                        "Manually add the following line before </body>: {Tag}",
                        indexPath, BodyScriptTag);
                }
                else
                {
                    IsVersionStale = true;
                    LastFailureReason =
                        $"The script tag in {indexPath} is out of date and could not be updated: {ex.Message}";
                    _logger.LogDebug(ex,
                        "ProfilesPlugin: IO error updating script version in {Path}. " +
                        "The existing injection is still functional.", indexPath);
                }
            }
            catch (Exception ex)
            {
                if (!InjectionSucceeded)
                {
                    LastFailureReason = "Unexpected error while patching index.html: " + ex.Message;
                }
                _logger.LogError(ex,
                    "ProfilesPlugin: Unexpected error while patching {Path}.", indexPath);
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
        /// Searches all locations Jellyfin is known to place its web client on every
        /// supported platform (Windows installer, Linux packages, Docker images,
        /// portable/Scoop). Returns the full path to index.html or <c>null</c>.
        /// </summary>
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

        // ── Error reporting ──────────────────────────────────────────────────────

        private void LogPermissionError(string indexPath, Exception ex)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogWarning(
                    ex,
                    "ProfilesPlugin: Permission denied writing to {Path}.\n\n" +
                    "WINDOWS FIX — Grant write access and restart Jellyfin (Run CMD as Administrator):\n" +
                    "  icacls \"{IndexPath}\" /grant \"NT AUTHORITY\\NetworkService:(M)\"\n\n" +
                    "Or add the following line before </body> manually (Notepad as Administrator):\n" +
                    "  {Tag}",
                    indexPath, indexPath, BodyScriptTag);
            }
            else if (IsRunningInDocker())
            {
                _logger.LogWarning(
                    ex,
                    "ProfilesPlugin: Permission denied writing to {Path}.\n\n" +
                    "DOCKER FIX — Set ownership to the Jellyfin service user, then restart:\n" +
                    "  docker exec -u root <container-name> chown jellyfin:jellyfin {IndexPath}\n" +
                    "  docker exec -u root <container-name> chmod 664 {IndexPath}\n\n" +
                    "After Jellyfin restarts and injects the script you can optionally restore\n" +
                    "read-only permissions: docker exec -u root <container-name> chmod 644 {IndexPath2}",
                    indexPath, indexPath, indexPath, indexPath);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "ProfilesPlugin: Permission denied writing to {Path}.\n\n" +
                    "LINUX FIX — Set ownership to the Jellyfin service user and grant write access, then restart Jellyfin:\n" +
                    "  sudo chown jellyfin:jellyfin {IndexPath}\n" +
                    "  sudo chmod 664 {IndexPath2}\n\n" +
                    "After Jellyfin restarts and injects the script you can optionally restore\n" +
                    "read-only permissions: sudo chmod 644 {IndexPath3}",
                    indexPath, indexPath, indexPath, indexPath);
            }
        }

        private static bool IsRunningInDocker() =>
            File.Exists("/.dockerenv") ||
            string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase);
    }
}
