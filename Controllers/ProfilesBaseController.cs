using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using Jellyfin.Profiles.Auth;
using AccessSchedule = Jellyfin.Database.Implementations.Entities.AccessSchedule;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Shared base for every Profiles sub-controller: helpers, static caches and injected services.
    /// Controllers are transient, so state meant to live for the whole app (JS content, audit-log
    /// path) is static.
    /// </summary>
    [ApiController]
    // ProfilesController is [AllowAnonymous] at class level because profiles.js and image/{id}
    // must work before sign-in. So every endpoint that needs a user calls GetCurrentUserId() and
    // returns 401 when it is null.
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    // Every error carries a stable code in X-Bonfire-Error. See ErrorCodes.
    [BonfireErrors]
    public abstract class ProfilesBaseController : ControllerBase
    {
        // ── One-time static caches ──────────────────────────────────────────────────
        internal static string? CachedProfilesJs;
        internal static readonly object JsCacheLock = new();

        internal static string? AuditLogPath;
        internal static readonly object AuditLogLock = new();

        /// <summary>
        /// The one lock around every read-modify-write of the plugin configuration.
        /// <para>
        /// It used to be taken on <c>Plugin.Instance.Configuration</c> itself. Jellyfin assigns a new
        /// configuration instance when an administrator saves the settings page, so monitors on the
        /// old object guarded nothing, two writers got in at once, and a saved profile, device or
        /// group was gone. Symptom: saved, then missing.
        /// </para>
        /// <para>
        /// Static because controllers are transient; an instance lock is a private lock per request.
        /// <c>tests/cs/configlock</c> guards this.
        /// </para>
        /// </summary>
        internal static readonly object ConfigLock = new();

        /// <summary>
        /// The configuration copied under <see cref="ConfigLock"/>, so it can be read without holding
        /// it. See <see cref="PluginConfiguration.SnapshotForReading"/>.
        /// <para>
        /// The provider calls this inside Jellyfin's per-user sign-in lock, so nothing here may wait
        /// on Jellyfin. A list copy cannot.
        /// </para>
        /// </summary>
        internal static PluginConfiguration? ReadConfigSnapshot()
        {
            lock (ConfigLock)
            {
                return Plugin.Instance?.Configuration?.SnapshotForReading();
            }
        }

        /// <summary>
        /// The mapping row for <paramref name="userId"/>, found with the lock held, or null. Walking
        /// <c>config.Mappings</c> outside <see cref="ConfigLock"/> throws "Collection was modified"
        /// when another request edits it. The row returned is the live one.
        /// <c>tests/js/mappingslock.js</c> fails any list walk outside the lock.
        /// </summary>
        internal static ProfileMapping? FindMapping(Guid userId)
        {
            lock (ConfigLock)
            {
                return Plugin.Instance?.Configuration?.Mappings?.FirstOrDefault(m => m.ProfileUserId == userId);
            }
        }

        /// <summary>The library picture with this id, found with the lock held, or null.</summary>
        internal static AvatarLibraryItem? FindLibraryAvatar(string? id)
        {
            lock (ConfigLock)
            {
                return Plugin.Instance?.Configuration?.AvatarLibrary?.FirstOrDefault(a =>
                    string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// True when <paramref name="mapping"/> is a sub-profile whose main account is turned off in
        /// Dashboard -> Users.
        /// </summary>
        internal static bool MainAccountDisabled(IUserManager users, ProfileMapping? mapping)
        {
            // A master's own row, or none, is the account itself; callers check its IsDisabled.
            // Disabling a main account used to leave its profiles enterable by PIN and the switcher
            // (R-1135). An unfound master is not "disabled": this only removes access.
            if (mapping == null || mapping.MasterUserId == mapping.ProfileUserId) return false;
            var master = users.GetUserById(mapping.MasterUserId);
            return master != null && users.GetUserDto(master, string.Empty).Policy?.IsDisabled == true;
        }

        /// <summary>
        /// Should a switch to <paramref name="targetUserId"/> rewrite that account's policy from its
        /// master's? True only for a genuine sub-profile.
        /// </summary>
        /// <remarks>
        /// Issue #27. The switch path inherits streaming limits, tag filters and the enabled library
        /// list from a profile's master. It was gated only on <c>targetUser.Id != callerMasterUserId</c>,
        /// so a Bonfire switch into someone else's main account ran it too, treating the target as
        /// its own master. A main user normally has <c>EnableAllFolders = true</c> and an empty
        /// <c>EnabledFolders</c>; the legacy branch read that empty list as authoritative and wrote
        /// <c>EnableAllFolders = false</c> with no folders. Reported as "that user's library access is
        /// cleared". It also set <c>IsAdministrator = false</c> and <c>IsHidden = true</c>, so an admin
        /// was demoted, and their sub-profiles lost access with it.
        /// A master's self-mapping is excluded like a missing one: a real account owns its policy.
        /// </remarks>
        internal static bool ShouldInheritMasterPolicy(
            ProfileMapping? mapping, Guid targetUserId, Guid callerMasterUserId)
        {
            // No mapping: a main Jellyfin account reached through a Bonfire link.
            if (mapping == null) return false;

            // A master's self-mapping. It is its own master, so there is nothing to inherit.
            if (mapping.ProfileUserId == mapping.MasterUserId) return false;

            // Switching back to the caller's own master account.
            if (targetUserId == callerMasterUserId) return false;

            return true;
        }

        // ── DI fields (set by derived constructors) ─────────────────────────────────
        protected readonly IUserManager _userManager;
        protected readonly ISessionManager _sessionManager;
        protected readonly IDeviceManager _deviceManager;
        protected readonly ILibraryManager _libraryManager;
        protected readonly INetworkManager _networkManager;
        protected readonly ILogger _logger;

        protected ProfilesBaseController(
            IUserManager userManager,
            ISessionManager sessionManager,
            IDeviceManager deviceManager,
            ILibraryManager libraryManager,
            INetworkManager networkManager,
            ILogger logger)
        {
            _userManager = userManager;
            _sessionManager = sessionManager;
            _deviceManager = deviceManager;
            _libraryManager = libraryManager;
            _networkManager = networkManager;
            _logger = logger;
        }

        // ── Auth helpers ────────────────────────────────────────────────────────────

        protected Guid? GetCurrentUserId()
        {
            var claim = User?.FindFirst("Jellyfin-UserId")
                        ?? User?.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null)
            {
                _logger.LogWarning("ProfilesPlugin: User ID claim not found in User principal.");
                return null;
            }
            if (!Guid.TryParse(claim.Value, out var userId))
            {
                _logger.LogWarning("ProfilesPlugin: Failed to parse User ID claim '{Value}' as Guid.", claim.Value);
                return null;
            }
            // All zeros is what an API key carries, not a user. Elsewhere an empty id means "unset" or
            // "every household". Administrator checks accept a key through CallerIsApiKey. R-1119.
            if (userId == Guid.Empty) return null;
            return userId;
        }

        protected string? GetAuthorizationParameter(string name)
            => ParseAuthorizationParameter(
                   Request.Headers["Authorization"].FirstOrDefault(),
                   Request.Headers["X-Emby-Authorization"].FirstOrDefault(),
                   name);

        /// <summary>
        /// Reads one parameter from a MediaBrowser authorization header, the way Jellyfin's
        /// <c>AuthorizationContext.GetParts</c> does.
        /// <para>
        /// It used to read only <c>Authorization</c>, split on every comma and return the value raw.
        /// This supplies the DeviceId the device restriction checks, and a DeviceId that fails to
        /// parse looks like a device not on the whitelist, so a restricted profile could not be
        /// reached from any device. Three fixes:
        /// </para>
        /// <list type="number">
        /// <item><description><c>X-Emby-Authorization</c> is still sent by some clients, and Jellyfin
        /// falls back to it while legacy authorization is on (off by default on 12.x). Without it
        /// every parameter was null for them: the switch was refused and the device picker
        /// recorded nothing.</description></item>
        /// <item><description>Commas inside a quoted value belong to the value. The device name is
        /// user-settable and sent unencoded (<c>apiClient.setRequestHeaders</c> interpolates
        /// <c>_deviceName</c>), so "Living Room, TV" shifted every later parameter.</description></item>
        /// <item><description>Some clients URL-encode values and Jellyfin decodes them. Without decoding
        /// one device was recorded under two spellings and showed as "Xbox%20One".</description></item>
        /// </list>
        /// <para>Static and string-based so a harness can call it without a request.</para>
        /// </summary>
        internal static string? ParseAuthorizationParameter(
            string? authorizationHeader, string? embyAuthorizationHeader, string name)
        {
            var header = !string.IsNullOrEmpty(authorizationHeader)
                ? authorizationHeader
                : embyAuthorizationHeader;
            if (string.IsNullOrEmpty(header)) return null;

            // Strip the scheme prefix so the first token parses as Client="...". Older clients open with
            // "Emby "; left in, it joined the first key and "Emby Client" never matched "Client". R-1124.
            foreach (var scheme in new[] { "MediaBrowser ", "Emby " })
            {
                if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    header = header.Substring(scheme.Length);
                    break;
                }
            }

            // Quote-aware scan. `inQuotes` flips on every quote character, so a comma seen
            // while inside a value is kept rather than treated as a separator.
            var key = string.Empty;
            var start = 0;
            var inQuotes = false;
            string? found = null;

            void Consider(string k, string v)
            {
                // No `found == null` guard: Jellyfin's GetParts assigns `result[key] = ...`,
                // so a key sent twice keeps the last value, and so must this.
                if (string.Equals(k.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    found = WebUtility.UrlDecode(v.Trim().Trim('"'));
                }
            }

            int i;
            for (i = 0; i < header.Length; i++)
            {
                var c = header[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && c == '=' && key.Length == 0)
                {
                    key = header.Substring(start, i - start);
                    start = i + 1;
                }
                else if (!inQuotes && c == ',')
                {
                    if (key.Length > 0) Consider(key, header.Substring(start, i - start));
                    key = string.Empty;
                    start = i + 1;
                }
            }

            if (key.Length > 0) Consider(key, header.Substring(start, i - start));

            return found;
        }

        // The primitives moved to Auth/PinHasher.cs so the authentication provider could share them.
        // These stay as thin wrappers so call sites and harnesses still find them.

        protected string HashPin(string? pin) => PinHasher.Hash(pin);

        /// <summary>True when the stored hash still uses the legacy format and should be upgraded.</summary>
        protected static bool IsLegacyPinHash(string? storedHash) => PinHasher.IsLegacy(storedHash);

        /// <summary>
        /// Constant-time comparison of a PIN against a stored hash of either format. False for an
        /// empty hash; callers handle "no PIN set".
        /// </summary>
        protected bool VerifyPinHash(string? pin, string? storedHash)
        {
            var result = PinHasher.Verify(pin, storedHash);
            if (result == PinHasher.PinResult.MalformedHash)
            {
                _logger.LogWarning("ProfilesPlugin: Stored PIN hash is malformed; refusing to verify.");
            }

            return result == PinHasher.PinResult.Match;
        }

        protected bool VerifyPinAndUpgrade(string? pin, ProfileMapping mapping)
        {
            if (!VerifyPinHash(pin, mapping.PinHash)) return false;

            if (IsLegacyPinHash(mapping.PinHash))
            {
                // Hashed before the lock: PBKDF2 takes tens of milliseconds, and every
                // other reader of the configuration waited for it when it ran inside.
                var upgraded = HashPin(pin);
                lock (ConfigLock)
                {
                    // The row to change is the live one: callers may hold a snapshot's copy, and an upgrade
                    // written to a copy is lost. Falls back to the row given if it is not in the live list.
                    var row = Plugin.Instance?.Configuration?.Mappings?
                        .FirstOrDefault(m => m.ProfileUserId == mapping.ProfileUserId) ?? mapping;

                    // Re-check inside the lock — a concurrent request may have upgraded it.
                    if (IsLegacyPinHash(row.PinHash))
                    {
                        row.PinHash = upgraded;
                        mapping.PinHash = row.PinHash;
                        Plugin.Instance?.SaveConfiguration();
                        _logger.LogInformation(
                            "ProfilesPlugin: Upgraded legacy PIN hash for profile {Id} to PBKDF2.",
                            mapping.ProfileUserId);
                    }
                }
            }

            return true;
        }

        // Pure functions so the whole matrix (opted in or out, local or remote, PIN or not) can be
        // tested. /switch and /verify-pin must agree, or the client is told a switch will work and
        // then refused.

        /// <summary>
        /// The rule for entering anything in another household: its master or one of its
        /// sub-profiles. The PIN is the target's own; the consent is always the owning master's.
        /// </summary>
        internal static (bool HouseholdLanBypass, bool BlockedUnprotected) EvaluateCrossHouseholdEntry(
            string? targetPinHash, ProfileMapping? ownerMapping, bool isLocal)
        {
            // Consent has to come from the household being entered. Remote requests never
            // qualify, so a leaked Bonfire code is worth nothing outside the house.
            bool householdLanBypass = isLocal && (ownerMapping?.AllowHouseholdLanBypass ?? false);

            // Anything with no PIN has nothing to prove ownership with; the opt-in is the
            // only thing that can stand in for one, and only on the local network.
            bool blocked = string.IsNullOrEmpty(targetPinHash) && !householdLanBypass;

            return (householdLanBypass, blocked);
        }

        /// <summary>What a switch into <see cref="EvaluateSwitch"/>'s target is allowed to do.</summary>
        internal sealed class SwitchRules
        {
            /// <summary>The target is not reachable from the caller's household at all.</summary>
            public bool Unauthorized { get; init; }

            /// <summary>Reachable in principle, refused as it stands; the text says why.</summary>
            public string? Refusal { get; init; }

            /// <summary>The target is in another household, reached through a Bonfire.</summary>
            public bool CrossHousehold { get; init; }

            /// <summary>The owning household consented to PIN-less entry on this network.</summary>
            public bool HouseholdLanBypass { get; init; }

            /// <summary>The target's own mapping row, if it has one.</summary>
            public ProfileMapping? Target { get; init; }
        }

        /// <summary>
        /// Who may enter what: the one rule behind /switch, /verify-pin and /list, so they cannot
        /// drift (a switch promised then refused, or a list offering a door that does not open).
        /// <para>
        /// Across a Bonfire link, the master-account rules now cover sub-profiles. They used to
        /// open freely with or without a PIN, so joining a Bonfire let every member into your
        /// PIN-less profiles from anywhere. A profile in another household now needs its PIN, or
        /// its owner's "Allow household switching on this network" while local. The owner's "hide
        /// my sub-profiles" and the caller's "hide others' sub-profiles" now refuse the switch
        /// instead of only hiding the tile.
        /// </para>
        /// </summary>
        /// <param name="linkedAccounts">
        /// The accounts shown in the caller's household (issue #39), each with whether it is an
        /// administrator. See <see cref="LinkedAccount"/> for the rules.
        /// </param>
        internal static SwitchRules EvaluateSwitch(
            IReadOnlyList<ProfileMapping> mappings,
            Guid callerMasterUserId,
            Guid targetId,
            ICollection<Guid> linkedMasterIds,
            bool isLocal,
            IReadOnlyDictionary<Guid, bool>? linkedAccounts)
        {
            var target = mappings.FirstOrDefault(m => m.ProfileUserId == targetId);

            // Checked ahead of a Bonfire, whose rules would refuse an account with no PIN. The
            // link's password was the consent; a Bonfire code never was.
            if (targetId != callerMasterUserId && linkedAccounts != null && linkedAccounts.TryGetValue(targetId, out var linkedIsAdmin))
            {
                // Linked as a main account and made a profile since: the link is stale, and a
                // profile is only ever opened from its own household or a Bonfire.
                if (target != null && target.MasterUserId != Guid.Empty && target.MasterUserId != targetId)
                {
                    return new SwitchRules { Unauthorized = true, Target = target };
                }

                if (string.IsNullOrEmpty(target?.PinHash))
                {
                    return linkedIsAdmin
                        ? new SwitchRules { Target = target, CrossHousehold = true, Refusal = AccountLinks.AdminNeedsPinMessage }
                        : new SwitchRules { Target = target, CrossHousehold = true };
                }

                // An administrator's PIN is asked for on every network.
                return new SwitchRules
                {
                    Target = target,
                    CrossHousehold = true,
                    HouseholdLanBypass = !linkedIsAdmin && isLocal && target!.AllowHouseholdLanBypass
                };
            }

            Guid household;
            if (targetId == callerMasterUserId) household = callerMasterUserId;
            else if (linkedMasterIds.Contains(targetId)) household = targetId;
            else if (target != null && target.MasterUserId != Guid.Empty && linkedMasterIds.Contains(target.MasterUserId))
                household = target.MasterUserId;
            else return new SwitchRules { Unauthorized = true, Target = target };

            if (household == callerMasterUserId)
            {
                return new SwitchRules { Target = target };
            }

            ProfileMapping? Row(Guid id) => mappings.FirstOrDefault(m => m.ProfileUserId == id && m.MasterUserId == id);
            var owner = Row(household);
            bool isMaster = targetId == household;

            if (!isMaster)
            {
                var mine = Row(callerMasterUserId);
                if ((owner?.HideMySubProfilesFromOthers ?? false) || (mine?.HideOthersSubProfilesFromMe ?? false))
                {
                    return new SwitchRules { Unauthorized = true, Target = target };
                }
            }

            var (bypass, blocked) = EvaluateCrossHouseholdEntry(target?.PinHash, owner, isLocal);
            if (blocked)
            {
                return new SwitchRules
                {
                    Target = target,
                    CrossHousehold = true,
                    Refusal = isMaster
                        ? "This account has no PIN set, so it cannot be opened from a shared Bonfire. "
                          + "Its owner must either set a profile PIN, or turn on \"Allow household "
                          + "switching on this network\" in their Bonfire settings."
                        : "This profile has no PIN, so it cannot be opened from a shared Bonfire. "
                          + "Its owner must either set a PIN on it, or turn on \"Allow household "
                          + "switching on this network\" in their Bonfire settings."
                };
            }

            return new SwitchRules { Target = target, CrossHousehold = true, HouseholdLanBypass = bypass };
        }

        /// <summary>
        /// Whether a PIN prompt can be skipped for this switch.
        /// <para>
        /// Within your household: <see cref="ProfileMapping.BypassPinOnLocalNetwork"/>, or for your
        /// main account, turning off <see cref="ProfileMapping.AskPinToOpen"/>. Across a Bonfire link
        /// only <paramref name="householdLanBypass"/> counts; your settings do not reach another
        /// account.
        /// </para>
        /// <para>
        /// This decides entry only. Managing the household checks the PIN itself
        /// (<see cref="ManagementAllowed"/>).
        /// </para>
        /// <para>
        /// <paramref name="isTrusted"/> means this address is listed for the household
        /// (<see cref="IsTrustedFor"/>, which keeps administrators out). The main account and its
        /// profiles then open without a PIN. Never across a link, and never the main PIN for
        /// changing settings.
        /// </para>
        /// </summary>
        protected internal static bool CanSkipPin(
            ProfileMapping? mapping, bool isLocal, bool isCrossAccountMasterSwitch, bool householdLanBypass,
            bool isTrusted = false)
        {
            if (isCrossAccountMasterSwitch) return householdLanBypass;
            if (mapping == null) return false;
            if (isTrusted) return true;
            if (IsMainRow(mapping) && !mapping.AskPinToOpen) return true;
            return mapping.BypassPinOnLocalNetwork && isLocal;
        }

        /// <summary>
        /// Whether <paramref name="address"/> is listed for <paramref name="mapping"/>'s household and
        /// neither the main account nor the account being opened is an administrator (promoted since
        /// the entry was saved, or made a profile). Cheap while the list is empty.
        /// </summary>
        protected bool IsTrustedFor(ProfileMapping? mapping, System.Net.IPAddress? address, PluginConfiguration? config)
        {
            if (mapping == null) return false;
            return Auth.TrustedAddresses.Applies(
                config, address, mapping.MasterUserId, mapping.ProfileUserId,
                id => Auth.TrustedAddresses.IsAdministrator(_userManager, id));
        }

        /// <summary>
        /// Why <paramref name="id"/> cannot be named by a trusted address, or null: it must be a real
        /// main account and not an administrator.
        /// </summary>
        protected string? TrustedAccountProblem(Guid id)
        {
            if (_userManager.GetUserById(id) == null) return "That account does not exist.";
            var own = FindMapping(id);
            if (own != null && own.MasterUserId != id)
                return "A profile can not be listed. Use its main account.";
            if (Auth.TrustedAddresses.IsAdministrator(_userManager, id))
                return "An administrator always asks for a PIN.";
            return null;
        }

        /// <summary>
        /// Records that <paramref name="householdId"/> (a main account) has made a request from the
        /// server's own network, and returns whether it has now been seen there. Saves only the first
        /// time. Reads inside the lock like <c>UpdateAdminSettings</c>, since a snapshot may be stale.
        /// </summary>
        protected static bool NoteHouseholdSeenLocally(Guid householdId, bool isLocal, PluginConfiguration? snapshot)
        {
            if (!isLocal) return snapshot?.HouseholdsSeenLocally?.Contains(householdId) ?? false;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return true;
                config.HouseholdsSeenLocally ??= new List<Guid>();
                if (!config.HouseholdsSeenLocally.Contains(householdId))
                {
                    config.HouseholdsSeenLocally.Add(householdId);
                    Plugin.Instance?.SaveConfiguration();
                }
                return true;
            }
        }

        /// <summary>A main account's own row, as opposed to one of its sub-profiles.</summary>
        internal static bool IsMainRow(ProfileMapping m) =>
            m.MasterUserId != Guid.Empty && m.ProfileUserId == m.MasterUserId;

        /// <summary>The header a client may send the main account's PIN in, for routes with no body field for it.</summary>
        internal const string MainPinHeader = "X-Bonfire-Master-Pin";

        /// <summary>
        /// The check in front of everything that changes a household: profiles, devices, sharing and
        /// switcher settings. Callers check first that the caller is the main account.
        /// <para>
        /// With a main PIN set, the PIN is required, from <paramref name="bodyPin"/> or the
        /// <see cref="MainPinHeader"/> header, throttled per account. It used to be checked on five
        /// routes only (create, update, delete, theme, artwork), so an open main account left sharing,
        /// devices and switcher settings unguarded.
        /// </para>
        /// <para>
        /// Without one the household is unguarded, unless the administrator requires main PINs, in
        /// which case nothing changes until one is set. <paramref name="settingOwnPin"/> is the
        /// exception: the route that sets it.
        /// </para>
        /// </summary>
        protected bool ManagementAllowed(
            Guid masterUserId, PluginConfiguration config, string? bodyPin, out ActionResult? refusal, bool settingOwnPin = false)
        {
            var masterMapping = MappingIn(config, masterUserId);
            if (masterMapping == null || string.IsNullOrEmpty(masterMapping.PinHash))
            {
                if (config.RequireMainPin && !settingOwnPin)
                {
                    refusal = BadRequest(MainPinRequiredMessage);
                    return false;
                }

                refusal = null;
                return true;
            }

            var pin = !string.IsNullOrEmpty(bodyPin) ? bodyPin : Request.Headers[MainPinHeader].ToString();

            // No PIN sent is not a wrong guess. Counting it would let a page that forgot to send one, or
            // a cached script from before a route needed it, lock the owner out.
            if (string.IsNullOrEmpty(pin))
            {
                refusal = BadRequest(MainPinMissingMessage);
                return false;
            }

            return VerifyMasterPinLimited(pin, masterMapping, config, out refusal);
        }

        /// <summary>Whether this main account has set a PIN.</summary>
        internal static bool MainHasPin(Guid masterUserId, PluginConfiguration config)
        {
            var row = MappingIn(config, masterUserId);
            return row != null && !string.IsNullOrEmpty(row.PinHash);
        }

        /// <summary>
        /// The row for <paramref name="userId"/>, found with <see cref="ConfigLock"/> held. Create,
        /// delete and copy-rules change the list under it, and an unlocked walk throws "Collection was
        /// modified" (r2-11-02).
        /// </summary>
        internal static ProfileMapping? MappingIn(PluginConfiguration config, Guid userId)
        {
            lock (ConfigLock)
            {
                return config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            }
        }

        /// <summary>How many profiles <paramref name="masterUserId"/> has made, counted under <see cref="ConfigLock"/>.</summary>
        internal static int ProfileCountOf(PluginConfiguration config, Guid masterUserId)
        {
            lock (ConfigLock)
            {
                return config.Mappings.Count(m => m.MasterUserId == masterUserId && m.ProfileUserId != masterUserId);
            }
        }

        /// <summary>Refusal text when the administrator requires main PINs and this account has none.</summary>
        internal const string MainPinRequiredMessage = "Set a PIN for your account first.";

        /// <summary>Refusal text when a PIN is needed and none was sent.</summary>
        internal const string MainPinMissingMessage = "Enter your account's PIN.";

        /// <summary>/update and /create given both allLibraries: true and a library list (R-1144).</summary>
        internal const string LibrariesTwiceMessage = "Send enabledFolders or allLibraries, not both.";

        /// <summary>A negative maxParentalRating. No rating has a score below zero; with no limit on the main account it was stored as asked (R-1141).</summary>
        internal const string RatingNegativeMessage = "maxParentalRating cannot be below zero.";

        /// <summary>
        /// The most devices one profile's list may name, the same as the most a household may have on
        /// record (<see cref="Auth.DeviceRegistry.MaxDevicesPerHousehold"/>). A fixed limit: a list can
        /// only name devices the household has been seen on (R-1155).
        /// </summary>
        internal const int MaxAllowedDevicesPerProfile = Auth.DeviceRegistry.MaxDevicesPerHousehold;

        /// <summary>Why <paramref name="value"/> is not a usable maxParentalRating, or null. "" is no limit.</summary>
        internal static string? ValidateRatingScore(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!int.TryParse(value, out var score)) return RatingNotScoreMessage;
            return score < 0 ? RatingNegativeMessage : null;
        }

        /// <summary>
        /// A device list as stored: ids trimmed, blanks and repeats dropped. Ids are compared ordinally,
        /// so " tv-1" was a device nobody has. Refused when too long or too many (R-1155). Null in,
        /// null out.
        /// </summary>
        internal static (List<string>? Ids, string? Error) NormalizeAllowedDeviceIds(IEnumerable<string>? ids)
        {
            if (ids == null) return (null, null);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var raw in ids)
            {
                var id = raw?.Trim();
                if (string.IsNullOrEmpty(id)) continue;
                if (id.Length > Auth.DeviceRegistry.MaxDeviceIdLength) return (null, "A device id is too long.");
                if (seen.Add(id)) result.Add(id);
                if (result.Count > MaxAllowedDevicesPerProfile) return (null, "Too many devices in the list.");
            }
            return (result, null);
        }

        /// <summary>The most tags a profile may block, and the most it may allow. A fixed limit (r2-22-01).</summary>
        internal const int MaxTagsPerList = 100;

        /// <summary>The longest a tag may be.</summary>
        internal const int MaxTagLength = 100;

        internal const string TooManyTagsMessage = "Too many tags in the list.";
        internal const string TagTooLongMessage = "A tag is too long.";

        /// <summary>
        /// Refuses a tag list that is long or holds a long tag, before it goes into the Jellyfin
        /// user's policy, where every read would carry it. Counted after trimming and de-duplicating,
        /// as stored. A list left out is fine.
        /// </summary>
        internal static string? TagListError(IEnumerable<string>? tags)
        {
            if (tags == null) return null;
            var normalized = NormalizeTags(tags);
            if (normalized.Count > MaxTagsPerList) return TooManyTagsMessage;
            if (normalized.Any(t => t.Length > MaxTagLength)) return TagTooLongMessage;
            return null;
        }

        /// <summary>A maxParentalRating that isn't a score. It used to be read as no limit (R-1144).</summary>
        internal const string RatingNotScoreMessage = "maxParentalRating must be a rating score, or \"\" for no limit.";

        /// <summary>
        /// Null when the caller is a Jellyfin administrator, otherwise the result to return.
        /// <paramref name="action"/> completes "Only administrators can ...".
        /// </summary>
        protected ActionResult? RequireAdministrator(string action)
        {
            // An API key from Dashboard -> API Keys has no user: its id claim is all zeros, so the lookup
            // below found nobody and every key was refused. Jellyfin gives a key the administrator role
            // (CustomAuthenticationHandler, same in 10.11 and 12.0), and the claims come from there.
            if (CallerIsApiKey()) return null;

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
                return Unauthorized($"Only administrators can {action}.");

            return null;
        }

        /// <summary>
        /// True when the caller signed in with an API key. Both claims are needed: a key always has the
        /// administrator role, and nothing else is marked as a key.
        /// </summary>
        protected bool CallerIsApiKey()
            => string.Equals(User?.FindFirst("Jellyfin-IsApiKey")?.Value, "True", StringComparison.OrdinalIgnoreCase)
               && User!.IsInRole("Administrator");

        /// <summary>The caller's name, for a log line about what an administrator did.</summary>
        protected string CallerName()
        {
            if (CallerIsApiKey()) return "An API key";
            var id = GetCurrentUserId();
            return (id.HasValue ? _userManager.GetUserById(id.Value)?.Username : null) ?? "An administrator";
        }

        /// <summary>
        /// Answers with <paramref name="body"/>, or 304 when the caller already has it.
        /// The tag hashes the body as sent, so it changes exactly when the answer does (a setting, the
        /// caller's network, a profile added elsewhere). Nothing has to bump a version.
        /// </summary>
        protected ActionResult ETagged(object body)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            var tag = "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";

            // Private: the answer is the caller's own. no-cache: keep it, but ask every time.
            Response.Headers["Cache-Control"] = "private, no-cache";
            Response.Headers["ETag"] = tag;

            if (IfNoneMatchHas(tag))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }
            return Ok(body);
        }

        /// <summary>
        /// Whether If-None-Match names <paramref name="etag"/>. The header is a list, and a proxy that
        /// compresses the body turns a strong tag into <c>W/"..."</c>; comparing the whole header
        /// never matched, so every request behind one paid for the full body. Weak comparison is the
        /// spec's rule for If-None-Match. <c>*</c> is not honored: nothing here is a create.
        /// </summary>
        protected bool IfNoneMatchHas(string etag)
        {
            var sent = Request.Headers["If-None-Match"].ToString();
            if (string.IsNullOrEmpty(sent)) return false;
            var want = etag.StartsWith("W/", StringComparison.Ordinal) ? etag.Substring(2) : etag;
            foreach (var part in sent.Split(','))
            {
                var t = part.Trim();
                if (t.StartsWith("W/", StringComparison.Ordinal)) t = t.Substring(2);
                if (string.Equals(t, want, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Writes a line to Jellyfin's Activity log (Dashboard -> Activity). Never throws or waits:
        /// losing a log line must not fail what it describes.
        /// <para>
        /// Fetched from the request services rather than injected, so the constructor every harness
        /// builds does not change. Without a request, or a server that lacks it, nothing is written.
        /// </para>
        /// </summary>
        protected void LogActivity(string name, string type, Guid userId, string? overview = null,
            Microsoft.Extensions.Logging.LogLevel severity = Microsoft.Extensions.Logging.LogLevel.Information)
        {
            try
            {
                var activity = HttpContext?.RequestServices?.GetService(typeof(MediaBrowser.Model.Activity.IActivityManager))
                    as MediaBrowser.Model.Activity.IActivityManager;
                if (activity == null) return;

                var entry = new Jellyfin.Database.Implementations.Entities.ActivityLog(name, "Bonfire" + type, userId)
                {
                    ShortOverview = overview,
                    LogSeverity = severity,
                };
                _ = activity.CreateAsync(entry).ContinueWith(
                    t => _logger.LogDebug(t.Exception, "ProfilesPlugin: Could not write to the activity log."),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: Could not write to the activity log.");
            }
        }

        /// <summary>
        /// True when the account carries administrator rights. For warnings and log lines about how
        /// much a session is worth, not for authorizing the caller (check the caller's own policy).
        /// </summary>
        protected bool IsUserAdministrator(Guid userId)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null) return false;

            try
            {
                return _userManager.GetUserDto(user, string.Empty).Policy?.IsAdministrator ?? false;
            }
            catch (Exception ex)
            {
                // A failed policy lookup must not read as "not an administrator" without a trace; callers
                // use this to decide how loudly to warn.
                _logger.LogWarning(ex, "ProfilesPlugin: Could not read policy for user {Id}.", userId);
                return false;
            }
        }

        // IUserManager.Users was renamed GetUsers() in Jellyfin 10.11.7. We compile against 10.11.5
        // (lowered in v1.1.13 to fix a loader crash there) and call whichever member exists through
        // reflection, cached in a static since these run on request paths.

        private const System.Reflection.BindingFlags PublicInstance =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        // Resolved once per user-manager implementation type. IUserManager is a singleton in
        // Jellyfin, so in practice these each resolve exactly once for the process lifetime.
        private static readonly ConcurrentDictionary<Type, System.Reflection.MethodInfo?> GetUsersMethodCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> UsersPropertyCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.MethodInfo?> ChangePasswordCache = new();

        protected IEnumerable<Jellyfin.Database.Implementations.Entities.User> GetAllUsers()
            => AllUsersOf(_userManager, _logger);

        /// <summary>
        /// Every account, from whichever of <c>GetUsers()</c> or <c>Users</c> this Jellyfin has. For
        /// code outside a controller: <c>userManager.Users</c> compiles against our package and throws
        /// MissingMethodException on 10.11.7+, inside whatever lambda holds it, before its own try
        /// (tests/binchk.sh found one in LimitsProvider).
        /// </summary>
        internal static IEnumerable<Jellyfin.Database.Implementations.Entities.User> AllUsersOf(
            IUserManager userManager, ILogger? logger = null)
        {
            var type = userManager.GetType();

            var method = GetUsersMethodCache.GetOrAdd(type, t => t.GetMethod("GetUsers", PublicInstance));
            if (method != null)
            {
                try
                {
                    return (IEnumerable<Jellyfin.Database.Implementations.Entities.User>)method.Invoke(userManager, null)!;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "ProfilesPlugin: GetUsers() reflection failed, falling back.");
                }
            }

            var prop = UsersPropertyCache.GetOrAdd(type, t => t.GetProperty("Users", PublicInstance));
            if (prop != null)
                return (IEnumerable<Jellyfin.Database.Implementations.Entities.User>)prop.GetValue(userManager)!;

            logger?.LogError("ProfilesPlugin: Could not resolve user list from IUserManager.");
            return Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>();
        }

        protected Task ChangePasswordCompat(
            Jellyfin.Database.Implementations.Entities.User user, string newPassword)
        {
            var type = _userManager.GetType();
            var userType = user.GetType();

            var resolved = ChangePasswordCache.GetOrAdd(type, t =>
                t.GetMethod("ChangePassword", PublicInstance, null, new[] { typeof(Guid), typeof(string) }, null)
                ?? t.GetMethod("ChangePassword", PublicInstance, null, new[] { userType, typeof(string) }, null));

            if (resolved != null)
            {
                // The Guid overload takes the user's id; the entity overload takes the user.
                var firstArg = resolved.GetParameters()[0].ParameterType == typeof(Guid)
                    ? (object)user.Id
                    : user;
                return (Task)resolved.Invoke(_userManager, new[] { firstArg, newPassword })!;
            }

            // Not a quiet success: a caller that carried on would believe the account has a password
            // nobody knows, and left it with none (r2-08-01). Callers catch this and undo, as for
            // RenameUserCompat.
            _logger.LogError("ProfilesPlugin: Could not resolve ChangePassword on IUserManager.");
            throw new MissingMethodException("IUserManager has no ChangePassword this plugin can call.");
        }

        private static readonly ConcurrentDictionary<Type, System.Reflection.MethodInfo?> RenameUserCache = new();

        private System.Reflection.MethodInfo? ResolveRenameUser() =>
            RenameUserCache.GetOrAdd(_userManager.GetType(), t =>
                t.GetMethod("RenameUser", PublicInstance, null,
                    new[] { typeof(Guid), typeof(string), typeof(string) }, null)
                ?? t.GetMethod("RenameUser", PublicInstance, null,
                    new[] { typeof(Jellyfin.Database.Implementations.Entities.User), typeof(string) }, null));

        /// <summary>Whether this server has a RenameUser overload <see cref="RenameUserCompat"/> can call.</summary>
        protected bool CanRenameUser() => ResolveRenameUser() != null;

        /// <summary>
        /// Renames an account on whichever overload the server has. We compile against 10.11.5
        /// (RenameUser(User, string)); 10.11.11 and 12.0 replaced it with
        /// RenameUser(Guid, string, string), so the direct call threw MissingMethodException after the
        /// route had already written everything else. Same approach as
        /// <see cref="ChangePasswordCompat"/>. Throws if neither exists; callers check
        /// <see cref="CanRenameUser"/> before writing anything.
        /// </summary>
        protected Task RenameUserCompat(
            Jellyfin.Database.Implementations.Entities.User user, string newName)
        {
            var resolved = ResolveRenameUser()
                ?? throw new MissingMethodException("IUserManager has no RenameUser this plugin can call.");

            var args = resolved.GetParameters().Length == 3
                ? new object[] { user.Id, user.Username, newName }
                : new object[] { user, newName };
            return (Task)resolved.Invoke(_userManager, args)!;
        }

        // Kept in audit_log.json, not PluginConfiguration.xml, so a switch never rewrites the whole
        // configuration.

        /// <summary>
        /// How long a switch stays in the log. Entries name accounts, devices and IP addresses, and
        /// "newest 1000" alone kept a quiet server's for years. A fixed limit; a household wanting
        /// longer can download the log first.
        /// </summary>
        internal static readonly TimeSpan AuditLogMaxAge = TimeSpan.FromDays(90);

        /// <summary>Drops entries older than <see cref="AuditLogMaxAge"/>. Caller must hold AuditLogLock.</summary>
        private static void PruneAuditLogByAge(List<AuditLogEntry> logs)
            => logs.RemoveAll(l => l.Timestamp < DateTime.UtcNow - AuditLogMaxAge);

        /// <summary>
        /// Empties the log in memory and on disk. Behind the dashboard's delete button, which checks
        /// the caller is an administrator.
        /// </summary>
        protected bool ClearAuditLog()
        {
            lock (AuditLogLock)
            {
                // Memory is emptied only once the file is, so a failed write is reported and the dashboard
                // keeps showing what is on disk.
                var empty = new List<AuditLogEntry>();
                if (!WriteAuditLogs(empty)) return false;
                _auditCache = empty;
                return true;
            }
        }

        /// <summary>The log's file, or null when the plugin has no data folder yet.</summary>
        protected string? GetAuditLogPath()
        {
            if (AuditLogPath != null) return AuditLogPath;
            lock (AuditLogLock)
            {
                if (AuditLogPath != null) return AuditLogPath;

                // Plugin.Instance can be null on the first call, before the plugin finishes constructing.
                // Without this guard the first request threw instead of degrading.
                var instance = Plugin.Instance;
                if (instance == null)
                {
                    // Memory only. This once wrote to a file in the system temp folder, a forgotten second copy
                    // of usernames and addresses (R-1313).
                    _logger.LogError("ProfilesPlugin: Plugin instance unavailable; audit log will not persist.");
                    return null;
                }

                var folder = Path.Combine(instance.AppPaths.DataPath, "plugins", "ProfilesManagement");
                Directory.CreateDirectory(folder);
                AuditLogPath = Path.Combine(folder, "audit_log.json");
            }
            return AuditLogPath!;
        }

        // In-memory mirror of audit_log.json. Without it every switch re-read and re-parsed the whole
        // file (up to 1000 entries) on the request path, which users felt as a stall. Loaded once per
        // process.
        private static List<AuditLogEntry>? _auditCache;

        /// <summary>Reads the audit log from disk. Prefer <see cref="GetAuditLogs"/>, which caches.</summary>
        protected List<AuditLogEntry> ReadAuditLogs() => TryReadAuditLogs(out _);

        /// <param name="failed">True when the file exists but could not be read (locked, no
        /// permission). Such an empty result must not be cached or written back, or the next switch
        /// would replace the intact log.</param>
        private List<AuditLogEntry> TryReadAuditLogs(out bool failed)
        {
            failed = false;
            try
            {
                var path = GetAuditLogPath();
                if (path != null && System.IO.File.Exists(path))
                    return JsonSerializer.Deserialize<List<AuditLogEntry>>(
                        System.IO.File.ReadAllText(path)) ?? new List<AuditLogEntry>();
            }
            catch (JsonException ex)
            {
                // Read as empty, the next switch would write a one-entry log over the damaged one. It is
                // moved aside instead so it can still be looked at.
                _logger.LogError(ex, "ProfilesPlugin: Audit log is damaged; keeping it as audit_log.json.bad.");
                try
                {
                    var path = GetAuditLogPath();
                    if (path != null) System.IO.File.Move(path, path + ".bad", true);
                }
                catch (Exception moveEx)
                {
                    _logger.LogError(moveEx, "ProfilesPlugin: Could not move the damaged audit log aside.");
                    failed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Failed to read audit log.");
                failed = true;
            }
            return new List<AuditLogEntry>();
        }

        /// <summary>
        /// Audit entries, loaded from disk on the first call. Caller must hold AuditLogLock. When the
        /// read failed the list is returned uncached, so the next call retries and nothing is written
        /// meanwhile.
        /// </summary>
        private List<AuditLogEntry> GetAuditLogsLocked()
        {
            if (_auditCache != null) return _auditCache;
            var loaded = TryReadAuditLogs(out var failed);
            PruneAuditLogByAge(loaded);
            if (!failed) _auditCache = loaded;
            return loaded;
        }

        /// <summary>Snapshot of the audit log for read-only callers (the admin dashboard).</summary>
        protected List<AuditLogEntry> GetAuditLogSnapshot()
        {
            lock (AuditLogLock)
            {
                // Pruned here too: a server running for months with nobody switching would keep showing
                // expired entries.
                var logs = GetAuditLogsLocked();
                PruneAuditLogByAge(logs);
                return new List<AuditLogEntry>(logs);
            }
        }

        /// <summary>
        /// Writes the log through a temp file and a move, so a crash or full disk leaves the old file
        /// whole. False on failure; true also with no data folder (memory-only by design).
        /// </summary>
        protected bool WriteAuditLogs(List<AuditLogEntry> logs)
        {
            string? temp = null;
            try
            {
                var path = GetAuditLogPath();
                if (path == null) return true;
                temp = UniqueTempName(path);
                System.IO.File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = false }));
                MoveIntoPlace(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Failed to write audit log.");
                try { if (temp != null) System.IO.File.Delete(temp); } catch { }
                return false;
            }
        }

        protected void RecordAuditLog(string masterUsername, string targetUsername)
        {
            // Client-supplied and shown on the dashboard. The dashboard escapes it; the cap stops one
            // request writing a megabyte into a log kept on disk.
            var device = Cap(GetAuthorizationParameter("Device") ?? "Unknown Device");
            var client = Cap(GetAuthorizationParameter("Client") ?? "Unknown Client");

            static string Cap(string value) => value.Length > 128 ? value.Substring(0, 128) : value;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown IP";

            lock (AuditLogLock)
            {
                var logs = GetAuditLogsLocked();
                PruneAuditLogByAge(logs);
                logs.Add(new AuditLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    MasterUsername = masterUsername,
                    TargetUsername = targetUsername,
                    DeviceName = device,
                    Client = client,
                    IpAddress = ip
                });
                // Keep the newest 1000 in oldest-first order. Sorting descending (as this once did) flipped
                // the file on every trim, so later appends landed at the wrong end.
                if (logs.Count > 1000)
                {
                    _auditCache = logs.OrderByDescending(l => l.Timestamp)
                                      .Take(1000)
                                      .OrderBy(l => l.Timestamp)
                                      .ToList();
                }
                // The read failed and nothing is cached: saving now would replace the intact file with this
                // one entry. The switch itself is not held up.
                if (_auditCache == null)
                {
                    _logger.LogError("ProfilesPlugin: Audit log could not be read; this switch was not recorded.");
                    return;
                }
            }

            // Saved off the request thread; serializing and writing the whole file inline added disk I/O
            // to the click that opens the home screen. A hard crash can lose the latest entries, which is
            // fine for an informational log, and the in-memory copy still serves the dashboard.
            // Run through BackgroundWork so a stop waits for it: after an in-process restart the new
            // copy's AuditLogLock is a different object, and two copies writing one file corrupts it.
            _ = BackgroundWork.Run(_ =>
            {
                try
                {
                    lock (AuditLogLock)
                    {
                        // Write the log as it is now, not a copy from queue time: two writes can take the lock in
                        // either order, and the older copy going last left the file missing the newest entry.
                        if (_auditCache != null) WriteAuditLogs(_auditCache);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProfilesPlugin: Background audit log write failed.");
                }
            });
        }

        // ── Misc shared helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Whether this account can reach the server's own settings.
        /// <para>
        /// A helper rather than inline because <c>Policy.IsAdministrator</c> in a route body is how
        /// this codebase spells "administrators only", and tests/js/apidocs.js reads that token to
        /// check documented against enforced authorization. Using it for data made a user-level route
        /// look admin-only. Keep the token out of route bodies.
        /// </para>
        /// </summary>
        protected bool IsAdministratorAccount(Jellyfin.Database.Implementations.Entities.User user)
            => _userManager.GetUserDto(user, string.Empty).Policy?.IsAdministrator ?? false;

        /// <summary>
        /// Makes a profile's policy inherit its master's, on creation and on every switch.
        /// <para>
        /// A profile is a real Jellyfin account, so anything not written here is whatever a new user
        /// gets. This used to copy eight fields, so a master's restrictions (no remote access, an
        /// access schedule, a bitrate cap, no downloads, no Live TV) were missing from its profiles.
        /// Creating a profile was a way out of your own restrictions.
        /// </para>
        /// <para>Three kinds of field:</para>
        /// <list type="bullet">
        /// <item><description><b>Copied.</b> What the account may see and do: folders, tags, rating
        /// (re-clamped by the caller), transcoding, remote access, playback, downloads, Live TV,
        /// channels, devices, schedules.</description></item>
        /// <item><description><b>The stricter of the two.</b> Limits a profile may tighten but not
        /// loosen: bitrate, session count, SyncPlay, forced transcoding, blocked unrated
        /// items.</description></item>
        /// <item><description><b>Never more than the master.</b> Management permissions a new account
        /// gets by default: a profile keeps one only if its master has it.</description></item>
        /// </list>
        /// <para>Static so tests/cs/switchpolicy can drive it with real policies.</para>
        /// </summary>
        internal static void CopyUserPolicy(
            MediaBrowser.Model.Users.UserPolicy source,
            MediaBrowser.Model.Users.UserPolicy destination,
            Guid destinationUserId)
        {
            // ── copied ──
            destination.EnabledFolders = source.EnabledFolders;
            destination.EnableAllFolders = source.EnableAllFolders;
            destination.MaxParentalRating = source.MaxParentalRating;
            destination.MaxParentalSubRating = source.MaxParentalSubRating;
            destination.BlockedTags = source.BlockedTags;
            destination.AllowedTags = source.AllowedTags;
            destination.EnablePlaybackRemuxing = source.EnablePlaybackRemuxing;
            destination.EnableVideoPlaybackTranscoding = source.EnableVideoPlaybackTranscoding;
            destination.EnableAudioPlaybackTranscoding = source.EnableAudioPlaybackTranscoding;
            destination.EnableRemoteAccess = source.EnableRemoteAccess;
            destination.EnableMediaPlayback = source.EnableMediaPlayback;
            destination.EnableContentDownloading = source.EnableContentDownloading;
            destination.EnableSyncTranscoding = source.EnableSyncTranscoding;
            destination.EnableMediaConversion = source.EnableMediaConversion;
            destination.EnableLiveTvAccess = source.EnableLiveTvAccess;
            destination.EnableAllChannels = source.EnableAllChannels;
            destination.EnabledChannels = source.EnabledChannels ?? Array.Empty<Guid>();
            destination.BlockedChannels = source.BlockedChannels ?? Array.Empty<Guid>();
            destination.EnableAllDevices = source.EnableAllDevices;
            destination.EnabledDevices = source.EnabledDevices ?? Array.Empty<string>();

            // New rows owned by the profile. Handing Jellyfin the master's own rows would try to move
            // them, keyed by the master's id, onto another account.
            destination.AccessSchedules = (source.AccessSchedules ?? Array.Empty<AccessSchedule>())
                .Select(s => new AccessSchedule(s.DayOfWeek, s.StartHour, s.EndHour, destinationUserId))
                .ToArray();

            // ── the stricter of the two ──
            destination.RemoteClientBitrateLimit = StricterLimit(source.RemoteClientBitrateLimit, destination.RemoteClientBitrateLimit);
            destination.MaxActiveSessions = StricterLimit(source.MaxActiveSessions, destination.MaxActiveSessions);
            destination.ForceRemoteSourceTranscoding = source.ForceRemoteSourceTranscoding || destination.ForceRemoteSourceTranscoding;
            // CreateAndJoinGroups, JoinGroups, None: a larger value allows less.
            if ((int)source.SyncPlayAccess > (int)destination.SyncPlayAccess)
            {
                destination.SyncPlayAccess = source.SyncPlayAccess;
            }

            var masterUnrated = source.BlockUnratedItems;
            var profileUnrated = destination.BlockUnratedItems;
            destination.BlockUnratedItems = masterUnrated == null ? profileUnrated
                : profileUnrated == null ? masterUnrated
                : masterUnrated.Union(profileUnrated).ToArray();

            // ── never more than the master ──
            destination.IsAdministrator = false;
            destination.EnableUserPreferenceAccess &= source.EnableUserPreferenceAccess;
            destination.EnableRemoteControlOfOtherUsers &= source.EnableRemoteControlOfOtherUsers;
            destination.EnableSharedDeviceControl &= source.EnableSharedDeviceControl;
            destination.EnableLiveTvManagement &= source.EnableLiveTvManagement;
            // EnableContentDeletion means every folder; without it, only EnableContentDeletionFromFolders
            // (BaseItem.IsAuthorizedToDelete), and an empty list means none. That list is not copied, so
            // it is narrowed to the master's here.
            var destinationDeletesAnywhere = destination.EnableContentDeletion;
            destination.EnableContentDeletion &= source.EnableContentDeletion;
            destination.EnableCollectionManagement &= source.EnableCollectionManagement;
            destination.EnableSubtitleManagement &= source.EnableSubtitleManagement;
            destination.EnableLyricManagement &= source.EnableLyricManagement;
            destination.EnablePublicSharing &= source.EnablePublicSharing;
            if (!destination.EnableContentDeletion)
            {
                var masterFolders = source.EnableContentDeletionFromFolders ?? Array.Empty<string>();
                if (source.EnableContentDeletion)
                {
                    // The master deletes everywhere, so the profile's own list is no wider.
                    destination.EnableContentDeletionFromFolders ??= Array.Empty<string>();
                }
                else if (destinationDeletesAnywhere)
                {
                    destination.EnableContentDeletionFromFolders = masterFolders.ToArray();
                }
                else
                {
                    destination.EnableContentDeletionFromFolders =
                        (destination.EnableContentDeletionFromFolders ?? Array.Empty<string>())
                        .Intersect(masterFolders, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
        }

        /// <summary>The smaller of two limits where zero means "none".</summary>
        private static int StricterLimit(int a, int b)
        {
            if (a <= 0) return Math.Max(b, 0);
            if (b <= 0) return a;
            return Math.Min(a, b);
        }

        /// <summary>
        /// A profile's parental rating, never above its master's.
        /// <para>
        /// A rating is a score then a sub-score; a null score is no limit, and a null sub-score is no
        /// limit within that score. The switch path used to restore the profile's own rating after
        /// inheriting and never compare it with the master's, so tightening a master to PG-13 left
        /// its profiles where they were. The sub-score (new in 10.11) was never looked at.
        /// </para>
        /// </summary>
        internal static (int? Score, int? SubScore) ClampRating(
            int? profileScore, int? profileSubScore, int? masterScore, int? masterSubScore)
        {
            if (!masterScore.HasValue) return (profileScore, profileSubScore);

            if (!profileScore.HasValue || profileScore.Value > masterScore.Value)
            {
                return (masterScore, masterSubScore);
            }

            if (profileScore.Value < masterScore.Value || !masterSubScore.HasValue)
            {
                return (profileScore, profileSubScore);
            }

            // Same score: the sub-score may not be above the master's either.
            return (profileScore,
                profileSubScore.HasValue ? Math.Min(profileSubScore.Value, masterSubScore.Value) : masterSubScore);
        }

        /// <summary>
        /// Notes the calling device against the caller's household.
        /// <para>
        /// The recording lives in <see cref="Auth.DeviceRegistry"/> because this is no longer the
        /// only caller. A television never reaches a Bonfire route, so for three releases only the
        /// web switcher, which had no use for it, wrote the map. See
        /// <see cref="Auth.BonfireSessionListener"/>.
        /// </para>
        /// </summary>
        protected void RecordDeviceActivity()
        {
            var deviceId = GetAuthorizationParameter("DeviceId");
            if (string.IsNullOrWhiteSpace(deviceId)) return;

            // Attributed to the caller's master account so the picker can be scoped by ownership.
            // KnownDevices is one server-wide list; without this every household would see every
            // other's hardware.
            var callerId = GetCurrentUserId();
            var view = ReadConfigSnapshot();

            // Households only, as the session listener does: any other signed-in account could write
            // rows into the device list.
            if (callerId == null || !Auth.DeviceRegistry.IsHousehold(view, callerId.Value)) return;
            var ownerId = Auth.DeviceRegistry.HouseholdOf(view, callerId.Value);

            // Refresh only. A token is not a password: one issued by a PIN sign-in reaches here too and
            // must not make its device known. A password sign-in made the record.
            Auth.DeviceRegistry.RecordSightingAndSave(
                deviceId,
                GetAuthorizationParameter("Device"),
                GetAuthorizationParameter("Client"),
                ownerId,
                _logger,
                mayGrant: false);
        }

        // ── Device housekeeping ─────────────────────────────────────────────────────

        /// <summary>
        /// When LastSeen was last written to disk, for all devices at once. It was one entry per
        /// device, never pruned, and cost a configuration rewrite per active device per hour. Now
        /// every device's LastSeen rides on whichever save comes first. Read and set under ConfigLock.
        /// </summary>
        internal static DateTime LastSeenPersistedAt = DateTime.MinValue;

        internal static readonly TimeSpan DeviceLastSeenWriteInterval = TimeSpan.FromHours(1);

        /// <summary>How long a device may go unseen before it drops from the picker.
        /// <para>
        /// Left at 180 days on purpose. The worry was that a row per connection made the list
        /// unreadable, but the id does not rotate: jellyfin-web keeps it in <c>localStorage</c> under
        /// <c>_deviceId2</c> until site data is cleared. Duplicates come from one machine having
        /// several ids (second origin, second browser profile, reinstall). A shorter window would not
        /// fix those and would drop devices people still own, like a summer television or a tablet
        /// in a drawer. Merging is the fix; see <see cref="MergeDeviceRecords"/>.
        /// </para>
        /// </summary>
        private static readonly TimeSpan DeviceRetention = TimeSpan.FromDays(180);

        private static DateTime _lastDevicePrune = DateTime.MinValue;

        /// <summary>
        /// Drops devices unused for <see cref="DeviceRetention"/>, unless some profile still names
        /// them. True when anything was removed.
        /// <para>
        /// A whitelisted device is kept however old: removing it would widen that profile's access,
        /// because an empty <c>AllowedDeviceIds</c> means "any device". Tidying must never turn a
        /// restriction off.
        /// </para>
        /// <para>Caller must hold <see cref="ConfigLock"/>.</para>
        /// </summary>
        internal static bool PruneStaleDevices(PluginConfiguration config, DateTime now, ILogger? logger)
        {
            if (now - _lastDevicePrune < TimeSpan.FromDays(1)) return false;
            _lastDevicePrune = now;

            var removed = RemoveStaleDevices(config, now);
            if (removed > 0)
            {
                logger?.LogInformation(
                    "ProfilesPlugin: Dropped {Count} device(s) unseen for {Days} days.",
                    removed, (int)DeviceRetention.TotalDays);
            }
            return removed > 0;
        }

        /// <summary>
        /// The pruning with no throttle or logger, so a harness can drive it. Returns how many records
        /// were removed. Caller must hold ConfigLock.
        /// </summary>
        internal static int RemoveStaleDevices(PluginConfiguration config, DateTime now)
        {
            var whitelisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in config.Mappings)
                // Every other reader trims these and tolerates a null list; a padded id here let its device
                // row be pruned while a profile still named it.
                foreach (var id in m.AllowedDeviceIds ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(id)) whitelisted.Add(id.Trim());

            return config.KnownDevices.RemoveAll(d =>
                now - d.LastSeen > DeviceRetention
                && !whitelisted.Contains((d.DeviceId ?? "").Trim())
                // A merge keeps the old id as an alias, and a profile may still name it.
                && !(d.Aliases ?? new List<string>()).Any(a => !string.IsNullOrEmpty(a) && whitelisted.Contains(a.Trim())));
        }

        /// <summary>
        /// Counts one PIN attempt for the web switcher, or returns the 429 to send. Per address and
        /// profile, plus the account's one allowance across every door (<see cref="PinBudget"/>).
        /// Counted before the PIN is checked so concurrent guesses cannot all pass. Pair with
        /// <see cref="EndPinAttempt"/> on success.
        /// </summary>
        protected ActionResult? BeginPinAttempt(string addressKey, Guid profileId)
        {
            if (!RateLimiter.Pin.TryBegin(addressKey))
            {
                LogLockout(profileId, addressKey);
                return TooManyAttempts(RateLimiter.Pin, addressKey, "Too many failed PIN attempts.");
            }

            if (!PinBudget.TryBegin(profileId))
            {
                LogLockout(profileId, profileId.ToString("N"));
                return TooManyAttempts(PinBudget.RetryAfter(profileId), "Too many failed PIN attempts.");
            }

            return null;
        }

        /// <summary>A PIN locked by too many wrong tries: one Activity log line per lock. Shared with the apps' door, see <see cref="Auth.PinLockout"/>.</summary>
        protected void LogLockout(Guid userId, string key)
            => Auth.PinLockout.Raise(userId, key, HttpContext?.RequestServices, _userManager, _logger);

        /// <summary>The right PIN, or none needed: both allowances start again.</summary>
        protected static void EndPinAttempt(string addressKey, Guid profileId)
        {
            RateLimiter.Pin.Reset(addressKey);
            PinBudget.End(profileId);
        }

        /// <summary>
        /// Counts a master-PIN check on a profile edit, keyed on the account. These checks had no
        /// limit, so every edit endpoint was an unthrottled way to learn the master's PIN, which also
        /// opens the account on other apps and across a Bonfire.
        /// </summary>
        protected bool VerifyMasterPinLimited(string? pin, ProfileMapping masterMapping, PluginConfiguration config, out ActionResult? refusal)
        {
            // The same allowance as every other door to this PIN (PinBudget). It had its own, ten per
            // quarter hour with no daily cap, and a child reaches it through "more time".
            var mainId = masterMapping.ProfileUserId;
            if (!PinBudget.TryBegin(mainId))
            {
                LogLockout(mainId, mainId.ToString("N"));
                refusal = TooManyAttempts(PinBudget.RetryAfter(mainId), "Too many failed PIN attempts.");
                return false;
            }

            if (!VerifyPinAndUpgrade(pin, masterMapping))
            {
                refusal = BadRequest("Invalid Master PIN code.");
                return false;
            }

            PinBudget.End(mainId);
            refusal = null;
            return true;
        }

        /// <summary>
        /// A 429 that says how long the wait really is, with a <c>Retry-After</c> header so automated
        /// callers read the same number the person sees.
        /// <para>
        /// These used to say "please try again in 15 minutes", the window width, not the wait. The
        /// limiter slides and frees a slot when the oldest attempt ages out, so the real wait is
        /// usually much shorter.
        /// </para>
        /// </summary>
        internal ActionResult TooManyAttempts(RateLimiter limiter, string key, string what)
            => TooManyAttempts(limiter.RetryAfter(key), what);

        internal ActionResult TooManyAttempts(TimeSpan wait, string what)
        {
            if (wait > TimeSpan.Zero)
            {
                Response.Headers["Retry-After"] =
                    ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                what + " Please try again " + RateLimiter.DescribeWait(wait) + ".");
        }

        // ── Reading a submitted configuration ───────────────────────────────────────

        /// <summary>
        /// Finds a property on a JSON object, ignoring case.
        /// <para>
        /// An administrator should not have to know the casing of a configuration export. Jellyfin's
        /// REST serializer emits PascalCase, but a file that went through another tool can arrive
        /// camelCased, and binding straight to the type would read it as an empty configuration.
        /// </para>
        /// </summary>
        internal static bool TryProperty(JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind != JsonValueKind.Object) return false;

            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Deserializes a JSON element in either casing, returning null instead of throwing on
        /// anything malformed.
        /// </summary>
        internal static T? Read<T>(JsonElement element) where T : class
        {
            try
            {
                return element.Deserialize<T>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ── Device restrictions ─────────────────────────────────────────────────────

        /// <summary>Why a device was or was not allowed to switch into a profile.</summary>
        internal enum DeviceAccess
        {
            /// <summary>The profile names no devices, so every device may use it.</summary>
            NotRestricted,

            /// <summary>The calling device is on the profile's list.</summary>
            Allowed,

            /// <summary>The list is populated and this device is not on it. The intended refusal.</summary>
            DeniedNotOnList,

            /// <summary>
            /// The request carried no usable DeviceId, so the restriction could not be evaluated. Still a
            /// refusal, since an unidentifiable device must not pass, but reported as a different fault
            /// from the one above; they used to be one silent message.
            /// </summary>
            DeniedNoDeviceId,

            /// <summary>
            /// Every device on the list is gone: none of the ids is a device the server still knows.
            /// Nothing can match, so the profile is unreachable until the list is edited.
            /// <para>
            /// Still a refusal. Falling back to "allow anything" would turn a restriction off, which
            /// tidying must never do (the rule <see cref="RemoveStaleDevices"/> follows). The caller can
            /// say so instead of repeating "not allowed on this device" to someone whose only device no
            /// longer exists. The profile's owner is never device-restricted, so the list can be fixed
            /// from the edit form.
            /// </para>
            /// </summary>
            DeniedListIsStale
        }

        /// <summary>
        /// Whether <paramref name="deviceId"/> may switch into the profile in <paramref name="mapping"/>.
        /// <paramref name="knownDevices"/> is the server's device list, used only to tell a stale
        /// whitelist from an ordinary refusal; null skips that.
        /// <para>
        /// Pure and static so both call sites share one set of rules (each had a copy, and copies
        /// drift) and a harness can drive every outcome.
        /// </para>
        /// </summary>
        /// <param name="mappings">
        /// The configuration's mappings, to work out the profile's household and match a television's
        /// derived ids to its bare one (Auth.DeviceRegistry.IsSameDevice). Null compares ids exactly,
        /// as every caller did before 1.6.3.3, when a profile restricted to a television's signed-in
        /// row could not be entered from its sign-in screen.
        /// </param>
        internal static DeviceAccess EvaluateDeviceRestriction(
            ProfileMapping? mapping, string? deviceId, IEnumerable<KnownDevice>? knownDevices,
            IEnumerable<ProfileMapping>? mappings)
        {
            // A master account is never device-restricted; otherwise an owner could lock themselves out
            // of their own household.
            if (mapping == null
                || mapping.ProfileUserId == mapping.MasterUserId
                || mapping.AllowedDeviceIds == null)
            {
                return DeviceAccess.NotRestricted;
            }

            var allowed = mapping.AllowedDeviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToList();

            // An empty list means "any device", and so does one of only blank strings.
            if (allowed.Count == 0) return DeviceAccess.NotRestricted;

            var candidate = deviceId?.Trim();
            if (string.IsNullOrEmpty(candidate)) return DeviceAccess.DeniedNoDeviceId;

            if (allowed.Any(id => string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return DeviceAccess.Allowed;
            }

            // An id merged into an allowed device is that device (R-1049).
            if (Auth.DeviceRegistry.Row(knownDevices, candidate) is KnownDevice merged
                && !string.Equals(merged.DeviceId?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)
                && allowed.Any(id => string.Equals(id, merged.DeviceId?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return DeviceAccess.Allowed;
            }

            if (mappings != null)
            {
                // The household, master included: whoever is signed in on the television is the account
                // its derived id was built over.
                var members = mappings
                    .Where(m => m.MasterUserId == mapping.MasterUserId)
                    .Select(m => m.ProfileUserId)
                    .Append(mapping.MasterUserId)
                    .Distinct()
                    .ToList();
                var known = knownDevices?.ToList();

                // The names the household gave its profiles: Android TV signs in through
                // sha1(bare id + "+" + the typed name), and that session's row is one an administrator can
                // tick.
                var names = mappings
                    .Where(m => m.MasterUserId == mapping.MasterUserId
                                && m.ProfileUserId != m.MasterUserId
                                && !string.IsNullOrWhiteSpace(m.ProfileName))
                    .Select(m => m.ProfileName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (allowed.Any(id => Auth.DeviceRegistry.IsSameDevice(candidate, id, members, known, names)))
                {
                    return DeviceAccess.Allowed;
                }
            }

            if (knownDevices != null)
            {
                var known = knownDevices
                    .Select(d => d.DeviceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!allowed.Any(known.Contains)) return DeviceAccess.DeniedListIsStale;
            }

            return DeviceAccess.DeniedNotOnList;
        }

        /// <summary>
        /// The profiles whose device list would be left empty, and so unrestricted, if every id in
        /// <paramref name="removingIds"/> were dropped.
        /// <para>
        /// An empty <c>AllowedDeviceIds</c> means "any device", so removing the last entry switches
        /// the restriction off. Forgetting a device is explicit, but the widening is as silent, so
        /// callers refuse and name the profiles in the way. Same rule as
        /// <see cref="RemoveStaleDevices"/>, from the other side.
        /// </para>
        /// </summary>
        internal static List<Guid> ProfilesLeftUnrestrictedBy(
            PluginConfiguration config, IEnumerable<string> removingIds)
            => HouseholdProfilesLeftUnrestrictedBy(config, removingIds, null);

        internal static List<Guid> HouseholdProfilesLeftUnrestrictedBy(
            PluginConfiguration config, IEnumerable<string> removingIds, Guid? householdId)
        {
            var removing = removingIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var affected = new List<Guid>();
            if (removing.Count == 0) return affected;

            foreach (var m in config.Mappings)
            {
                if (m.AllowedDeviceIds == null) continue;
                // Only that household's profiles, when one is named (R-1048).
                if (householdId.HasValue && m.MasterUserId != householdId.Value) continue;

                var current = m.AllowedDeviceIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .ToList();

                // Not restricted to begin with, so nothing can be widened.
                if (current.Count == 0) continue;

                if (current.All(removing.Contains)) affected.Add(m.ProfileUserId);
            }

            return affected;
        }

        /// <summary>
        /// Folds <paramref name="fromId"/> into <paramref name="intoId"/>: every profile that allowed
        /// the old device now allows the new one, and the old record is dropped. False when there was
        /// nothing to merge. Caller must hold ConfigLock.
        /// <para>
        /// Why one machine has several records is explained on <see cref="GroupLikelyDuplicates"/>.
        /// </para>
        /// <para>
        /// Additive on the whitelist: <paramref name="intoId"/> is added before
        /// <paramref name="fromId"/> is removed, so no list is ever empty and no restriction is
        /// switched off midway.
        /// </para>
        /// </summary>
        internal static bool MergeDeviceRecords(PluginConfiguration config, string? fromId, string? intoId)
            => MergeDeviceRecordsCore(config, fromId, intoId, true);

        /// <param name="proven">False for a merge nobody vouched for (the time rule in
        /// <see cref="Auth.DeviceRegistry.MergeSameDevices"/>): the old id is not kept as an alias,
        /// because every device list the new row is on honors an alias (R2-02-01).</param>
        internal static bool MergeDeviceRecordsCore(PluginConfiguration config, string? fromId, string? intoId, bool proven)
        {
            fromId = fromId?.Trim();
            intoId = intoId?.Trim();

            if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(intoId)) return false;
            if (string.Equals(fromId, intoId, StringComparison.OrdinalIgnoreCase)) return false;

            var source = config.KnownDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, fromId, StringComparison.OrdinalIgnoreCase));
            var target = config.KnownDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, intoId, StringComparison.OrdinalIgnoreCase));

            if (source == null || target == null) return false;

            foreach (var m in config.Mappings)
            {
                if (m.AllowedDeviceIds == null) continue;

                var allowsSource = m.AllowedDeviceIds.Any(id =>
                    string.Equals(id?.Trim(), fromId, StringComparison.OrdinalIgnoreCase));
                if (!allowsSource) continue;

                var allowsTarget = m.AllowedDeviceIds.Any(id =>
                    string.Equals(id?.Trim(), intoId, StringComparison.OrdinalIgnoreCase));

                // Add first, remove second. The other order empties a single-entry list in between, and an
                // empty list means "any device" to everything that reads it.
                if (!allowsTarget) m.AllowedDeviceIds.Add(target.DeviceId);

                m.AllowedDeviceIds.RemoveAll(id =>
                    string.Equals(id?.Trim(), fromId, StringComparison.OrdinalIgnoreCase));
            }

            // The surviving record keeps the better details: the later sighting and a real name over a
            // placeholder.
            if (source.LastSeen > target.LastSeen) target.LastSeen = source.LastSeen;
            // A name an administrator typed outranks one a client reported (R-1055).
            if (source.NameIsCustom && !target.NameIsCustom)
            {
                target.DeviceName = source.DeviceName;
                target.NameIsCustom = true;
            }
            else if (IsPlaceholderDeviceName(target.DeviceName) && !IsPlaceholderDeviceName(source.DeviceName))
                target.DeviceName = source.DeviceName;
            if (string.IsNullOrWhiteSpace(target.Client) && !string.IsNullOrWhiteSpace(source.Client))
                target.Client = source.Client;
            if (target.MasterUserId == Guid.Empty) target.MasterUserId = source.MasterUserId;
            // One television seen under two ids plays if either did.
            if (source.CanPlay == true || (target.CanPlay == null && source.CanPlay != null)) target.CanPlay = source.CanPlay;
            // Every household that used either id still owns the one left (R-1050).
            target.Owners = Auth.DeviceRegistry.AddOwners(target.Owners,
                new[] { target.MasterUserId, source.MasterUserId }.Concat(source.Owners ?? new List<Guid>()).ToArray());

            // The old id is the same machine at another address and will be back. Kept as an alias so
            // its next request is this record, on every list this one is on (R-1049). Newest last; the
            // oldest go past the cap. Not for an unproven merge (see <paramref name="proven"/>): the old
            // id is forgotten, and if it returns it is a new row that merges again once it qualifies.
            var aliases = (target.Aliases ?? new List<string>())
                .Concat(proven ? source.Aliases ?? new List<string>() : new List<string>())
                .Concat(proven ? new[] { source.DeviceId } : new string[0])
                .Select(a => a?.Trim() ?? string.Empty)
                .Where(a => a.Length > 0 && !string.Equals(a, target.DeviceId?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(KnownDevice.MaxAliases)
                .Reverse()
                .ToList();
            target.Aliases = aliases;

            config.KnownDevices.Remove(source);
            return true;
        }

        /// <summary>
        /// Groups devices that are almost certainly one machine, so an administrator can merge them
        /// in one action.
        /// <para>
        /// One machine has several records because jellyfin-web keeps its device id in
        /// <c>localStorage</c> under <c>_deviceId2</c>, per origin. The LAN address and a domain name
        /// mint two, as do a new browser profile, cleared site data or a reinstall.
        /// </para>
        /// <para>
        /// That id is <c>btoa(navigator.userAgent + '|' + Date.now())</c>, so two ids sharing a long
        /// prefix have byte-identical user agents: the same browser version on the same OS. With the
        /// same client and reported name, that is as good as client-supplied data gets.
        /// </para>
        /// <para>
        /// Still only a suggestion; nothing here merges on its own. These are ids in use side by
        /// side (two origins, two browser profiles), which Auth.DeviceRegistry.MergeSameDevices
        /// leaves apart. Cleared site data is an old id gone silent, and that one merges
        /// automatically. Two people with the same phone model and browser version would share a
        /// group, and merging them would widen a whitelist, which device housekeeping must never do
        /// alone. Grouping is scoped to one household, and then a person confirms.
        /// </para>
        /// </summary>
        internal static List<List<KnownDevice>> GroupLikelyDuplicates(IEnumerable<KnownDevice> devices)
        {
            // Long enough to rule out base64 alignment coincidence: 32 characters is 24 bytes of user
            // agent, well past "Mozilla/5.0 (" and into the platform.
            const int MinSharedPrefix = 32;

            var groups = new List<List<KnownDevice>>();

            foreach (var byName in devices
                .Where(d => !string.IsNullOrEmpty(d.DeviceId))
                .GroupBy(d => (d.DeviceName ?? string.Empty).Trim().ToLowerInvariant()
                              + "\0" + (d.Client ?? string.Empty).Trim().ToLowerInvariant()))
            {
                var candidates = byName.OrderByDescending(d => d.LastSeen).ToList();
                if (candidates.Count < 2) continue;

                // Within one name+client set, split again by shared id prefix so different machines that
                // report the same name are not swept together.
                while (candidates.Count > 0)
                {
                    var head = candidates[0];
                    var group = new List<KnownDevice> { head };
                    candidates.RemoveAt(0);

                    for (var i = candidates.Count - 1; i >= 0; i--)
                    {
                        var shared = FirstDifferingIndex(new[] { head.DeviceId, candidates[i].DeviceId });
                        if (shared >= MinSharedPrefix)
                        {
                            group.Add(candidates[i]);
                            candidates.RemoveAt(i);
                        }
                    }

                    if (group.Count > 1) groups.Add(group);
                }
            }

            return groups;
        }

        /// <summary>Names that carry no information, and so must never suppress a fallback.</summary>
        internal static bool IsPlaceholderDeviceName(string? name)
            => string.IsNullOrWhiteSpace(name)
               || string.Equals(name.Trim(), "Unknown Device", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Makes every device in the list readable on its own and distinct from the others.
        /// <para>
        /// The picker rendered several identical rows: "Unknown Device" for every client that sent no
        /// name, and one "Chrome" per browser origin for a single computer. A row you cannot tell
        /// from the one above is not a choice.
        /// </para>
        /// <para>
        /// Two passes. A placeholder name falls back to the client; any label still shared gets a
        /// short piece of the device id. The id is unique but unreadable, so it is the last resort.
        /// </para>
        /// </summary>
        internal static List<KnownDevice> DisambiguateDeviceNames(List<KnownDevice> input)
        {
            // Works on copies. It used to rename what it was given, so live records would have stored a
            // display name as the real one. R-1116.
            var devices = input.Select(d => new KnownDevice
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                Client = d.Client,
                LastSeen = d.LastSeen,
                MasterUserId = d.MasterUserId,
                NameIsCustom = d.NameIsCustom,
                Owners = new List<Guid>(d.Owners ?? new List<Guid>())
            }).ToList();

            foreach (var d in devices)
            {
                // The row ScopeDevicesToHousehold makes for a whitelisted device with no record left. The
                // English stays for older clients; the codes let the client translate it.
                if (d.LastSeen == DateTime.MinValue
                    && d.DeviceName == "Previously allowed device" && d.Client == "Not seen recently")
                {
                    d.NameCode = "previouslyAllowed";
                    d.ClientCode = "notSeenRecently";
                    continue;
                }

                if (!IsPlaceholderDeviceName(d.DeviceName)) continue;

                d.DeviceName = !string.IsNullOrWhiteSpace(d.Client)
                               && !string.Equals(d.Client.Trim(), "Unknown Client", StringComparison.OrdinalIgnoreCase)
                    ? d.Client.Trim()
                    : "Unrecognised device";
                if (d.DeviceName == "Unrecognised device") d.NameCode = "unrecognised";
            }

            var byLabel = devices
                .GroupBy(d => DeviceLabel(d), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var group in byLabel)
            {
                var ids = group.Select(d => d.DeviceId ?? string.Empty).ToList();
                var from = FirstDifferingIndex(ids);

                foreach (var d in group)
                {
                    var hint = IdFragment(d.DeviceId, from);
                    if (!string.IsNullOrEmpty(hint)) d.DeviceName += " (" + hint + ")";
                }
            }

            return devices;
        }

        /// <summary>
        /// The first character position at which these ids stop agreeing.
        /// <para>
        /// A jellyfin-web device id is <c>btoa(navigator.userAgent + '|' + Date.now())</c>, so every
        /// id one browser mints starts with the same forty-odd characters. A fragment from the front
        /// gave four picker rows all reading "Chrome (TW96aW)", base64 for "Mozil". The back is no
        /// better: timestamps end in zeros. So the fragment comes from where the group differs,
        /// computed per group.
        /// </para>
        /// </summary>
        internal static int FirstDifferingIndex(IReadOnlyList<string> ids)
        {
            if (ids.Count < 2) return 0;

            var shortest = ids.Min(s => s?.Length ?? 0);
            for (var i = 0; i < shortest; i++)
            {
                var c = ids[0][i];
                foreach (var id in ids)
                {
                    if (char.ToLowerInvariant(id[i]) != char.ToLowerInvariant(c)) return i;
                }
            }
            // One id is a prefix of another, or they are equal; the end of the shortest is the first
            // place they can be told apart.
            return shortest;
        }

        /// <summary>Six characters of an id from <paramref name="from"/>, or as near as it reaches.</summary>
        internal static string IdFragment(string? deviceId, int from)
        {
            if (string.IsNullOrEmpty(deviceId)) return string.Empty;

            const int Width = 6;
            // Back off the window instead of running past the end, so a short id still yields a
            // fragment.
            var start = Math.Min(from, Math.Max(0, deviceId.Length - Width));
            return deviceId.Substring(start, Math.Min(Width, deviceId.Length - start));
        }

        /// <summary>
        /// Undoes URL encoding left in a stored device name by the parser used before 1.5.10, which
        /// returned header values verbatim. A phone recorded as <c>Pixel+8</c> keeps that name until
        /// it contacts the server again.
        /// <para>
        /// Applied only when the value looks encoded: a <c>%</c> escape, or a plus in a value with no
        /// spaces. "Pixel 8 Pro+" was typed by someone and must survive; "Pixel+8" was not.
        /// </para>
        /// </summary>
        internal static string DecodeLegacyDeviceName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var looksEncoded = Regex.IsMatch(name, "%[0-9A-Fa-f]{2}")
                               || (name.IndexOf('+') >= 0 && name.IndexOf(' ') < 0);
            if (!looksEncoded) return name;

            try
            {
                // A plus at either end cannot be an encoded space between words: "TV+" is a name,
                // "Pixel+8" is not.
                var protectedName = name;
                if (protectedName.EndsWith("+", StringComparison.Ordinal))
                {
                    protectedName = protectedName.Substring(0, protectedName.Length - 1) + "%2B";
                }
                if (protectedName.StartsWith("+", StringComparison.Ordinal))
                {
                    protectedName = "%2B" + protectedName.Substring(1);
                }
                var decoded = WebUtility.UrlDecode(protectedName);
                return string.IsNullOrWhiteSpace(decoded) ? name : decoded;
            }
            catch (Exception)
            {
                return name;
            }
        }

        /// <summary>What a device reads as in the picker: the name, qualified by its client.</summary>
        internal static string DeviceLabel(KnownDevice d)
            => string.IsNullOrWhiteSpace(d.Client)
               || string.Equals(d.DeviceName?.Trim(), d.Client.Trim(), StringComparison.OrdinalIgnoreCase)
                ? (d.DeviceName ?? string.Empty).Trim()
                : (d.DeviceName ?? string.Empty).Trim() + " · " + d.Client.Trim();

        /// <summary>The message sent to the client for a refusal. Never used for an allow.</summary>
        internal static string DeviceAccessMessage(DeviceAccess access) => access switch
        {
            DeviceAccess.DeniedNoDeviceId =>
                "This device did not identify itself, so it cannot be checked against the profile's device list.",
            DeviceAccess.DeniedListIsStale =>
                "None of this profile's allowed devices are known to the server any more. Edit the profile to choose a device again.",
            _ => "This profile is not allowed on this device."
        };

        /// <summary>
        /// Selects the devices a master account may see in the "Allowed Devices" picker.
        /// <para>
        /// (1) Only devices recorded as owned by this master. KnownDevices is one server-wide list,
        /// so anything looser leaks other households' hardware. Unowned (Guid.Empty) records must not
        /// count as visible: on an install from before ownership, every record is unowned.
        /// (2) A device already on one of this account's whitelists is always listed, even switched
        /// off or aged out. The edit form rebuilds AllowedDeviceIds from the checkboxes it rendered,
        /// so an omitted device is dropped on save, and an emptied whitelist restricts nothing.
        /// </para>
        /// Pure and static so the rules can be tested directly.
        /// </summary>
        protected static List<KnownDevice> ScopeDevicesToHousehold(
            IEnumerable<KnownDevice> knownDevices,
            Guid masterUserId,
            ISet<string> whitelistedDeviceIds)
        {
            // Device ids are compared ignoring case everywhere else; here it depended on the comparer
            // of whatever set the caller built. R-1115.
            whitelistedDeviceIds = new HashSet<string>(
                whitelistedDeviceIds ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);

            var devices = knownDevices
                // A whitelist is whatever the client posted, so it vouches only for a record nobody else
                // owns: a typed-in id must not return another household's device name and last-seen.
                .Where(d => d.OwnedBy(masterUserId)
                            || (whitelistedDeviceIds.Contains(d.DeviceId)
                                && d.MasterUserId == Guid.Empty
                                && !(d.Owners?.Count > 0)))
                // A service that signed in as a user is not somebody's device (see
                // KnownDevice.CanPlay), unless a profile is already restricted to it.
                .Where(d => Auth.DeviceRegistry.IsListedFor(d, whitelistedDeviceIds))
                .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(d => d.LastSeen).First())
                .OrderByDescending(d => d.LastSeen)
                .ToList();

            // Rule (2) for a whitelisted device with no KnownDevices record left at all.
            var present = devices.Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var orphanId in whitelistedDeviceIds.Where(id => !present.Contains(id)))
            {
                devices.Add(new KnownDevice
                {
                    DeviceId = orphanId,
                    DeviceName = "Previously allowed device",
                    Client = "Not seen recently",
                    LastSeen = DateTime.MinValue,
                    MasterUserId = masterUserId
                });
            }

            return devices;
        }

        protected HashSet<Guid> GetLinkedMasterUserIds(Guid masterUserId, PluginConfiguration config)
        {
            var linked = new HashSet<Guid> { masterUserId };
            lock (ConfigLock)
            {
                foreach (var g in config.BonfireGroups.Where(g => g.OwnerUserId == masterUserId))
                    foreach (var id in g.MemberUserIds) linked.Add(id);
                foreach (var g in config.BonfireGroups.Where(g => g.MemberUserIds.Contains(masterUserId)))
                {
                    linked.Add(g.OwnerUserId);
                    foreach (var id in g.MemberUserIds) linked.Add(id);
                }
            }
            return linked;
        }

        /// <summary>
        /// The accounts shown in <paramref name="householdId"/>'s switcher (issue #39) that still
        /// exist, each with whether it is an administrator, for <see cref="EvaluateSwitch(IReadOnlyList{ProfileMapping}, Guid, Guid, ICollection{Guid}, bool, IReadOnlyDictionary{Guid, bool}?)"/>.
        /// Read live, so an account made an administrator after it was added counts as one.
        /// </summary>
        protected Dictionary<Guid, bool> LinkedAccountsFor(Guid householdId, PluginConfiguration config)
        {
            List<Guid> ids;
            lock (ConfigLock)
            {
                ids = AccountLinks.In(config, householdId);
            }

            var result = new Dictionary<Guid, bool>();
            foreach (var id in ids)
            {
                var user = _userManager.GetUserById(id);
                if (user != null) result[id] = IsAdministratorAccount(user);
            }

            return result;
        }

        /// <summary>
        /// Signs out every device that came into another household through a door since closed: a
        /// kick, a leave, a deleted Bonfire, a removed link, or the owner hiding its profiles
        /// (R-1302). Each entry is asked what /switch would ask now, on the home network so a LAN-only
        /// consent still counts as a door; a refused one is signed out on that device only and
        /// forgotten. Never throws: the change that called it is already saved.
        /// </summary>
        protected async Task<int> SignOutClosedEntriesAsync()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return 0;

            List<CrossHouseholdEntry> entries;
            List<ProfileMapping> mappings;
            lock (ConfigLock)
            {
                entries = config.CrossHouseholdEntries?.Select(e => e.Copy()).ToList() ?? new List<CrossHouseholdEntry>();
                mappings = config.Mappings.ToList();
            }
            if (entries.Count == 0) return 0;

            var closed = new List<CrossHouseholdEntry>();
            foreach (var e in entries)
            {
                var rules = EvaluateSwitch(mappings, e.EnteredBy, e.ProfileUserId,
                    GetLinkedMasterUserIds(e.EnteredBy, config), isLocal: true, LinkedAccountsFor(e.EnteredBy, config));
                if (rules.Unauthorized || rules.Refusal != null) closed.Add(e);
                // In on the owner's home-network consent alone, which has since been withdrawn.
                else if (e.WithoutPin && !string.IsNullOrEmpty(rules.Target?.PinHash) && !rules.HouseholdLanBypass) closed.Add(e);
            }
            return await SignOutEntriesAsync(config, closed).ConfigureAwait(false);
        }

        /// <summary>
        /// A profile or account got a new PIN: every device another household opened it on with a
        /// PIN, or with none while it had none, is signed out (R-1302b). Knowing the old PIN is all
        /// those proved. Entries in on the owner's home-network consent stay, since it still stands.
        /// Call after the change is saved, and only for a PIN set to a value: clearing one opens a
        /// door. Never throws.
        /// </summary>
        protected async Task<int> SignOutAfterPinChangeAsync(Guid profileUserId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return 0;

            List<CrossHouseholdEntry> closed;
            lock (ConfigLock)
            {
                closed = config.CrossHouseholdEntries?.Where(e => e.ProfileUserId == profileUserId && !e.WithoutPin)
                    .Select(e => e.Copy()).ToList() ?? new List<CrossHouseholdEntry>();
            }
            return await SignOutEntriesAsync(config, closed).ConfigureAwait(false);
        }

        private async Task<int> SignOutEntriesAsync(PluginConfiguration config, List<CrossHouseholdEntry> closed)
        {
            if (closed.Count == 0) return 0;

            lock (ConfigLock)
            {
                config.CrossHouseholdEntries?.RemoveAll(e => closed.Any(c => c.ProfileUserId == e.ProfileUserId
                    && string.Equals(c.DeviceId, e.DeviceId, StringComparison.Ordinal)));
                Plugin.Instance?.SaveConfiguration();
            }

            int signedOut = 0;
            foreach (var e in closed)
            {
                try
                {
                    // The (account, device) pair: a device id alone would sign every account on that television
                    // out, its own household's included.
                    var devices = _deviceManager?.GetDevices(new Jellyfin.Data.Queries.DeviceQuery { DeviceId = e.DeviceId, UserId = e.ProfileUserId }).Items;
                    if (devices == null) continue;
                    foreach (var device in devices)
                    {
                        await _sessionManager.Logout(device).ConfigureAwait(false);
                        signedOut++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ProfilesPlugin: could not sign {ProfileId} out on device {DeviceId}.", e.ProfileUserId, e.DeviceId);
                }
            }

            if (signedOut > 0)
            {
                _logger.LogInformation("ProfilesPlugin: signed out {Count} device(s) that had come in from another household.", signedOut);
            }
            return signedOut;
        }

        protected int GetMaxProfilesForUser(Guid userId, PluginConfiguration config)
        {
            lock (ConfigLock)
            {
                var ov = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == userId);
                return ov?.MaxProfiles ?? config.MaxProfilesPerUser;
            }
        }

        /// <summary>
        /// The size a picture saved now must fit: the administrator's picture quality setting
        /// (<see cref="ImageQualities"/>). Standard is the 2 MB it always was.
        /// </summary>
        protected static int MaxProfileImageBytes
            => ImageQualities.For(Plugin.Instance?.Configuration?.ProfileImageQuality).MaxBytes;

        /// <summary>
        /// Streams an image from disk with a cache validator.
        /// <para>
        /// The four image endpoints used to read the whole file into memory per request. The gate
        /// renders every avatar in the household at once, so opening it allocated all of them, and a
        /// 2 MB picture goes straight to the large object heap, which is not compacted by default.
        /// <c>PhysicalFile</c> hands the path to <c>SendFileAsync</c>, which streams without the copy.
        /// </para>
        /// <para>
        /// The validator is length plus last-write time, not a content hash, which would mean reading
        /// the whole file to avoid reading it. It changes whenever the image does. A browser that has
        /// the picture gets a 304 and no body; these are requested constantly because the gate is
        /// drawn on every page load.
        /// </para>
        /// <para>
        /// <c>private</c>, not <c>public</c>: the endpoints are anonymous so the gate can render before
        /// sign-in, but the images are one household's faces and do not belong in a shared proxy cache.
        /// </para>
        /// </summary>
        protected ActionResult ImageFileResult(string path, string contentType)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return NotFound();
            }
            catch (Exception ex)
            {
                // Deleted between being found and being served, or unreadable.
                _logger.LogWarning(ex, "ProfilesPlugin: Could not stat image {Path}.", path);
                return NotFound();
            }

            var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                "\"" + info.Length.ToString("x", CultureInfo.InvariantCulture)
                + "-" + info.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"");

            Response.Headers["Cache-Control"] = "private, max-age=3600";

            // This overload answers If-None-Match and If-Modified-Since itself, so no branch here can
            // drift from it.
            return PhysicalFile(path, contentType, info.LastWriteTimeUtc, etag);
        }

        // Bounds for the server-wide limit and the per-account override. The upper bound was checked
        // nowhere, so the settings page saved 2,000,000,000 and the gate tried to lay out that many
        // tiles. Past about a dozen is more than "Who's Watching?" can show on a TV without
        // scrolling.
        protected const int MinProfilesPerUser = 1;
        protected const int MaxProfilesPerUserLimit = 20;

        /// <summary>
        /// Null when <paramref name="value"/> is within bounds, else the message to return. It names
        /// the bound, so the administrator knows what to type next.
        /// </summary>
        protected static string? ValidateProfileLimit(int value) =>
            value < MinProfilesPerUser || value > MaxProfilesPerUserLimit
                ? $"Maximum profiles must be between {MinProfilesPerUser} and {MaxProfilesPerUserLimit}."
                : null;

        /// <summary>
        /// Minimum length of the emergency disable code. It is submitted with no authentication, so
        /// length does the work a login would. The rate limiter caps guessing at five an hour, but a
        /// short code would still fall.
        /// </summary>
        protected const int MinPanicCodeLength = 10;

        // Avatar colors and image URLs are stored server-side and rendered on other accounts'
        // switcher screens via Bonfire groups. They are validated here so a hostile value never
        // reaches the client; profiles.js validates again on render.

        private static readonly System.Text.RegularExpressions.Regex HexColorRegex =
            new("^#[0-9a-fA-F]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        internal const string DefaultAvatarColor = "#00A4DC";

        /// <summary>
        /// Library ids from a request: valid ids kept, the rest dropped. Null stays null, meaning
        /// "inherit the master's libraries", which is not an empty list. See
        /// <see cref="Models.CreateProfileRequest.EnabledFolders"/>.
        /// </summary>
        internal static List<Guid>? ParseFolderIds(IEnumerable<string>? ids)
        {
            if (ids == null) return null;

            return ids
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
        }

        /// <summary>The longest name a household may give a profile.</summary>
        internal const int MaxProfileNameLength = 32;

        // The characters Jellyfin accepts in a username, since the name becomes part of one.
        private static readonly Regex ProfileNameRegex = new(@"^[\w\-'.@+ ]+$", RegexOptions.Compiled);

        /// <summary>
        /// Why a profile name cannot be used, or null. Nothing checked it before: a null threw a 500,
        /// an empty one created an account called <c>master_</c>, a character Jellyfin refuses in a
        /// username failed inside <c>CreateUserAsync</c> as a 500 with no message, and a long name
        /// went unchecked into every screen showing it, other households' switchers included.
        /// </summary>
        internal static string? ValidateProfileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the profile.";

            var trimmed = name.Trim();
            if (trimmed.Length > MaxProfileNameLength)
                return $"A profile name can be at most {MaxProfileNameLength} characters.";
            if (!ProfileNameRegex.IsMatch(trimmed))
                return "A profile name can use letters, numbers, spaces and - ' . _ @ +.";

            return null;
        }

        /// <summary>
        /// Why <paramref name="name"/> cannot be a profile's name because an account already uses it,
        /// or null (R-1336). Jellyfin's <c>AuthenticateUser</c> looks a typed name up with
        /// <c>GetUserByName</c> before asking any provider, so a "Bob" tile signs in as the real Bob:
        /// it can never open, and each wrong PIN counts against Bob. <paramref name="except"/> is an
        /// account allowed to keep its own name.
        /// </summary>
        internal static string? ValidateProfileNameFree(IUserManager users, string? name, Guid? except = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var taken = users.GetUserByName(name.Trim());
            return taken != null && taken.Id != except ? "A user with this name already exists." : null;
        }

        /// <summary>
        /// A PIN as stored: four to eight ASCII digits. <c>char.IsDigit</c> also accepts other
        /// scripts' digits, which no television remote can type and the sign-in provider never tries.
        /// </summary>
        internal static bool IsValidPin(string pin)
            => pin.Length >= 4 && pin.Length <= 8 && pin.All(char.IsAsciiDigit);

        /// <summary>The inactivity lock, in minutes: zero (off) to a day.</summary>
        internal static int ClampLockoutMinutes(int minutes) => Math.Clamp(minutes, 0, 24 * 60);

        /// <summary>
        /// The color if it is a plain 6-digit hex triplet, else the default. Anything else could break
        /// out of the <c>style="..."</c> attribute it lands in.
        /// </summary>
        protected static string SanitizeAvatarColor(string? color)
            => IsAvatarColor(color) ? color!.Trim() : DefaultAvatarColor;

        internal static bool IsAvatarColor(string? color)
            => !string.IsNullOrWhiteSpace(color) && HexColorRegex.IsMatch(color.Trim());

        /// <summary>
        /// The colors offered in the profile form, in order. profiles.js has the same list;
        /// tests/js/avatarcolors.js checks they match.
        /// </summary>
        internal static readonly string[] AvatarPalette =
        {
            "#00A4DC", "#E50914", "#22C55E", "#EAB308", "#A855F7", "#EC4899",
            "#F97316", "#06B6D4", "#3B82F6", "#10B981", "#6366F1", "#8B5CF6",
            "#D946EF", "#F43F5E", "#14B8A6", "#F59E0B", "#84CC16", "#64748B",
        };

        /// <summary>
        /// The first palette color nobody in the household uses, so a new tile does not look like the
        /// last (every profile used to start blue). With the palette used up, it wraps.
        /// </summary>
        internal static string NextAvatarColor(Guid masterUserId, PluginConfiguration config)
        {
            var rows = config.Mappings
                .Where(m => m.MasterUserId == masterUserId || m.ProfileUserId == masterUserId)
                .ToList();
            var used = new HashSet<string>(rows.Select(m => (m.AvatarColor ?? string.Empty).Trim()),
                StringComparer.OrdinalIgnoreCase);
            // A main account with no row of its own is drawn in the default colour.
            if (!rows.Any(m => m.ProfileUserId == masterUserId)) used.Add(DefaultAvatarColor);

            return AvatarPalette.FirstOrDefault(c => !used.Contains(c))
                   ?? AvatarPalette[rows.Count % AvatarPalette.Length];
        }

        /// <summary>
        /// Validates an external image URL: absolute http(s) only. A bare "starts with http" check
        /// would pass <c>http" onerror="...</c> into an img tag.
        /// </summary>
        protected static bool IsValidImageUrl(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               // Also refuses ) ; \ space { } (R-1110): the URL lands inside a CSS url(...) in places, where
               // any of them would end the value or start a rule. Checked on the trimmed value, as stored.
               && value.Trim().IndexOfAny(new[] { '"', '\'', '<', '>', ')', ';', '\\', ' ', '{', '}' }) < 0
               // Uri.TryCreate drops a tab, CR or LF inside the URL, so the raw string it accepted would be
               // stored with them still in it (r2-10-06).
               && !value.Trim().Any(char.IsControl);

        // Every stored image is written twice: a full-size master and a thumbnail, both made by the
        // browser's canvas before upload, so the plugin needs no image-processing dependency. Grids
        // and switcher cards request the thumbnail; twenty full-size avatars would decode tens of
        // megabytes of bitmap and stall the TV browsers this plugin supports.

        /// <summary>Suffix distinguishing the thumbnail file from its master.</summary>
        protected const string ThumbSuffix = "_t";

        /// <summary>
        /// Formats accepted from a data URL. The client re-encodes through a canvas, so this is what
        /// it can emit, not what a user may pick.
        /// <para>
        /// SVG is excluded on purpose: it can carry script, and these files are served from the
        /// server's own origin.
        /// </para>
        /// </summary>
        private static readonly (string Mime, string Extension, string ContentType)[] StorableImageFormats =
        {
            ("image/png",  ".png",  "image/png"),
            ("image/webp", ".webp", "image/webp"),
            ("image/gif",  ".gif",  "image/gif"),
            ("image/jpeg", ".jpg",  "image/jpeg"),
        };

        /// <summary>Most files one folder import will list. A guard against a folder of thousands.</summary>
        protected const int MaxScanFiles = 200;

        /// <summary>Largest file the folder import will hand to the browser to resize.</summary>
        protected const long MaxScanFileBytes = 25L * 1024 * 1024;

        protected static readonly string[] StorableImageExtensions = { ".jpg", ".png", ".webp", ".gif" };

        /// <summary>Maps a stored file extension back to a MIME type for the response.</summary>
        protected static string ContentTypeForExtension(string extension)
        {
            foreach (var f in StorableImageFormats)
                if (string.Equals(f.Extension, extension, StringComparison.OrdinalIgnoreCase)) return f.ContentType;
            return "application/octet-stream";
        }

        /// <summary>
        /// Decodes a <c>data:image/...;base64,...</c> payload. Null for anything not a well-formed,
        /// in-budget image in a format we store.
        /// <para>Callers that want the reason use <see cref="ParseImageDataUrl"/>.</para>
        /// </summary>
        protected (byte[] Bytes, string Extension)? DecodeImageDataUrl(string? dataUrl, string context)
        {
            var parsed = ParseImageDataUrl(dataUrl, context);
            return parsed.Error == null ? (parsed.Bytes!, parsed.Extension!) : null;
        }

        /// <summary>
        /// The one parse of an image data URL: the exact declared type, the base64, the size budget
        /// and the file signature. Answers the bytes and extension, or the message to show.
        /// <see cref="CheckProfileImage"/> and <see cref="DecodeImageDataUrl"/> both use it, so the
        /// check and the save cannot disagree.
        /// </summary>
        protected (byte[]? Bytes, string? Extension, string? Error) ParseImageDataUrl(string? dataUrl, string context)
        {
            const string notSupported = "Not a supported image.";
            const string unreadable = "That image could not be read.";

            if (string.IsNullOrEmpty(dataUrl) ||
                !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return (null, null, notSupported);

            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex < 0) return (null, null, unreadable);

            var header = dataUrl.Substring(0, commaIndex);

            // The declared type, exactly: "data:image/png;base64" names image/png. A substring match let
            // "image/png-evil" or a parameter that merely mentions a type through. R-1111.
            var declared = header.Substring("data:".Length);
            var semicolon = declared.IndexOf(';');
            if (semicolon >= 0) declared = declared.Substring(0, semicolon);
            declared = declared.Trim();

            string? extension = null;
            foreach (var f in StorableImageFormats)
            {
                if (string.Equals(declared, f.Mime, StringComparison.OrdinalIgnoreCase)) { extension = f.Extension; break; }
            }

            // Reject rather than default. Falling back to .jpg would store "data:image/svg+xml,<script>"
            // as a JPEG and serve it as one; SVG is excluded because these files come from our origin.
            if (extension == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Rejected image for {Context} — '{Header}' is not a format this plugin stores.",
                    context, header.Length > 64 ? header.Substring(0, 64) : header);
                return (null, null, notSupported);
            }

            // Measured before decoding: base64 is four characters to three bytes, so a longer payload
            // cannot be in budget, and decoding first allocated an oversize body just to discard it.
            // r2-22-02.
            var payloadLength = dataUrl.Length - commaIndex - 1;
            if (payloadLength > ((MaxProfileImageBytes + 2) / 3 * 4) + 4)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Image for {Context} is {Size} characters of base64, over the limit. Rejected.",
                    context, payloadLength);
                return (null, null, "That image is too large.");
            }

            try
            {
                var bytes = Convert.FromBase64String(dataUrl.Substring(commaIndex + 1));
                if (bytes.Length > MaxProfileImageBytes)
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: Image for {Context} is {Size} bytes, over the {Limit} byte limit. Rejected.",
                        context, bytes.Length, MaxProfileImageBytes);
                    return (null, null, "That image is too large.");
                }

                // The bytes must match the header. The file is served from this origin under the type its
                // extension implies, so a payload that is not that kind of image is refused. R-1111.
                if (!LooksLike(extension, bytes))
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: Rejected image for {Context} — the data is not a {Extension} file.",
                        context, extension);
                    return (null, null, notSupported);
                }
                return (bytes, extension, null);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: Image for {Context} was not valid base64.", context);
                return (null, null, unreadable);
            }
        }

        /// <summary>The file signature for a stored extension: PNG, JPEG, GIF or WebP.</summary>
        internal static bool LooksLike(string extension, byte[] b)
        {
            bool At(int offset, params byte[] sig)
            {
                if (b.Length < offset + sig.Length) return false;
                for (var i = 0; i < sig.Length; i++) if (b[offset + i] != sig[i]) return false;
                return true;
            }

            switch (extension)
            {
                case ".png": return At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
                case ".jpg": return At(0, 0xFF, 0xD8, 0xFF);
                case ".gif": return At(0, 0x47, 0x49, 0x46, 0x38);
                case ".webp": return At(0, 0x52, 0x49, 0x46, 0x46) && At(8, 0x57, 0x45, 0x42, 0x50);
                default: return false;
            }
        }

        /// <summary>
        /// Removes every variant of a stored image, in both formats and sizes. Best effort: a file
        /// open elsewhere or read-only is left and noted, not thrown, so a locked picture cannot turn
        /// a delete or save into a 500. R-1112.
        /// </summary>
        protected static void DeleteImageFiles(string folder, string baseName)
            => DeleteImageFilesExcept(folder, baseName, null);

        /// <summary>A profile's own image, for a caller outside the controllers (UserRecords).</summary>
        internal static void DeleteProfileImage(Guid profileId)
            => DeleteImageFiles(ProfileImageFolder, profileId.ToString());

        /// <summary>As <see cref="DeleteImageFiles"/>, sparing the named files.</summary>
        private static bool DeleteImageFilesExcept(string folder, string baseName, ISet<string>? keep)
        {
            var allGone = true;
            foreach (var ext in StorableImageExtensions)
            {
                foreach (var name in new[] { $"{baseName}{ext}", $"{baseName}{ThumbSuffix}{ext}" })
                {
                    if (keep != null && keep.Contains(name)) continue;
                    var p = Path.Combine(folder, name);
                    try
                    {
                        if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        System.Diagnostics.Trace.TraceWarning("ProfilesPlugin: Could not delete image {0}: {1}", p, ex.Message);
                        allGone = false;
                    }
                }
            }
            return allGone;
        }

        /// <summary>
        /// Writes a master image and, if supplied, its thumbnail. Stale variants are cleared so a
        /// format change cannot leave the old file shadowing the new. Returns the stored extension,
        /// or null if nothing was written.
        /// </summary>
        protected string? WriteImageFiles(string folder, string baseName, string? dataUrl, string? thumbDataUrl, string context)
        {
            var master = DecodeImageDataUrl(dataUrl, context);
            if (master == null) return null;

            Directory.CreateDirectory(folder);

            // New files first, old after. The old picture used to go first, so a failed write left the
            // person with neither (R-1113). Each is written beside its name and moved into place, so a
            // failure never leaves half a file under the real name.
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var masterName = $"{baseName}{master.Value.Extension}";
            PlaceImageFile(folder, masterName, master.Value.Bytes);
            kept.Add(masterName);

            // A missing or unusable thumbnail is not fatal; the master is served instead. That costs
            // bandwidth, where refusing the save would cost the user their picture.
            var thumb = DecodeImageDataUrl(thumbDataUrl, context + " thumbnail");
            if (thumb != null)
            {
                var thumbName = $"{baseName}{ThumbSuffix}{thumb.Value.Extension}";
                PlaceImageFile(folder, thumbName, thumb.Value.Bytes);
                kept.Add(thumbName);
            }

            // Stale variants go last. One that cannot be removed fails the save: FindImageFile prefers
            // .jpg, so a leftover would keep showing instead of the picture just written.
            if (!DeleteImageFilesExcept(folder, baseName, kept))
            {
                throw new IOException("A previous picture for " + baseName + " could not be removed.");
            }

            return master.Value.Extension;
        }

        private static void PlaceImageFile(string folder, string name, byte[] bytes)
        {
            var target = Path.Combine(folder, name);
            // A name of its own per write: two saves of one profile at once used to share a temp file
            // and could move each other's bytes into place.
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                System.IO.File.WriteAllBytes(temp, bytes);
                MoveIntoPlace(temp, target);
            }
            catch
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { }
                throw;
            }
        }

        /// <summary>
        /// A staging name no other save shares: a fixed one let two saves at once, or a file left by
        /// one that died, block each other.
        /// </summary>
        internal static string UniqueTempName(string target)
            => target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        /// <summary>
        /// Removes staged files a write never moved into place. Every writer deletes its own on a
        /// failure, so these are only left when the process stops mid-write, and before this nothing
        /// ever removed them (r3-02-01). Only names <see cref="UniqueTempName"/> makes, and only ones
        /// older than <paramref name="olderThan"/>, so a write in flight is never touched.
        /// </summary>
        internal static int SweepStagedFiles(string folder, TimeSpan olderThan)
        {
            if (!Directory.Exists(folder)) return 0;
            var cutoff = DateTime.UtcNow - olderThan;
            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dot = name.LastIndexOf('.');
                if (dot < 0 || !Guid.TryParseExact(name.Substring(dot + 1), "N", out _)) continue;
                try
                {
                    if (System.IO.File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    System.IO.File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return removed;
        }

        /// <summary>
        /// Moves a staged file over its target. Windows refuses a replace while another replace of the
        /// same name is in flight, so a second save of the same picture gets a few tries.
        /// </summary>
        internal static void MoveIntoPlace(string temp, string target)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    System.IO.File.Move(temp, target, true);
                    return;
                }
                catch (Exception ex) when (attempt < 8 && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    System.Threading.Thread.Sleep(15 * attempt);
                }
            }
        }

        /// <summary>
        /// Resolves a stored image on disk: the thumbnail when asked for, falling back to the master.
        /// </summary>
        protected static (string Path, string ContentType)? FindImageFile(string folder, string baseName, bool wantThumb)
        {
            var names = wantThumb
                ? new[] { baseName + ThumbSuffix, baseName }
                : new[] { baseName };

            foreach (var name in names)
            {
                foreach (var ext in StorableImageExtensions)
                {
                    var p = Path.Combine(folder, $"{name}{ext}");
                    if (System.IO.File.Exists(p)) return (p, ContentTypeForExtension(ext));
                }
            }
            return null;
        }

        /// <summary>The directory holding per-profile avatar images.</summary>
        protected static string ProfileImageFolder => Path.Combine(
            Plugin.Instance?.AppPaths.DataPath ?? Path.GetTempPath(),
            "plugins", "ProfilesManagement");

        /// <summary>The directory holding the administrator's shared avatar library.</summary>
        protected static string AvatarLibraryFolder => Path.Combine(ProfileImageFolder, "avatars");

        /// <summary>The directory holding per-profile library tile artwork (GitHub issue #19).</summary>
        protected static string LibraryArtFolder => Path.Combine(ProfileImageFolder, "libraryart");

        /// <summary>
        /// File name for one profile's artwork for one library. Derived from the two ids, not stored,
        /// so the configuration cannot drift from what is on disk and nothing is left to clean up.
        /// </summary>
        protected static string LibraryArtName(Guid profileId, Guid libraryId)
            => profileId.ToString("N") + "_" + libraryId.ToString("N");

        /// <summary>
        /// True when the administrator restricted profile pictures to the avatar library. Hiding the
        /// upload control is presentation; this is the rule.
        /// </summary>
        protected bool AreCustomAvatarsBlocked()
            => Plugin.Instance?.Configuration?.DisallowCustomAvatarUploads == true;

        /// <summary>
        /// Copies a library avatar to a profile, server-side, byte for byte.
        /// <para>
        /// This makes "only allow avatars from this library" enforceable. A library choice normally
        /// means the client crops it and posts the result, which looks like any upload, so the server
        /// cannot tell them apart. Passing the library id makes it verifiable: nothing is decoded and
        /// only bytes the administrator published can land. A locked-down server gives up per-user
        /// cropping, a fair trade for a consistent set.
        /// </para>
        /// </summary>
        /// <para>
        /// Answers (Url, Error), never a bare null: callers read null as "no picture", so a missing
        /// library avatar removed the profile's own with a 200 and no message (R-1108). Exactly one is
        /// set.
        /// </para>
        protected (string? Url, string? Error) CopyLibraryAvatarToProfile(Guid profileId, string libraryAvatarId)
        {
            var error = CheckLibraryAvatar(libraryAvatarId);
            if (error != null) return (null, error);

            return CopyLibraryAvatar(libraryAvatarId, ProfileImageFolder, profileId.ToString())
                ? ($"/plugins/profiles/image/{profileId}?v={DateTime.UtcNow.Ticks}", null)
                : (null, "That picture is no longer available.");
        }

        /// <summary>
        /// Why a library avatar can't be chosen, or null. Writes nothing, so a caller can refuse
        /// before changing anything else.
        /// </summary>
        protected string? CheckLibraryAvatar(string libraryAvatarId)
        {
            var config = Plugin.Instance?.Configuration;
            if (StarterAvatars.IsStarterId(libraryAvatarId))
            {
                return StarterAvatars.Find(libraryAvatarId) != null && StarterAvatars.Enabled(config)
                    ? null
                    : "No such avatar.";
            }

            var item = FindLibraryAvatar(libraryAvatarId);
            if (item == null) return "No such avatar.";
            return FindImageFile(AvatarLibraryFolder, item.Id, false) == null ? "That picture is no longer available." : null;
        }

        /// <summary>
        /// Why a picture can't be saved for <paramref name="profileId"/>, or null. Writes nothing.
        /// The same rules <see cref="SaveProfileImage"/> applies, asked first so a request that would
        /// be refused is refused before anything else in it is done. A data URL is judged by
        /// <see cref="ParseImageDataUrl"/>, the parse the save uses, so the two agree.
        /// </summary>
        protected string? CheckProfileImage(Guid profileId, string? input, string? currentImage)
        {
            if (string.IsNullOrEmpty(input)) return null;

            // Our own picture URL, and only this profile's: the unchanged value, or its address with a
            // cache-buster. Anything else under the prefix was stored unchecked, so a quote, "../" or
            // another profile's URL could land in a rendered img src (R-1109).
            if (input.StartsWith("/plugins/profiles/image/", StringComparison.OrdinalIgnoreCase))
            {
                return IsOwnImageUrl(profileId, input) || string.Equals(input, currentImage, StringComparison.Ordinal)
                    ? null
                    : "Not a supported image.";
            }

            if (AreCustomAvatarsBlocked()) return "This server only allows pictures from the avatar library.";
            if (IsValidImageUrl(input)) return null;
            if (!input.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return "Not a supported image.";

            return ParseImageDataUrl(input, $"profile {profileId}").Error;
        }

        private static bool IsOwnImageUrl(Guid profileId, string url)
        {
            const string prefix = "/plugins/profiles/image/";
            var rest = url.Substring(prefix.Length);
            var query = rest.IndexOf('?');
            var id = query < 0 ? rest : rest.Substring(0, query);
            if (!Guid.TryParse(id, out var parsed) || parsed != profileId) return false;
            if (query < 0) return true;

            var tail = rest.Substring(query);
            return tail.Length > 3 && tail.StartsWith("?v=", StringComparison.Ordinal) && tail.Substring(3).All(char.IsAsciiDigit);
        }

        /// <summary>
        /// Copies a published library avatar to <paramref name="destFolder"/> as
        /// <paramref name="baseName"/>, master and thumbnail. False when the id is unknown or nothing
        /// is on disk for it.
        /// </summary>
        protected bool CopyLibraryAvatar(string libraryAvatarId, string destFolder, string baseName)
        {
            var config = Plugin.Instance?.Configuration;

            // A starter avatar is written out of the assembly: one file, no thumbnail (at 256px it is
            // thumbnail size, and FindImageFile falls back to the master).
            if (StarterAvatars.IsStarterId(libraryAvatarId))
            {
                var starter = StarterAvatars.Find(libraryAvatarId);
                if (starter == null || !StarterAvatars.Enabled(config))
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: Asked for starter avatar '{Avatar}', which is unknown or switched off.",
                        libraryAvatarId);
                    return false;
                }

                using var source = StarterAvatars.Open(starter.Value.Slug);
                if (source == null)
                {
                    _logger.LogWarning("ProfilesPlugin: Starter avatar {Slug} is missing from the plugin assembly.", starter.Value.Slug);
                    return false;
                }

                // Copied beside the old picture and swapped in once whole. It used to delete the old one
                // first, so a copy that failed halfway left no picture (R-1108).
                Directory.CreateDirectory(destFolder);
                var stagedStarter = UniqueTempName(Path.Combine(destFolder, baseName + StarterAvatars.Extension));
                try
                {
                    using (var target = System.IO.File.Create(stagedStarter))
                    {
                        source.CopyTo(target);
                    }

                    DeleteImageFiles(destFolder, baseName);
                    MoveIntoPlace(stagedStarter, Path.Combine(destFolder, baseName + StarterAvatars.Extension));
                }
                finally
                {
                    if (System.IO.File.Exists(stagedStarter)) System.IO.File.Delete(stagedStarter);
                }
                return true;
            }

            var item = FindLibraryAvatar(libraryAvatarId);
            if (item == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Asked for library avatar '{Avatar}', which does not exist.",
                    libraryAvatarId);
                return false;
            }

            // Confirmed before anything is removed: a listed avatar whose file is gone would clear the
            // destination and leave the caller pointing at a picture that does not exist.
            if (FindImageFile(AvatarLibraryFolder, item.Id, false) == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Library avatar {Avatar} is listed but has no file on disk.", item.Id);
                return false;
            }

            Directory.CreateDirectory(destFolder);

            // Every file is copied beside the old ones and swapped in after, so a failed copy leaves the
            // current picture alone (R-1108).
            var staged = new List<(string Temp, string Final)>();
            try
            {
                foreach (var (suffix, wantThumb) in new[] { (string.Empty, false), (ThumbSuffix, true) })
                {
                    var source = FindImageFile(AvatarLibraryFolder, item.Id, wantThumb);
                    // FindImageFile falls back to the master when there is no thumbnail; copy into the thumbnail
                    // slot only a distinct file.
                    if (source == null) continue;
                    if (wantThumb && !Path.GetFileNameWithoutExtension(source.Value.Path).EndsWith(ThumbSuffix, StringComparison.Ordinal))
                        continue;

                    var extension = Path.GetExtension(source.Value.Path);
                    var final = Path.Combine(destFolder, $"{baseName}{suffix}{extension}");
                    var temp = UniqueTempName(final);
                    System.IO.File.Copy(source.Value.Path, temp, true);
                    staged.Add((temp, final));
                }

                if (staged.Count == 0)
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: Library avatar {Avatar} is listed but has no file on disk.", item.Id);
                    return false;
                }

                DeleteImageFiles(destFolder, baseName);
                foreach (var (temp, final) in staged)
                {
                    MoveIntoPlace(temp, final);
                }
                return true;
            }
            finally
            {
                foreach (var (temp, _) in staged)
                {
                    if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp);
                }
            }
        }

        /// <summary>
        /// Saves a profile's picture. Answers (Url, Error): empty input is a deliberate removal,
        /// (null, null); a refusal is (null, reason) and touches nothing. It used to answer a bare
        /// null for both, and callers stored it, so a picture that was merely too large removed the
        /// existing one with a 200 and no message (R-1108).
        /// </summary>
        protected (string? Url, string? Error) SaveProfileImage(
            Guid profileId, string? profileImageInput, string? thumbInput = null, string? currentImage = null)
        {
            var folder = ProfileImageFolder;

            if (string.IsNullOrEmpty(profileImageInput))
            {
                DeleteImageFiles(folder, profileId.ToString());
                return (null, null);
            }

            // Enforced here, not only by hiding the upload control, so a hand-written request cannot get
            // around the administrator's setting. A locked-down server accepts pictures only through
            // CopyLibraryAvatarToProfile.
            var error = CheckProfileImage(profileId, profileImageInput, currentImage);
            if (error != null)
            {
                _logger.LogWarning("ProfilesPlugin: Rejected a profile picture for {Id}: {Reason}", profileId, error);
                return (null, error);
            }

            if (profileImageInput.StartsWith("/plugins/profiles/image/", StringComparison.OrdinalIgnoreCase))
                return (profileImageInput, null);

            if (IsValidImageUrl(profileImageInput)) return (profileImageInput.Trim(), null);

            try
            {
                var ext = WriteImageFiles(folder, profileId.ToString(), profileImageInput, thumbInput,
                    $"profile {profileId}");
                if (ext != null)
                {
                    // The cache-buster makes a changed picture show at once; otherwise browsers keep the old one
                    // at the same URL.
                    return ($"/plugins/profiles/image/{profileId}?v={DateTime.UtcNow.Ticks}", null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Failed to save image for {Id}.", profileId);
            }

            return (null, "That image could not be read.");
        }

        protected string GenerateSecureCode()
        {
            const string chars = "ABCDEFGHJKLMNOPQRSTUVWXYZ23456789";
            // GetInt32 is rejection-sampled, unlike (byte % 33), so every character is uniform over the
            // alphabet.
            return new string(Enumerable.Range(0, 6)
                .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)])
                .ToArray());
        }

        protected HashSet<Guid> GetMasterAccessibleFolders(UserPolicy masterPolicy)
            => MasterAccessibleFolders(_libraryManager, masterPolicy);

        /// <summary>Static so <see cref="Auth.ProfilePolicySync"/> can use it outside a request.</summary>
        internal static HashSet<Guid> MasterAccessibleFolders(ILibraryManager libraryManager, UserPolicy masterPolicy)
            => MasterAccessibleFolders(libraryManager, masterPolicy, LibraryOwnership.ManagedByOwnerIds(libraryManager));

        /// <summary>
        /// What the main account can reach and Bonfire manages. Libraries another plugin manages
        /// (<paramref name="owned"/>) are never in it, so "every library" on a profile never grants one.
        /// </summary>
        internal static HashSet<Guid> MasterAccessibleFolders(ILibraryManager libraryManager, UserPolicy masterPolicy, HashSet<Guid> owned)
            => MasterAccessibleFolders(
                masterPolicy,
                owned,
                () => libraryManager.GetVirtualFolders()
                    .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty));

        /// <summary>
        /// The same, for a caller that already holds the server's library ids (a policy pass reads
        /// them once). <paramref name="allFolderIds"/> is asked only for "every library".
        /// </summary>
        internal static HashSet<Guid> MasterAccessibleFolders(
            UserPolicy masterPolicy, HashSet<Guid> owned, Func<IEnumerable<Guid>> allFolderIds)
        {
            HashSet<Guid> masterAccessibleFolders;
            if (masterPolicy.EnableAllFolders)
            {
                masterAccessibleFolders = allFolderIds().ToHashSet();
            }
            else
            {
                masterAccessibleFolders = (masterPolicy.EnabledFolders ?? Array.Empty<Guid>()).ToHashSet();
            }
            var masterBlocked = masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>();
            masterAccessibleFolders.ExceptWith(masterBlocked);
            masterAccessibleFolders.ExceptWith(owned);
            return masterAccessibleFolders;
        }

        /// <summary>
        /// Writes a profile's libraries from the form's list: those the main account can reach, the
        /// rest blocked. Libraries another plugin manages are left out of both lists. On an edit
        /// (<paramref name="keepOwned"/>) what the account already has of them is put back; a new
        /// account has none. Returns the libraries granted.
        /// </summary>
        internal static List<Guid> ApplyListedFolders(
            ILibraryManager libraryManager, UserPolicy masterPolicy, List<Guid> requested, UserPolicy target, bool keepOwned)
        {
            var owned = LibraryOwnership.ManagedByOwnerIds(libraryManager);
            var kept = keepOwned ? LibraryOwnership.KeptAccess(owned, target) : (Enabled: Array.Empty<Guid>(), Blocked: Array.Empty<Guid>());
            var masterAccessible = MasterAccessibleFolders(libraryManager, masterPolicy, owned);
            var validated = requested.Where(id => masterAccessible.Contains(id)).ToList();

            target.EnableAllFolders = false;
            target.EnabledFolders = validated.Concat(kept.Enabled).ToArray();

            var blockedMediaFolders = LibraryOwnership.ManagedFolderIds(libraryManager)
                .Where(id => !validated.Contains(id))
                .ToArray();
            var masterBlocked = (masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>()).Where(id => !owned.Contains(id));
            target.BlockedMediaFolders = blockedMediaFolders.Union(masterBlocked).Union(kept.Blocked).ToArray();
            return validated;
        }

        /// <summary>
        /// Gives a profile the main account's libraries. With nothing owned by another plugin, a copy
        /// of the main account's settings. Otherwise an explicit list of what Bonfire manages, since
        /// copying "every library" would hand over the owned ones too. Returns the libraries granted.
        /// </summary>
        internal static List<Guid> ApplyInheritedFolders(
            ILibraryManager libraryManager, UserPolicy masterPolicy, UserPolicy target, bool keepOwned)
        {
            var owned = LibraryOwnership.ManagedByOwnerIds(libraryManager);
            var masterAccessible = MasterAccessibleFolders(libraryManager, masterPolicy, owned);
            if (owned.Count == 0)
            {
                target.EnableAllFolders = masterPolicy.EnableAllFolders;
                target.EnabledFolders = masterPolicy.EnabledFolders;
                target.BlockedMediaFolders = masterPolicy.BlockedMediaFolders;
            }
            else
            {
                var kept = keepOwned ? LibraryOwnership.KeptAccess(owned, target) : (Enabled: Array.Empty<Guid>(), Blocked: Array.Empty<Guid>());
                target.EnableAllFolders = false;
                target.EnabledFolders = masterAccessible.Concat(kept.Enabled).ToArray();
                target.BlockedMediaFolders = (masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>())
                    .Where(id => !owned.Contains(id)).Union(kept.Blocked).ToArray();
            }

            return masterAccessible.ToList();
        }

        // Jellyfin enforces BlockedTags/AllowedTags in BaseItem.IsVisibleViaTags against an item's
        // inherited tags (its own, every parent's, the collection folder's), so tagging a series or
        // library cascades to everything inside.

        /// <summary>
        /// Trims, drops blanks and de-duplicates a tag list ignoring case. Never null, so "unset" and
        /// "empty" read alike.
        /// </summary>
        protected static List<string> NormalizeTags(IEnumerable<string>? tags)
        {
            if (tags == null) return new List<string>();
            return tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Resolves the tag policy written to a sub-profile's Jellyfin user, never wider than its
        /// master's. Blocked tags add up. The allow-list is intersected with the master's when the
        /// master has one: a sub-profile can narrow it, not widen it.
        /// </summary>
        internal static (string[] Blocked, string[] Allowed) ResolveTagPolicy(
            UserPolicy masterPolicy,
            IEnumerable<string>? profileBlocked,
            IEnumerable<string>? profileAllowed)
        {
            var masterBlockedTags = NormalizeTags(masterPolicy.BlockedTags);
            var masterAllowedTags = NormalizeTags(masterPolicy.AllowedTags);

            var blocked = NormalizeTags(profileBlocked)
                .Concat(masterBlockedTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allowed = NormalizeTags(profileAllowed);
            if (masterAllowedTags.Count > 0)
            {
                allowed = allowed.Count == 0
                    ? masterAllowedTags
                    : allowed.Where(t => masterAllowedTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();

                // None of the profile's allowed tags is allowed for its master any more. Create and update
                // refuse that, so this is a master whose allow-list changed afterward.
                // The profile should see what carries one of its tags AND one of the master's, which
                // Jellyfin's allow-list cannot say. Falling back to the master's list showed a profile
                // limited to "Kids" everything in "Family" once its master was limited to Family. Showing
                // nothing never shows too much. The tag explains why in Dashboard -> Users, and editing the
                // profile asks for tags its master allows.
                if (allowed.Count == 0) allowed = new List<string> { NoAllowedTagMatches };
            }

            return (blocked.ToArray(), allowed.ToArray());
        }

        /// <summary>
        /// The allow-list written when none of a profile's allowed tags is still allowed for its
        /// master. No item carries it, so the profile sees nothing until edited.
        /// </summary>
        internal const string NoAllowedTagMatches = "Bonfire: none of this profile's allowed tags are allowed for its main account";

        /// <summary>
        /// Validates a requested allow-list against the master's. An error message when the request
        /// would leave the profile nothing visible, otherwise null.
        /// </summary>
        protected string? ValidateAllowedTags(UserPolicy masterPolicy, IEnumerable<string>? profileAllowed)
        {
            var requested = NormalizeTags(profileAllowed);
            if (requested.Count == 0) return null;

            var masterAllowedTags = NormalizeTags(masterPolicy.AllowedTags);
            if (masterAllowedTags.Count == 0) return null;

            if (!requested.Any(t => masterAllowedTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                return "None of the selected allowed tags are permitted by your account. "
                     + "A sub-profile can only narrow the tags you are allowed to see, not add new ones. "
                     + $"Your account allows: {string.Join(", ", masterAllowedTags)}.";
            }

            return null;
        }

        protected List<object> GetBonfireGroupMembers(BonfireGroup group, PluginConfiguration config)
        {
            return (group.MemberUserIds ?? new List<Guid>()).Select(id => (object)new
            {
                UserId = id,
                Username = _userManager.GetUserById(id)?.Username ?? "Unknown User"
            }).ToList();
        }
    }
}
