using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Admin, device management, image-serving, and audit-log endpoints.
    /// All helpers are inherited from ProfilesBaseController.
    /// </summary>
    [Route("plugins/profiles")]
    public class AdminController : ProfilesBaseController
    {
        public AdminController(
            IUserManager userManager,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            INetworkManager networkManager,
            ILogger<AdminController> logger)
            : base(userManager, sessionManager, libraryManager, networkManager, logger)
        {
        }

        // ── Device management ───────────────────────────────────────────────────────

        [HttpGet("devices")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<KnownDevice>> GetDevices()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var devices = config.KnownDevices
                .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(d => d.LastSeen).First())
                .OrderByDescending(d => d.LastSeen)
                .ToList();
            return Ok(devices);
        }

        [HttpPost("devices/delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult DeleteDevice([FromBody] DeleteDeviceRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can delete devices.");

            if (string.IsNullOrEmpty(request.DeviceId))
                return BadRequest("DeviceId is required.");

            lock (config)
            {
                var toRemove = config.KnownDevices
                    .Where(d => string.Equals(d.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var d in toRemove)
                    config.KnownDevices.Remove(d);

                // Remove from any profile's allowed device list
                foreach (var mapping in config.Mappings)
                    mapping.AllowedDeviceIds?.RemoveAll(id =>
                        string.Equals(id, request.DeviceId, StringComparison.OrdinalIgnoreCase));

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        // ── Profile image serving (unauthenticated — [AllowAnonymous] inherited from base) ──

        [HttpGet("image/{profileId}")]
        public ActionResult GetProfileImage(Guid profileId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return NotFound();

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            if (mapping == null || string.IsNullOrEmpty(mapping.ProfileImage)) return NotFound();

            var instance = Plugin.Instance;
            if (instance == null) return NotFound();

            var folder = Path.Combine(instance.AppPaths.DataPath, "plugins", "ProfilesManagement");
            var candidates = new[]
            {
                (Path.Combine(folder, $"{profileId}.jpg"), "image/jpeg"),
                (Path.Combine(folder, $"{profileId}.png"), "image/png"),
                (Path.Combine(folder, $"{profileId}.gif"), "image/gif"),
            };

            foreach (var (filePath, contentType) in candidates)
            {
                if (System.IO.File.Exists(filePath))
                    return File(System.IO.File.ReadAllBytes(filePath), contentType);
            }

            if (mapping.ProfileImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Redirect(mapping.ProfileImage);

            return NotFound();
        }

        // ── Admin endpoints ─────────────────────────────────────────────────────────

        [HttpPost("admin/set-profile-limit")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult SetProfileLimit([FromBody] SetProfileLimitRequest request)
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
                return Unauthorized("Only administrators can update profile limits.");

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            lock (config)
            {
                if (request.MaxProfiles.HasValue)
                {
                    if (request.MaxProfiles.Value < 1)
                        return BadRequest("Maximum profiles must be at least 1.");

                    var existing = config.UserProfileLimitOverrides.FirstOrDefault(o => o.UserId == request.UserId);
                    if (existing != null)
                        existing.MaxProfiles = request.MaxProfiles.Value;
                    else
                        config.UserProfileLimitOverrides.Add(new UserProfileLimitOverride
                        {
                            UserId = request.UserId,
                            MaxProfiles = request.MaxProfiles.Value
                        });
                }
                else
                {
                    config.UserProfileLimitOverrides.RemoveAll(o => o.UserId == request.UserId);
                }

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        [HttpGet("admin/audit-logs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<AuditLogEntry>> GetAuditLogs()
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
                return Unauthorized("Only administrators can view audit logs.");

            lock (AuditLogLock)
            {
                return Ok(ReadAuditLogs().OrderByDescending(l => l.Timestamp).ToList());
            }
        }
    }
}
