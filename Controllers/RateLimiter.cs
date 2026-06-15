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
