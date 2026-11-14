using System;
using System.Linq;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Decides whether a Quick Connect request should be refused so the app opens on its password
    /// field instead.
    /// <para><b>Why anything is needed.</b> Android TV opens its sign-in screen on Quick Connect
    /// and offers no way to ask it not to. <c>ARG_SKIP_QUICKCONNECT</c> exists in
    /// <c>UserLoginFragment</c> and no caller sets it, so <c>HasPassword</c> and nothing else the
    /// server can say changes the choice. The one lever is the response to
    /// <c>POST /QuickConnect/Initiate</c>: a 401 makes the client take its own
    /// <c>UnavailableQuickConnectState</c> path straight to the credentials form, where a PIN is
    /// typed. Nothing is faked: for that request Quick Connect is unavailable.</para>
    /// <para><b>Why not per profile.</b> It cannot be. <c>initiateQuickConnect()</c> takes no
    /// arguments, and the client randomizes the device id for it:</para>
    ///
    /// <code>
    /// quickConnectApi.update(baseUrl = server.address,
    ///     deviceInfo = defaultDeviceInfo.forUser(UUID.randomUUID()))
    /// </code>
    ///
    /// <para>so the request names no user and does not carry the set's usual id.
    /// <c>QuickConnectManager.TryConnect</c> sees only Client, Device, DeviceId and Version.</para>
    /// <para><b>Why the device name and not the client name.</b> The name is what survives
    /// unrandomized. The client string does not survive app updates: the app installed on the test
    /// television reports <c>"Jellyfin Android TV"</c> while current app source builds
    /// <c>"Jellyfin for Android TV"</c>. Matching that would have broken silently on an
    /// upgrade.</para>
    /// <para><b>The rule.</b> Leave Quick Connect alone the first time a device is seen: before
    /// anyone has signed in there are no profiles to offer. Afterwards, go to the PIN screen.
    /// "This account's first time" is not observable here because the request carries no account,
    /// so the equivalent is "a device no household has signed in on yet".</para>
    /// <para><b>What it costs when it is wrong.</b> Device names are not unique; Android TV reports
    /// the model, so every set of one make shares a name. Until 1.6.3.3 a household's television
    /// took Quick Connect away from <i>everyone</i> on the server with the same model, including
    /// people who never use Bonfire, for good, because their devices are never recorded. Now a
    /// sign-in by an account outside every household notes the name
    /// (<see cref="NoteSharedName"/>), and a noted name keeps Quick Connect for everybody. The
    /// household's set then opens on Quick Connect as it would without Bonfire, with the password
    /// field one button away. A name nobody else has signed in with yet is still refused once, and
    /// a username and password still work. Logged both ways.</para>
    /// </summary>
    public static class QuickConnectGate
    {
        /// <summary>
        /// The clients whose sign-in screen opens on Quick Connect with no way to ask it not to, and
        /// which therefore need this at all.
        /// <para><b>Why a list and not every client.</b> Refusing Quick Connect only helps reach a
        /// password field the app is standing in front of. Every other client surveyed already offers
        /// one: Roku's Quick Connect is a button on a sign-in group whose primary control is the
        /// password box, Swiftfin fills the username and moves focus to the password, Findroid and
        /// Wholphin open on a credentials form. Denying theirs would turn a deliberate Quick Connect
        /// press into "Quick Connect not available" and fix nothing.</para>
        /// <para><b>Matched as a substring on purpose.</b> The full client string is not stable: the
        /// installed app reports <c>"Jellyfin Android TV"</c> while current source builds
        /// <c>"Jellyfin for Android TV"</c>. Both contain <c>"Android TV"</c>. This is also why the
        /// <i>device</i> name, not the client name, decides whether a household has used the
        /// device.</para>
        /// </summary>
        private static readonly string[] OpensOnQuickConnect = { "Android TV" };

        /// <summary>
        /// True when this Quick Connect request should be refused.
        /// <para>
        /// Every uncertain case answers false. Refusing wrongly takes away the only way into a device
        /// that has none yet; allowing wrongly costs an extra keypress.
        /// </para>
        /// </summary>
        public static bool ShouldDeny(PluginConfiguration? config, string? deviceName, string? client)
            => ShouldDenyFor(config, deviceName, client, null);

        /// <summary>
        /// <see cref="ShouldDeny"/>, told which household accounts have no password to fall back to.
        /// <para><b>Why.</b> The refusal sends the app to its password field. An account that cannot
        /// sign in by password (an SSO account, or one whose provider is gone, which Jellyfin files
        /// under its invalid provider) has nothing to type there, and SSO works only through the web UI
        /// or Quick Connect, so refusing would strand it. The request names no account, so the question
        /// is asked of the household that owns the matching device: if its main account has no password
        /// path, Quick Connect stays. Judged by what the account can do, never by a plugin's
        /// name.</para>
        /// </summary>
        /// <param name="needsQuickConnect">True for a household main account that cannot sign
        /// in by password. Null judges nobody, which is the old behaviour.</param>
        public static bool ShouldDenyFor(
            PluginConfiguration? config, string? deviceName, string? client, Func<Guid, bool>? needsQuickConnect)
        {
            if (config?.KnownDevices == null || !config.SkipQuickConnectOnKnownDevices)
            {
                return false;
            }

            // A client with a password field of its own keeps Quick Connect. So do unknown clients: this
            // can only take away, so the uncertain answer leaves the app as its authors built it.
            if (!OpensOnQuickConnectFirst(client)) return false;

            // Nothing to match on is not a match, or a stored record with a blank name would answer for
            // every device that sends none.
            var wanted = deviceName?.Trim();
            if (string.IsNullOrEmpty(wanted)) return false;

            // Somebody outside every household uses a television by this name. Refusing would take Quick
            // Connect from them, and the request cannot say whose television this is.
            if (config.QuickConnectSharedDeviceNames != null
                && config.QuickConnectSharedDeviceNames.Any(n =>
                       string.Equals(n?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var matching = config.KnownDevices.Where(d =>
                d.MasterUserId != Guid.Empty
                && !string.IsNullOrWhiteSpace(d.DeviceName)
                && string.Equals(d.DeviceName.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            // One household on this television with no password to fall back to is enough to keep Quick
            // Connect for the set: it is that person's only way in.
            if (needsQuickConnect != null && matching.Any(d => needsQuickConnect(d.MasterUserId))) return false;

            return matching.Count > 0;
        }

        /// <summary>
        /// True when this account cannot sign in by password: its provider is named and no enabled
        /// provider carries that name, which is when Jellyfin hands it to its invalid provider. An
        /// account bound to nothing is tried against every provider, so it can. A missing account
        /// answers true: nothing can be judged, and the uncertain answer keeps Quick Connect.
        /// </summary>
        public static bool HasNoPasswordPath(MediaBrowser.Controller.Library.IUserManager? users, Guid userId)
        {
            var user = users?.GetUserById(userId);
            if (users == null || user == null) return true;

            var bound = user.AuthenticationProviderId;
            if (string.IsNullOrEmpty(bound)) return false;

            return !users.GetAuthenticationProviders().Any(p =>
                string.Equals(p.Id, bound, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// True for the clients whose sign-in screen opens on Quick Connect with no way to ask it not
        /// to. See <see cref="OpensOnQuickConnect"/>.
        /// </summary>
        public static bool OpensOnQuickConnectFirst(string? client)
        {
            var who = client?.Trim();
            return !string.IsNullOrEmpty(who)
                && OpensOnQuickConnect.Any(needle => who.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The most names kept. Oldest are dropped first.</summary>
        internal const int MaxSharedNames = 500;

        /// <summary>
        /// Notes that an account outside every household signed in on a television of this name.
        /// Returns true when the list changed and is worth saving.
        /// <para><b>Caller must hold <c>ConfigLock</c>.</b> Called by the session listener for sign-ins
        /// it otherwise ignores; only for clients this gate could refuse, and never for a blank or
        /// placeholder name, so the list stays short.</para>
        /// </summary>
        public static bool NoteSharedName(PluginConfiguration? config, string? deviceName, string? client)
        {
            if (config == null || !OpensOnQuickConnectFirst(client)) return false;

            var name = deviceName?.Trim();
            if (string.IsNullOrEmpty(name) || ProfilesBaseController.IsPlaceholderDeviceName(name)) return false;

            // A name no household device carries changes nothing about the gate, so it is not worth an
            // entry or a save. Without this, any account could grow the list by signing in under invented
            // names and force a configuration write each time.
            if (config.KnownDevices == null || !config.KnownDevices.Any(d =>
                    d.MasterUserId != Guid.Empty
                    && string.Equals(d.DeviceName?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            config.QuickConnectSharedDeviceNames ??= new System.Collections.Generic.List<string>();
            if (config.QuickConnectSharedDeviceNames.Any(n => string.Equals(n?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            config.QuickConnectSharedDeviceNames.Add(name);
            while (config.QuickConnectSharedDeviceNames.Count > MaxSharedNames)
            {
                config.QuickConnectSharedDeviceNames.RemoveAt(0);
            }

            return true;
        }

        /// <summary>
        /// <see cref="NoteSharedName"/> with the lock and the save around it, the configuration read
        /// inside the lock, like <see cref="DeviceRegistry.RecordAndSave"/>.
        /// Returns true when a name was added.
        /// </summary>
        public static bool NoteSharedNameAndSave(string? deviceName, string? client)
        {
            // Nothing to note is decided without the lock, so the sign-ins this ignores, nearly all of
            // them, never wait on it.
            if (!OpensOnQuickConnectFirst(client) || string.IsNullOrWhiteSpace(deviceName)) return false;

            lock (ProfilesBaseController.ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (!NoteSharedName(config, deviceName, client)) return false;

                Plugin.Instance?.SaveConfiguration();
                return true;
            }
        }
    }
}
