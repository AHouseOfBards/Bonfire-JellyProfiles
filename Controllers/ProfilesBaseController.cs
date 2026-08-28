using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Shared base controller that provides all helper methods, static caches,
    /// and dependency injection common to every Profiles sub-controller.
    ///
    /// Controllers are transient in ASP.NET Core, but static fields live for the
    /// entire app lifetime, making them suitable for one-time caches (JS content,
    /// audit-log file path) that we don't want to resolve on every request.
    /// </summary>
    [ApiController]
    // NOTE: ProfilesController uses [AllowAnonymous] at the class level because
    // MediaController (profiles.js + image/{id}) must be unauthenticated, and
    // ASP.NET Core applies method-level [AllowAnonymous] only when a class-level
    // [Authorize] is present. Since Jellyfin's auth policy name is not part of
    // the public plugin API, the safest approach is:
    //   - class level [AllowAnonymous]  ← all controllers inherit this
    //   - every endpoint that needs a user calls GetCurrentUserId() and returns
    //     401 if it is null — this is done consistently across all endpoints.
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public abstract class ProfilesBaseController : ControllerBase
    {
        // ── One-time static caches ──────────────────────────────────────────────────
        internal static string? CachedProfilesJs;
        internal static readonly object JsCacheLock = new();

        internal static string? AuditLogPath;
        internal static readonly object AuditLogLock = new();

        /// <summary>
        /// The one lock guarding every read-modify-write of the plugin configuration.
        /// <para>
        /// Twenty-six sites used to do <c>var config = Plugin.Instance?.Configuration;</c>
        /// and then locked that instance. That is not mutual exclusion. When an administrator
        /// saves the settings page Jellyfin calls <c>BasePlugin&lt;T&gt;.UpdateConfiguration</c>,
        /// which assigns a <em>new</em> configuration instance — so every monitor already held
        /// on the old object is guarding something nothing else will ever lock, while every
        /// request arriving afterwards locks the new one. Two writers get inside at once, and
        /// whatever the first one wrote goes away with the object it wrote to. The symptom is
        /// a profile, device or group that was saved and simply is not there.
        /// </para>
        /// <para>
        /// A static field, because controllers are transient: an instance field would hand
        /// every request its own private lock, which is the same bug wearing a different hat.
        /// Proven, and guarded against coming back, by <c>tests/cs/configlock</c> — it calls
        /// the real <c>UpdateConfiguration</c> to show the instance is replaced, then walks two
        /// threads through the old pattern and catches both inside the critical section.
        /// </para>
        /// </summary>
        internal static readonly object ConfigLock = new();

        // ── DI fields (set by derived constructors) ─────────────────────────────────
        protected readonly IUserManager _userManager;
        protected readonly ISessionManager _sessionManager;
        protected readonly ILibraryManager _libraryManager;
        protected readonly INetworkManager _networkManager;
        protected readonly ILogger _logger;

        protected ProfilesBaseController(
            IUserManager userManager,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            INetworkManager networkManager,
            ILogger logger)
        {
            _userManager = userManager;
            _sessionManager = sessionManager;
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
            return userId;
        }

        protected string? GetAuthorizationParameter(string name)
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader)) return null;

            // Strip the scheme prefix (e.g. "MediaBrowser ") so the first token
            // parses as "Client=\"...\"" rather than "MediaBrowser Client=\"...\"".
            const string scheme = "MediaBrowser ";
            if (authHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                authHeader = authHeader.Substring(scheme.Length);

            var parts = authHeader.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring(name.Length + 1).Trim('"', ' ');
                    return value;
                }
            }
            return null;
        }

        // ── PIN hashing ─────────────────────────────────────────────────────────────
        // PINs are 4-8 digits, so the entire keyspace (10^4 - 10^8) is trivially
        // enumerable against a fast unsalted digest. Hashes are therefore PBKDF2-SHA256
        // with a per-PIN random salt, stored as:
        //     pbkdf2.sha256$<iterations>$<base64 salt>$<base64 hash>
        //
        // Hashes written before this change are bare 64-char SHA-256 hex. Those are still
        // accepted on verification and transparently re-hashed to the new format on the
        // next successful entry, so no existing PIN is invalidated.

        private const int PinIterations = 150_000;
        private const int PinSaltBytes = 16;
        private const int PinHashBytes = 32;
        private const string PinHashPrefix = "pbkdf2.sha256$";

        protected string HashPin(string? pin)
        {
            if (string.IsNullOrEmpty(pin)) return string.Empty;

            var salt = RandomNumberGenerator.GetBytes(PinSaltBytes);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin), salt, PinIterations, HashAlgorithmName.SHA256, PinHashBytes);

            return $"{PinHashPrefix}{PinIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>Legacy (pre-PBKDF2) unsalted SHA-256 hex digest. Verification only.</summary>
        private static string LegacyHashPin(string pin)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin))).ToLowerInvariant();

        /// <summary>True when the stored hash still uses the legacy format and should be upgraded.</summary>
        protected static bool IsLegacyPinHash(string? storedHash)
            => !string.IsNullOrEmpty(storedHash) && !storedHash.StartsWith(PinHashPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Constant-time comparison of a candidate PIN against a stored hash of either format.
        /// Returns false for an empty stored hash — "no PIN set" is handled by the callers.
        /// </summary>
        protected bool VerifyPinHash(string? pin, string? storedHash)
        {
            if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(storedHash)) return false;

            if (IsLegacyPinHash(storedHash))
            {
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(LegacyHashPin(pin)),
                    Encoding.UTF8.GetBytes(storedHash));
            }

            // pbkdf2.sha256$<iterations>$<salt>$<hash>
            var parts = storedHash.Substring(PinHashPrefix.Length).Split('$');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var iterations)
                || iterations <= 0)
            {
                _logger.LogWarning("ProfilesPlugin: Stored PIN hash is malformed; refusing to verify.");
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[1]);
                var expected = Convert.FromBase64String(parts[2]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: Stored PIN hash has invalid base64; refusing to verify.");
                return false;
            }
        }

        /// <summary>
        /// Verifies a PIN against a mapping and, when it matches a legacy hash, upgrades the
        /// stored hash to PBKDF2 in place. Call this instead of <see cref="VerifyPinHash"/>
        /// wherever the mapping is available so old hashes drain away over time.
        /// </summary>
        protected bool VerifyPinAndUpgrade(string? pin, ProfileMapping mapping, PluginConfiguration config)
        {
            if (!VerifyPinHash(pin, mapping.PinHash)) return false;

            if (IsLegacyPinHash(mapping.PinHash))
            {
                lock (ConfigLock)
                {
                    // Re-check inside the lock — a concurrent request may have upgraded it.
                    if (IsLegacyPinHash(mapping.PinHash))
                    {
                        mapping.PinHash = HashPin(pin);
                        Plugin.Instance?.SaveConfiguration();
                        _logger.LogInformation(
                            "ProfilesPlugin: Upgraded legacy PIN hash for profile {Id} to PBKDF2.",
                            mapping.ProfileUserId);
                    }
                }
            }

            return true;
        }

        // ── Cross-account (Bonfire) switch rules ────────────────────────────────────
        // Pulled out as pure functions so the full matrix — opted in or out, local or
        // remote, PIN set or not — can be exercised directly. /switch and /verify-pin must
        // agree exactly: if they drift, the client is told a switch will work and then the
        // server refuses it.

        /// <summary>
        /// Decides whether a switch into <paramref name="targetMapping"/>'s account from a
        /// linked Bonfire may skip its PIN, and whether the switch must be refused outright.
        /// </summary>
        /// <param name="targetMapping">The mapping of the account being entered, or null if it has none.</param>
        /// <param name="isLocal">Whether Jellyfin classified the request's source address as local.</param>
        /// <returns>
        /// HouseholdLanBypass — the owner opted in and the client really is local, so no PIN
        /// is asked for. BlockedUnprotected — the account has no PIN and no opt-in, so it is
        /// not reachable through a shared Bonfire at all.
        /// </returns>
        protected internal static (bool HouseholdLanBypass, bool BlockedUnprotected) EvaluateCrossAccountSwitch(
            ProfileMapping? targetMapping, bool isLocal)
        {
            // Consent has to come from the account being entered. Remote requests never
            // qualify, so a leaked Bonfire code is worth nothing outside the house.
            bool householdLanBypass = isLocal && (targetMapping?.AllowHouseholdLanBypass ?? false);

            // An account with no PIN has nothing to prove ownership with; the opt-in is the
            // only thing that can stand in for one, and only on the local network.
            bool blocked = string.IsNullOrEmpty(targetMapping?.PinHash) && !householdLanBypass;

            return (householdLanBypass, blocked);
        }

        /// <summary>
        /// Whether a PIN prompt can be skipped for this switch.
        /// <para>
        /// Within your own household that is <see cref="ProfileMapping.BypassPinOnLocalNetwork"/>,
        /// your own convenience setting. Across a Bonfire link it is only ever
        /// <paramref name="householdLanBypass"/> — your setting does not reach into
        /// somebody else's account.
        /// </para>
        /// </summary>
        protected internal static bool CanSkipPin(
            ProfileMapping? mapping, bool isLocal, bool isCrossAccountMasterSwitch, bool householdLanBypass)
        {
            return isCrossAccountMasterSwitch
                ? householdLanBypass
                : mapping != null && mapping.BypassPinOnLocalNetwork && isLocal;
        }

        /// <summary>
        /// Returns null when the caller is a Jellyfin administrator, or the ActionResult to
        /// return when they are not. <paramref name="action"/> completes the sentence
        /// "Only administrators can …".
        /// </summary>
        protected ActionResult? RequireAdministrator(string action)
        {
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
        /// True when the given user account carries administrator rights. Used where a warning
        /// or a log line needs to reflect how much a session for that account is worth, rather
        /// than to authorise the caller — for that, check the caller's own policy directly.
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
                // Never let a policy lookup failure read as "not an administrator" without a
                // trace: the callers use this to decide how loudly to warn.
                _logger.LogWarning(ex, "ProfilesPlugin: Could not read policy for user {Id}.", userId);
                return false;
            }
        }

        // ── Cross-version compatibility helpers ─────────────────────────────────────
        // IUserManager.Users was renamed to GetUsers() in Jellyfin 10.11.7.
        // We compile against 10.11.5 (see Jellyfin.Profiles.csproj — the target was lowered
        // in v1.1.13 to fix a loader crash on 10.11.5) and use reflection to call whichever
        // member is present at runtime. The resolved member is cached in a static after the
        // first call, since these run on request paths.

        private const System.Reflection.BindingFlags PublicInstance =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        // Resolved once per user-manager implementation type. IUserManager is a singleton in
        // Jellyfin, so in practice these each resolve exactly once for the process lifetime.
        private static readonly ConcurrentDictionary<Type, System.Reflection.MethodInfo?> GetUsersMethodCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> UsersPropertyCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.MethodInfo?> ChangePasswordCache = new();

        protected IEnumerable<Jellyfin.Database.Implementations.Entities.User> GetAllUsers()
        {
            var type = _userManager.GetType();

            var method = GetUsersMethodCache.GetOrAdd(type, t => t.GetMethod("GetUsers", PublicInstance));
            if (method != null)
            {
                try
                {
                    return (IEnumerable<Jellyfin.Database.Implementations.Entities.User>)method.Invoke(_userManager, null)!;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ProfilesPlugin: GetUsers() reflection failed, falling back.");
                }
            }

            var prop = UsersPropertyCache.GetOrAdd(type, t => t.GetProperty("Users", PublicInstance));
            if (prop != null)
                return (IEnumerable<Jellyfin.Database.Implementations.Entities.User>)prop.GetValue(_userManager)!;

            _logger.LogError("ProfilesPlugin: Could not resolve user list from IUserManager.");
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

            _logger.LogError("ProfilesPlugin: Could not resolve ChangePassword on IUserManager.");
            return Task.CompletedTask;
        }

        // ── Audit log helpers ───────────────────────────────────────────────────────
        // Stored in a separate audit_log.json rather than in PluginConfiguration.xml
        // so that a profile switch never causes the entire config to be rewritten.

        protected string GetAuditLogPath()
        {
            if (AuditLogPath != null) return AuditLogPath;
            lock (AuditLogLock)
            {
                if (AuditLogPath != null) return AuditLogPath;

                // Fix #4: guard against Plugin.Instance being null on first call
                var instance = Plugin.Instance;
                if (instance == null)
                {
                    _logger.LogError("ProfilesPlugin: Plugin instance unavailable; audit log will not persist.");
                    return Path.Combine(Path.GetTempPath(), "bonfire_audit_log_fallback.json"); // harmless fallback — writes silently to temp file
                }

                var folder = Path.Combine(instance.AppPaths.DataPath, "plugins", "ProfilesManagement");
                Directory.CreateDirectory(folder);
                AuditLogPath = Path.Combine(folder, "audit_log.json");
            }
            return AuditLogPath!;
        }

        // In-memory mirror of audit_log.json. Without it, every profile switch re-read and
        // re-deserialized the whole file (up to 1000 entries) before appending — synchronous
        // disk I/O and JSON parsing on the switch request path, which is exactly the click
        // that users perceive as a stall. Loaded once per process, then kept in step in memory.
        private static List<AuditLogEntry>? _auditCache;

        /// <summary>Reads the audit log from disk. Prefer <see cref="GetAuditLogs"/>, which caches.</summary>
        protected List<AuditLogEntry> ReadAuditLogs()
        {
            try
            {
                var path = GetAuditLogPath();
                if (System.IO.File.Exists(path))
                    return JsonSerializer.Deserialize<List<AuditLogEntry>>(
                        System.IO.File.ReadAllText(path)) ?? new List<AuditLogEntry>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Failed to read audit log.");
            }
            return new List<AuditLogEntry>();
        }

        /// <summary>Audit entries, loading from disk only on the first call. Caller must hold AuditLogLock.</summary>
        private List<AuditLogEntry> GetAuditLogsLocked()
            => _auditCache ??= ReadAuditLogs();

        /// <summary>Snapshot of the audit log for read-only callers (the admin dashboard).</summary>
        protected List<AuditLogEntry> GetAuditLogSnapshot()
        {
            lock (AuditLogLock)
            {
                return new List<AuditLogEntry>(GetAuditLogsLocked());
            }
        }

        protected void WriteAuditLogs(List<AuditLogEntry> logs)
        {
            try
            {
                System.IO.File.WriteAllText(
                    GetAuditLogPath(),
                    JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Failed to write audit log.");
            }
        }

        protected void RecordAuditLog(string masterUsername, string targetUsername)
        {
            var device = GetAuthorizationParameter("Device") ?? "Unknown Device";
            var client = GetAuthorizationParameter("Client") ?? "Unknown Client";
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown IP";

            List<AuditLogEntry> snapshot;
            lock (AuditLogLock)
            {
                var logs = GetAuditLogsLocked();
                logs.Add(new AuditLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    MasterUsername = masterUsername,
                    TargetUsername = targetUsername,
                    DeviceName = device,
                    Client = client,
                    IpAddress = ip
                });
                // Keep the newest 1000 but preserve oldest-first order on disk. Sorting
                // descending here (as this once did) flipped the file every time it was
                // trimmed, so later appends landed at the wrong end of the timeline.
                if (logs.Count > 1000)
                {
                    _auditCache = logs.OrderByDescending(l => l.Timestamp)
                                      .Take(1000)
                                      .OrderBy(l => l.Timestamp)
                                      .ToList();
                    logs = _auditCache;
                }
                snapshot = new List<AuditLogEntry>(logs);
            }

            // Persist off the request thread. Serialising and writing the whole file used to
            // happen inline on every switch, adding disk I/O to the click that takes the user
            // to the home screen. The trade-off is that a hard crash can lose the most recent
            // entries — acceptable for an informational switch log, and the in-memory copy
            // still serves the dashboard correctly in the meantime.
            _ = Task.Run(() =>
            {
                try
                {
                    lock (AuditLogLock)
                    {
                        WriteAuditLogs(snapshot);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProfilesPlugin: Background audit log write failed.");
                }
            });
        }

        // ── Misc shared helpers ─────────────────────────────────────────────────────

        protected void CopyUserPolicy(
            MediaBrowser.Model.Users.UserPolicy source,
            MediaBrowser.Model.Users.UserPolicy destination)
        {
            destination.EnabledFolders = source.EnabledFolders;
            destination.EnableAllFolders = source.EnableAllFolders;
            destination.MaxParentalRating = source.MaxParentalRating;
            destination.BlockedTags = source.BlockedTags;
            destination.AllowedTags = source.AllowedTags;
            destination.EnablePlaybackRemuxing = source.EnablePlaybackRemuxing;
            destination.EnableVideoPlaybackTranscoding = source.EnableVideoPlaybackTranscoding;
            destination.EnableAudioPlaybackTranscoding = source.EnableAudioPlaybackTranscoding;
        }

        protected void RecordDeviceActivity()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            var deviceId = GetAuthorizationParameter("DeviceId");
            var deviceName = GetAuthorizationParameter("Device");
            var client = GetAuthorizationParameter("Client");
            if (string.IsNullOrEmpty(deviceId)) return;

            // Attribute the device to the caller's master account so the device picker can be
            // scoped by ownership. KnownDevices is a single server-wide list, so without this
            // every household would see every other household's hardware.
            var callerId = GetCurrentUserId();
            var ownerId = Guid.Empty;
            if (callerId != null)
            {
                var callerMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == callerId.Value);
                ownerId = callerMapping != null ? callerMapping.MasterUserId : callerId.Value;
            }

            lock (ConfigLock)
            {
                var existing = config.KnownDevices.FirstOrDefault(d =>
                    string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

                var now = DateTime.UtcNow;
                var save = false;

                if (existing != null)
                {
                    existing.LastSeen = now;
                    existing.DeviceName = deviceName ?? existing.DeviceName;
                    existing.Client = client ?? existing.Client;

                    // LastSeen was in-memory only, to keep a full PluginConfiguration.xml
                    // rewrite off every request. The cost was that it never survived a
                    // restart: the device list came back ordered by whenever each record was
                    // first written, and "last seen" showed a date from before the restart —
                    // so the column an administrator uses to decide what to revoke was
                    // reliably wrong after every server update.
                    //
                    // Written at most once an hour per device instead. That is far finer than
                    // the "unseen for 180 days" question the value is actually used to answer,
                    // and it is one write an hour rather than one a request.
                    //
                    // Throttled against when it was last *persisted*, not last seen: LastSeen
                    // is bumped in memory on every request, so comparing against it would
                    // never reach an hour on a device that is in regular use — which is every
                    // device this matters for.
                    var lastWrite = DevicePersistedAt.TryGetValue(existing.DeviceId, out var at)
                        ? at
                        : DateTime.MinValue;
                    if (now - lastWrite >= DeviceLastSeenWriteInterval)
                    {
                        DevicePersistedAt[existing.DeviceId] = now;
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
                        DeviceName = deviceName ?? "Unknown Device",
                        Client = client ?? "Unknown Client",
                        LastSeen = now,
                        MasterUserId = ownerId
                    });
                    DevicePersistedAt[deviceId] = now;
                    save = true;
                }

                // Only while we are writing anyway, and at most once a day. KnownDevices is a
                // single server-wide list that only ever grew: every phone that ever hit the
                // server stayed in it forever, and the device picker is a list an administrator
                // has to read.
                if (save) save |= PruneStaleDevices(config, now);

                if (save) Plugin.Instance?.SaveConfiguration();
            }
        }

        // ── Device housekeeping ─────────────────────────────────────────────────────

        /// <summary>When each device's LastSeen was last written to disk. See RecordDeviceActivity.</summary>
        private static readonly ConcurrentDictionary<string, DateTime> DevicePersistedAt = new();

        private static readonly TimeSpan DeviceLastSeenWriteInterval = TimeSpan.FromHours(1);

        /// <summary>How long a device may go unseen before it is dropped from the picker.</summary>
        private static readonly TimeSpan DeviceRetention = TimeSpan.FromDays(180);

        private static DateTime _lastDevicePrune = DateTime.MinValue;

        /// <summary>
        /// Drops devices nobody has used for <see cref="DeviceRetention"/>, unless some profile
        /// still names them. True when anything was removed.
        /// <para>
        /// A device on a whitelist is kept however old it is: removing it would silently widen
        /// that profile's access, because an empty <c>AllowedDeviceIds</c> means "any device".
        /// Tidying a list must never turn a restriction off.
        /// </para>
        /// <para>Caller must hold <see cref="ConfigLock"/>.</para>
        /// </summary>
        private bool PruneStaleDevices(PluginConfiguration config, DateTime now)
        {
            if (now - _lastDevicePrune < TimeSpan.FromDays(1)) return false;
            _lastDevicePrune = now;

            var removed = RemoveStaleDevices(config, now);
            if (removed > 0)
            {
                _logger.LogInformation(
                    "ProfilesPlugin: Dropped {Count} device(s) unseen for {Days} days.",
                    removed, (int)DeviceRetention.TotalDays);
            }
            return removed > 0;
        }

        /// <summary>
        /// The pruning itself, with no throttle and no logger, so it can be driven directly by
        /// a harness. Returns how many records were removed. Caller must hold ConfigLock.
        /// </summary>
        internal static int RemoveStaleDevices(PluginConfiguration config, DateTime now)
        {
            var whitelisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in config.Mappings)
                foreach (var id in m.AllowedDeviceIds)
                    if (!string.IsNullOrEmpty(id)) whitelisted.Add(id);

            return config.KnownDevices.RemoveAll(d =>
                now - d.LastSeen > DeviceRetention && !whitelisted.Contains(d.DeviceId));
        }

        /// <summary>
        /// Selects the devices a given master account may see in the "Allowed Devices" picker.
        /// <para>
        /// Two rules, and both matter:
        /// (1) Only devices recorded as owned by this master are listed. KnownDevices is a
        ///     single server-wide list, so anything looser leaks other households' hardware —
        ///     in particular, unowned (Guid.Empty) records must NOT be treated as visible,
        ///     because on an install predating device ownership every record is unowned.
        /// (2) A device already on one of this account's whitelists is always listed even if it
        ///     is switched off or has aged out of KnownDevices. The edit form rebuilds
        ///     AllowedDeviceIds from the checkboxes it rendered, so an omitted device is
        ///     silently dropped on save — and a whitelist that empties out restricts nothing.
        /// </para>
        /// Pure and static so the scoping rules can be tested directly.
        /// </summary>
        protected static List<KnownDevice> ScopeDevicesToHousehold(
            IEnumerable<KnownDevice> knownDevices,
            Guid masterUserId,
            ISet<string> whitelistedDeviceIds)
        {
            var devices = knownDevices
                .Where(d => d.MasterUserId == masterUserId
                            || whitelistedDeviceIds.Contains(d.DeviceId))
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

        protected int GetMaxProfilesForUser(Guid userId, PluginConfiguration config)
        {
            lock (ConfigLock)
            {
                var ov = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == userId);
                return ov?.MaxProfiles ?? config.MaxProfilesPerUser;
            }
        }

        protected const int MaxProfileImageBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Streams an image from disk with a cache validator.
        /// <para>
        /// All four image endpoints did <c>File(System.IO.File.ReadAllBytes(path), type)</c>
        /// — the whole file onto the managed heap, per image, per request. The profile gate
        /// renders every avatar in the household at once, so opening it allocated all of
        /// them together; a 2 MB picture goes straight to the large object heap, which is
        /// not compacted by default. <c>PhysicalFile</c> hands the path to the server's
        /// <c>SendFileAsync</c>, which streams it without the copy.
        /// </para>
        /// <para>
        /// The validator is length plus last-write time rather than a hash of the content:
        /// hashing would mean reading the whole file to avoid reading the whole file. It
        /// changes whenever the image does, which is what a validator has to do. With it,
        /// a browser that already has the picture gets a 304 and no body at all — and these
        /// are re-requested constantly, because the gate is drawn on every page load.
        /// </para>
        /// <para>
        /// <c>private</c>, not <c>public</c>: the endpoints are anonymous so the gate can
        /// render before sign-in, but the images are one household's faces and have no
        /// business in a shared proxy cache.
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

            // This overload answers If-None-Match and If-Modified-Since itself, so the 304
            // is handled by the framework rather than by a branch here that could drift.
            return PhysicalFile(path, contentType, info.LastWriteTimeUtc, etag);
        }

        // Bounds for both the server-wide limit and the per-account override. The lower bound
        // was already checked in one place and not the other; the upper bound was checked
        // nowhere, so the settings page happily saved 2,000,000,000 and the gate then tried to
        // lay out that many tiles. Anything above about a dozen is already past what the
        // "Who's Watching?" screen can show without scrolling on a TV.
        protected const int MinProfilesPerUser = 1;
        protected const int MaxProfilesPerUserLimit = 20;

        /// <summary>
        /// Null when <paramref name="value"/> is within bounds, otherwise the message to
        /// return. Says what the bound is: "must be between 1 and 20" tells an administrator
        /// what to type next, where "invalid value" sends them back to the documentation.
        /// </summary>
        protected static string? ValidateProfileLimit(int value) =>
            value < MinProfilesPerUser || value > MaxProfilesPerUserLimit
                ? $"Maximum profiles must be between {MinProfilesPerUser} and {MaxProfilesPerUserLimit}."
                : null;

        /// <summary>
        /// Minimum length of the emergency disable code. It is submitted without any
        /// authentication, so length is doing the work that a login would normally do; the
        /// rate limiter caps guessing at five an hour, but a short code would still fall.
        /// </summary>
        protected const int MinPanicCodeLength = 10;

        // ── Presentation-value validation ───────────────────────────────────────────
        // Avatar colours and image URLs are stored server-side and rendered on other
        // accounts' switcher screens via Bonfire groups. They are validated here so a
        // hostile value can never reach the client in the first place; profiles.js
        // re-validates on render as defence in depth.

        private static readonly System.Text.RegularExpressions.Regex HexColorRegex =
            new("^#[0-9a-fA-F]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        internal const string DefaultAvatarColor = "#00A4DC";

        /// <summary>
        /// Returns the colour if it is a plain 6-digit hex triplet, otherwise the default.
        /// Anything else could break out of the <c>style="..."</c> attribute it lands in.
        /// </summary>
        protected static string SanitizeAvatarColor(string? color)
            => !string.IsNullOrWhiteSpace(color) && HexColorRegex.IsMatch(color.Trim())
                ? color.Trim()
                : DefaultAvatarColor;

        /// <summary>
        /// Validates an externally supplied image URL. Only absolute http(s) URLs are
        /// accepted — a bare "starts with http" check would happily pass a value like
        /// <c>http" onerror="...</c> straight through to an img tag.
        /// </summary>
        protected static bool IsValidImageUrl(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && value.IndexOfAny(new[] { '"', '\'', '<', '>' }) < 0;

        // ── Image storage ───────────────────────────────────────────────────────────
        // Every stored image is written twice: a full-size master and a small thumbnail.
        // Both are produced by the browser's canvas before upload rather than resized here,
        // which keeps the plugin free of any image-processing dependency. The thumbnail is
        // what grids and switcher cards request — a picker showing twenty full-size avatars
        // would otherwise decode tens of megabytes of bitmap, which is enough to stall the
        // TV browsers this plugin explicitly supports.

        /// <summary>Suffix distinguishing the thumbnail file from its master.</summary>
        protected const string ThumbSuffix = "_t";

        /// <summary>
        /// Formats accepted from a data URL. The client re-encodes everything through a
        /// canvas, so this is the set it can emit, not the set a user may pick from — the
        /// browser decodes far more than this on the way in.
        /// <para>
        /// SVG is deliberately absent: it can carry script, and these files are served from
        /// the server's own origin.
        /// </para>
        /// </summary>
        private static readonly (string Mime, string Extension, string ContentType)[] StorableImageFormats =
        {
            ("image/png",  ".png",  "image/png"),
            ("image/webp", ".webp", "image/webp"),
            ("image/gif",  ".gif",  "image/gif"),
            ("image/jpeg", ".jpg",  "image/jpeg"),
        };

        /// <summary>All extensions this plugin may have written, newest scheme first.</summary>
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
        /// Decodes a <c>data:image/…;base64,…</c> payload. Returns null for anything that is
        /// not a well-formed, in-budget image in a format we are willing to store.
        /// </summary>
        protected (byte[] Bytes, string Extension)? DecodeImageDataUrl(string? dataUrl, string context)
        {
            if (string.IsNullOrEmpty(dataUrl) ||
                !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;

            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex < 0) return null;

            var header = dataUrl.Substring(0, commaIndex);
            string? extension = null;
            foreach (var f in StorableImageFormats)
            {
                if (header.Contains(f.Mime, StringComparison.OrdinalIgnoreCase)) { extension = f.Extension; break; }
            }

            // Reject rather than defaulting. Falling back to .jpg would store
            // "data:image/svg+xml,<script>…" as a JPEG and then serve it as one — the format
            // is excluded precisely because these files come back from our own origin.
            if (extension == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Rejected image for {Context} — '{Header}' is not a format this plugin stores.",
                    context, header.Length > 64 ? header.Substring(0, 64) : header);
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(dataUrl.Substring(commaIndex + 1));
                if (bytes.Length > MaxProfileImageBytes)
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: Image for {Context} is {Size} bytes, over the {Limit} byte limit. Rejected.",
                        context, bytes.Length, MaxProfileImageBytes);
                    return null;
                }
                return (bytes, extension);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: Image for {Context} was not valid base64.", context);
                return null;
            }
        }

        /// <summary>Removes every variant of a stored image, in both formats and both sizes.</summary>
        protected static void DeleteImageFiles(string folder, string baseName)
        {
            foreach (var ext in StorableImageExtensions)
            {
                foreach (var name in new[] { $"{baseName}{ext}", $"{baseName}{ThumbSuffix}{ext}" })
                {
                    var p = Path.Combine(folder, name);
                    if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                }
            }
        }

        /// <summary>
        /// Writes a master image and, when supplied, its thumbnail. Stale variants are
        /// cleared first so a format change cannot leave the previous file shadowing the new
        /// one. Returns the stored extension, or null if nothing was written.
        /// </summary>
        protected string? WriteImageFiles(string folder, string baseName, string? dataUrl, string? thumbDataUrl, string context)
        {
            var master = DecodeImageDataUrl(dataUrl, context);
            if (master == null) return null;

            Directory.CreateDirectory(folder);
            DeleteImageFiles(folder, baseName);

            System.IO.File.WriteAllBytes(Path.Combine(folder, $"{baseName}{master.Value.Extension}"), master.Value.Bytes);

            // A missing or unusable thumbnail is not fatal — the master is served in its
            // place. Falling back costs bandwidth; refusing the whole save would cost the
            // user their picture.
            var thumb = DecodeImageDataUrl(thumbDataUrl, context + " thumbnail");
            if (thumb != null)
            {
                System.IO.File.WriteAllBytes(
                    Path.Combine(folder, $"{baseName}{ThumbSuffix}{thumb.Value.Extension}"), thumb.Value.Bytes);
            }

            return master.Value.Extension;
        }

        /// <summary>
        /// Resolves a stored image on disk, preferring the thumbnail when one is asked for
        /// and falling back to the master when it does not exist.
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
        /// File name for one profile's artwork for one library. Derived from the two ids rather
        /// than stored, so the configuration cannot drift out of step with what is on disk and
        /// there is no orphaned identifier to clean up.
        /// </summary>
        protected static string LibraryArtName(Guid profileId, Guid libraryId)
            => profileId.ToString("N") + "_" + libraryId.ToString("N");

        /// <summary>
        /// True when the administrator has restricted profile pictures to the avatar library.
        /// Hiding the upload control is presentation; this is the rule.
        /// </summary>
        protected bool AreCustomAvatarsBlocked()
            => Plugin.Instance?.Configuration?.DisallowCustomAvatarUploads == true;

        /// <summary>
        /// Copies a library avatar to a profile, server-side, byte for byte.
        /// <para>
        /// This exists so the "only allow avatars from this library" setting can actually be
        /// enforced. Choosing a library picture normally means the client crops it and posts
        /// the result, which arrives looking exactly like any other upload — the server has no
        /// way to tell them apart. Passing the library id instead makes the choice verifiable:
        /// nothing is decoded, and the only bytes that can land are ones the administrator
        /// published. The cost is that a locked-down server gives up per-user cropping, which
        /// is a fair trade for a setting whose point is a consistent set.
        /// </para>
        /// </summary>
        protected string? CopyLibraryAvatarToProfile(Guid profileId, string libraryAvatarId)
        {
            return CopyLibraryAvatar(libraryAvatarId, ProfileImageFolder, profileId.ToString())
                ? $"/plugins/profiles/image/{profileId}?v={DateTime.UtcNow.Ticks}"
                : null;
        }

        /// <summary>
        /// Copies a published library avatar to <paramref name="destFolder"/> under
        /// <paramref name="baseName"/>, master and thumbnail alike. Returns false when the id
        /// is unknown or nothing is on disk for it.
        /// </summary>
        protected bool CopyLibraryAvatar(string libraryAvatarId, string destFolder, string baseName)
        {
            var config = Plugin.Instance?.Configuration;
            var item = config?.AvatarLibrary.FirstOrDefault(a =>
                string.Equals(a.Id, libraryAvatarId, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Asked for library avatar '{Avatar}', which does not exist.",
                    libraryAvatarId);
                return false;
            }

            // Confirmed before anything is removed: a listed avatar whose file has gone
            // would otherwise clear the destination and leave the caller pointing at a
            // picture that no longer exists.
            if (FindImageFile(AvatarLibraryFolder, item.Id, false) == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Library avatar {Avatar} is listed but has no file on disk.", item.Id);
                return false;
            }

            Directory.CreateDirectory(destFolder);
            DeleteImageFiles(destFolder, baseName);

            bool copiedAny = false;
            foreach (var (suffix, wantThumb) in new[] { (string.Empty, false), (ThumbSuffix, true) })
            {
                var source = FindImageFile(AvatarLibraryFolder, item.Id, wantThumb);
                // FindImageFile falls back to the master when no thumbnail exists; only copy
                // it into the thumbnail slot if it really is a distinct file.
                if (source == null) continue;
                if (wantThumb && !Path.GetFileNameWithoutExtension(source.Value.Path).EndsWith(ThumbSuffix, StringComparison.Ordinal))
                    continue;

                var extension = Path.GetExtension(source.Value.Path);
                System.IO.File.Copy(source.Value.Path, Path.Combine(destFolder, $"{baseName}{suffix}{extension}"), true);
                copiedAny = true;
            }

            if (!copiedAny)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Library avatar {Avatar} is listed but has no file on disk.", item.Id);
            }

            return copiedAny;
        }

        protected string? SaveProfileImage(Guid profileId, string? profileImageInput, string? thumbInput = null)
        {
            var folder = ProfileImageFolder;

            if (string.IsNullOrEmpty(profileImageInput))
            {
                DeleteImageFiles(folder, profileId.ToString());
                return null;
            }

            if (profileImageInput.StartsWith("/plugins/profiles/image/", StringComparison.OrdinalIgnoreCase))
                return profileImageInput;

            // Enforced here, not only by hiding the upload control, so a hand-written request
            // cannot walk around a setting the administrator is relying on. A locked-down
            // server accepts pictures solely through CopyLibraryAvatarToProfile.
            if (AreCustomAvatarsBlocked())
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Rejected a profile picture for {Id} — this server allows library avatars only.",
                    profileId);
                return null;
            }

            if (IsValidImageUrl(profileImageInput)) return profileImageInput.Trim();

            if (profileImageInput.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var ext = WriteImageFiles(folder, profileId.ToString(), profileImageInput, thumbInput,
                        $"profile {profileId}");
                    if (ext != null)
                    {
                        // The cache-buster is what makes a changed picture appear immediately;
                        // without it browsers keep showing the previous one at the same URL.
                        return $"/plugins/profiles/image/{profileId}?v={DateTime.UtcNow.Ticks}";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProfilesPlugin: Failed to save image for {Id}.", profileId);
                }
                return null;
            }

            // Unrecognised shape — reject rather than storing an arbitrary string that
            // would later be rendered into an img src.
            _logger.LogWarning(
                "ProfilesPlugin: Rejected profile image for {Id} — not a data:image payload or a valid http(s) URL.",
                profileId);
            return null;
        }

        protected string GenerateSecureCode()
        {
            const string chars = "ABCDEFGHJKLMNOPQRSTUVWXYZ23456789";
            // RandomNumberGenerator.GetInt32 is rejection-sampled, so unlike (byte % 33)
            // every character is uniformly distributed over the alphabet.
            return new string(Enumerable.Range(0, 6)
                .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)])
                .ToArray());
        }

        protected HashSet<Guid> GetMasterAccessibleFolders(UserPolicy masterPolicy)
        {
            HashSet<Guid> masterAccessibleFolders;
            if (masterPolicy.EnableAllFolders)
            {
                masterAccessibleFolders = _libraryManager.GetVirtualFolders()
                    .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .ToHashSet();
            }
            else
            {
                masterAccessibleFolders = (masterPolicy.EnabledFolders ?? Array.Empty<Guid>()).ToHashSet();
            }
            var masterBlocked = masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>();
            masterAccessibleFolders.ExceptWith(masterBlocked);
            return masterAccessibleFolders;
        }

        // ── Tag-based filtering ─────────────────────────────────────────────────────
        // Jellyfin enforces BlockedTags/AllowedTags in BaseItem.IsVisibleViaTags, matching
        // against an item's *inherited* tags (its own, plus every parent, plus the collection
        // folder). Tagging a series or a whole library therefore cascades to everything inside.

        /// <summary>
        /// Trims, drops blanks, and de-duplicates a tag list case-insensitively.
        /// Never returns null, so callers can treat "unset" and "empty" alike.
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
        /// Resolves the tag policy actually written to a sub-profile's Jellyfin user, clamped so
        /// the profile can never see more than its master.
        /// Blocked tags are additive (a sub-profile cannot unblock what the master blocks); the
        /// allow-list is intersected with the master's when the master has one, so a sub-profile
        /// can narrow it but never widen it.
        /// </summary>
        protected (string[] Blocked, string[] Allowed) ResolveTagPolicy(
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

                // An empty intersection would hide the profile's entire library. The create/update
                // endpoints reject that up front; this is the defensive path for a master whose
                // allow-list changed after the sub-profile was configured.
                if (allowed.Count == 0) allowed = masterAllowedTags;
            }

            return (blocked.ToArray(), allowed.ToArray());
        }

        /// <summary>
        /// Validates a requested allow-list against the master's. Returns an error message when the
        /// request would leave the profile with nothing visible, otherwise null.
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
            return group.MemberUserIds.Select(id => (object)new
            {
                UserId = id,
                Username = _userManager.GetUserById(id)?.Username ?? "Unknown User"
            }).ToList();
        }
    }
}
