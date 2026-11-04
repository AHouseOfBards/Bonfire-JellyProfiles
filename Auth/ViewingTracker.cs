using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// What each limited profile has watched today, extra time given, and requests for more
    /// (1.7.2). Kept in memory and written to <c>viewing_usage.json</c> every few minutes by
    /// <see cref="ViewingTimer"/>, so a restart loses at most that much.
    ///
    /// <para><b>The rules are read from an index, not the configuration.</b> The gate runs on
    /// every playback request; copying the configuration for each would cost more than the
    /// request. The index is rebuilt at most every 30 seconds, and at once after Bonfire's own
    /// routes change a rule or the household's zone (<see cref="Invalidate"/>).</para>
    ///
    /// <para>One static lock, <see cref="UsageLock"/>, for everything here, as
    /// tests/cs/configlock requires of every lock in the plugin.</para>
    /// </summary>
    internal static class ViewingTracker
    {
        internal static readonly object UsageLock = new();

        /// <summary>For the harness: the clock everything here reads.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        internal const int WarnMinutes = 5;
        internal const int GraceMinutes = 5;
        internal static readonly TimeSpan IndexLife = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan AskQuiet = TimeSpan.FromMinutes(10);

        /// <summary>A limited profile, as the gate and the timer need it.</summary>
        internal sealed class Limited
        {
            public Guid ProfileId { get; init; }
            public Guid MasterId { get; init; }
            public string Name { get; init; } = string.Empty;
            public ViewingRules Rules { get; init; } = new();
            public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
        }

        internal sealed class Entry
        {
            public string Day { get; set; } = string.Empty;
            public double Seconds { get; set; }
            public DateTime? ExtraUntilUtc { get; set; }
            public DateTime? AskedUtc { get; set; }
            public string LimitNoticeDay { get; set; } = string.Empty;

            // Not written down: a restart starts these afresh, which at worst repeats a warning.
            internal DateTime? WarnedForUtc;
            internal DateTime? GraceEndsUtc;
            internal bool StopSent;
            internal DateTime? LastRefusalMessageUtc;
        }

        private static readonly Dictionary<Guid, Entry> Entries = new();
        private static Dictionary<Guid, Limited> _index = new();
        private static DateTime _indexUntil = DateTime.MinValue;
        private static bool _loaded;
        private static bool _dirty;

        // ── The rules ───────────────────────────────────────────────────────────────

        /// <summary>Every profile with a rule switched on. Empty while the emergency disable is on.</summary>
        internal static IReadOnlyDictionary<Guid, Limited> Index()
        {
            var now = DateTime.UtcNow;
            lock (UsageLock)
            {
                if (now < _indexUntil) return _index;
            }

            var built = new Dictionary<Guid, Limited>();
            if (!Plugin.IsPanicDisabled)
            {
                lock (ProfilesBaseController.ConfigLock)
                {
                    var config = Plugin.Instance?.Configuration;
                    if (config?.Mappings != null)
                    {
                        foreach (var m in config.Mappings)
                        {
                            if (m.Viewing == null || !m.Viewing.IsLimited) continue;
                            if (m.MasterUserId == Guid.Empty || m.MasterUserId == m.ProfileUserId) continue;
                            var main = config.Mappings.FirstOrDefault(x => x.ProfileUserId == m.MasterUserId);
                            built[m.ProfileUserId] = new Limited
                            {
                                ProfileId = m.ProfileUserId,
                                MasterId = m.MasterUserId,
                                Name = m.ProfileName,
                                Rules = m.Viewing.Copy(),
                                Zone = HouseholdTime.Resolve(main?.TimeZone, config.DefaultTimeZone),
                            };
                        }
                    }
                }
            }

            lock (UsageLock)
            {
                _index = built;
                _indexUntil = now + IndexLife;
                return _index;
            }
        }

        /// <summary>Re-read the rules on the next request. Called when a rule or a zone is saved.</summary>
        internal static void Invalidate()
        {
            lock (UsageLock) _indexUntil = DateTime.MinValue;
        }

        // ── Where a profile stands ──────────────────────────────────────────────────

        /// <summary>The state of a limited profile now, or null when it has no limits.</summary>
        internal static ViewingState? State(Guid profileId, DateTime nowUtc)
        {
            return Index().TryGetValue(profileId, out var limited) ? State(limited, nowUtc) : null;
        }

        internal static ViewingState State(Limited limited, DateTime nowUtc)
        {
            var day = ViewingClock.DayOf(limited.Zone, nowUtc);
            double seconds;
            DateTime? extra;
            lock (UsageLock)
            {
                EnsureLoaded();
                Entries.TryGetValue(limited.ProfileId, out var e);
                seconds = e != null && e.Day == day ? e.Seconds : 0;
                extra = e?.ExtraUntilUtc;
            }
            return ViewingClock.Evaluate(limited.Rules, limited.Zone, seconds, extra, nowUtc);
        }

        /// <summary>When the profile last asked for more time, if it is still waiting.</summary>
        internal static DateTime? AskedUtc(Guid profileId)
        {
            lock (UsageLock)
            {
                EnsureLoaded();
                return Entries.TryGetValue(profileId, out var e) ? e.AskedUtc : null;
            }
        }

        // ── Changes ─────────────────────────────────────────────────────────────────

        internal static void AddSeconds(Guid profileId, string day, double seconds)
        {
            if (seconds <= 0) return;
            lock (UsageLock)
            {
                var e = EntryFor(profileId);
                if (e.Day != day)
                {
                    e.Day = day;
                    e.Seconds = 0;
                }
                e.Seconds += seconds;
                _dirty = true;
            }
        }

        /// <summary>
        /// More time, from now or from the end of time already given. Playback is allowed
        /// until then whatever the hours or the limit say. Answers any request waiting.
        /// </summary>
        internal static DateTime Grant(Guid profileId, int minutes, DateTime nowUtc)
        {
            lock (UsageLock)
            {
                var e = EntryFor(profileId);
                var from = e.ExtraUntilUtc.HasValue && e.ExtraUntilUtc.Value > nowUtc ? e.ExtraUntilUtc.Value : nowUtc;
                e.ExtraUntilUtc = from.AddMinutes(minutes);
                e.AskedUtc = null;
                e.WarnedForUtc = null;
                e.GraceEndsUtc = null;
                e.StopSent = false;
                _dirty = true;
                return e.ExtraUntilUtc.Value;
            }
        }

        /// <summary>Answers a request for more time with no. Time already given stays.</summary>
        internal static void Decline(Guid profileId)
        {
            lock (UsageLock)
            {
                var e = EntryFor(profileId);
                e.AskedUtc = null;
                _dirty = true;
            }
        }

        /// <summary>Records a request for more time. False if one was made in the last ten minutes.</summary>
        internal static bool Ask(Guid profileId, DateTime nowUtc)
        {
            lock (UsageLock)
            {
                var e = EntryFor(profileId);
                if (e.AskedUtc.HasValue && nowUtc - e.AskedUtc.Value < AskQuiet) return false;
                e.AskedUtc = nowUtc;
                _dirty = true;
                return true;
            }
        }

        /// <summary>Forgets a profile: its rules were switched off, or it was deleted.</summary>
        internal static void Forget(Guid profileId)
        {
            lock (UsageLock)
            {
                EnsureLoaded();
                if (Entries.Remove(profileId)) _dirty = true;
            }
        }

        /// <summary>For the timer: the entry to keep its warning state on. Call under <see cref="UsageLock"/>.</summary>
        internal static Entry EntryFor(Guid profileId)
        {
            EnsureLoaded();
            if (!Entries.TryGetValue(profileId, out var e))
            {
                e = new Entry();
                Entries[profileId] = e;
            }
            return e;
        }

        // ── On disk ─────────────────────────────────────────────────────────────────

        private sealed class FileShape
        {
            public Dictionary<string, Entry> Profiles { get; set; } = new();
        }

        internal static string? FilePath()
        {
            var paths = Plugin.Instance?.AppPaths;
            return paths == null ? null : Path.Combine(paths.DataPath, "plugins", "ProfilesManagement", "viewing_usage.json");
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            var path = FilePath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
                if (shape?.Profiles == null) return;
                foreach (var pair in shape.Profiles)
                {
                    if (Guid.TryParse(pair.Key, out var id) && pair.Value != null) Entries[id] = pair.Value;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                // A broken file is today's minutes lost, not a server that won't start.
            }
        }

        /// <summary>Writes what changed since the last save. Never throws.</summary>
        internal static void Save(ILogger? logger)
        {
            string? json = null;
            var path = FilePath();
            lock (UsageLock)
            {
                if (!_dirty || path == null) return;
                var shape = new FileShape();
                foreach (var pair in Entries) shape.Profiles[pair.Key.ToString("N")] = pair.Value;
                json = JsonSerializer.Serialize(shape);
                _dirty = false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                logger?.LogWarning("ProfilesPlugin: couldn't save viewing time: {Reason}", ex.Message);
                lock (UsageLock) _dirty = true;
            }
        }

        /// <summary>For the harness: forget everything, including what was loaded.</summary>
        internal static void Reset()
        {
            lock (UsageLock)
            {
                Entries.Clear();
                _index = new Dictionary<Guid, Limited>();
                _indexUntil = DateTime.MinValue;
                _loaded = false;
                _dirty = false;
            }
        }

        // ── Words ───────────────────────────────────────────────────────────────────

        /// <summary>A time of day in the household's zone, written the way <paramref name="language"/> writes it.</summary>
        internal static (string Day, string Time, bool Today) Format(DateTime utc, TimeZoneInfo zone, DateTime nowUtc, string? language)
        {
            CultureInfo culture;
            try { culture = string.IsNullOrEmpty(language) ? CultureInfo.GetCultureInfo("en") : CultureInfo.GetCultureInfo(language); }
            catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo("en"); }
            var local = ViewingClock.Local(zone, utc);
            var today = local.Date == ViewingClock.Local(zone, nowUtc).Date;
            return (local.ToString("dddd", culture), local.ToString("t", culture), today);
        }
    }
}
