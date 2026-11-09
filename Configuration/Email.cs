using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// The server's outgoing mail, set on the dashboard. Bonfire sends three kinds of mail
    /// through it: the code that confirms a main account's address, the code that resets a
    /// forgotten main PIN, and notices a main account has asked for.
    ///
    /// <para><b>Verified is derived, not stored as a flag.</b> A test that succeeds records
    /// <see cref="VerifiedFingerprint"/>, a hash of every field the connection uses. Change
    /// any of them and the stored hash no longer matches, so the setup reads as unverified
    /// until it is tested again — nothing has to remember to clear a flag on each edit.</para>
    ///
    /// <para><b>The password is stored as typed.</b> It has to be: the server sends it to
    /// the mail server. It sits in the plugin's configuration file with the rest, is never
    /// sent back to the dashboard (only whether one is set), and the dashboard says to use
    /// an app password rather than an account's own.</para>
    /// </summary>
    public class EmailSettings
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 587;

        /// <summary>One of <see cref="EmailSecurity"/>.</summary>
        public string Security { get; set; } = EmailSecurity.StartTls;

        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string FromAddress { get; set; } = string.Empty;

        // The name mail is sent under used to be a setting. It is "Bonfire" now, always
        // (Mailer.FromDisplayName). A file that still has the element loads; the next save
        // leaves it out. Nothing in the connection depended on it, so the fingerprint below
        // is unchanged and a verified setup stays verified.

        /// <summary>The <see cref="Fingerprint"/> at the last test that went through.</summary>
        public string VerifiedFingerprint { get; set; } = string.Empty;

        /// <summary>Enough filled in to try.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

        /// <summary>Configured, and tested since anything was last changed.</summary>
        public bool IsVerified => IsConfigured
            && !string.IsNullOrEmpty(VerifiedFingerprint)
            && string.Equals(VerifiedFingerprint, Fingerprint(), StringComparison.Ordinal);

        /// <summary>Every field the connection depends on, hashed together.</summary>
        public string Fingerprint()
        {
            var joined = string.Join("\n", Host.Trim().ToLowerInvariant(), Port, EmailSecurity.Normalize(Security),
                Username, Password, FromAddress.Trim().ToLowerInvariant());
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
        }

        public EmailSettings CopyForReading() => (EmailSettings)MemberwiseClone();
    }

    /// <summary>How the connection is secured.</summary>
    public static class EmailSecurity
    {
        /// <summary>Plain connection upgraded with STARTTLS. Port 587, or 25.</summary>
        public const string StartTls = "starttls";

        /// <summary>No encryption: a relay on the same machine or network.</summary>
        public const string None = "none";

        public static bool IsKnown(string? value) =>
            string.Equals(value, StartTls, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, None, StringComparison.OrdinalIgnoreCase);

        public static string Normalize(string? value) =>
            string.Equals(value, None, StringComparison.OrdinalIgnoreCase) ? None : StartTls;
    }

    /// <summary>
    /// A main account's confirmed address. Only confirmed addresses are stored here: one
    /// waiting for its code lives in memory with the code (<c>EmailCodes</c>), so an address
    /// nobody confirmed is never written down, and never mailed again.
    /// </summary>
    public class AccountEmail
    {
        public Guid UserId { get; set; }
        public string Address { get; set; } = string.Empty;
        public DateTime ConfirmedUtc { get; set; }

        /// <summary>The language the account was using when it confirmed, for notices sent
        /// later with no request to read it from. Empty is English.</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>Mail when a PIN in the household is locked by wrong tries.</summary>
        public bool NotifyLockout { get; set; } = true;

        /// <summary>Mail when the household is signed in on a device for the first time.</summary>
        public bool NotifyNewDevice { get; set; } = true;

        /// <summary>Mail when a profile asks for more time.</summary>
        public bool NotifyTimeAsked { get; set; } = true;

        /// <summary>Mail when a profile reaches its daily limit. Off by default: it is daily.</summary>
        public bool NotifyLimitReached { get; set; }

        public AccountEmail Copy() => (AccountEmail)MemberwiseClone();
    }

    /// <summary>
    /// Which features need which others (Logan's dependency rule): a feature can't be turned
    /// on — by the administrator, in Settings, or in first-time setup — until what it needs
    /// works. One table, read by the routes that enforce it, by /info for clients, and by
    /// the dashboard.
    /// </summary>
    public static class Features
    {
        /// <summary>Outgoing mail is set up and has been tested.</summary>
        public const string Email = "email";

        /// <summary>A main account can reset a forgotten PIN by email.</summary>
        public const string PinResetByEmail = "pinResetByEmail";

        /// <summary>Feature → what it needs. Nothing listed needs nothing.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Requires = new Dictionary<string, string[]>
        {
            [PinResetByEmail] = new[] { Email },
        };

        /// <summary>Whether this feature's own switch is on, ignoring what it needs.</summary>
        private static bool SwitchedOn(string feature, PluginConfiguration config) => feature switch
        {
            Email => config.Email?.IsVerified == true,
            PinResetByEmail => config.EnablePinResetByEmail,
            _ => false
        };

        /// <summary>The first thing this feature needs that isn't working, or null.</summary>
        public static string? MissingFor(string feature, PluginConfiguration config)
        {
            if (!Requires.TryGetValue(feature, out var needs)) return null;
            return needs.FirstOrDefault(n => !Available(n, config));
        }

        /// <summary>On, and everything it needs is too.</summary>
        public static bool Available(string feature, PluginConfiguration config) =>
            SwitchedOn(feature, config) && MissingFor(feature, config) == null;
    }
}
