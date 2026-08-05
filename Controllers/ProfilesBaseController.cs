using System;
using System.Collections.Concurrent;
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
                lock (config)
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

            lock (AuditLogLock)
            {
                var logs = ReadAuditLogs();
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
                    logs = logs.OrderByDescending(l => l.Timestamp)
                               .Take(1000)
                               .OrderBy(l => l.Timestamp)
                               .ToList();
                WriteAuditLogs(logs);
            }
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

            lock (config)
            {
                var existing = config.KnownDevices.FirstOrDefault(d =>
                    string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    // Update in-memory only — LastSeen is informational and does not need
                    // to trigger a full PluginConfiguration.xml rewrite on every request.
                    existing.LastSeen = DateTime.UtcNow;
                    existing.DeviceName = deviceName ?? existing.DeviceName;
                    existing.Client = client ?? existing.Client;

                    // Claim ownership for records written before MasterUserId existed. This is
                    // the one case worth persisting, so the migration happens exactly once.
                    if (existing.MasterUserId == Guid.Empty && ownerId != Guid.Empty)
                    {
                        existing.MasterUserId = ownerId;
                        Plugin.Instance?.SaveConfiguration();
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
                        LastSeen = DateTime.UtcNow,
                        MasterUserId = ownerId
                    });
                    Plugin.Instance?.SaveConfiguration();
                }
            }
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
            lock (config)
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
            lock (config)
            {
                var ov = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == userId);
                return ov?.MaxProfiles ?? config.MaxProfilesPerUser;
            }
        }

        protected const int MaxProfileImageBytes = 2 * 1024 * 1024;

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

        protected string? SaveProfileImage(Guid profileId, string? profileImageInput)
        {
            var pluginDataFolder = Path.Combine(
                Plugin.Instance?.AppPaths.DataPath ?? Path.GetTempPath(),
                "plugins", "ProfilesManagement");

            if (string.IsNullOrEmpty(profileImageInput))
            {
                foreach (var ext in new[] { ".jpg", ".png", ".gif" })
                {
                    var p = Path.Combine(pluginDataFolder, $"{profileId}{ext}");
                    if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                }
                return null;
            }

            if (profileImageInput.StartsWith("/plugins/profiles/image/", StringComparison.OrdinalIgnoreCase))
                return profileImageInput;

            if (IsValidImageUrl(profileImageInput)) return profileImageInput.Trim();

            if (profileImageInput.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var commaIndex = profileImageInput.IndexOf(',');
                    if (commaIndex >= 0)
                    {
                        var mimePart = profileImageInput.Substring(0, commaIndex);
                        var bytes = Convert.FromBase64String(profileImageInput.Substring(commaIndex + 1));

                        if (bytes.Length > MaxProfileImageBytes)
                        {
                            _logger.LogWarning("ProfilesPlugin: Image for {Id} exceeds 2 MB limit. Rejected.", profileId);
                            return null;
                        }

                        string ext = mimePart.Contains("image/png") ? ".png"
                                   : mimePart.Contains("image/gif") ? ".gif"
                                   : ".jpg";

                        Directory.CreateDirectory(pluginDataFolder);
                        foreach (var old in new[] { ".jpg", ".png", ".gif" })
                        {
                            var op = Path.Combine(pluginDataFolder, $"{profileId}{old}");
                            if (System.IO.File.Exists(op)) System.IO.File.Delete(op);
                        }
                        System.IO.File.WriteAllBytes(Path.Combine(pluginDataFolder, $"{profileId}{ext}"), bytes);
                        return $"/plugins/profiles/image/{profileId}?v={DateTime.UtcNow.Ticks}";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProfilesPlugin: Failed to save image for {Id}.", profileId);
                }
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
