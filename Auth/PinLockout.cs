using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// What happens when a PIN is locked by too many wrong tries: one Activity log line and
    /// the lockout email, however the guesses arrived.
    /// <para>
    /// This lived on the controller, so only the web switcher's lockouts were ever written
    /// down. The same PIN is guessed at from apps through the authentication provider, and
    /// there the lock was a log line nobody reads. Both doors call this, and share one
    /// throttle, so a lock is announced once however many doors it is hit through.
    /// </para>
    /// </summary>
    internal static class PinLockout
    {
        // When each lockout was last written to the Activity log. Every refused attempt
        // after the limit is another refusal, and one line per guess would bury the log.
        private static readonly ConcurrentDictionary<string, DateTime> Logged = new();

        /// <summary>
        /// Announces a lock, at most once per key every 15 minutes. <paramref name="services"/>
        /// supplies the user and activity managers; either may be missing.
        /// </summary>
        internal static void Raise(Guid userId, string key, IServiceProvider? services, IUserManager? users, ILogger? logger)
        {
            var now = DateTime.UtcNow;
            if (Logged.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(15)) return;
            if (Logged.Count > 1000) Logged.Clear();
            Logged[key] = now;

            try
            {
                var name = ProfilesBaseController.ReadConfigSnapshot()?.Mappings
                               .FirstOrDefault(m => m.ProfileUserId == userId)?.ProfileName
                           ?? (users ?? services?.GetService(typeof(IUserManager)) as IUserManager)?.GetUserById(userId)?.Username
                           ?? "A profile";

                if (services?.GetService(typeof(MediaBrowser.Model.Activity.IActivityManager))
                    is MediaBrowser.Model.Activity.IActivityManager activity)
                {
                    var entry = new Jellyfin.Database.Implementations.Entities.ActivityLog(
                        name + "'s PIN is locked after too many wrong tries", "BonfirePinLocked", userId)
                    {
                        LogSeverity = LogLevel.Warning,
                    };
                    _ = activity.CreateAsync(entry).ContinueWith(
                        t => logger?.LogDebug(t.Exception, "ProfilesPlugin: Could not write to the activity log."),
                        TaskContinuationOptions.OnlyOnFaulted);
                }

                Mail.Notifications.PinLocked(userId, name, logger);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "ProfilesPlugin: Could not announce a PIN lockout.");
            }
        }
    }
}
