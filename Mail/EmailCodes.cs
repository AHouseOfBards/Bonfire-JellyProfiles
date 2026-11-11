using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Profiles.Mail
{
    /// <summary>
    /// One-time codes sent by mail: six digits, fifteen minutes, five tries.
    ///
    /// <para><b>A code, not a link.</b> Bonfire's screens run on televisions. A link opens
    /// in the phone's browser, a long way from the TV that asked; six digits read off the
    /// phone and typed with a remote work everywhere.</para>
    ///
    /// <para><b>Kept in memory.</b> A code lives fifteen minutes, so a restart costs at
    /// most a second request, and nothing secret or unconfirmed is ever written to the
    /// configuration file. Only the hash is held, compared in fixed time.</para>
    ///
    /// <para>Five wrong tries spend the code: a million possibilities against five
    /// guesses per code, and sending a new code is rate-limited separately.</para>
    ///
    /// <para><b>The worst case, written down (R-1064).</b> A signed-in main account that
    /// knows its own PIN can send five codes an hour and try five guesses on each: 25 guesses
    /// an hour, about 220,000 a year, which is roughly a 22% chance of hitting one code if
    /// someone guessed nonstop for twelve months from a signed-in session. The owner is
    /// mailed whenever the PIN is reset or the address changes, so it would not go unseen.
    /// Accepted rather than tightened: eight digits are harder to read off a phone and type
    /// with a remote, and a lower send limit would lock out a real typo or two.</para>
    /// </summary>
    internal static class EmailCodes
    {
        internal const string ConfirmAddress = "confirm";
        internal const string ResetPin = "reset";

        internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
        internal const int MaxTries = 5;

        private sealed class Entry
        {
            public byte[] Hash = Array.Empty<byte>();
            public string Address = string.Empty;
            public DateTime ExpiresUtc;
            public int Tries;
        }

        // One lock for every read and write. Locking the entry instead let a check holding
        // an old code remove the new one issued while it ran: removal is by key, and the
        // key by then named the new code. Codes are a handful per hour; one lock costs
        // nothing.
        private static readonly object CodesLock = new();
        private static readonly Dictionary<string, Entry> Codes = new();

        /// <summary>For the harness: lets a test move time without waiting.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        private static string Key(string purpose, Guid userId) => purpose + ":" + userId.ToString("N");

        private static byte[] HashOf(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

        /// <summary>A fresh code for this account and purpose, replacing any before it.</summary>
        internal static string Issue(string purpose, Guid userId, string address) =>
            Issue(purpose, userId, address, out _);

        /// <summary>
        /// Like the above, and hands back a way to take the new code out again if the mail
        /// never left (R-1062). The code it replaced comes back, because the person may
        /// already have it in their inbox; only a code nobody was sent is dropped. A newer
        /// code issued meanwhile is left alone.
        /// </summary>
        internal static string Issue(string purpose, Guid userId, string address, out Action undo)
        {
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var entry = new Entry
            {
                Hash = HashOf(code),
                Address = address,
                ExpiresUtc = Now() + Lifetime,
            };
            var key = Key(purpose, userId);
            Entry? replaced;
            lock (CodesLock)
            {
                Codes.TryGetValue(key, out replaced);
                Codes[key] = entry;
            }
            undo = () =>
            {
                lock (CodesLock)
                {
                    if (!Codes.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
                    if (replaced != null && replaced.ExpiresUtc > Now() && replaced.Tries < MaxTries) Codes[key] = replaced;
                    else Codes.Remove(key);
                }
            };
            return code;
        }

        /// <summary>The address a live code was sent to, or null.</summary>
        internal static string? PendingAddress(string purpose, Guid userId)
        {
            lock (CodesLock)
            {
                return Codes.TryGetValue(Key(purpose, userId), out var e) && e.ExpiresUtc > Now() ? e.Address : null;
            }
        }

        internal enum Result { Ok, Wrong, Expired, None }

        /// <summary>
        /// Checks a code. Right: it is spent, and the address it was sent to comes back.
        /// Wrong: one try used; the fifth spends it. Gone: none to check.
        /// </summary>
        internal static Result Check(string purpose, Guid userId, string? code, out string address)
        {
            address = string.Empty;
            var key = Key(purpose, userId);
            lock (CodesLock)
            {
                if (!Codes.TryGetValue(key, out var entry)) return Result.None;

                if (entry.ExpiresUtc <= Now() || entry.Tries >= MaxTries)
                {
                    Codes.Remove(key);
                    return Result.Expired;
                }

                var typed = (code ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
                if (typed.Length == 6 && CryptographicOperations.FixedTimeEquals(HashOf(typed), entry.Hash))
                {
                    Codes.Remove(key);
                    address = entry.Address;
                    return Result.Ok;
                }

                entry.Tries++;
                if (entry.Tries >= MaxTries) Codes.Remove(key);
                return Result.Wrong;
            }
        }

        internal static void Forget(string purpose, Guid userId)
        {
            lock (CodesLock) Codes.Remove(Key(purpose, userId));
        }
    }
}
