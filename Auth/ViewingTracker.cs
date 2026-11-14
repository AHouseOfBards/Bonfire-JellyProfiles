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
    /// <para>The gate reads the rules from an index, not the configuration, because it runs on
    /// every playback request and copying the configuration each time costs more than the
    /// request. The index is rebuilt at most every 30 seconds, and at once when a rule or the
    /// household's zone changes (<see cref="Invalidate"/>).</para>
    ///
    /// <para>One static lock, <see cref="UsageLock"/>, covers everything here, as
    /// tests/cs/configlock requires.</para>
    /// </summary>
    internal static class ViewingTracker
    {
        internal static readonly object UsageLock = new();

        /// <summary>Held for the whole of a save, so the file is written by one at a time. See <see cref="Save"/>.</summary>
        private static readonly object SaveLock = new();

        /// <summary>For the harness: the clock everything here reads.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        internal const int WarnMinutes = 5;
        internal const int GraceMinutes = 5;
        internal static readonly TimeSpan IndexLife = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan AskQuiet = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The most more time one profile can be given in a household day: four hours. A fixed
        /// limit, not a setting. It caps what a guessed, watched or lent PIN can grant.
        /// </summary>
        internal const int MaxDailyGrantMinutes = 240;
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

            /// <summary>The household day <see cref="GrantedMinutes"/> was counted on.</summary>
            public string GrantDay { get; set; } = string.Empty;

            /// <summary>Minutes of more time given on <see cref="GrantDay"/>, for the daily cap.</summary>
            public int GrantedMinutes { get; set; }

            // Not written down: a restart starts these afresh, which at worst repeats a warning.
            internal DateTime? WarnedForUtc;
            internal DateTime? GraceEndsUtc;
            internal bool StopSent;
            internal DateTime? LastRefusalMessageUtc;
        }

        /// <summary>For the harness: runs between a save's snapshot and its write.</summary>
        internal static Action? BeforeWrite = null;

        /// <summary>Set by <see cref="ViewingTimer"/> when it is built; for what is found when the file is read.</summary>
        internal static ILogger? Log = null;

        private static readonly Dictionary<Guid, Entry> Entries = new();
        private static Dictionary<Guid, Limited> _index = new();
        private static DateTime _indexUntil = DateTime.MinValue;

        // Bumped by Invalidate. A rebuild that began before a bump may have read the rules
        // before the save, so it may use what it built but must not mark it fresh.
        private static int _indexGeneration;

        /// <summary>For the harness: runs after a rebuild has read the rules and before it publishes them.</summary>
        internal static Action? AfterBuild = null;
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

            int generation;
            lock (UsageLock) generation = _indexGeneration;
            var built = new Dictionary<Guid, Limited>();
            if (!Plugin.IsOff)
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

            AfterBuild?.Invoke();
            bool current;
            IReadOnlyDictionary<Guid, Limited> result;
            lock (UsageLock)
            {
                _index = built;
                // Not fresh if a rule was saved mid-build, so the next read rebuilds.
                current = generation == _indexGeneration;
                _indexUntil = current ? now + IndexLife : DateTime.MinValue;
                result = _index;
            }

            // Only a current rebuild is published. Never before the configuration is loaded:
            // the set would be empty and replace the one last written.
            if (current && Plugin.Instance?.Configuration != null) PublishLimited(built.Keys);
            return result;
        }

        private static string? _publishedKey;
        private static int _saidPublishFailed;

        // Campground.Shared refuses everyone in the index with limits_unavailable when Bonfire
        // does not answer. Written only when the set changed. Never throws.
        private static void PublishLimited(IEnumerable<Guid> ids)
        {
            try
            {
                var set = ids.Distinct().ToArray();
                var key = string.Join(",", set.Select(g => g.ToString("N")).OrderBy(x => x, StringComparer.Ordinal));
                if (key == _publishedKey) return;
                Campground.Shared.Contracts.Limits.PublishLimited(set);
                _publishedKey = key;
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Exchange(ref _saidPublishFailed, 1) == 0)
                {
                    Log?.LogWarning(ex, "ProfilesPlugin: could not publish the limited profiles to other Campground plugins.");
                }
            }
        }

        /// <summary>
        /// Re-reads the rules and publishes who is limited now. For a save that can change a profile's
        /// limits or remove a profile. Call it after leaving ConfigLock: a changed set goes to a file.
        /// </summary>
        internal static void RulesChanged()
        {
            Invalidate();
            Index();
        }

        /// <summary>Re-read the rules on the next request. Called when a rule or a zone is saved.</summary>
        internal static void Invalidate()
        {
            lock (UsageLock)
            {
                _indexGeneration++;
                _indexUntil = DateTime.MinValue;
            }
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

        /// <summary>How many more minutes can be given to the profile on this household day.</summary>
        internal static int GrantMinutesLeft(Guid profileId, TimeZoneInfo zone, DateTime nowUtc)
        {
            var day = ViewingClock.DayOf(zone, nowUtc);
            lock (UsageLock)
            {
                EnsureLoaded();
                var given = Entries.TryGetValue(profileId, out var e) && e.GrantDay == day ? e.GrantedMinutes : 0;
                return Math.Max(0, MaxDailyGrantMinutes - given);
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

        /// <summary>
        /// <see cref="Grant"/>, unless the day's grants for this profile would then pass
        /// <see cref="MaxDailyGrantMinutes"/>. The check and the count are one step under the
        /// lock, so two grants at once cannot both squeeze under it. The day is the household's:
        /// it starts again at midnight in the zone the profile's rules are read in.
        /// </summary>
        internal static bool TryGrant(Guid profileId, int minutes, DateTime nowUtc, TimeZoneInfo zone)
        {
            var day = ViewingClock.DayOf(zone, nowUtc);
            lock (UsageLock)
            {
                var e = EntryFor(profileId);
                var given = e.GrantDay == day ? e.GrantedMinutes : 0;
                if (given + minutes > MaxDailyGrantMinutes) return false;
                e.GrantDay = day;
                e.GrantedMinutes = given + minutes;
            }
            Grant(profileId, minutes, nowUtc);
            return true;
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

        /// <summary>
        /// A profile's rules were switched off. Forgets it unless it holds today's (or yesterday's UTC
        /// day, which covers every zone) usage or grants: switched off and on again, the minutes
        /// already watched and the daily cap on more time must still count (r2-07-03).
        /// </summary>
        internal static void ForgetUnlessRecent(Guid profileId)
        {
            lock (UsageLock)
            {
                EnsureLoaded();
                if (!Entries.TryGetValue(profileId, out var e)) return;
                var cutoff = Now().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(e.Day, cutoff) >= 0 || string.CompareOrdinal(e.GrantDay, cutoff) >= 0) return;
                Entries.Remove(profileId);
                _dirty = true;
            }
        }

        /// <summary>
        /// A profile is not playing and its grace is over: forget the grace and the stop, so the next
        /// play is warned and stopped like the first (r2-07-01). Never creates an entry.
        /// </summary>
        internal static void ClearSpentGrace(Guid profileId, DateTime nowUtc)
        {
            lock (UsageLock)
            {
                if (!Entries.TryGetValue(profileId, out var e)) return;
                if (e.GraceEndsUtc.HasValue && nowUtc >= e.GraceEndsUtc.Value)
                {
                    e.GraceEndsUtc = null;
                    e.StopSent = false;
                }
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
                // A broken file loses today's minutes but must not stop the server. Logged, or
                // a day of counted time just vanishes.
                Log?.LogWarning(ex, "ProfilesPlugin: {File} could not be read and was dropped; today's viewing time starts again from nothing.", path);
            }
        }

        /// <summary>Writes what changed since the last save. Never throws.</summary>
        internal static void Save(ILogger? logger)
        {
            var path = FilePath();
            if (path == null) return;

            // One save at a time, snapshot and write together. The snapshot was taken under
            // UsageLock and written outside it, to a temp file all writers shared, so
            // overlapping saves collided or left the older snapshot in the file (R-1022).
            // Lock order is SaveLock then UsageLock. The gate only takes UsageLock, so a slow
            // disk delays only the next save.
            lock (SaveLock)
            {
                string json;
                lock (UsageLock)
                {
                    if (!_dirty) return;
                    var shape = new FileShape();
                    foreach (var pair in Entries) shape.Profiles[pair.Key.ToString("N")] = pair.Value;
                    json = JsonSerializer.Serialize(shape);
                    _dirty = false;
                }

                string? temp = null;
                try
                {
                    BeforeWrite?.Invoke();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // A unique name per write: a fixed one let concurrent writes move each
                    // other's bytes. A failed write removes its file.
                    temp = Controllers.ProfilesBaseController.UniqueTempName(path);
                    File.WriteAllText(temp, json);
                    Controllers.ProfilesBaseController.MoveIntoPlace(temp, path);
                }
                catch (Exception ex)
                {
                    try { if (temp != null) File.Delete(temp); } catch (Exception) { }
                    // Any exception: Save never throws, and a bad data path counts as a failed
                    // write like a locked file.
                    logger?.LogWarning("ProfilesPlugin: couldn't save viewing time: {Reason}", ex.Message);
                    lock (UsageLock) _dirty = true;
                }
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

            _publishedKey = null;
            _saidPublishFailed = 0;
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
