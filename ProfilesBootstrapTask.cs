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
        private static string BodyScriptTag
        {
            get
            {
                // The version is only a cache-buster. Fall back to the assembly's own version
                // rather than a hardcoded literal, which silently went stale every release.
                var version = Plugin.Instance?.Version?.ToString()
                              ?? typeof(ProfilesBootstrapTask).Assembly.GetName().Version?.ToString()
                              ?? "0";
                return $"<script src=\"/plugins/profiles/profiles.js?v={version}\" defer></script>";
            }
        }

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

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<ProfilesBootstrapTask> _logger;

        public ProfilesBootstrapTask(
            IApplicationPaths appPaths,
            ILogger<ProfilesBootstrapTask> logger)
        {
            _appPaths = appPaths;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            CleanupOldDlls();
            TryPatchIndex();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        private void CleanupOldDlls()
        {
            try
            {
                var pluginDir = Path.Combine(_appPaths.PluginsPath, "Bonfire");
                if (Directory.Exists(pluginDir))
                {
                    foreach (var file in Directory.GetFiles(pluginDir, "*.old"))
                    {
                        try { File.Delete(file); } catch { /* best effort */ }
                    }
                }
            }
            catch { /* best effort */ }
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
                bool bodyVersionCurrent = html.Contains(BodyScriptTag, StringComparison.Ordinal);

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
                        html = headRegex.Replace(html, HeadScript);
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
                    if (!html.Contains(BodyScriptTag, StringComparison.Ordinal))
                    {
                        html = bodyRegex.Replace(html, BodyScriptTag);
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

                // Only claim success if both markers are actually present in the final
                // document. Previously this was set unconditionally, so an index.html
                // without a <head> or </body> anchor reported success while the switcher
                // silently never loaded — and the dashboard's "injection failed" banner,
                // the only signal users get, could never appear.
                bool injected = html.Contains(BodyMarker, StringComparison.Ordinal);

                if (!injected)
                {
                    // Only clear InjectionSucceeded if there was genuinely nothing before.
                    // (If an old working injection existed, InjectionSucceeded was already
                    // set true above and should stay that way.)
                    if (!InjectionSucceeded)
                    {
                        _logger.LogWarning(
                            "ProfilesPlugin: Could not find the expected <head> and </body> anchors in {Path}. " +
                            "The client script was NOT injected. Add the following line before </body> manually: {Tag}",
                            indexPath, BodyScriptTag);
                    }
                    return;
                }

                if (changed)
                {
                    WriteFileAtomic(indexPath, html);
                    _logger.LogInformation(
                        "ProfilesPlugin: Client scripts injected successfully into {Path}.",
                        indexPath);
                }

                InjectionSucceeded = true;
                IsVersionStale = false;
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
                    LogPermissionError(indexPath, ex);
                }
                else
                {
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
                    _logger.LogWarning(
                        ex,
                        "ProfilesPlugin: IO error reading/writing {Path}. " +
                        "Manually add the following line before </body>: {Tag}",
                        indexPath, BodyScriptTag);
                }
                else
                {
                    _logger.LogDebug(ex,
                        "ProfilesPlugin: IO error updating script version in {Path}. " +
                        "The existing injection is still functional.", indexPath);
                }
            }
            catch (Exception ex)
            {
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
            catch { /* best effort */ }

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
