using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Bonfire's own record of which household a device belongs to.
    ///
    /// <para><b>Why we keep one at all.</b> Jellyfin already tracks devices, and
    /// <c>IDeviceManager</c> looked like the obvious place to ask "who uses this
    /// television". It is not, because a <c>Device</c> row belongs to a <i>session</i>
    /// rather than to a device:</para>
    ///
    /// <code>
    /// public async Task Logout(Device device)          // SessionManager
    /// {
    ///     await _deviceManager.DeleteDevice(device).ConfigureAwait(false);
    /// </code>
    ///
    /// <para>and Android TV's "Switch account" destroys the session <b>before</b> it opens
    /// the picker:</para>
    ///
    /// <code>
    /// sessionRepository.destroyCurrentSession()
    /// activity?.startActivity(ActivityDestinations.startup(activity))
    /// </code>
    ///
    /// <para>So the exact action that opens the picker deletes the evidence the picker
    /// needs. 1.6.1.4 through 1.6.1.6 shipped against <c>IDeviceManager</c> and could never
    /// have worked on the client they were built for. Ours survives a sign-out precisely
    /// because it is not a session artefact.</para>
    ///
    /// <para><b>This is not a new store.</b> <see cref="PluginConfiguration.KnownDevices"/>
    /// has held exactly this — a device id, an owning master, a last-seen stamp — since the
    /// device-restrictions work, along with pruning and a dashboard row an administrator can
    /// delete. What it lacked was a writer a television ever reaches: both call sites were
    /// authenticated Bonfire routes, which only the web switcher calls, so the map was
    /// populated exclusively by the one client that had no use for it. See
    /// <see cref="BonfireSessionListener"/>, which is the writer that fixes that.</para>
    ///
    /// <para><b>Not a security boundary.</b> A device id is client-supplied. Forging one
    /// reveals a household's profile <i>names</i> against a baseline of nothing; entry still
    /// needs the PIN, and library and rating enforcement is server-side regardless.</para>
    /// </summary>
    public static class DeviceRegistry
    {
        /// <summary>
        /// The master account a device belongs to, or <see cref="Guid.Empty"/> if we have no
        /// usable record of it.
        /// <para>
        /// Records written before <c>MasterUserId</c> existed carry <c>Guid.Empty</c>, and
        /// those identify nobody — returning them would hand whichever household asked first
        /// a device that might belong to another.
        /// </para>
        /// </summary>
        public static Guid FindMaster(PluginConfiguration? config, string? deviceId)
        {
            if (config?.KnownDevices == null || string.IsNullOrWhiteSpace(deviceId))
            {
                return Guid.Empty;
            }

            // Trimmed on both sides. The stored id is trimmed at the door by Record, and an
            // id arriving with a stray space is a different string to every ordinal
            // comparison in the plugin.
            var wanted = deviceId.Trim();

            var exact = Lookup(config, wanted);
            if (exact != Guid.Empty) return exact;

            // ── one television, two device ids ───────────────────────────────────────
            //
            // This is what made 1.6.1.7 useless while recording perfectly. Some clients
            // derive a PER-USER device id once somebody is signed in, and send the plain
            // one when nobody is — which is exactly the sign-in screen we are answering.
            //
            // Jellyfin Android TV, SessionRepository.setCurrentSession:
            //
            //     val deviceInfo = session?.let { defaultDeviceInfo.forUser(it.userId) }
            //         ?: defaultDeviceInfo
            //
            //     fun DeviceInfo.forUser(user: String): DeviceInfo = copy(
            //         id = SHA-1("${id}+$user") as lowercase hex)
            //
            // Verified against Logan's 2026-09-10 log:
            // sha1("9a6dae35cc29c74f" + "+" + "8615867e-3617-4ad3-8d62-639b3bdc1305")
            //   == "7629ac2570f4154e81c8ce3f99a6d4764eb7e734", the id the session carried,
            // while the picker asked about "9a6dae35cc29c74f".
            //
            // Jellyfin for Android appends instead of hashing, no separator - the same log
            // shows both "6044d6840404e4e8" and that id with the household guid glued on.
            //
            // A hash cannot be reversed, so the derivation is applied FORWARDS: for every
            // account Bonfire knows, work out what this device's id would have become had
            // that account signed in on it, and look for that. Bounded by the number of
            // Bonfire users rather than the server's, and a sign-in screen is opened by
            // hand — this is not on any hot path.
            foreach (var userId in HouseholdMembers(config))
            {
                var suffix = userId.ToString();

                // Jellyfin for Android: id + userId, no separator.
                var appended = Lookup(config, wanted + suffix);
                if (appended != Guid.Empty) return appended;

                // Jellyfin Android TV: sha1(id + "+" + userId), lowercase hex.
                var hashed = Lookup(config, Sha1Hex(wanted + "+" + suffix));
                if (hashed != Guid.Empty) return hashed;
            }

            return Guid.Empty;
        }

        /// <summary>
        /// The owning master of the record with this exact device id, or
        /// <see cref="Guid.Empty"/> when there is none or it names nobody.
        /// </summary>
        private static Guid Lookup(PluginConfiguration config, string deviceId)
        {
            var row = config.KnownDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

            return row?.MasterUserId ?? Guid.Empty;
        }

        /// <summary>
        /// Every account in any household Bonfire runs — masters and sub-profiles both.
        /// <para>
        /// Sub-profiles are included because after the first switch it is a profile, not the
        /// master, that a television is signed in as, so the per-user id is derived over the
        /// profile's own guid while the record still belongs to the household.
        /// </para>
        /// </summary>
        private static IEnumerable<Guid> HouseholdMembers(PluginConfiguration config)
        {
            if (config.Mappings == null) yield break;

            var seen = new HashSet<Guid>();
            foreach (var m in config.Mappings)
            {
                if (m.MasterUserId != Guid.Empty && seen.Add(m.MasterUserId)) yield return m.MasterUserId;
                if (m.ProfileUserId != Guid.Empty && seen.Add(m.ProfileUserId)) yield return m.ProfileUserId;
            }
        }

        /// <summary>
        /// The shortest device id we will ever store as a derivation of another. Roku's own
        /// id is a 36-character channel client id, so this rejects nothing real — it exists
        /// so an account with a short name can never strip a device id down to a stub that
        /// some unrelated client might genuinely send.
        /// </summary>
        internal const int MinimumBareDeviceIdLength = 8;

        /// <summary>
        /// The id this client would have sent <i>before</i> anyone signed in, when the id it
        /// is sending now was built by appending the account's name to it — or null when it
        /// was not built that way.
        ///
        /// <para><b>Why this is reversed here and not derived at lookup.</b> Jellyfin Roku
        /// appends the signed-in account's name to its device id, in
        /// <c>session.user.SetServerDeviceName()</c>:</para>
        ///
        /// <code>
        /// deviceName = localGlobal.device.id                  ' getChannelClientID()
        /// if isChainValid(localGlobal, "session.user.friendlyName")
        ///     deviceName = deviceName + localGlobal.session.user.friendlyName
        /// </code>
        ///
        /// <para>where <c>friendlyName</c> is the account name with everything outside
        /// <c>[^a-zA-Z0-9\-\_]</c> removed. So a Roku sends the bare id at its sign-in screen
        /// and the appended one once somebody is in — the same split that made 1.6.1.7
        /// useless on Android TV.</para>
        ///
        /// <para><b>Roku cannot be handled the way Android TV is.</b> That derivation is
        /// applied forwards over <see cref="HouseholdMembers"/>, which yields Guids; this
        /// suffix is a <i>name</i>. Worse, it is not always a name we hold:
        /// <c>ProfileMapping</c> carries <c>ProfileName</c>, but Roku's <c>AboutMe()</c> asks
        /// <c>/Users/{id}</c> — which Bonfire does not rewrite — so on a launch that restores
        /// a stored token the suffix is the account's <i>real</i> username, while on a fresh
        /// PIN login it is the household's name for it. Guessing which would mean trying
        /// every name we know and hoping.</para>
        ///
        /// <para><b>So it is reversed at the moment of recording instead</b>, where
        /// <c>SessionInfo.UserName</c> says exactly which name the client used. Concatenation
        /// is reversible when the suffix is known; that is the whole reason the Android TV
        /// hash has to be applied forwards and this does not.</para>
        ///
        /// <para>Ordinal, not case-insensitive: Roku appends the name verbatim after
        /// stripping, so a case-folded comparison could only ever strip the wrong thing.</para>
        /// </summary>
        public static string? BareDeviceIdFor(string? deviceId, string? accountName)
        {
            var id = deviceId?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(accountName)) return null;

            var friendly = RokuFriendlyName(accountName);

            // A very short suffix is a coincidence waiting to happen — "c74f" is a plausible
            // account name and also the tail of a real ANDROID_ID.
            if (friendly.Length < 3) return null;

            if (!id.EndsWith(friendly, StringComparison.Ordinal)) return null;

            var bare = id.Substring(0, id.Length - friendly.Length);
            return bare.Length < MinimumBareDeviceIdLength ? null : bare;
        }

        /// <summary>
        /// An account name the way Roku writes it into a device id: everything outside
        /// <c>[a-zA-Z0-9\-\_]</c> removed, matching its own
        /// <c>CreateObject("roRegex", "[^a-zA-Z0-9\-\_]", "")</c>.
        /// </summary>
        private static string RokuFriendlyName(string name)
        {
            var kept = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_')
                {
                    kept.Append(c);
                }
            }

            return kept.ToString();
        }

        /// <summary>
        /// Lowercase hex SHA-1, matching Kotlin's
        /// <c>digest().joinToString("") { "%02x".format(it) }</c>.
        /// <para>
        /// Not a security use — it re-derives an identifier another program already chose,
        /// so the algorithm is theirs to pick and ours to match.
        /// </para>
        /// </summary>
        private static string Sha1Hex(string value)
        {
#pragma warning disable CA5350 // matching a client's own identifier, not protecting anything
            var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
#pragma warning restore CA5350
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Notes that <paramref name="ownerId"/>'s household was seen on this device. Returns
        /// true when something changed that is worth writing to disk.
        /// <para><b>Caller must hold <see cref="ProfilesBaseController.ConfigLock"/>.</b>
        /// Separated from the saving so a harness can drive it without a plugin instance —
        /// see <see cref="RecordAndSave"/> for the call the plugin actually makes.</para>
        /// </summary>
        public static bool Record(
            PluginConfiguration? config,
            string? deviceId,
            string? deviceName,
            string? client,
            Guid ownerId,
            DateTime now,
            ILogger? logger)
        {
            if (config?.KnownDevices == null) return false;

            // Trimmed at the door. The id is compared against whitelists with an ordinal
            // comparison in several places, so a stray space is a different device — and the
            // record written here is what those comparisons are made against.
            deviceId = deviceId?.Trim();
            deviceName = deviceName?.Trim();
            client = client?.Trim();
            if (string.IsNullOrEmpty(deviceId)) return false;

            var existing = config.KnownDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

            var save = false;

            if (existing != null)
            {
                existing.LastSeen = now;

                // A name only ever improves. A client that sends nothing must not replace a
                // good name with a blank — which is how rows ended up reading "Unknown
                // Device" despite having been named at some point — and a name an
                // administrator typed outranks whatever the client reports, or the rename
                // would last until this device's next request.
                if (!existing.NameIsCustom && !ProfilesBaseController.IsPlaceholderDeviceName(deviceName))
                {
                    existing.DeviceName = deviceName!;
                }

                if (!string.IsNullOrWhiteSpace(client)) existing.Client = client;

                // LastSeen was in-memory only, to keep a full PluginConfiguration.xml rewrite
                // off every request. The cost was that it never survived a restart: the
                // device list came back ordered by whenever each record was first written,
                // and "last seen" showed a date from before the restart — so the column an
                // administrator uses to decide what to revoke was reliably wrong after every
                // server update.
                //
                // Written at most once an hour per device instead. That is far finer than the
                // "unseen for 180 days" question the value is actually used to answer, and it
                // is one write an hour rather than one a request.
                //
                // Throttled against when it was last *persisted*, not last seen: LastSeen is
                // bumped in memory on every request, so comparing against it would never
                // reach an hour on a device that is in regular use — which is every device
                // this matters for.
                var lastWrite = ProfilesBaseController.DevicePersistedAt.TryGetValue(existing.DeviceId, out var at)
                    ? at
                    : DateTime.MinValue;
                if (now - lastWrite >= ProfilesBaseController.DeviceLastSeenWriteInterval)
                {
                    ProfilesBaseController.DevicePersistedAt[existing.DeviceId] = now;
                    save = true;
                }

                // Claim ownership for records written before MasterUserId existed. This is
                // the one case worth persisting immediately, so it happens exactly once.
                if (existing.MasterUserId == Guid.Empty && ownerId != Guid.Empty)
                {
                    existing.MasterUserId = ownerId;
                    save = true;
                }
            }
            else
            {
                // First time we've seen this device — persist it.
                config.KnownDevices.Add(new KnownDevice
                {
                    DeviceId = deviceId,
                    // Left blank rather than stamped "Unknown Device". The picker fills a
                    // blank in from the client name and, failing that, from the device id, so
                    // a nameless device still reads as something an administrator can tell
                    // apart — see DisambiguateDeviceNames. Storing the placeholder made every
                    // nameless device render as the same row.
                    DeviceName = ProfilesBaseController.IsPlaceholderDeviceName(deviceName)
                        ? string.Empty
                        : deviceName!,
                    Client = string.IsNullOrWhiteSpace(client) ? string.Empty : client,
                    LastSeen = now,
                    MasterUserId = ownerId
                });
                ProfilesBaseController.DevicePersistedAt[deviceId] = now;
                save = true;
            }

            // Only while we are writing anyway, and at most once a day. KnownDevices is a
            // single server-wide list that only ever grew: every phone that ever hit the
            // server stayed in it forever, and the device picker is a list an administrator
            // has to read.
            if (save) save |= ProfilesBaseController.PruneStaleDevices(config, now, logger);

            return save;
        }

        /// <summary>
        /// <see cref="Record"/>, with the lock and the save around it.
        /// <para>
        /// The configuration is read <b>inside</b> the lock deliberately. Jellyfin replaces
        /// <c>Plugin.Configuration</c> wholesale whenever an administrator saves the plugin's
        /// settings, so a reference taken before the lock can be an orphan by the time the
        /// lock is held, and the write lands on an object nothing will ever save. Twenty-four
        /// older sites still read it early (P2-25); this is the shape they are moving to.
        /// </para>
        /// </summary>
        public static void RecordAndSave(
            string? deviceId,
            string? deviceName,
            string? client,
            Guid ownerId,
            ILogger? logger)
        {
            lock (ProfilesBaseController.ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                if (Record(config, deviceId, deviceName, client, ownerId, DateTime.UtcNow, logger))
                {
                    Plugin.Instance?.SaveConfiguration();
                }
            }
        }

        /// <summary>
        /// Whether Bonfire runs this account's household at all — that is, whether it owns
        /// sub-profiles or is one.
        /// <para>
        /// The filter for the sign-in listener, and it has to be asked separately from
        /// <see cref="HouseholdOf"/>, which answers "whose household is this account in"
        /// with the account itself when it knows of no mapping. That is the right answer to
        /// that question and a useless one to filter on, because it is never
        /// <see cref="Guid.Empty"/>: keying on it would record a device for every account on
        /// the server, and on a forty-user server that is every phone and browser writing a
        /// row into an XML file that gets rewritten whole.
        /// </para>
        /// </summary>
        public static bool IsHousehold(PluginConfiguration? config, Guid userId)
        {
            if (config?.Mappings == null || userId == Guid.Empty) return false;

            return config.Mappings.Any(m => m.MasterUserId == userId || m.ProfileUserId == userId);
        }

        /// <summary>
        /// The household an account belongs to: its master if it is a sub-profile, itself
        /// otherwise.
        /// <para>
        /// A device must be attributed to the <i>household</i>, never to whoever happened to
        /// sign in. Attributing it to a sub-profile would mean the second person to use a
        /// television took it over, and the master's own profiles would stop being offered on
        /// it.
        /// </para>
        /// </summary>
        public static Guid HouseholdOf(PluginConfiguration? config, Guid userId)
        {
            if (config?.Mappings == null || userId == Guid.Empty) return userId;

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            return mapping != null ? mapping.MasterUserId : userId;
        }
    }
}
