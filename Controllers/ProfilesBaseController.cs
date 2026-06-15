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

        protected string HashPin(string? pin)
        {
            if (string.IsNullOrEmpty(pin)) return string.Empty;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(pin));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // ── Cross-version compatibility helpers ─────────────────────────────────────
        // IUserManager.Users was renamed to GetUsers() in Jellyfin 10.11.7.
        // We compile against 10.11.6 and use reflection to call whichever is present.

        protected IEnumerable<Jellyfin.Database.Implementations.Entities.User> GetAllUsers()
        {
            var type = _userManager.GetType();
            var method = type.GetMethod("GetUsers",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
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
            var prop = type.GetProperty("Users",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null)
                return (IEnumerable<Jellyfin.Database.Implementations.Entities.User>)prop.GetValue(_userManager)!;

            _logger.LogError("ProfilesPlugin: Could not resolve user list from IUserManager.");
            return Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>();
        }

        protected Task ChangePasswordCompat(
            Jellyfin.Database.Implementations.Entities.User user, string newPassword)
        {
            var type = _userManager.GetType();
            var byGuid = type.GetMethod("ChangePassword",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(Guid), typeof(string) }, null);
            if (byGuid != null)
                return (Task)byGuid.Invoke(_userManager, new object[] { user.Id, newPassword })!;

            var byUser = type.GetMethod("ChangePassword",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { user.GetType(), typeof(string) }, null);
            if (byUser != null)
                return (Task)byUser.Invoke(_userManager, new object[] { user, newPassword })!;

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
                    return Path.GetTempPath(); // harmless fallback — writes silently to temp
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
                if (logs.Count > 1000)
                    logs = logs.OrderByDescending(l => l.Timestamp).Take(1000).ToList();
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
                }
                else
                {
                    // First time we've seen this device — persist it.
                    config.KnownDevices.Add(new KnownDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName ?? "Unknown Device",
                        Client = client ?? "Unknown Client",
                        LastSeen = DateTime.UtcNow
                    });
                    Plugin.Instance?.SaveConfiguration();
                }
            }
        }

        protected HashSet<Guid> GetLinkedMasterUserIds(Guid masterUserId, PluginConfiguration config)
        {
            var linked = new HashSet<Guid> { masterUserId };
            foreach (var g in config.BonfireGroups.Where(g => g.OwnerUserId == masterUserId))
                foreach (var id in g.MemberUserIds) linked.Add(id);
            foreach (var g in config.BonfireGroups.Where(g => g.MemberUserIds.Contains(masterUserId)))
            {
                linked.Add(g.OwnerUserId);
                foreach (var id in g.MemberUserIds) linked.Add(id);
            }
            return linked;
        }

        protected int GetMaxProfilesForUser(Guid userId, PluginConfiguration config)
        {
            var ov = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == userId);
            return ov?.MaxProfiles ?? config.MaxProfilesPerUser;
        }

        protected const int MaxProfileImageBytes = 2 * 1024 * 1024;

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

            if (profileImageInput.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return profileImageInput;
            if (profileImageInput.StartsWith("/plugins/profiles/image/", StringComparison.OrdinalIgnoreCase)) return profileImageInput;

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
            return profileImageInput;
        }

        protected string GenerateSecureCode()
        {
            const string chars = "ABCDEFGHJKLMNOPQRSTUVWXYZ23456789";
            var bytes = new byte[6];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
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
