using System;
using System.Collections.Generic;
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

        /// <summary>Trips the emergency disable. There is no code path that clears it.</summary>
        internal static void TripPanicDisable() => _panicDisabled = true;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            AppPaths = applicationPaths;
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
            lock (ProfilesBaseController.ConfigLock)
            {
                current = Configuration;
                if (!ReferenceEquals(current, incoming)) CopyInto(incoming, current);
                SaveConfiguration();
            }

            ConfigurationChanged?.Invoke(this, current);
        }

        private static void CopyInto(PluginConfiguration from, PluginConfiguration to)
        {
            foreach (var property in typeof(PluginConfiguration).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    property.SetValue(to, property.GetValue(from));
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

        /// <summary>
        /// A configuration with no households, devices or Bonfires: what Jellyfin's
        /// fallback creates. Checked first so an ordinary save never reads the file back.
        /// </summary>
        private static bool LooksEmpty(PluginConfiguration c)
            => (c.Mappings?.Count ?? 0) == 0
               && (c.KnownDevices?.Count ?? 0) == 0
               && (c.BonfireGroups?.Count ?? 0) == 0;

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
