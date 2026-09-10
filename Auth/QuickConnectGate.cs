using System;
using System.Linq;
using Jellyfin.Profiles.Configuration;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Decides whether a Quick Connect request should be refused so the app opens on its
    /// password field instead.
    ///
    /// <para><b>Why anything is needed.</b> Android TV opens its sign-in screen on Quick
    /// Connect and offers no way to ask it not to — <c>ARG_SKIP_QUICKCONNECT</c> exists in
    /// <c>UserLoginFragment</c> and no caller ever sets it, so the choice is not affected by
    /// <c>HasPassword</c> or by anything else the server can say. The one lever is the
    /// response to <c>POST /QuickConnect/Initiate</c>: a 401 makes the client take its own
    /// <c>UnavailableQuickConnectState</c> path straight to the credentials form, which is
    /// where a PIN is typed. Nothing is faked — for that request Quick Connect genuinely is
    /// unavailable.</para>
    ///
    /// <para><b>Why not per profile.</b> It cannot be. <c>initiateQuickConnect()</c> takes no
    /// arguments, and the client randomises the device id for it:</para>
    ///
    /// <code>
    /// quickConnectApi.update(baseUrl = server.address,
    ///     deviceInfo = defaultDeviceInfo.forUser(UUID.randomUUID()))
    /// </code>
    ///
    /// <para>so the request names no user and does not even carry the set's usual id.
    /// <c>QuickConnectManager.TryConnect</c> sees only Client, Device, DeviceId and
    /// Version.</para>
    ///
    /// <para><b>Why the device name and not the client name.</b> The name is what survives
    /// unrandomised. The client string does not survive app updates: the app installed on
    /// Logan's television reports <c>"Jellyfin Android TV"</c> while the current app source
    /// builds <c>"Jellyfin for Android TV"</c>. Matching that would have broken silently on
    /// an upgrade, which is the worst way for a setting to stop working.</para>
    ///
    /// <para><b>The rule.</b> Leave Quick Connect alone the first time a device is seen —
    /// before anyone has signed in there are no profiles to offer and Quick Connect is the
    /// easy way in. Afterwards, go to the PIN screen. "This account's first time" is not
    /// observable here, because the request carries no account, so the observable equivalent
    /// is "a device no household has signed in on yet".</para>
    ///
    /// <para><b>What it costs when it is wrong.</b> Device names are not unique, so a second
    /// television with the same name as one already in use would be sent to the password
    /// field on its first sign-in — a mild inconvenience, and a username and password still
    /// work. A renamed device gets Quick Connect once more. Both are logged.</para>
    /// </summary>
    public static class QuickConnectGate
    {
        /// <summary>
        /// True when this Quick Connect request should be refused.
        /// <para>
        /// Every uncertain case answers false. Refusing wrongly takes away the only way into
        /// a device that has none yet; allowing wrongly costs an extra keypress.
        /// </para>
        /// </summary>
        public static bool ShouldDeny(PluginConfiguration? config, string? deviceName)
        {
            if (config?.KnownDevices == null || !config.SkipQuickConnectOnKnownDevices)
            {
                return false;
            }

            // Nothing to match on is not a match — and must not be, or a stored record with
            // a blank name would answer for every device that sends none.
            var wanted = deviceName?.Trim();
            if (string.IsNullOrEmpty(wanted)) return false;

            return config.KnownDevices.Any(d =>
                d.MasterUserId != Guid.Empty
                && !string.IsNullOrWhiteSpace(d.DeviceName)
                && string.Equals(d.DeviceName.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }
    }
}
