using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Mail
{
    /// <summary>
    /// Notices a main account has asked for, sent to its confirmed address. Each one is
    /// fire-and-forget: the request or sign-in that caused it never waits on a mail server,
    /// and a failure is logged, never thrown.
    ///
    /// <para>Throttled per account and kind, so a burst (a child trying PINs on two
    /// televisions) is one mail, not ten.</para>
    /// </summary>
    internal static class Notifications
    {
        internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

        private static readonly ConcurrentDictionary<string, DateTime> LastSent = new();

        /// <summary>For the harness: the sends started, so a test can wait for them.</summary>
        internal static readonly ConcurrentQueue<Task> Pending = new();

        /// <summary>A PIN somewhere in <paramref name="userId"/>'s household was locked.</summary>
        internal static void PinLocked(Guid userId, string name, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            var mainId = row != null && row.MasterUserId != Guid.Empty ? row.MasterUserId : userId;
            Send(config, mainId, "lockout", a => a.NotifyLockout,
                "email.lockout.subject", "email.lockout.body",
                new Dictionary<string, string> { ["name"] = name }, logger);
        }

        /// <summary><paramref name="mainId"/>'s household was signed in on a device for the first time.</summary>
        internal static void NewDevice(Guid mainId, string deviceName, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            Send(config, mainId, "device", a => a.NotifyNewDevice,
                "email.newDevice.subject", "email.newDevice.body",
                new Dictionary<string, string> { ["device"] = string.IsNullOrWhiteSpace(deviceName) ? "?" : deviceName }, logger);
        }

        /// <summary>A profile in <paramref name="mainId"/>'s household asked for more time.
        /// Throttled per profile, so two children asking are two mails.</summary>
        internal static void TimeAsked(Guid mainId, Guid profileId, string name, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            Send(config, mainId, "asked-" + profileId.ToString("N"), a => a.NotifyTimeAsked,
                "email.timeAsked.subject", "email.timeAsked.body",
                new Dictionary<string, string> { ["name"] = name }, logger);
        }

        /// <summary>A profile reached its daily limit. The caller sends it once a day.</summary>
        internal static void LimitReached(Guid mainId, Guid profileId, string name, int minutes, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            Send(config, mainId, "limit-" + profileId.ToString("N"), a => a.NotifyLimitReached,
                "email.limitReached.subject", "email.limitReached.body",
                new Dictionary<string, string> { ["name"] = name, ["minutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) }, logger);
        }

        private static void Send(PluginConfiguration config, Guid mainId, string kind, Func<AccountEmail, bool> wanted,
            string subjectKey, string bodyKey, Dictionary<string, string> values, ILogger? logger)
        {
            if (!Features.Available(Features.Email, config)) return;
            var account = config.AccountEmails.FirstOrDefault(a => a.UserId == mainId);
            if (account == null || string.IsNullOrEmpty(account.Address) || !wanted(account)) return;

            var key = kind + ":" + mainId.ToString("N");
            var now = DateTime.UtcNow;
            if (LastSent.TryGetValue(key, out var last) && now - last < Quiet) return;
            LastSent[key] = now;
            if (LastSent.Count > 5000) LastSent.Clear();

            var settings = config.Email;
            var subject = EmailText.Get(subjectKey, account.Language, values);
            var body = EmailText.Get(bodyKey, account.Language, values);
            var task = BackgroundWork.Run(async _ =>
            {
                try
                {
                    await Mailer.SendAsync(settings, account.Address, subject, body).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("ProfilesPlugin: Couldn't send a {Kind} notice: {Reason}", kind, Mailer.Describe(ex));
                }
            });
            Pending.Enqueue(task);
            while (Pending.Count > 50 && Pending.TryDequeue(out _)) { }
        }
    }
}
