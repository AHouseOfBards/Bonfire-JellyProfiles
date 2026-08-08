using System;
using System.Collections.Generic;
using Jellyfin.Profiles.Configuration;
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
        /// next page load.
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

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Profiles",
                    DisplayName = "Profiles",
                    EnableInMainMenu = true,
                    EmbeddedResourcePath = GetType().Namespace + ".Web.profilesDashboard.html"
                }
            };
        }
    }
}
