using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Profiles
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Bonfire";

        /// <summary>
        /// Accounts bound to Bonfire's sign-in provider would be refused their real password
        /// once it is gone, so Jellyfin's own provider is put back first.
        /// </summary>
        public override void OnUninstalling()
        {
            ProfilesBootstrapTask.RestoreProvidersForUninstall();
            base.OnUninstalling();
        }
        public override Guid Id => Guid.Parse("b1462fca-774b-4b13-8d02-e2d4f2bc18b9");

        public static Plugin? Instance { get; private set; }

        public IApplicationPaths AppPaths { get; }

        private static volatile bool _panicDisabled;

        /// <summary>
        /// True once the emergency disable code has been entered. While set, the plugin
        /// serves an inert client script, so the profile gate and switcher disappear on the
        /// next page load, and it stops editing Jellyfin's own responses — the sign-in user
        /// list, the two responses naming the signed-in account, and Quick Connect.
        /// <para>
        /// Deliberately in memory only, never written to the configuration: the escape hatch
        /// exists because the plugin has made the web interface hard to use, and a flag that
        /// survived a restart could leave a server stuck in a state whose own settings page
        /// is the thing you need it to reach. Restarting Jellyfin always restores the plugin.
        /// </para>
        /// </summary>
        public static bool IsPanicDisabled => _panicDisabled;

        /// <summary>
        /// True while Bonfire does nothing: the emergency code has been entered, or an
        /// administrator has turned it off (<see cref="PluginConfiguration.BonfireOn"/>).
        /// Everything that stops for the emergency code asks this, so the two behave the
        /// same. Only what reports the code itself asks <see cref="IsPanicDisabled"/>:
        /// <c>panic-state</c> and <c>admin/panic-status</c>.
        /// </summary>
        public static bool IsOff => _panicDisabled || Instance?.Configuration?.BonfireOn == false;

        /// <summary>
        /// Called by every path that can change <see cref="PluginConfiguration.BonfireOn"/>:
        /// <c>admin/settings</c>, the import, and Jellyfin's own configuration save
        /// (<see cref="UpdateConfiguration"/>). <see cref="IsOff"/> reaches every reader at
        /// once, except what they have cached: the viewing-limits index (30 seconds) and the
        /// tokens the proxy switcher has put down as not ours. One place, so a new path
        /// cannot clear one and forget the other. Outside ConfigLock.
        /// </summary>
        internal static void ForgetOnOffCaches()
        {
            Auth.ViewingTracker.Invalidate();
            Auth.ProxySwitcher.ForgetUnproxied();
        }

        /// <summary>Trips the emergency disable. There is no code path that clears it.</summary>
        internal static void TripPanicDisable() => _panicDisabled = true;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : this(applicationPaths, xmlSerializer, RunningVersionText())
        {
        }

        /// <summary>The version a harness plays; the public constructor passes the real one.</summary>
        internal Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, string runningVersion)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            AppPaths = applicationPaths;

            // Where Campground plugins keep their shared state (<data>/campground). The one
            // thing Bonfire reads there today is the shared emergency brake, which the
            // index.html coordinator checks: a file named DISABLE (everything) or
            // DISABLE.bonfire leaves Bonfire's tags out of the page. The first plugin to
            // call this sets the root; later calls are no-ops.
            try
            {
                Campground.Shared.Storage.CampgroundPaths.Initialize(applicationPaths);
            }
            catch (Exception)
            {
                // A data folder that cannot be created costs only the file-based brake. The
                // plugin itself must still start.
            }

            // Before anything in this start can save: the configuration is read lazily, on
            // first use, and the first save is what overwrites the file.
            BackUpIfVersionChanged(runningVersion);

            // A new install requires a main PIN (P2-703) and starts with Bonfire off. This is
            // done here and not as the properties' defaults because a file saved by an older
            // version has no element for either, and XmlSerializer leaves a missing element at
            // the constructor's value: a default of true for RequireMainPin would turn it on
            // for that server, and a default of false for BonfireOn would turn Bonfire off.
            // A missing file is a new install, whatever sits beside it: an administrator who
            // deletes PluginConfiguration.xml to reset has asked for a fresh start, and a
            // last good copy found there used to skip this branch, leave Bonfire on with no
            // main PIN rule, and be overwritten by the first save. The copy is moved aside,
            // not restored. (A file that EXISTS but won't read is the recovery case, in
            // TryRestoreOver.) r2-19-03.
            //
            // Saved here, not left in memory. Reading Configuration with no file runs
            // Jellyfin's LoadConfiguration, which saves the plain defaults straight away; a
            // value set only in memory after that was gone at the next restart, so a new
            // install that restarted before its first save came back on (and without the
            // main PIN rule).
            if (!System.IO.File.Exists(ConfigurationFilePath))
            {
                SetLastGoodAside();
                lock (ProfilesBaseController.ConfigLock)
                {
                    var fresh = Configuration;
                    fresh.RequireMainPin = true;
                    fresh.BonfireOn = false;
                    SaveConfiguration();
                }
            }
        }

        private static string RunningVersionText()
        {
            var assembly = typeof(Plugin).Assembly;
            var text = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            var plus = text.IndexOf('+');
            return plus > 0 ? text.Substring(0, plus) : text;
        }

        /// <summary>
        /// Takes a saved configuration by copying it onto the instance already in use,
        /// rather than replacing that instance.
        /// <para>
        /// Jellyfin's own implementation assigns the new object. Every request that had
        /// already read <c>Plugin.Instance.Configuration</c> then went on mutating an orphan:
        /// its changes were saved nowhere, and a lock taken on the old object excluded
        /// nobody. ConfigLock fixed the second half. This fixes the first, at the one place
        /// it could happen, instead of at each of the two dozen places that read the
        /// configuration before taking the lock — which were the known open issue (P2-25).
        /// With the reference fixed for the life of the process, reading early is safe.
        /// </para>
        /// <para>
        /// Reached by Jellyfin's generic <c>POST /Plugins/{id}/Configuration</c>. The
        /// plugin's own settings page saves through <c>admin/settings</c> instead and mutates
        /// in place already. Both signatures are identical in 10.11 and 12.0.
        /// </para>
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            if (configuration is not PluginConfiguration incoming)
            {
                base.UpdateConfiguration(configuration);
                return;
            }

            PluginConfiguration current;
            bool switched;
            lock (ProfilesBaseController.ConfigLock)
            {
                current = Configuration;
                var wasOn = current.BonfireOn;
                // The same checks admin/settings makes. This route is Jellyfin's, and without
                // them a body could save a theme, a limit or a switcher location the
                // dashboard would have refused. A value that fails keeps what is live.
                // r2-19-02.
                var before = new PluginConfiguration
                {
                    MaxProfilesPerUser = current.MaxProfilesPerUser,
                    PinGuessesPerDay = current.PinGuessesPerDay,
                    ProfileImageQuality = current.ProfileImageQuality,
                    DefaultSwitcherLocation = current.DefaultSwitcherLocation,
                    DefaultTheme = current.DefaultTheme,
                    DefaultTimeZone = current.DefaultTimeZone,
                    TrustedAddressEntries = current.TrustedAddressEntries
                };
                if (!ReferenceEquals(current, incoming)) CopyInto(incoming, current);
                NormalizeApplied(current, before);
                switched = current.BonfireOn != wasOn;
                SaveConfiguration();
            }

            if (switched) ForgetOnOffCaches();
            ConfigurationChanged?.Invoke(this, current);
        }

        /// <summary>
        /// Holds the fields admin/settings checks to the same rules, after a saved
        /// configuration has been copied onto <paramref name="live"/>. A value refused there
        /// goes back to what <paramref name="before"/> held. Done on the live object and not
        /// on the posted one, so the caller's object is left as it was sent. Called inside
        /// ConfigLock.
        /// </summary>
        internal static void NormalizeApplied(PluginConfiguration live, PluginConfiguration before)
        {
            if (live.MaxProfilesPerUser < 1 || live.MaxProfilesPerUser > 20)
                live.MaxProfilesPerUser = before.MaxProfilesPerUser;
            // A security limit: lower, never higher.
            if (live.PinGuessesPerDay < 1 || live.PinGuessesPerDay > PinBudget.DailyLimit)
                live.PinGuessesPerDay = before.PinGuessesPerDay;

            live.ProfileImageQuality = ImageQualities.IsKnown(live.ProfileImageQuality)
                ? ImageQualities.Normalize(live.ProfileImageQuality)
                : before.ProfileImageQuality;

            var location = live.DefaultSwitcherLocation;
            live.DefaultSwitcherLocation =
                string.Equals(location, SwitcherLocations.Button, StringComparison.OrdinalIgnoreCase)
                || string.Equals(location, SwitcherLocations.Menu, StringComparison.OrdinalIgnoreCase)
                    ? SwitcherLocations.Normalize(location)
                    : before.DefaultSwitcherLocation;

            // Known to the configuration as saved: a custom theme sent in the same body counts.
            live.DefaultTheme = Themes.IsKnown(live.DefaultTheme, live)
                ? Themes.Normalize(live.DefaultTheme, live)
                : before.DefaultTheme;

            if (!string.IsNullOrEmpty(live.DefaultTimeZone))
            {
                live.DefaultTimeZone = HouseholdTime.TryFind(live.DefaultTimeZone, out _)
                    ? live.DefaultTimeZone.Trim()
                    : before.DefaultTimeZone;
            }

            live.ProxyClients = Auth.ProxyClients.Normalize(live.ProxyClients);

            live.TrustedAddressEntries = Auth.TrustedAddresses.Validate(live.TrustedAddressEntries) == null
                ? Auth.TrustedAddresses.Normalize(live.TrustedAddressEntries)
                : before.TrustedAddressEntries;
        }

        private static void CopyInto(PluginConfiguration from, PluginConfiguration to)
        {
            foreach (var property in typeof(PluginConfiguration).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    var value = property.GetValue(from);
                    // A body with a list set to null is not a request to null the live one:
                    // every route that then read it threw. The list keeps what it had. R-1128.
                    if (value == null && property.PropertyType.IsGenericType
                        && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)) continue;
                    // The same for any section the configuration declares non-null (Email,
                    // Onboarding, Maintenance...): a posted null there threw in every later
                    // route until a restart. The import path uses the same rule. r2-19-01.
                    if (value == null && !PluginConfiguration.AcceptsNull(property)) continue;
                    property.SetValue(to, value);
                }
            }
        }

        // ── The last good copy (R-1127) ─────────────────────────────────────────────
        //
        // When PluginConfiguration.xml won't read, Jellyfin's BasePlugin.LoadConfiguration
        // creates a default configuration and SAVES it over the file:
        //
        //     catch
        //     {
        //         var config = Activator.CreateInstance<TConfigurationType>();
        //         SaveConfiguration(config);
        //         return config;
        //     }
        //
        // Every household, PIN, link, theme and email setting went with it, and the plugin
        // kept no copy. A crash in the middle of a write is enough, and so is a Jellyfin
        // version that doesn't know an enum value the file holds.
        //
        // That save comes through the virtual SaveConfiguration below, with the very object
        // LoadConfiguration then returns. So it is caught there: the unreadable file is kept
        // beside the original, the last copy that did save is read, and its values are copied
        // onto that object before anything is written. Every other save refreshes the copy.

        /// <summary>Where the last configuration that saved cleanly is kept.</summary>
        internal string LastGoodPath => ConfigurationFilePath + ".lastgood";

        /// <inheritdoc />
        public override void SaveConfiguration(PluginConfiguration config)
        {
            // One save and its copy at a time, so the copy is never taken of a half-written file.
            lock (SaveAndCopyLock)
            {
                if (config != null && LooksEmpty(config) && TryRestoreOver(config))
                {
                    base.SaveConfiguration(config);
                    return;
                }

                base.SaveConfiguration(config!);

                try
                {
                    System.IO.File.Copy(ConfigurationFilePath, LastGoodPath, overwrite: true);
                }
                catch (Exception)
                {
                    // The save itself happened. A missing copy only matters on the day the file
                    // breaks, and the next save tries again.
                }
            }
        }

        private static readonly object SaveAndCopyLock = new();

        // ── A copy for every version change (R-1319) ────────────────────────────────
        //
        // The same idea as the last good copy, for the other way the file loses things: a
        // rollback. An older version loads a file a newer one wrote, and its first save
        // writes it back without every setting it doesn't know, so upgrading again finds
        // them gone. Nothing kept a copy.
        //
        // The version that last ran is written beside the file (<file>.version). When the
        // running one differs, the file is copied to <file>.<that version>.bak, named for the
        // version that wrote it, before anything in this start can save. Beside the last good
        // copy and taken under the same lock; the newest few are kept.

        /// <summary>How many versioned copies are kept.</summary>
        internal const int VersionBackupsKept = 3;

        private void BackUpIfVersionChanged(string runningVersion)
        {
            try
            {
                lock (SaveAndCopyLock)
                {
                    var path = ConfigurationFilePath;
                    var marker = path + ".version";
                    var previous = System.IO.File.Exists(marker) ? System.IO.File.ReadAllText(marker).Trim() : string.Empty;
                    if (string.Equals(previous, runningVersion, StringComparison.Ordinal)) return;

                    if (System.IO.File.Exists(path))
                    {
                        // A file from a build that never wrote its version down: the version
                        // that wrote it is unknown, so the copy says what it was taken before.
                        var label = previous.Length > 0 ? previous : "before-" + runningVersion;
                        var bak = path + "." + SafeLabel(label) + ".bak";
                        System.IO.File.Copy(path, bak, overwrite: true);
                        System.IO.File.SetLastWriteTimeUtc(bak, DateTime.UtcNow);
                        PruneVersionBackups(path);
                    }

                    System.IO.File.WriteAllText(marker, runningVersion);
                }
            }
            catch (Exception)
            {
                // A copy that couldn't be made must not stop the plugin loading. The next
                // start tries again, because the version was not written down.
            }
        }

        /// <summary>
        /// Moves the last good copy to <c>.lastgood-&lt;stamp&gt;</c> so a fresh install's
        /// first save does not overwrite it. Never throws.
        /// </summary>
        private void SetLastGoodAside()
        {
            try
            {
                lock (SaveAndCopyLock)
                {
                    if (!System.IO.File.Exists(LastGoodPath)) return;
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
                    System.IO.File.Move(LastGoodPath, LastGoodPath + "-" + stamp, overwrite: true);
                    PruneCopies(ConfigurationFilePath, ".lastgood-*");
                }
            }
            catch (Exception)
            {
                // The first save then overwrites it, as it did before; nothing worse.
            }
        }

        /// <summary>Keeps the newest few copies matching <c>&lt;file&gt;&lt;suffixPattern&gt;</c>.</summary>
        private static void PruneCopies(string configurationPath, string suffixPattern)
        {
            var folder = System.IO.Path.GetDirectoryName(configurationPath)!;
            var pattern = System.IO.Path.GetFileName(configurationPath) + suffixPattern;
            var old = new System.IO.DirectoryInfo(folder).GetFiles(pattern)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal) // the stamp in the name is the time; a copy keeps its source's write time
                .Skip(VersionBackupsKept);
            foreach (var file in old)
            {
                try { file.Delete(); } catch (Exception) { /* left for the next time */ }
            }
        }

        private static string SafeLabel(string label)
        {
            var chars = label.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray();
            return new string(chars);
        }

        private static void PruneVersionBackups(string configurationPath)
        {
            var folder = System.IO.Path.GetDirectoryName(configurationPath)!;
            var pattern = System.IO.Path.GetFileName(configurationPath) + ".*.bak";
            var old = new System.IO.DirectoryInfo(folder).GetFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(VersionBackupsKept);
            foreach (var file in old)
            {
                try { file.Delete(); } catch (Exception) { /* left for the next start */ }
            }
        }

        /// <summary>
        /// A configuration with no households, devices, Bonfires or links: what Jellyfin's
        /// fallback creates. Checked first so an ordinary save never reads the file back.
        /// </summary>
        private static bool LooksEmpty(PluginConfiguration c)
            => (c.Mappings?.Count ?? 0) == 0
               && (c.KnownDevices?.Count ?? 0) == 0
               && (c.BonfireGroups?.Count ?? 0) == 0
               && (c.LinkedAccounts?.Count ?? 0) == 0;

        /// <summary>
        /// When the file on disk is there but won't read, and a last good copy does: keeps
        /// the unreadable file, copies the good values onto <paramref name="config"/> and
        /// returns true. A missing file is a new install, and a file that reads is a
        /// household that really did delete everything; both are left alone.
        /// </summary>
        private bool TryRestoreOver(PluginConfiguration config)
        {
            try
            {
                var path = ConfigurationFilePath;
                if (!System.IO.File.Exists(path)) return false;

                try
                {
                    XmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), path);
                    return false;
                }
                catch (Exception)
                {
                    // Unreadable: the case this exists for.
                }

                // Kept whether or not there is anything to restore: with no last good copy
                // (a file broken before this release), it is all that is left to repair by hand.
                var kept = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                System.IO.File.Copy(path, kept, overwrite: true);
                // Kept like the .bak copies: a file that breaks again and again must not fill
                // the folder. The one just made is the newest, so it stays. r2-19-04.
                PruneCopies(path, ".unreadable-*");
                RestoredFrom = kept;
                if (!System.IO.File.Exists(LastGoodPath)) return false;

                var good = (PluginConfiguration)XmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), LastGoodPath);
                CopyInto(good, config);
                Restored = true;
                return true;
            }
            catch (Exception)
            {
                // The last good copy won't read either, or the disk refused. Nothing better to
                // offer than Jellyfin's defaults, but the unreadable file is already kept.
                return false;
            }
        }

        /// <summary>
        /// Set when this start found the configuration unreadable: where the file was kept.
        /// <see cref="Restored"/> says whether the last good copy replaced it. Both logged by
        /// the bootstrap task, which has a logger.
        /// </summary>
        internal static string? RestoredFrom { get; private set; }

        internal static bool Restored { get; private set; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Bonfire",
                    DisplayName = "Bonfire",
                    EnableInMainMenu = true,
                    EmbeddedResourcePath = GetType().Namespace + ".Web.profilesDashboard.html"
                }
            };
        }
    }
}
