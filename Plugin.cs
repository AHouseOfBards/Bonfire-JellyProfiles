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
                if (!ReferenceEquals(current, incoming))
                {
                    foreach (var property in typeof(PluginConfiguration).GetProperties(
                                 System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                        {
                            property.SetValue(current, property.GetValue(incoming));
                        }
                    }
                }

                SaveConfiguration();
            }

            ConfigurationChanged?.Invoke(this, current);
        }

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
