using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Profiles.Configuration;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// The apps <see cref="ProxySwitcher"/> may serve, and which of them an administrator
    /// has ticked.
    ///
    /// <para><b>Only apps we add.</b> Each entry is one we have read the source of and tried:
    /// the picker is made of folders, and it only works on an app that opens a folder when
    /// it is clicked and does not open it while scrolling past. An administrator typing in
    /// a client name would get an app nobody has checked that on.</para>
    ///
    /// <para><b>Matched on the client name the app sends.</b> That is client-supplied, and it
    /// is fine here: a miss means the app behaves exactly as it does today, and a forged
    /// match gets a forger the picker for an account they already hold the token of.</para>
    /// </summary>
    public static class ProxyClients
    {
        /// <summary>One app the picker can be served to.</summary>
        public sealed record Entry(string Key, string Name, string ClientPrefix);

        /// <summary>
        /// Every app that can be ticked. Moonfin first, because we can test it.
        ///
        /// <para>A stopgap per app: once an app supports Bonfire's own profile selector, its
        /// entry is removed here and its checkbox from the dashboard in the same release.
        /// A server that had it ticked loses the key through <see cref="Normalize"/>.</para>
        /// </summary>
        public static readonly IReadOnlyList<Entry> Catalogue = new[]
        {
            // Moonfin sends "Moonfin for Android TV", "Moonfin for iOS" and so on.
            new Entry("moonfin", "Moonfin", "Moonfin"),
        };

        /// <summary>The known keys from <paramref name="keys"/>, lower case, once each.</summary>
        public static List<string> Normalize(IEnumerable<string>? keys)
        {
            var known = new List<string>();
            if (keys == null) return known;

            foreach (var raw in keys)
            {
                var key = raw?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(key) || known.Contains(key)) continue;
                if (Catalogue.Any(e => e.Key == key)) known.Add(key);
            }

            return known;
        }

        /// <summary>True when any app is ticked. The cheap check every request makes first.</summary>
        public static bool AnyEnabled(PluginConfiguration? config)
            => config?.ProxyClients != null && config.ProxyClients.Count > 0;

        /// <summary>
        /// A cheap first look at a request's authorization headers: false when neither can
        /// name a ticked app. A substring test, so it can say yes wrongly but never no wrongly;
        /// the full parse follows only on a yes.
        /// </summary>
        public static bool MayBe(PluginConfiguration? config, string? authorization, string? embyAuthorization)
        {
            if (!AnyEnabled(config)) return false;

            foreach (var entry in Catalogue)
            {
                if (!config!.ProxyClients.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)) continue;
                if ((authorization ?? string.Empty).Contains(entry.ClientPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                if ((embyAuthorization ?? string.Empty).Contains(entry.ClientPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>True when <paramref name="clientName"/> belongs to an app that is ticked.</summary>
        public static bool IsEnabled(PluginConfiguration? config, string? clientName)
        {
            if (string.IsNullOrWhiteSpace(clientName) || !AnyEnabled(config)) return false;

            foreach (var entry in Catalogue)
            {
                if (!config!.ProxyClients.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)) continue;
                if (clientName.Trim().StartsWith(entry.ClientPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
    }
}
