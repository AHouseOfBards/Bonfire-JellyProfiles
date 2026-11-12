using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Thread-safe IP-based rate limiter. Instantiate one per logical gate
    /// (e.g. PIN attempts, Bonfire code attempts) with the appropriate threshold.
    ///
    /// Replaces the old duplicated BonfireRateLimiter / PinRateLimiter static classes.
    /// </summary>
    internal sealed class RateLimiter
    {
        // ── Two pre-configured singleton instances used by the controller ─────────
        /// <summary>3 attempts per 15 minutes — used for Bonfire invite-code guessing.</summary>
        internal static readonly RateLimiter Bonfire = new(maxAttempts: 3, windowMinutes: 15);

        /// <summary>5 attempts per 15 minutes — used for profile PIN entry.</summary>
        internal static readonly RateLimiter Pin = new(maxAttempts: 5, windowMinutes: 15);

        /// <summary>
        /// 5 attempts per hour — the emergency disable code. Far tighter than the others
        /// because the code is submitted without any accompanying authentication, so this
        /// limiter is the only thing standing between it and an offline-speed guess. A real
        /// administrator needs one attempt, and the code is long enough to be worth typing
        /// carefully; an attacker gets 120 guesses a day against it.
        /// </summary>
        internal static readonly RateLimiter Panic = new(maxAttempts: 5, windowMinutes: 60);

        /// <summary>
        /// All callers of the emergency code together, 200 an hour. The per-address limit
        /// above is walked around by anyone with many addresses, and each guess costs a
        /// PBKDF2 derivation and a map entry that lives for an hour. This bounds both. The
        /// price is that a flood can hold the code shut for the hour; the answer to that is
        /// the same as to a forgotten code, restart Jellyfin.
        /// </summary>
        internal static readonly RateLimiter PanicAll = new(maxAttempts: 200, windowMinutes: 60);

        /// <summary>
        /// The key to count an address under: an IPv6 address as its /64, because a single
        /// subscriber is routinely handed the whole of one and can use any address in it.
        /// An IPv4 address, or anything that does not parse, is returned as it is.
        /// </summary>
        internal static string AddressKey(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6
                || address.IsIPv4MappedToIPv6)
            {
                return ip;
            }

            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new System.Net.IPAddress(bytes) + "/64";
        }

        /// <summary>
        /// Per account, across every address and every door: the quarter-hour half of
        /// <see cref="PinBudget"/>. Keyed on the account, not the address, because a caller
        /// with more than one address (any IPv6 host has billions) walks around an address
        /// key.
        /// </summary>
        internal static readonly RateLimiter AccountPin = new(maxAttempts: 5, windowMinutes: 15);

        /// <summary>
        /// The same accounts, over a day: the other half of <see cref="PinBudget"/>. Five
        /// every fifteen minutes is still 480 a day, which gets through a four-digit PIN in
        /// about three weeks; twenty a day makes it most of a year.
        /// </summary>
        internal static readonly RateLimiter AccountPinDaily = new(maxAttempts: PinBudget.DailyLimit, windowMinutes: 24 * 60);

        /// <summary>
        /// Per account, for its password typed into another household's "add an account".
        /// Never reset by a success. The per-address and per-household limits in front of it
        /// were each cleared by any right password, and a caller always has one to hand (its
        /// own account, or one already on its switcher), so together they limited nothing
        /// (R-1068). Addresses and households are cheap to change; the account being guessed
        /// is the one thing every guess shares. Keyed on the name typed, so a name that
        /// doesn't exist is counted the same and can't be told apart by the 429.
        /// </summary>
        internal static readonly RateLimiter LinkPerAccount = new(maxAttempts: 10, windowMinutes: 60);

        /// <summary>
        /// The same account over a day, the other half of <see cref="LinkPerAccount"/>. Ten
        /// an hour is 240 guesses a day at one account, from a caller who only has to be
        /// signed in; twenty a day is a fixed limit, not a setting, because no household
        /// wants more guesses at somebody else's password (r2-10-01).
        /// </summary>
        internal static readonly RateLimiter LinkPerAccountDaily = new(maxAttempts: 20, windowMinutes: 24 * 60);

        // ── State ──────────────────────────────────────────────────────────────────
        private readonly int _maxAttempts;
        private readonly int _windowMinutes;
        private readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new();
        private readonly object _cleanupLock = new();
        private DateTime _nextCleanup = DateTime.UtcNow.AddMinutes(5);

        /// <summary>A limiter of its own, for a gate declared beside the code it guards.</summary>
        internal static RateLimiter Create(int maxAttempts, int windowMinutes) => new(maxAttempts, windowMinutes);

        private RateLimiter(int maxAttempts, int windowMinutes)
        {
            _maxAttempts = maxAttempts;
            _windowMinutes = windowMinutes;
        }

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Counts an attempt before it is made, and says whether it may be made at all.
        /// <para>
        /// A separate "is it limited" check followed by a "record the failure" step left the
        /// whole verification in between — a PBKDF2 derivation, tens of milliseconds — for
        /// concurrent requests to pass the check together, so a hundred parallel guesses
        /// all got through a limit of five. Here the check and the count are one step under
        /// one lock. Call <see cref="Reset"/> when the attempt succeeds.
        /// </para>
        /// </summary>
        public bool TryBegin(string key) => TryBeginWithin(key, _maxAttempts);

        /// <summary>
        /// <see cref="TryBegin(string)"/> against a lower limit than the one this limiter was
        /// made with, for a limit an administrator may make stricter. Never higher. Its own
        /// name, not an overload: harnesses find TryBegin by name.
        /// </summary>
        public bool TryBeginWithin(string key, int maxAttempts)
        {
            if (string.IsNullOrEmpty(key)) return true;
            var max = Math.Min(Math.Max(maxAttempts, 1), _maxAttempts);
            PruneExpiredEntries();

            while (true)
            {
                var list = _attempts.GetOrAdd(key, _ => new List<DateTime>());
                lock (list)
                {
                    // The sweep may have dropped this list from the dictionary between the
                    // lookup and the lock. Counting into an orphan would lose the attempt.
                    if (!_attempts.TryGetValue(key, out var current) || !ReferenceEquals(current, list))
                    {
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    list.RemoveAll(t => t < now.AddMinutes(-_windowMinutes));
                    if (list.Count >= max) return false;

                    list.Add(now);
                    return true;
                }
            }
        }

        /// <summary>
        /// How long until this caller may try again — the time left before the oldest
        /// attempt in the window expires. <see cref="TimeSpan.Zero"/> when they are not
        /// limited.
        /// <para>
        /// The message used to say "try again in 15 minutes", which describes a fixed
        /// lockout. This is a sliding window: it starts from the oldest of the attempts
        /// still counted, so somebody who mistyped a PIN five times over a quarter of an
        /// hour is usually a minute away from another go, not fifteen. Telling them
        /// fifteen is not a rounding error — it is the difference between waiting and
        /// giving up, and the number was never even an upper bound they could rely on,
        /// because a further failed attempt does not extend it.
        /// </para>
        /// </summary>
        public TimeSpan RetryAfter(string ipAddress) => RetryAfterWithin(ipAddress, _maxAttempts);

        /// <summary><see cref="RetryAfter"/> against the lower limit given to <see cref="TryBeginWithin"/>.</summary>
        public TimeSpan RetryAfterWithin(string ipAddress, int maxAttempts)
        {
            if (string.IsNullOrEmpty(ipAddress)) return TimeSpan.Zero;
            var max = Math.Min(Math.Max(maxAttempts, 1), _maxAttempts);

            if (!_attempts.TryGetValue(ipAddress, out var list)) return TimeSpan.Zero;

            lock (list)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-_windowMinutes);
                list.RemoveAll(t => t < cutoff);
                if (list.Count < max) return TimeSpan.Zero;

                // A slot frees when enough of the oldest entries age out to bring the count
                // under the limit: the oldest one at the full limit, a later one under a
                // lower limit.
                var sorted = new List<DateTime>(list);
                sorted.Sort();
                var freeing = sorted[list.Count - max];

                var wait = freeing.AddMinutes(_windowMinutes) - DateTime.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// "in 3 minutes" / "in 40 seconds" — the wait, rounded the way a person would
        /// say it. Never "in 0 minutes": anything under a minute is given in seconds, and
        /// a wait that has already elapsed is reported as a moment rather than as nothing.
        /// </summary>
        public static string DescribeWait(TimeSpan wait)
        {
            var seconds = (int)Math.Ceiling(wait.TotalSeconds);
            if (seconds <= 1) return "in a moment";
            if (seconds < 60) return $"in {seconds} seconds";

            var minutes = (int)Math.Ceiling(wait.TotalMinutes);
            return minutes == 1 ? "in a minute" : $"in {minutes} minutes";
        }

        public void Reset(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return;
            _attempts.TryRemove(ipAddress, out _);
        }

        // ── Periodic cleanup ───────────────────────────────────────────────────────

        private void PruneExpiredEntries()
        {
            var now = DateTime.UtcNow;
            if (now < _nextCleanup) return;

            lock (_cleanupLock)
            {
                if (now < _nextCleanup) return;
                _nextCleanup = now.AddMinutes(5);

                var cutoff = now.AddMinutes(-_windowMinutes);
                foreach (var key in _attempts.Keys)
                {
                    if (_attempts.TryGetValue(key, out var list))
                    {
                        lock (list)
                        {
                            list.RemoveAll(t => t < cutoff);
                            if (list.Count == 0)
                                _attempts.TryRemove(key, out _);
                        }
                    }
                }
            }
        }
    }
}
