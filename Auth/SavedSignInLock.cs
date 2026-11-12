using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Makes a Roku ask for a profile's PIN every time somebody picks it, the way the web
    /// switcher does, rather than once per television.
    ///
    /// <para><b>The defect (issue #40).</b> Bonfire checks the PIN correctly; the Roku app
    /// never asks it again. Its sign-in form has a "Save Credentials?" box, ticked by
    /// default, that stores the profile's access token on the set. Picking that profile on
    /// the user list then tries the stored token first and goes straight in:</para>
    ///
    /// <code>
    /// ' jellyfin-roku, source/ShowScenes.bs, LoginFlow
    /// myToken = get_user_setting("token")
    /// if myToken &lt;&gt; invalid
    ///     session.user.Update("authToken", myToken)
    ///     currentUserResponse = AboutMe()
    /// </code>
    ///
    /// <para>and "Change user" in its menu calls <c>SignOut(false)</c>, which keeps the
    /// stored token. So a PIN typed once on a television opened that profile for anybody
    /// holding the remote, for good.</para>
    ///
    /// <para><b>The fix.</b> The one request a Roku is certain to make between one person
    /// leaving and the next choosing is <c>GET /Users/Public</c>, the list its picker is
    /// drawn from. When that arrives from a television a household uses, the saved
    /// sign-ins of that household's PIN-protected accounts <i>on that television</i> are
    /// revoked. The app's own recovery then does the rest, verified in the same file:
    /// <c>AboutMe()</c> answers 401, it deletes the token, tries an empty password (which
    /// the provider refuses for a profile with a PIN), and opens its password form.</para>
    ///
    /// <para><b>Roku only, by the shape of the id, not by the client name.</b> Roku builds
    /// its signed-in device id as its bare id with the account name appended
    /// (<see cref="DeviceRegistry.BareDeviceIdFor"/>), so only a row whose id is exactly
    /// <c>bare + name</c> for that account is touched. Jellyfin Android TV must NOT be
    /// handled this way: a revoked stored token there ends in
    /// <c>ApiClientErrorLoginState</c>, which its picker shows as "server connection
    /// failed", with the dead token still stored — the profile would become unreachable
    /// from that card. Android TV has its own setting for this ("Always ask for
    /// credentials"), and the docs point to it.</para>
    ///
    /// <para><b>What it leaves alone.</b> A request carrying a token (somebody is signed in,
    /// so this is not a picker), accounts with no PIN, a profile whose owner allowed skipping
    /// its PIN on the home network when the request is local, every other household, every
    /// other device, and everything while PIN sign-in on apps is off — without it a revoked
    /// profile would have no way back in on that television at all.</para>
    ///
    /// <para><b>Not a boundary of its own.</b> The device id is client-supplied, so somebody
    /// who knows a household's Roku id can make that Roku ask for its PINs again. That is
    /// the whole of what it buys them.</para>
    /// </summary>
    public static class SavedSignInLock
    {
        /// <summary>
        /// The accounts to lock on this device, each with the exact device ids its Roku
        /// sign-ins could have been stored under. Separated from the revoking so a harness
        /// can check the decision without a server.
        /// </summary>
        /// <param name="usernameOf">
        /// The account's real username. Roku appends whichever name it was given: the
        /// household's name for the profile when it came off our sign-in list, the real one
        /// when it was typed in full or restored by <c>AboutMe()</c>.
        /// </param>
        public static IReadOnlyList<LockTarget> Targets(
            PluginConfiguration? config,
            string? deviceId,
            bool isLocal,
            Func<Guid, string?> usernameOf,
            System.Net.IPAddress? address = null,
            Func<Guid, bool>? isAdministrator = null)
        {
            var none = Array.Empty<LockTarget>();
            var bare = deviceId?.Trim();

            if (config?.Mappings == null || !config.EnableClientPinLogin) return none;
            if (string.IsNullOrEmpty(bare) || bare.Length < DeviceRegistry.MinimumBareDeviceIdLength) return none;

            var master = DeviceRegistry.FindMaster(config, bare);
            if (master == Guid.Empty) return none;

            var mappings = config.Mappings.ToList();
            // No way to tell who is an administrator means nobody is trusted.
            var adminCheck = isAdministrator ?? (_ => true);
            var hasProfiles = mappings.Any(m => m.MasterUserId == master && m.ProfileUserId != master);

            var targets = new List<LockTarget>();
            foreach (var m in mappings.Where(m => m.MasterUserId == master))
            {
                if (string.IsNullOrEmpty(m.PinHash)) continue;

                // A master's own row only counts while it actually owns profiles - the same
                // rule the provider uses before it will open a master by PIN.
                if (m.ProfileUserId == master && !hasProfiles) continue;

                // The web switcher skips this PIN on the home network, or from an address
                // listed for this household (never an administrator's); so does this.
                if (m.BypassPinOnLocalNetwork && isLocal) continue;
                if (TrustedAddresses.Applies(config, address, m.MasterUserId, m.ProfileUserId, adminCheck)) continue;

                var names = new List<string>();
                if (m.ProfileUserId != master && !string.IsNullOrWhiteSpace(m.ProfileName)) names.Add(m.ProfileName);
                var username = usernameOf(m.ProfileUserId);
                if (!string.IsNullOrWhiteSpace(username)) names.Add(username);

                var ids = names
                    .Select(DeviceRegistry.RokuFriendlyName)
                    .Where(friendly => friendly.Length >= 3)
                    .Select(friendly => bare + friendly)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (ids.Count > 0) targets.Add(new LockTarget(m.ProfileUserId, m.ProfileName, ids));
            }

            return targets;
        }

        /// <summary>
        /// Revokes the saved Roku sign-ins of this device's PIN-protected accounts. Returns
        /// how many were revoked. Never throws: this runs in front of the request every
        /// client signs in through.
        /// </summary>
        public static async Task<int> LockAsync(
            string? deviceId,
            bool requestCarriesToken,
            bool isLocal,
            System.Net.IPAddress? address,
            PluginConfiguration? config,
            IDeviceManager deviceManager,
            ISessionManager sessionManager,
            IUserManager userManager,
            ILogger logger)
        {
            // Somebody is signed in on this client, so this is not a picker being shown.
            if (requestCarriesToken) return 0;

            var revoked = 0;
            try
            {
                var targets = Targets(config, deviceId, isLocal, id => userManager.GetUserById(id)?.Username, address,
                    id => TrustedAddresses.IsAdministrator(userManager, id));

                if (targets.Count == 0) return 0;

                // One lookup for the whole request, not one per PIN-protected account. A
                // request with nothing to lock never gets here (r2-06-05).
                var rows = deviceManager.GetDevices(new DeviceQuery())?.Items;
                if (rows == null) return 0;

                foreach (var target in targets)
                {
                    foreach (var row in rows.Where(r => r.UserId == target.UserId
                        && target.DeviceIds.Contains(r.DeviceId?.Trim() ?? string.Empty, StringComparer.Ordinal)).ToList())
                    {
                        await sessionManager.Logout(row).ConfigureAwait(false);
                        revoked++;

                        logger.LogInformation(
                            "ProfilesPlugin: {Profile} has a PIN, so its saved sign-in on {DeviceId} was cleared "
                            + "as the sign-in screen opened. The app will ask for the PIN.",
                            string.IsNullOrEmpty(target.Name) ? target.UserId.ToString("N") : target.Name,
                            row.DeviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ProfilesPlugin: could not clear saved sign-ins on device {DeviceId}; the app may open a "
                    + "PIN-protected profile without asking.",
                    deviceId);
            }

            return revoked;
        }

        /// <summary>One account to lock, and the device ids its saved sign-ins may carry.</summary>
        public sealed class LockTarget
        {
            public LockTarget(Guid userId, string name, IReadOnlyList<string> deviceIds)
            {
                UserId = userId;
                Name = name;
                DeviceIds = deviceIds;
            }

            public Guid UserId { get; }

            public string Name { get; }

            public IReadOnlyList<string> DeviceIds { get; }
        }
    }
}
