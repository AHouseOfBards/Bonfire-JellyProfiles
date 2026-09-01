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
