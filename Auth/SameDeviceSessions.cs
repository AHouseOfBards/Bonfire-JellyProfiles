using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Ends an account's own leftover sessions on one physical device, before a switch signs
    /// it in there again (issue #41).
    ///
    /// <para><b>Why a switch needs this.</b> Jellyfin's
    /// <c>SessionManager.AuthenticateNewSessionInternal</c> counts the user's in-memory sessions
    /// and throws "User is at their maximum number of sessions" <i>before</i>
    /// <c>GetAuthorizationToken</c> logs out the old row for the same device. So with
    /// MaxActiveSessions set, the account's previous session on this device counts against the
    /// limit it is about to replace. An in-memory session is removed only by a logout, a
    /// restart, or its web socket closing, so one that never had a socket stays.</para>
    ///
    /// <para>Jellyfin for Android makes it worse. Its device id is <c>base + userId</c> once
    /// someone is signed in, read once at page load. The picker, signed in as the main account,
    /// switches with <c>base + mainId</c>. After the reload the profile's requests carry
    /// <c>base + profileId</c>, a second session with no device row of its own, so deleting the
    /// device in the dashboard does not end it. Every app restart left the profile another
    /// session until the limit was reached; then the switch failed, the page reloaded into the
    /// picker, and it failed again.</para>
    ///
    /// <para><b>What is ended:</b> only the target account's sessions, and only on ids
    /// <see cref="DeviceRegistry.IsSamePhysicalDevice"/> places on this device. Its device rows
    /// are deleted rather than passed to <c>ISessionManager.Logout(Device)</c>, which ends every
    /// session on the row's device id whoever owns it, possibly the main account's own picker
    /// session. The row for the exact id being switched with is left to Jellyfin, which logs it
    /// out a moment later as it always has.</para>
    /// </summary>
    internal static class SameDeviceSessions
    {
        /// <summary>Returns how many sessions and rows were ended.</summary>
        public static async Task<int> EndAsync(
            ISessionManager? sessions,
            IDeviceManager? devices,
            PluginConfiguration? config,
            Guid userId,
            string? deviceId,
            ILogger logger)
        {
            if (sessions == null || userId == Guid.Empty || string.IsNullOrWhiteSpace(deviceId)) return 0;
            var ended = 0;

            var own = (sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                .Where(s => s.UserId.Equals(userId) && DeviceRegistry.IsSamePhysicalDevice(deviceId, s.DeviceId, config))
                .ToList();
            foreach (var s in own)
            {
                try
                {
                    await sessions.ReportSessionEnded(s.Id).ConfigureAwait(false);
                    ended++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ProfilesPlugin: could not end session {Session} before a switch.", s.Id);
                }
            }

            if (devices != null)
            {
                var rows = (devices.GetDevices(new DeviceQuery { UserId = userId })?.Items
                        ?? (System.Collections.Generic.IReadOnlyList<Jellyfin.Database.Implementations.Entities.Security.Device>)Array.Empty<Jellyfin.Database.Implementations.Entities.Security.Device>())
                    .Where(d => !string.Equals(d.DeviceId?.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase)
                        && DeviceRegistry.IsSamePhysicalDevice(deviceId, d.DeviceId, config))
                    .ToList();
                foreach (var row in rows)
                {
                    try
                    {
                        await devices.DeleteDevice(row).ConfigureAwait(false);
                        ended++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "ProfilesPlugin: could not remove device {Device} before a switch.", row.DeviceId);
                    }
                }
            }

            if (ended > 0)
            {
                logger.LogInformation(
                    "ProfilesPlugin: ended {Count} earlier session(s) of {User} on this device before switching to it.",
                    ended, userId);
            }

            return ended;
        }
    }
}
