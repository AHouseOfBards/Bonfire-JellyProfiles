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
        /// Per profile, across every address: the web switcher's PIN check. <see cref="Pin"/>
        /// is keyed on address and profile together, which a caller with more than one
        /// address (any IPv6 host has billions) simply walks around.
        /// </summary>
        internal static readonly RateLimiter PinPerProfile = new(maxAttempts: 10, windowMinutes: 15);

        /// <summary>
        /// Per account, for a PIN typed into another app's sign-in screen. That path is
        /// reachable by anyone who can POST /Users/AuthenticateByName, and Jellyfin's own
        /// lockout does not cover it: <c>LoginAttemptsBeforeLockout</c> is null on every
        /// account Bonfire creates, which Jellyfin reads as "never lock". Keyed on the
        /// account, not the address, for the same reason as <see cref="PinPerProfile"/>.
        /// </summary>
        internal static readonly RateLimiter ClientPin = new(maxAttempts: 5, windowMinutes: 15);

        /// <summary>
        /// The same accounts, over a day. Five every fifteen minutes is still 480 a day,
        /// which gets through a four-digit PIN in about three weeks; twenty a day makes it
        /// most of a year.
        /// </summary>
        internal static readonly RateLimiter ClientPinDaily = new(maxAttempts: 20, windowMinutes: 24 * 60);

        // ── State ──────────────────────────────────────────────────────────────────
        private readonly int _maxAttempts;
        private readonly int _windowMinutes;
        private readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new();
        private readonly object _cleanupLock = new();
        private DateTime _nextCleanup = DateTime.UtcNow.AddMinutes(5);

        private RateLimiter(int maxAttempts, int windowMinutes)
        {
            _maxAttempts = maxAttempts;
            _windowMinutes = windowMinutes;
        }

        // ── Public API ─────────────────────────────────────────────────────────────

        public bool IsRateLimited(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return false;
            PruneExpiredEntries();

            if (_attempts.TryGetValue(ipAddress, out var list))
            {
                lock (list)
                {
                    list.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-_windowMinutes));
                    return list.Count >= _maxAttempts;
                }
            }
            return false;
        }

        /// <summary>
        /// Counts an attempt before it is made, and says whether it may be made at all.
        /// <para>
        /// <see cref="IsRateLimited"/> followed by <see cref="RecordFailure"/> leaves the
        /// whole verification in between — a PBKDF2 derivation, tens of milliseconds — for
        /// concurrent requests to pass the check together, so a hundred parallel guesses
        /// all got through a limit of five. Here the check and the count are one step under
        /// one lock. Call <see cref="Reset"/> when the attempt succeeds.
        /// </para>
        /// </summary>
        public bool TryBegin(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
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
                    if (list.Count >= _maxAttempts) return false;

                    list.Add(now);
                    return true;
                }
            }
        }

        public void RecordFailure(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return;
            PruneExpiredEntries();

            var list = _attempts.GetOrAdd(ipAddress, _ => new List<DateTime>());
            lock (list)
            {
                list.Add(DateTime.UtcNow);
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
        public TimeSpan RetryAfter(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return TimeSpan.Zero;

            if (!_attempts.TryGetValue(ipAddress, out var list)) return TimeSpan.Zero;

            lock (list)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-_windowMinutes);
                list.RemoveAll(t => t < cutoff);
                if (list.Count < _maxAttempts) return TimeSpan.Zero;

                // The window frees a slot when its oldest entry ages out.
                var oldest = list[0];
                foreach (var t in list) if (t < oldest) oldest = t;

                var wait = oldest.AddMinutes(_windowMinutes) - DateTime.UtcNow;
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
