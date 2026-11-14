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
    /// <para>Throttled per account, kind and subject: a burst about the same thing (a child
    /// trying PINs on one profile from two televisions) is one mail, not ten, while a
    /// different profile or device is its own notice. A new-device notice also has a ceiling
    /// per account, because a device id is client-supplied and keying on it alone would let
    /// one client mail the household without limit.</para>
    /// </summary>
    internal static class Notifications
    {
        internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

        private static readonly ConcurrentDictionary<string, DateTime> LastSent = new();

        /// <summary>For the harness: runs between the quiet-period check and the stamp.</summary>
        internal static Action? BetweenCheckAndStamp = null;

        /// <summary>New-device notices one account may get in one quiet period.</summary>
        internal const int DeviceNoticesPerPeriod = 5;

        private static readonly object BudgetLock = new();
        private static readonly Dictionary<Guid, (DateTime Start, int Count)> DeviceBudget = new();

        /// <summary>Longest device name a notice will carry.</summary>
        internal const int DeviceNameMax = 40;

        /// <summary>A client-set name made safe to print in a mail: no control characters
        /// (a line break would start a new line of text the sender did not write), runs of
        /// white space collapsed, and cut to <see cref="DeviceNameMax"/>.</summary>
        internal static string CleanDeviceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var sb = new System.Text.StringBuilder();
            foreach (var c0 in name)
            {
                var c = char.IsControl(c0) || char.IsWhiteSpace(c0) || c0 == '\u2028' || c0 == '\u2029' ? ' ' : c0;
                if (c == ' ' && (sb.Length == 0 || sb[sb.Length - 1] == ' ')) continue;
                sb.Append(c);
                if (sb.Length >= DeviceNameMax) break;
            }
            var clean = sb.ToString().Trim();
            return clean.Length == 0 ? "?" : clean;
        }

        /// <summary>For the harness: the sends started, so a test can wait for them.</summary>
        internal static readonly ConcurrentQueue<Task> Pending = new();

        /// <summary>A PIN somewhere in <paramref name="userId"/>'s household was locked.</summary>
        internal static void PinLocked(Guid userId, string name, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            var mainId = row != null && row.MasterUserId != Guid.Empty ? row.MasterUserId : userId;
            Send(config, mainId, "lockout", "lockout-" + userId.ToString("N"), a => a.NotifyLockout,
                "email.lockout.subject", "email.lockout.body",
                new Dictionary<string, string> { ["name"] = name }, logger);
        }

        /// <summary><paramref name="mainId"/>'s household was signed in on a device for the first time.</summary>
        internal static void NewDevice(Guid mainId, string deviceId, string deviceName, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            // Per device, so a second unknown device right after the first is still reported.
            Send(config, mainId, "device", "device-" + (deviceId ?? string.Empty).Trim().ToLowerInvariant(), a => a.NotifyNewDevice,
                "email.newDevice.subject", "email.newDevice.body",
                new Dictionary<string, string> { ["device"] = CleanDeviceName(deviceName) }, logger);
        }

        /// <summary>A profile in <paramref name="mainId"/>'s household asked for more time.
        /// Throttled per profile, so two children asking are two mails.</summary>
        internal static void TimeAsked(Guid mainId, Guid profileId, string name, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            Send(config, mainId, "asked", "asked-" + profileId.ToString("N"), a => a.NotifyTimeAsked,
                "email.timeAsked.subject", "email.timeAsked.body",
                new Dictionary<string, string> { ["name"] = name }, logger);
        }

        /// <summary>A profile reached its daily limit. The caller sends it once a day.</summary>
        internal static void LimitReached(Guid mainId, Guid profileId, string name, int minutes, ILogger? logger)
        {
            var config = ProfilesBaseController.ReadConfigSnapshot();
            if (config == null) return;
            Send(config, mainId, "limit", "limit-" + profileId.ToString("N"), a => a.NotifyLimitReached,
                "email.limitReached.subject", "email.limitReached.body",
                new Dictionary<string, string> { ["name"] = name, ["minutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) }, logger);
        }

        private static void Send(PluginConfiguration config, Guid mainId, string kind, string subject, Func<AccountEmail, bool> wanted,
            string subjectKey, string bodyKey, Dictionary<string, string> values, ILogger? logger)
        {
            if (!Features.Available(Features.Email, config)) return;
            var account = config.AccountEmails.FirstOrDefault(a => a.UserId == mainId);
            if (account == null || string.IsNullOrEmpty(account.Address) || !wanted(account)) return;

            var key = subject + ":" + mainId.ToString("N");
            var now = DateTime.UtcNow;
            // One step: two TVs failing a PIN together both read "not sent yet" when the check
            // and the stamp were separate, and the owner got two mails.
            lock (BudgetLock)
            {
                if (LastSent.TryGetValue(key, out var last) && now - last < Quiet) return;
                if (kind == "device" && !TakeDeviceBudget(mainId, now)) return;
                BetweenCheckAndStamp?.Invoke();
                LastSent[key] = now;
                if (LastSent.Count > 5000) LastSent.Clear();
            }

            var settings = config.Email;
            var mailSubject = EmailText.Get(subjectKey, account.Language, values);
            var body = EmailText.Get(bodyKey, account.Language, values);
            var task = BackgroundWork.Run(async _ =>
            {
                try
                {
                    await Mailer.SendAsync(settings, account.Address, mailSubject, body).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A mail that never left is not a notice the owner has had: the next real
                    // event must not be dropped because of it.
                    LastSent.TryRemove(new KeyValuePair<string, DateTime>(key, now));
                    if (kind == "device") RefundDeviceBudget(mainId);
                    logger?.LogWarning("ProfilesPlugin: Couldn't send a {Kind} notice: {Reason}", kind, Mailer.Describe(ex));
                }
            });
            Pending.Enqueue(task);
            while (Pending.Count > 50 && Pending.TryDequeue(out _)) { }
        }

        private static bool TakeDeviceBudget(Guid mainId, DateTime now)
        {
            lock (BudgetLock)
            {
                if (DeviceBudget.Count > 5000) DeviceBudget.Clear();
                if (!DeviceBudget.TryGetValue(mainId, out var b) || now - b.Start >= Quiet) b = (now, 0);
                if (b.Count >= DeviceNoticesPerPeriod) return false;
                DeviceBudget[mainId] = (b.Start, b.Count + 1);
                return true;
            }
        }

        private static void RefundDeviceBudget(Guid mainId)
        {
            lock (BudgetLock)
            {
                if (DeviceBudget.TryGetValue(mainId, out var b) && b.Count > 0) DeviceBudget[mainId] = (b.Start, b.Count - 1);
            }
        }
    }
}
