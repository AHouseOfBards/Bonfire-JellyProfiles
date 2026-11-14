using System;
using System.Collections.Concurrent;
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
    /// <para>Jellyfin's <c>IDeviceManager</c> cannot answer "who uses this television", because a
    /// <c>Device</c> row belongs to a session, not a device. <c>SessionManager.Logout</c> deletes it,
    /// and Android TV's "Switch account" destroys the session before it opens the picker. So the
    /// action that opens the picker deletes the evidence the picker needs. 1.6.1.4 through 1.6.1.6
    /// were built on <c>IDeviceManager</c> and could not work on that client.</para>
    ///
    /// <para>This is not a new store. <see cref="PluginConfiguration.KnownDevices"/> already held
    /// a device id, an owning master and a last-seen stamp. What it lacked was a writer a television
    /// reaches, since both call sites were authenticated Bonfire routes that only the web switcher
    /// calls. <see cref="BonfireSessionListener"/> is that writer.</para>
    ///
    /// <para>Not a security boundary. A device id is client-supplied. Forging one reveals a
    /// household's profile names and lets the forger try that household's PINs from there. Entry still
    /// needs the PIN, which is rate-limited, and library and rating enforcement is server-side.</para>
    /// </summary>
    public static class DeviceRegistry
    {
        /// <summary>
        /// The master account a device belongs to, or <see cref="Guid.Empty"/> if we have no usable
        /// record of it. Records written before <c>MasterUserId</c> existed carry <c>Guid.Empty</c> and
        /// identify nobody; returning them would give the first household to ask a device that might be
        /// another's.
        /// </summary>
        public static Guid FindMaster(PluginConfiguration? config, string? deviceId)
        {
            if (config?.KnownDevices == null || string.IsNullOrWhiteSpace(deviceId))
            {
                return Guid.Empty;
            }

            // Trimmed on both sides: an id with a stray space is a different string to every ordinal
            // comparison in the plugin.
            var wanted = deviceId.Trim();

            var exact = Lookup(config, wanted);
            if (exact != Guid.Empty) return exact;

            // One television, two device ids. This made 1.6.1.7 useless. Some clients derive a per-user
            // device id once somebody is signed in and send the plain one when nobody is, which is the
            // sign-in screen we are answering.
            //
            // Jellyfin Android TV (SessionRepository.setCurrentSession) uses
            // id = SHA-1("${id}+$user") as lowercase hex. A production log from 2026-09-10 confirms it:
            // sha1("9a6dae35cc29c74f" + "+" + "8615867e-3617-4ad3-8d62-639b3bdc1305") was the id the session
            // carried, while the picker asked about "9a6dae35cc29c74f". Jellyfin for Android appends the
            // user id with no separator.
            //
            // A hash cannot be reversed, so the derivation is applied forwards: for every account Bonfire
            // knows, work out what this device's id would be had that account signed in, and look for that.
            // A sign-in screen is opened by hand, so this is not on a hot path.
            //
            // Every derivation is tried and the most recently seen record wins. It used to return the first
            // match in Mappings order, so a television two households had used showed the older household's
            // profiles.
            KnownDevice? best = null;
            void Consider(string id)
            {
                var row = LookupRow(config, id);
                if (row == null || row.MasterUserId == Guid.Empty) return;
                if (best == null || row.LastSeen > best.LastSeen) best = row;
            }

            foreach (var userId in HouseholdMembers(config))
            {
                var suffix = userId.ToString();

                // Jellyfin for Android: id + userId, no separator.
                Consider(wanted + suffix);

                // Jellyfin Android TV: sha1(id + "+" + userId), lowercase hex.
                Consider(Sha1Hex(wanted + "+" + suffix));
            }

            // Jellyfin Android TV signs in through sha1(id + "+" + the name typed), and the name typed is
            // the one the sign-in screen showed: the household's name for the profile. The session that
            // starts is recorded under that id, so a television whose later account-derived session was
            // never recorded is still placed by it.
            foreach (var name in HouseholdNames(config))
            {
                Consider(Sha1Hex(wanted + "+" + name));
            }

            return best?.MasterUserId ?? Guid.Empty;
        }

        /// <summary>
        /// The recorded device a sign-in came from, when the client built its id from the name being
        /// signed in as. Otherwise the id as sent.
        ///
        /// <para>Jellyfin Android TV signs in by name through a one-off id,
        /// <c>defaultDeviceInfo.forUser(username)</c>, so choosing "Family" on a television whose id is
        /// <c>9a6dae35cc29c74f</c> signs in as <c>sha1("9a6dae35cc29c74f+Family")</c>. That id is not
        /// on record when it is checked (the session it starts is recorded afterwards), and it comes from
        /// a name, so neither <see cref="FindMaster"/> nor <see cref="IsSameDevice"/> could place it.
        /// 1.6.3.3 refused every profile chosen from an Android TV sign-in screen. Taken from a production
        /// log (2026-10-02).</para>
        ///
        /// <para>Applied backwards from the records: for each device a household has signed in on, what
        /// would its id be had this name been typed on it? One hash per recorded device, on a sign-in
        /// only. Only a 40-character hex id can be a SHA-1, so anything else is returned without hashing.</para>
        /// </summary>
        public static string? ResolveSignInDevice(PluginConfiguration? config, string? deviceId, string? username)
        {
            var sent = deviceId?.Trim();
            if (config?.KnownDevices == null || string.IsNullOrEmpty(sent) || string.IsNullOrEmpty(username)) return sent;

            // Already an id we hold: nothing to resolve.
            if (Lookup(config, sent) != Guid.Empty) return sent;

            if (sent.Length != 40 || !sent.All(Uri.IsHexDigit)) return sent;

            // The plain id is the one thing that would let us check this hash, and it is not recorded:
            // the session this sign-in starts is recorded under the hash, but only after the sign-in
            // succeeds. Android TV sends the plain id from its sign-in screen only. 1.6.3.4 searched the
            // records alone and could never succeed on a real set. So the sign-in screen's own request is
            // remembered (NoteSignInScreen) and tried first, the records second.
            var candidates = RecentSignInScreens()
                // Aliases too: MergeSameDevices can fold the set's own id into a derived id, and then the
                // alias is the only place the plain id is kept.
                .Concat(config.KnownDevices.ToList()
                    .Where(r => r.MasterUserId != Guid.Empty)
                    .SelectMany(r => new[] { r.DeviceId }.Concat(r.Aliases ?? new List<string>()))
                    .Select(id => id?.Trim()))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                // The name exactly as sent: the client hashed the string it was about to send.
                if (string.Equals(Sha1Hex(candidate + "+" + username), sent, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return sent;
        }

        // Sign-in screens recently seen

        /// <summary>
        /// Plain device ids whose sign-in screen was recently answered with a household's profiles, and
        /// when. In memory only: evidence for the sign-in that follows the list, not a record. Bounded,
        /// and old entries lapse.
        /// </summary>
        private static readonly ConcurrentDictionary<string, DateTime> _signInScreens =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long a sign-in screen is remembered. A set left on the PIN field overnight
        /// asks for the list again when it wakes.</summary>
        internal static readonly TimeSpan SignInScreenMemory = TimeSpan.FromHours(12);

        internal const int MaxSignInScreens = 512;

        /// <summary>
        /// Notes that this device's sign-in screen was offered a household's profiles. Called by
        /// <see cref="PublicUserInjector.ResolveHousehold"/> once it has placed the device, never for one
        /// it could not place, so a stranger's request adds nothing a sign-in could use.
        /// </summary>
        public static void NoteSignInScreen(string? deviceId)
        {
            var id = deviceId?.Trim();
            if (string.IsNullOrEmpty(id)) return;

            var now = DateTime.UtcNow;
            _signInScreens[id] = now;

            if (_signInScreens.Count > MaxSignInScreens)
            {
                foreach (var stale in _signInScreens
                    .OrderBy(kv => kv.Value)
                    .Take(_signInScreens.Count - MaxSignInScreens)
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    _signInScreens.TryRemove(stale, out _);
                }
            }
        }

        private static IEnumerable<string> RecentSignInScreens()
        {
            var cutoff = DateTime.UtcNow - SignInScreenMemory;
            return _signInScreens
                .Where(kv => kv.Value >= cutoff)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>For harnesses: forget every sign-in screen.</summary>
        internal static void ForgetSignInScreens() => _signInScreens.Clear();

        /// <summary>
        /// Whether two device ids are one television, by the three derivations the supported clients
        /// apply to their own id once somebody is signed in.
        ///
        /// <para>A television sends its bare id from its sign-in screen and a derived one afterwards, and
        /// Jellyfin starts a session under each, so one television is two rows in the device list. A
        /// profile restricted to "the living-room TV" by ticking the signed-in row was hidden from that
        /// television's sign-in screen, which asks with the bare id, because the check compared ids
        /// exactly.</para>
        ///
        /// <para>Applied in both directions, over <paramref name="householdMembers"/> only: the
        /// derivation is over whoever is signed in, which for a restricted profile's television is
        /// somebody in that profile's household.</para>
        ///
        /// <para>Roku appends a name that is not in the configuration in every form Roku uses (see
        /// <see cref="BareDeviceIdFor"/>). A Roku pair is accepted only when both ids are on record for
        /// the same household (the session listener records the bare id beside the appended one for this)
        /// and one is the other plus a suffix Roku could have written.</para>
        /// </summary>
        public static bool IsSameDevice(
            string? a,
            string? b,
            IEnumerable<Guid>? householdMembers,
            IEnumerable<KnownDevice>? knownDevices,
            IEnumerable<string>? memberNames = null)
        {
            var x = a?.Trim();
            var y = b?.Trim();
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return false;
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var userId in householdMembers ?? Enumerable.Empty<Guid>())
            {
                if (userId == Guid.Empty) continue;
                var suffix = userId.ToString();

                if (DerivedFrom(x, y, suffix) || DerivedFrom(y, x, suffix)) return true;
            }

            // The id Android TV signs in with: sha1(bare + "+" + the name typed). The session that
            // sign-in starts is recorded under it, so an administrator can tick it, and the sign-in screen,
            // which asks with the bare id, was then filtered out of its own profile.
            foreach (var name in memberNames ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (string.Equals(y, Sha1Hex(x + "+" + name), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x, Sha1Hex(y + "+" + name), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return IsRokuPair(x, y, knownDevices) || IsRokuPair(y, x, knownDevices);
        }

        /// <summary>
        /// Whether <paramref name="other"/> is an id the device that sent <paramref name="sent"/> has
        /// used, under any account Bonfire knows (issue #41).
        ///
        /// <para><see cref="IsSameDevice"/> relates a bare id to its derivations. This also relates two
        /// derivations of one bare id: Jellyfin for Android sends <c>base + mainId</c> from the picker and
        /// <c>base + profileId</c> once the page reloads into the profile, and neither comes from the
        /// other. So the bare id is recovered first by stripping a known account id from the end, and
        /// every derivation is compared against that. A SHA-1 id (Android TV) cannot be stripped but
        /// still matches its own bare id.</para>
        /// </summary>
        public static bool IsSamePhysicalDevice(string? sent, string? other, PluginConfiguration? config)
        {
            var x = sent?.Trim();
            var y = other?.Trim();
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return false;
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
            if (config == null) return false;

            var members = HouseholdMembers(config).ToList();
            var names = HouseholdNames(config).ToList();
            var bares = new List<string> { x };
            foreach (var userId in members)
            {
                var suffix = userId.ToString();
                if (x.Length - suffix.Length >= MinimumBareDeviceIdLength
                    && x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    bares.Add(x.Substring(0, x.Length - suffix.Length));
                }
            }

            return bares.Any(b => IsSameDevice(b, y, members, config.KnownDevices, names));
        }

        /// <summary>
        /// True when <paramref name="derived"/> is what <paramref name="bare"/> becomes once the account
        /// <paramref name="userSuffix"/> signs in. Jellyfin for Android appends the id, Jellyfin Android
        /// TV hashes <c>bare + "+" + id</c>.
        /// </summary>
        private static bool DerivedFrom(string bare, string derived, string userSuffix)
            => string.Equals(derived, bare + userSuffix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(derived, Sha1Hex(bare + "+" + userSuffix), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when <paramref name="appended"/> is <paramref name="bare"/> with a Roku-style name
        /// appended, and both are recorded for the same household.
        /// </summary>
        private static bool IsRokuPair(string bare, string appended, IEnumerable<KnownDevice>? knownDevices)
        {
            if (knownDevices == null) return false;
            if (bare.Length < MinimumBareDeviceIdLength) return false;
            if (appended.Length < bare.Length + 3) return false;
            if (!appended.StartsWith(bare, StringComparison.Ordinal)) return false;

            var suffix = appended.Substring(bare.Length);
            if (RokuFriendlyName(suffix) != suffix) return false;

            var rows = knownDevices.ToList();
            var owner = rows.FirstOrDefault(d => string.Equals(d.DeviceId?.Trim(), bare, StringComparison.OrdinalIgnoreCase));
            var other = rows.FirstOrDefault(d => string.Equals(d.DeviceId?.Trim(), appended, StringComparison.OrdinalIgnoreCase));
            if (owner == null || other == null) return false;

            // A household that owns both, not "the same last household": on a Roku two households
            // share, the two rows can be last seen by different ones (R-1050).
            return AddOwners(owner.Owners, owner.MasterUserId).Any(other.OwnedBy);
        }

        /// <summary>
        /// The owning master of the record with this exact device id, or
        /// <see cref="Guid.Empty"/> when there is none or it names nobody.
        /// </summary>
        private static Guid Lookup(PluginConfiguration config, string deviceId)
            => LookupRow(config, deviceId)?.MasterUserId ?? Guid.Empty;

        /// <summary>A new owners list: the old one plus these, without Guid.Empty or repeats.</summary>
        internal static List<Guid> AddOwners(List<Guid>? owners, params Guid[] add)
        {
            var result = owners?.Where(g => g != Guid.Empty).ToList() ?? new List<Guid>();
            foreach (var g in add)
                if (g != Guid.Empty && !result.Contains(g)) result.Add(g);
            return result;
        }

        /// <summary>
        /// True if <paramref name="householdId"/> has signed in on this device, under any of the ids
        /// <see cref="FindMaster"/> knows it by (R-1050). FindMaster answers "whose profiles does this
        /// sign-in screen show", which is one household. This answers "may this household's PIN be typed
        /// here", which on a shared device is several.
        /// </summary>
        public static bool IsOwnedBy(PluginConfiguration? config, string? deviceId, Guid householdId)
        {
            if (householdId == Guid.Empty || config?.KnownDevices == null || string.IsNullOrWhiteSpace(deviceId)) return false;
            var wanted = deviceId.Trim();
            if (LookupRow(config, wanted) is KnownDevice exact && exact.OwnedBy(householdId)) return true;

            // The same derivations as FindMaster.
            foreach (var userId in HouseholdMembers(config))
            {
                var suffix = userId.ToString();
                if (LookupRow(config, wanted + suffix)?.OwnedBy(householdId) == true) return true;
                if (LookupRow(config, Sha1Hex(wanted + "+" + suffix))?.OwnedBy(householdId) == true) return true;
            }
            foreach (var name in HouseholdNames(config))
            {
                if (LookupRow(config, Sha1Hex(wanted + "+" + name))?.OwnedBy(householdId) == true) return true;
            }
            return false;
        }

        private static KnownDevice? LookupRow(PluginConfiguration config, string deviceId)
            => Row(config.KnownDevices, deviceId);

        /// <summary>The record for this id: its own, or the one it was merged into (R-1049).</summary>
        internal static KnownDevice? Row(IEnumerable<KnownDevice>? devices, string? deviceId)
        {
            var wanted = deviceId?.Trim();
            if (devices == null || string.IsNullOrEmpty(wanted)) return null;
            var list = devices as IList<KnownDevice> ?? devices.ToList();
            return list.FirstOrDefault(d => string.Equals(d.DeviceId?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(d => d.Answers(wanted));
        }

        /// <summary>
        /// The names households gave their profiles. An Android TV sign-in screen shows them and hashes
        /// them into the device id it signs in with.
        /// </summary>
        internal static IEnumerable<string> HouseholdNames(PluginConfiguration config)
        {
            if (config.Mappings == null) return Enumerable.Empty<string>();

            return config.Mappings
                .Where(m => m.ProfileUserId != m.MasterUserId && !string.IsNullOrWhiteSpace(m.ProfileName))
                .Select(m => m.ProfileName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Every account in any household Bonfire runs, masters and sub-profiles both. Sub-profiles are
        /// included because after the first switch a television is signed in as a profile, so the per-user
        /// id is derived over the profile's guid while the record still belongs to the household.
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
        /// The shortest device id we will store as a derivation of another. Roku's id is a 36-character
        /// channel client id, so this rejects nothing real. It stops an account with a short name from
        /// stripping a device id down to a stub another client might send.
        /// </summary>
        internal const int MinimumBareDeviceIdLength = 8;

        /// <summary>
        /// The id this client would have sent before anyone signed in, when the id it sends now was built
        /// by appending the account's name. Null when it was not built that way.
        ///
        /// <para>Jellyfin Roku appends the signed-in account's name to its device id in
        /// <c>session.user.SetServerDeviceName()</c>, with everything outside <c>[^a-zA-Z0-9\-\_]</c>
        /// removed from the name. So a Roku sends the bare id at its sign-in screen and the appended one
        /// once somebody is in, the same split that made 1.6.1.7 useless on Android TV.</para>
        ///
        /// <para>This cannot be derived forwards as Android TV is, because the suffix is a name, not a
        /// Guid, and not always a name we hold. Roku's <c>AboutMe()</c> asks <c>/Users/{id}</c>, which
        /// Bonfire does not rewrite, so after a launch that restores a stored token the suffix is the
        /// real username, while after a fresh PIN login it is the household's name for the profile.</para>
        ///
        /// <para>So it is reversed when recording, where <c>SessionInfo.UserName</c> says exactly which
        /// name the client used. Concatenation is reversible when the suffix is known.</para>
        ///
        /// <para>Ordinal, not case-insensitive: Roku appends the name verbatim after stripping, so a
        /// case-folded comparison could only strip the wrong thing.</para>
        /// </summary>
        public static string? BareDeviceIdFor(string? deviceId, string? accountName)
        {
            var id = deviceId?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(accountName)) return null;

            var friendly = RokuFriendlyName(accountName);

            // A very short suffix is a coincidence waiting to happen: "c74f" is a plausible account name
            // and also the tail of a real ANDROID_ID.
            if (friendly.Length < 3) return null;

            if (!id.EndsWith(friendly, StringComparison.Ordinal)) return null;

            var bare = id.Substring(0, id.Length - friendly.Length);
            return bare.Length < MinimumBareDeviceIdLength ? null : bare;
        }

        /// <summary>
        /// An account name as Roku writes it into a device id: everything outside <c>[a-zA-Z0-9\-\_]</c>
        /// removed, matching its <c>CreateObject("roRegex", "[^a-zA-Z0-9\-\_]", "")</c>.
        /// </summary>
        internal static string RokuFriendlyName(string name)
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
        /// Lowercase hex SHA-1, matching Kotlin's <c>digest().joinToString("") { "%02x".format(it) }</c>.
        /// Not a security use: it re-derives an identifier another program chose.
        /// </summary>
        private static string Sha1Hex(string value)
        {
#pragma warning disable CA5350 // matching a client's own identifier, not protecting anything
            var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
#pragma warning restore CA5350
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>Longest device id recorded; a real client id is far shorter.</summary>
        public const int MaxDeviceIdLength = 128;

        /// <summary>Most device rows one household may own.</summary>
        public const int MaxDevicesPerHousehold = 200;

        /// <summary>Most device rows on the whole server.</summary>
        public const int MaxKnownDevices = 2000;

        /// <summary>
        /// Notes that <paramref name="ownerId"/>'s household was seen on this device. Returns true when
        /// something changed that is worth writing to disk.
        /// <para><b>Caller must hold <see cref="ProfilesBaseController.ConfigLock"/>.</b> Separate from
        /// the saving so a harness can drive it without a plugin instance; see
        /// <see cref="RecordAndSave"/>.</para>
        /// </summary>
        public static bool Record(
            PluginConfiguration? config,
            string? deviceId,
            string? deviceName,
            string? client,
            Guid ownerId,
            DateTime now,
            ILogger? logger)
            => RecordSighting(config, deviceId, deviceName, client, ownerId, now, logger, mayGrant: true);

        /// <summary>
        /// <see cref="Record"/>, saying whether this sighting may make the device known.
        /// <para>
        /// False for a session a PIN, an empty box or a switch started, and for an authenticated Bonfire
        /// call. Those refresh a record that exists (last seen, name) and never make one or add a
        /// household to one. A PIN works only on a device somebody signed in on with the password, and a
        /// record made by a PIN sign-in would be a device the PIN had vouched for itself.
        /// </para>
        /// </summary>
        public static bool RecordSighting(
            PluginConfiguration? config,
            string? deviceId,
            string? deviceName,
            string? client,
            Guid ownerId,
            DateTime now,
            ILogger? logger,
            bool mayGrant)
        {
            if (config?.KnownDevices == null) return false;

            // Trimmed at the door: ids are compared ordinally against whitelists, so a stray space is a
            // different device.
            deviceId = deviceId?.Trim();
            deviceName = deviceName?.Trim();
            client = client?.Trim();
            if (string.IsNullOrEmpty(deviceId) || deviceId.Length > MaxDeviceIdLength) return false;

            // An id merged into another record updates that record (R-1049). It used to make a fresh
            // one that no device list named.
            var existing = Row(config.KnownDevices, deviceId);

            var save = false;

            if (existing != null)
            {
                existing.LastSeen = now;

                // A name only improves. A client that sends nothing must not blank a good name (rows read
                // "Unknown Device"), and an administrator's typed name outranks the client's, or the rename
                // would last until the next request.
                if (!existing.NameIsCustom && !ProfilesBaseController.IsPlaceholderDeviceName(deviceName))
                {
                    existing.DeviceName = deviceName!;
                }

                if (!string.IsNullOrWhiteSpace(client)) existing.Client = client;

                // LastSeen used to be in memory only, to keep a PluginConfiguration.xml rewrite off every
                // request. It never survived a restart, so the device list was ordered by first write and "last
                // seen" was wrong after every server update.
                //
                // Now written at most once an hour. That is far finer than the "unseen for 180 days" question it
                // answers.
                //
                // Throttled against when LastSeen was last persisted, not last seen: LastSeen is bumped in
                // memory on every request, so comparing against it would never reach an hour on a device in
                // regular use.
                //
                // One stamp for all devices: a save writes the whole file, so every device's LastSeen goes out
                // with it. A stamp per device meant twenty rewrites an hour (SD-card wear on a Pi) and a
                // dictionary that never shrank. A device seen only between two saves is written with the next.
                if (now - ProfilesBaseController.LastSeenPersistedAt >= ProfilesBaseController.DeviceLastSeenWriteInterval)
                {
                    ProfilesBaseController.LastSeenPersistedAt = now;
                    save = true;
                }

                // Claim ownership for records written before MasterUserId existed. Persisted at once, so it
                // happens exactly once.
                if (!mayGrant)
                {
                    // Refresh only: whose device it is stays as it was.
                }
                else if (existing.MasterUserId == Guid.Empty && ownerId != Guid.Empty)
                {
                    existing.MasterUserId = ownerId;
                    save = true;
                }

                // Another household on the same device (R-1050). The first owner used to keep it for good.
                // Now both own it, and the sign-in screen shows whoever was here last. Saved only when the
                // household changes.
                else if (ownerId != Guid.Empty && existing.MasterUserId != ownerId)
                {
                    existing.Owners = AddOwners(existing.Owners, existing.MasterUserId, ownerId);
                    existing.MasterUserId = ownerId;
                    save = true;
                }
            }
            else if (!mayGrant)
            {
                return false;
            }
            else
            {
                // A new row is bounded per household and overall, so a signed-in account cannot grow a file
                // that is rewritten whole on every save.
                //
                // A household at its cap gives up its oldest row nobody depends on rather than refusing the new
                // device (R2-02-02). Otherwise one that fills its 200 rows with ids it minted would never record
                // a real device again, silently.
                if (ownerId != Guid.Empty && config.KnownDevices.Count(d => d.OwnedBy(ownerId)) >= MaxDevicesPerHousehold)
                {
                    var allowed = (config.Mappings ?? new List<ProfileMapping>())
                        .SelectMany(m => m.AllowedDeviceIds ?? new List<string>())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToList();
                    var oldest = config.KnownDevices
                        .Where(d => d.MasterUserId == ownerId
                                    && (d.Owners ?? new List<Guid>()).All(o => o == ownerId)
                                    && !allowed.Any(id => d.Answers(id)))
                        .OrderBy(d => d.LastSeen)
                        .FirstOrDefault();
                    if (oldest == null)
                    {
                        logger?.LogWarning(
                            "ProfilesPlugin: a household is at its limit of {Max} devices and every one is on a device list, so {DeviceId} was not recorded.",
                            MaxDevicesPerHousehold, deviceId);
                        return false;
                    }

                    config.KnownDevices.Remove(oldest);
                }

                if (config.KnownDevices.Count >= MaxKnownDevices)
                {
                    logger?.LogWarning(
                        "ProfilesPlugin: the server is at its limit of {Max} known devices, so {DeviceId} was not recorded.",
                        MaxKnownDevices, deviceId);
                    return false;
                }

                // First time we've seen this device — persist it.
                config.KnownDevices.Add(new KnownDevice
                {
                    DeviceId = deviceId,
                    // Left blank, not "Unknown Device". The picker fills a blank from the client name or the
                    // device id (see DisambiguateDeviceNames). Storing the placeholder made every nameless device
                    // render as the same row.
                    DeviceName = ProfilesBaseController.IsPlaceholderDeviceName(deviceName)
                        ? string.Empty
                        : deviceName!,
                    Client = string.IsNullOrWhiteSpace(client) ? string.Empty : client,
                    LastSeen = now,
                    FirstSeen = now,
                    MasterUserId = ownerId,
                    Owners = AddOwners(null, ownerId),
                });
                // Saved now, and every other device's LastSeen goes out with it.
                ProfilesBaseController.LastSeenPersistedAt = now;
                save = true;
            }

            // Only while writing anyway, and at most once a day. KnownDevices is one server-wide list
            // that only grew, so every phone that ever hit the server stayed in it and the device picker
            // became hard to read.
            if (save) save |= ProfilesBaseController.PruneStaleDevices(config, now, logger);

            // Riding on a write already happening (a new id, or the hourly LastSeen save) lets a
            // television with a new id tidy itself without anyone opening the device list. The list read does
            // the same for households that are not seen.
            if (save && ownerId != Guid.Empty) MergeSameDevices(config, ownerId, now);

            return save;
        }

        /// <summary>
        /// How long a new id must be in use, with the old one silent, before the two are taken for one
        /// device. A fixed limit: a shorter wait merges two tablets that are both used but not every
        /// week, which widens a device restriction.
        /// </summary>
        internal static readonly TimeSpan SameDeviceQuietPeriod = TimeSpan.FromDays(7);

        /// <summary>
        /// Folds this household's records that are one device into one, and returns how many records
        /// went. Caller must hold <see cref="ProfilesBaseController.ConfigLock"/>. Idempotent.
        ///
        /// <para>Only records with the same client app and reported name, owned by this household, are
        /// compared, and then one of two things must hold.</para>
        ///
        /// <para><b>The ids are derived from one another.</b> Jellyfin for Android sends its own id at
        /// the sign-in screen and <c>id + accountId</c> once signed in; Android TV sends
        /// <c>sha1(id + "+" + account)</c> per account (SessionRepository.setCurrentSession,
        /// util/sdk/DeviceInfoExtensions.kt). One television used by four profiles is four ids in use side
        /// by side, and timing cannot join them. The derivation can, given the set's own id: from a
        /// record, from stripping an account id off the end, or from a sign-in screen seen in the last few
        /// hours. This is proof, not a guess.</para>
        ///
        /// <para><b>The old id fell silent when the new one appeared</b> (owner decision, 2026-11-12): a
        /// reinstall, a reset, cleared site data. The older record's last sighting is no later than the
        /// newer one's first, and the newer one has been around for <see cref="SameDeviceQuietPeriod"/>.
        /// Records used in overlapping weeks, such as a second "Family Room TV" at another home, never
        /// merge. Neither does a row on any profile's allow list (aliases included) by this rule, because
        /// a wrong guess would hand its permission to another device. Not by address: addresses change
        /// and proxies hide them.</para>
        ///
        /// <para>Merged into the newer record by
        /// <see cref="ProfilesBaseController.MergeDeviceRecords"/>: add before remove, so no whitelist
        /// passes through empty, and the old id stays as an alias. Two browser windows on two origins are
        /// not derived, so they stay apart; Tidy up duplicates joins those.</para>
        /// </summary>
        internal static int MergeSameDevices(PluginConfiguration? config, Guid householdId, DateTime now)
        {
            if (config?.KnownDevices == null || householdId == Guid.Empty) return 0;

            // The cheap test first (R2-02-03). This runs under ConfigLock on a write, and what follows
            // hashes every row against every recent sign-in screen. If no two rows share a name and app, no
            // pair can be one device.
            var sameLabel = config.KnownDevices
                .Where(d => d.OwnedBy(householdId) && !string.IsNullOrWhiteSpace(d.DeviceId)
                            && !ProfilesBaseController.IsPlaceholderDeviceName(d.DeviceName)
                            && !string.IsNullOrWhiteSpace(d.Client))
                .GroupBy(d => d.DeviceName.Trim().ToLowerInvariant() + "\0" + d.Client.Trim().ToLowerInvariant())
                .Any(g => g.Count() > 1);
            if (!sameLabel) return 0;

            var members = HouseholdMembers(config).ToList();
            var names = HouseholdNames(config).ToList();
            var screens = RecentSignInScreens();
            var merged = 0;

            // One merge at a time, then look again: each changes the records the next compares.
            for (var guard = 0; guard < MaxDevicesPerHousehold; guard++)
            {
                var pair = FindSameDevicePair(config, householdId, members, names, screens, now);
                if (pair == null) break;
                if (!ProfilesBaseController.MergeDeviceRecordsCore(config, pair.Value.From, pair.Value.Into, pair.Value.Proven)) break;
                merged++;
            }

            return merged;
        }

        private static (string From, string Into, bool Proven)? FindSameDevicePair(
            PluginConfiguration config, Guid householdId, List<Guid> members, List<string> names,
            IEnumerable<string> screens, DateTime now)
        {
            var groups = config.KnownDevices
                .Where(d => d.OwnedBy(householdId) && !string.IsNullOrWhiteSpace(d.DeviceId)
                            && !ProfilesBaseController.IsPlaceholderDeviceName(d.DeviceName)
                            && !string.IsNullOrWhiteSpace(d.Client))
                .GroupBy(d => d.DeviceName.Trim().ToLowerInvariant() + "\0" + d.Client.Trim().ToLowerInvariant())
                .Where(g => g.Count() > 1);

            // Every id any profile is restricted to; a row is on it by its id or an alias.
            var allowed = (config.Mappings ?? new List<ProfileMapping>())
                .SelectMany(m => m.AllowedDeviceIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            bool Allowed(KnownDevice d) => allowed.Any(id => d.Answers(id));

            foreach (var g in groups)
            {
                var rows = g.OrderByDescending(d => d.LastSeen).ToList();
                for (var i = 0; i < rows.Count; i++)
                {
                    for (var j = 0; j < rows.Count; j++)
                    {
                        if (i == j) continue;
                        var older = rows[i];
                        var newer = rows[j];

                        // Proven: into the most recently seen, the id in use now.
                        if (i > j && Derived(older, newer, members, names, screens, config.KnownDevices))
                            return (older.DeviceId, newer.DeviceId, true);

                        // Never by time when either row is on an allow list (owner decision, 2026-11-12): a guess
                        // would hand the allowed device's permission to the other. Only proof may merge it.
                        if (Allowed(older) || Allowed(newer)) continue;

                        if (newer.FirstSeen != DateTime.MinValue
                            && older.LastSeen <= newer.FirstSeen
                            && now - newer.FirstSeen >= SameDeviceQuietPeriod)
                        {
                            // A guess, so no alias is left (R2-02-01): the old id would be honoured by every allow
                            // check once the newer row is allowed, and the older device may be a different one.
                            return (older.DeviceId, newer.DeviceId, false);
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>Whether two records are one device's ids by a client's own derivation.</summary>
        private static bool Derived(
            KnownDevice a, KnownDevice b, List<Guid> members, List<string> names,
            IEnumerable<string> screens, IEnumerable<KnownDevice> known)
        {
            var aIds = new[] { a.DeviceId }.Concat(a.Aliases ?? new List<string>()).ToList();
            var bIds = new[] { b.DeviceId }.Concat(b.Aliases ?? new List<string>()).ToList();

            // The set's own id, wherever it can be had.
            var bares = new List<string>(screens);
            foreach (var id in aIds.Concat(bIds))
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                bares.Add(id.Trim());
                foreach (var m in members)
                {
                    var suffix = m.ToString();
                    if (id.Length - suffix.Length >= MinimumBareDeviceIdLength
                        && id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        bares.Add(id.Substring(0, id.Length - suffix.Length).Trim());
                    }
                }
            }

            foreach (var bare in bares.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (aIds.Any(x => IsSameDevice(bare, x, members, known, names))
                    && bIds.Any(y => IsSameDevice(bare, y, members, known, names)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// <see cref="Record"/>, with the lock and the save around it.
        /// <para>
        /// The configuration is read inside the lock on purpose. Jellyfin's own save used to replace
        /// <c>Plugin.Configuration</c>, so a reference taken before the lock could be an orphan.
        /// Plugin.UpdateConfiguration now copies onto the instance in use (P2-25), but this shape does
        /// not depend on that staying in place.
        /// </para>
        /// </summary>
        public static void RecordAndSave(
            string? deviceId,
            string? deviceName,
            string? client,
            Guid ownerId,
            ILogger? logger)
            => RecordSightingAndSave(deviceId, deviceName, client, ownerId, logger, mayGrant: true);

        /// <summary><see cref="RecordAndSave"/>; see <see cref="RecordSighting"/> for <paramref name="mayGrant"/>.</summary>
        public static void RecordSightingAndSave(
            string? deviceId,
            string? deviceName,
            string? client,
            Guid ownerId,
            ILogger? logger,
            bool mayGrant)
        {
            var isNew = false;
            bool? judged = null;
            var looksLikeService = false;
            string? heldId = null;
            lock (ProfilesBaseController.ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                // New to this household, so a second household on a shared device is told too (R-1050).
                // Claiming a record from before owners existed is not news.
                var id = deviceId?.Trim();
                var row = string.IsNullOrEmpty(id) || config.KnownDevices == null ? null : Row(config.KnownDevices, id);
                isNew = mayGrant && !string.IsNullOrEmpty(id) && config.KnownDevices != null
                    && (row == null || (row.MasterUserId != Guid.Empty && !row.OwnedBy(ownerId)));

                if (RecordSighting(config, deviceId, deviceName, client, ownerId, DateTime.UtcNow, logger, mayGrant))
                {
                    Plugin.Instance?.SaveConfiguration();
                }

                if (isNew)
                {
                    // Keyed by the record's own id, which is what the judgement finds, even when the sighting
                    // arrived under an alias.
                    var recorded = Row(config.KnownDevices, id);
                    judged = recorded?.CanPlay;
                    heldId = recorded?.DeviceId ?? id;
                    looksLikeService = judged == null && IsLearnedService(config, deviceName, client);
                }
            }

            // Outside the lock: it takes a snapshot and sends on its own task.
            if (isNew && ownerId != Guid.Empty && judged != false)
            {
                var label = string.IsNullOrWhiteSpace(deviceName) ? (client ?? string.Empty) : deviceName!;
                // A lookalike of a learned service is held, not dropped: the fingerprint comes from a
                // judgement that only means "nothing reported in sixty seconds", and a second TV of the
                // same model was never announced even once it played (r3-01-01). A real service never
                // plays, so its notice still lapses.
                if (looksLikeService)
                {
                    logger?.LogDebug("ProfilesPlugin: {Device} looks like a service seen before; its notice waits for a playback.", label);
                    HoldNewDevice(ownerId, heldId ?? deviceId ?? string.Empty, label, logger);
                }
                else if (judged == true)
                {
                    Mail.Notifications.NewDevice(ownerId, deviceId ?? string.Empty, label, logger);
                }
                else
                {
                    // Not yet judged a player: a service signing in as the household (jfa-go) is sighted the
                    // same way and must not be announced as a device. The judgement arrives at NotePlayerAndSave,
                    // about a minute after an app signs in and at once on a playback start, and releases or drops
                    // this. No timer here, one entry per new device.
                    HoldNewDevice(ownerId, heldId ?? deviceId ?? string.Empty, label, logger);
                }
            }
        }

        private static readonly ConcurrentDictionary<string, (Guid Owner, string Id, string Label, DateTime At, ILogger? Logger)> HeldNewDevices
            = new(StringComparer.Ordinal);

        /// <summary>A held notice nobody judged is forgotten after this, so the table cannot grow.</summary>
        private static readonly TimeSpan HeldNoticeLife = TimeSpan.FromMinutes(10);

        private static string HeldKey(string? deviceId)
            => (deviceId ?? string.Empty).Trim().ToLowerInvariant();

        private static void HoldNewDevice(Guid owner, string deviceId, string label, ILogger? logger)
        {
            var now = DateTime.UtcNow;
            if (HeldNewDevices.Count > 200)
            {
                foreach (var kv in HeldNewDevices.ToArray())
                    if (now - kv.Value.At > HeldNoticeLife) HeldNewDevices.TryRemove(kv.Key, out _);
                if (HeldNewDevices.Count > 1000) HeldNewDevices.Clear();
            }

            HeldNewDevices[owner.ToString("N") + ":" + HeldKey(deviceId)] = (owner, deviceId, label, now, logger);
        }

        /// <summary>
        /// The device was judged a player: send each household's held notice for it. A device judged not
        /// a player keeps its notice for the rest of its ten minutes, because the judgement at sixty
        /// seconds only means "no capabilities yet" and a playback start can still prove it a player.
        /// </summary>
        private static void ReleaseHeld(string? canonicalId, bool canPlay)
        {
            if (!canPlay || HeldNewDevices.IsEmpty) return;
            var suffix = ":" + HeldKey(canonicalId);
            foreach (var kv in HeldNewDevices.ToArray())
            {
                if (!kv.Key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (!HeldNewDevices.TryRemove(kv.Key, out var held)) continue;
                if (DateTime.UtcNow - held.At <= HeldNoticeLife)
                    Mail.Notifications.NewDevice(held.Owner, held.Id, held.Label, held.Logger);
            }
        }

        /// <summary>How long a session may be signed in without showing it can play before its device is called a service.</summary>
        public static readonly TimeSpan PlayerGrace = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether a session's reported capabilities are those of a player. Jellyfin keeps them in memory
        /// per session and they are empty at SessionStarted, since an app posts them a moment after it
        /// signs in. So this is asked after <see cref="PlayerGrace"/>, not at sign-in.
        /// </summary>
        public static bool ReportsPlayback(int playableMediaTypes, bool supportsMediaControl)
            => playableMediaTypes > 0 || supportsMediaControl;

        /// <summary>
        /// Records what a session showed about <paramref name="deviceId"/>. True is sticky; false only
        /// fills in a device nothing is known about. Returns true when something changed.
        /// <b>Caller holds <see cref="ProfilesBaseController.ConfigLock"/>.</b>
        /// </summary>
        public static bool NotePlayer(PluginConfiguration? config, string? deviceId, bool canPlay)
        {
            var id = deviceId?.Trim();
            if (config?.KnownDevices == null || string.IsNullOrEmpty(id)) return false;

            var row = Row(config.KnownDevices, id);
            if (row == null) return false;

            if (canPlay)
            {
                if (row.CanPlay == true) return false;
                row.CanPlay = true;
                return true;
            }

            if (row.CanPlay != null) return false;
            row.CanPlay = false;
            return true;
        }

        /// <summary>How many service fingerprints are kept; the oldest goes first.</summary>
        internal const int MaxServiceFingerprints = 100;

        private static string? Fingerprint(string? deviceName, string? client)
        {
            var name = (deviceName ?? string.Empty).Trim().ToLowerInvariant();
            var app = (client ?? string.Empty).Trim().ToLowerInvariant();
            return name.Length == 0 && app.Length == 0 ? null : app + "|" + name;
        }

        /// <summary>Whether a device with this client and name matches one already judged a service.</summary>
        private static bool IsLearnedService(PluginConfiguration config, string? deviceName, string? client)
        {
            var fp = Fingerprint(deviceName, client);
            return fp != null && config.ServiceFingerprints != null && config.ServiceFingerprints.Contains(fp);
        }

        /// <summary>
        /// A device judged not a player records its client and name as what a service looks like. One
        /// later judged a player takes its fingerprint back out, so a real app slow to report is not
        /// remembered as a service. Returns true when the list changed.
        /// <b>Caller holds <see cref="ProfilesBaseController.ConfigLock"/>.</b>
        /// </summary>
        private static bool LearnService(PluginConfiguration config, KnownDevice row, bool canPlay)
        {
            var fp = Fingerprint(row.DeviceName, row.Client);
            if (fp == null) return false;
            config.ServiceFingerprints ??= new List<string>();
            if (canPlay) return config.ServiceFingerprints.Remove(fp);
            if (row.CanPlay != false || config.ServiceFingerprints.Contains(fp)) return false;
            config.ServiceFingerprints.Add(fp);
            while (config.ServiceFingerprints.Count > MaxServiceFingerprints) config.ServiceFingerprints.RemoveAt(0);
            return true;
        }

        /// <summary><see cref="NotePlayer"/>, taking the lock and saving as the plugin does.</summary>
        public static void NotePlayerAndSave(string? deviceId, bool canPlay)
        {
            string? canonical;
            lock (ProfilesBaseController.ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;
                var changed = NotePlayer(config, deviceId, canPlay);
                var row = Row(config.KnownDevices, deviceId?.Trim());
                if (row != null) changed |= LearnService(config, row, canPlay);
                if (changed) Plugin.Instance?.SaveConfiguration();
                canonical = row?.DeviceId ?? deviceId;
            }

            // Outside the lock, as the sighting's own notice is.
            ReleaseHeld(canonical, canPlay);
        }

        /// <summary>
        /// Whether a device row belongs in a household's device list: a player, one nothing is known
        /// about yet, or one a profile is already restricted to. The last is never dropped, because the
        /// edit form rebuilds the whitelist from what it was shown.
        /// </summary>
        public static bool IsListedFor(KnownDevice device, ISet<string> whitelistedDeviceIds)
            => device.CanPlay != false
               || (whitelistedDeviceIds?.Contains(device.DeviceId) ?? false);

        /// <summary>
        /// Whether Bonfire runs this account's household at all: whether it owns sub-profiles or is one.
        /// <para>
        /// The filter for the sign-in listener. It is separate from <see cref="HouseholdOf"/>, which
        /// returns the account itself when it knows of no mapping and so is never
        /// <see cref="Guid.Empty"/>. Keying on that would record a device for every account on the server,
        /// and on a forty-user server that is every phone and browser writing a row into an XML file
        /// that is rewritten whole.
        /// </para>
        /// </summary>
        public static bool IsHousehold(PluginConfiguration? config, Guid userId)
        {
            if (config?.Mappings == null || userId == Guid.Empty) return false;

            return config.Mappings.Any(m => m.MasterUserId == userId || m.ProfileUserId == userId);
        }

        /// <summary>
        /// The household an account belongs to: its master if it is a sub-profile, itself otherwise.
        /// <para>
        /// A device is attributed to the household, never to whoever signed in. Attributing it to a
        /// sub-profile would let the second person to use a television take it over, and the master's own
        /// profiles would stop being offered on it.
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
