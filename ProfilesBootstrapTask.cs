using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
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
        // The exact script tag to inject before </body>.
        // The URL /plugins/profiles/profiles.js is the path
        // Jellyfin uses to serve embedded resources from plugin assemblies.
        /// <summary>
        /// Cache-buster written into the script URL.
        ///
        /// Deliberately the assembly version and nothing else. This used to prefer
        /// Plugin.Instance?.Version with the assembly version as a fallback, which made the
        /// value depend on WHEN it was read: this hosted service can run before the Plugin
        /// constructor has assigned Plugin.Instance, so the tag could be written using one
        /// source and later compared against the other. If those two render differently
        /// (e.g. "1.2.8" vs "1.2.8.0") the comparison never matches again and the dashboard
        /// shows "script update pending" forever, no matter how many times the file is
        /// rewritten or what permissions are granted.
        /// </summary>
        internal static string ScriptVersion =>
            typeof(ProfilesBootstrapTask).Assembly.GetName().Version?.ToString() ?? "0";

        // The exact script tag to inject before </body>.
        // The URL /plugins/profiles/profiles.js is the path
        // Jellyfin uses to serve embedded resources from plugin assemblies.
        private static string BodyScriptTag =>
            $"<script src=\"/plugins/profiles/profiles.js?v={ScriptVersion}\" defer></script>";

        // Pulls the ?v= value out of whatever plugin script tag is currently in index.html.
        // Comparing the extracted version beats comparing the whole tag string: a hand-edited
        // file with different attribute order, quoting or spacing is still recognised as
        // current instead of being reported as stale forever.
        private static readonly Regex InjectedVersionRegex = new(
            @"/plugins/profiles/profiles\.js\?v=([^""'&\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Version recorded in index.html's script tag, or null if absent/unparseable.</summary>
        private static string? GetInjectedScriptVersion(string html)
        {
            var m = InjectedVersionRegex.Match(html);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>True when index.html's script tag already points at this build.</summary>
        private static bool IsScriptVersionCurrent(string html)
            => string.Equals(GetInjectedScriptVersion(html), ScriptVersion, StringComparison.Ordinal);

        // Unique substring to detect whether the body tag is already present.
        private const string BodyMarker = "/plugins/profiles/profiles.js";

        // Tiny inline script injected into <head> — runs before any deferred bundle,
        // before React renders. Reads the switching flag set by profiles.js before
        // each window.location.reload() and hides the html element instantly to
        // prevent the flash-of-content during profile switches.
        // A 4-second failsafe restores visibility if profiles.js fails to load.
        private const string HeadScript =
            "<script id=\"jpf-eh\">" +
            "!function(){" +
                "if(localStorage.getItem('jpf-sw')){" +
                    "var h=document.documentElement;" +
                    "h.style.opacity='0';" +
                    "h.style.background='#101010';" +
                    "h.style.colorScheme='dark';" +
                    "window.__jpReveal=setTimeout(function(){" +
                        "h.style.opacity='';" +
                        "h.style.background='';" +
                        "h.style.colorScheme='';" +
                    "},4e3);" +
                    "localStorage.removeItem('jpf-sw');" +
                "}" +
            "}();" +
            "</script>";

        // Unique substring to detect whether the head script is already present.
        private const string HeadMarker = "jpf-eh";

        // Exposed so the dashboard page JS can check whether setup is complete.
        internal static bool InjectionSucceeded { get; private set; }
        internal static bool IsVersionStale { get; private set; }
        internal static string? IndexPath { get; private set; }

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
        private static ProfilesBootstrapTask? _current;
        private static readonly object PatchLock = new();

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<ProfilesBootstrapTask> _logger;

        public ProfilesBootstrapTask(
            IApplicationPaths appPaths,
            ILogger<ProfilesBootstrapTask> logger)
        {
            _appPaths = appPaths;
            _logger = logger;
            _current = this;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            CleanupOldDlls();
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
            var self = _current;
            if (self == null) return;

            lock (PatchLock)
            {
                try
                {
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

                    InjectionSucceeded = hasBody;
                    IsVersionStale = hasBody && (!versionCurrent || !hasHead);

                    if (!hasBody)
                    {
                        LastFailureReason =
                            $"The plugin script tag is not present in {indexPath}.";
                    }
                    else if (!versionCurrent)
                    {
                        // Name both versions: without them this warning is unfalsifiable from
                        // the dashboard, since the version badge shows the plugin's version
                        // and says nothing about what is actually inside index.html.
                        var found = GetInjectedScriptVersion(html) ?? "none";
                        LastFailureReason =
                            $"index.html requests client script v{found}, but this build is "
                            + $"v{ScriptVersion}. Browsers may keep serving the older cached copy "
                            + "until they revalidate (within ~5 minutes), or immediately after a "
                            + "hard refresh.";
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

                bool changed = false;

                // ── 1. Update or inject head early-hide script ───────────────────
                var headRegex = new Regex(@"<script id=""jpf-eh"">[\s\S]*?</script>", RegexOptions.IgnoreCase);
                if (headRegex.IsMatch(html))
                {
                    if (!html.Contains(HeadScript, StringComparison.Ordinal))
                    {
                        // MatchEvaluator, not a replacement string: "$" is a substitution token
                        // in the string overload, so a "$" anywhere in the script would corrupt
                        // index.html silently.
                        html = headRegex.Replace(html, _ => HeadScript);
                        changed = true;
                    }
                }
                else
                {
                    int headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                    if (headIdx != -1)
                    {
                        html = html.Insert(headIdx + "<head>".Length, Environment.NewLine + HeadScript);
                        changed = true;
                    }
                }

                // ── 2. Update or inject body script ──────────────────────────────
                var bodyRegex = new Regex(@"<script[^>]*src=[""'][^""']*/plugins/profiles/profiles\.js[^""']*[""'][^>]*>\s*(</script>)?", RegexOptions.IgnoreCase);
                if (bodyRegex.IsMatch(html))
                {
                    if (!IsScriptVersionCurrent(html))
                    {
                        // MatchEvaluator — see the note on the head script above.
                        var tag = BodyScriptTag;
                        html = bodyRegex.Replace(html, _ => tag);
                        changed = true;
                    }
                }
                else
                {
                    int bodyIdx = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyIdx != -1)
                    {
                        html = html.Insert(bodyIdx, BodyScriptTag + Environment.NewLine);
                        changed = true;
                    }
                }

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

            foreach (var dir in candidates)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                try
                {
                    var fullDir = Path.GetFullPath(dir);
                    var candidate = Path.Combine(fullDir, "index.html");
                    if (File.Exists(candidate))
                    {
                        _logger.LogDebug(
                            "ProfilesPlugin: Found index.html at {Path}.", candidate);
                        return candidate;
                    }
                }
                catch (Exception ex)
                {
                    // Path may be syntactically invalid on this OS — skip it.
                    _logger.LogDebug(ex, "ProfilesPlugin: Candidate path '{Dir}' is invalid or inaccessible.", dir);
                }
            }

            return null;
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
