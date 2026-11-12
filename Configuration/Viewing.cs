using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Serialization;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// When a profile may watch, and for how long each day (1.7.2). Stored on the profile's
    /// own mapping, never a main account's: a main account is the parent. Null or nothing
    /// switched on means no limits, which is the default.
    ///
    /// <para><b>Times are local, in the household's zone</b> (<see cref="HouseholdTime"/>),
    /// written "HH:mm". Never UTC offsets: bedtime is 20:00 on both sides of a clock change.</para>
    ///
    /// <para><b>Video only.</b> Music and audiobooks are not counted or refused; "viewing
    /// hours" is what households asked for, and stopping a bedtime story at the limit is not.</para>
    /// </summary>
    public class ViewingRules
    {
        /// <summary>Playback only inside <see cref="Windows"/>. On with no windows means never.</summary>
        public bool HoursEnabled { get; set; }

        /// <summary>When playback is allowed. Starts empty: see <see cref="OnboardingSettings"/>
        /// for why a list here may never be filled by a constructor.</summary>
        public List<ViewingWindow> Windows { get => _windows; set => _windows = value ?? new(); }

        // Never null, whatever a body or a hand-edited file holds: Evaluate and CopyRules read it
        // without a guard. r2-21-04.
        private List<ViewingWindow> _windows = new();

        /// <summary>Minutes of video a day. 0 is no limit.</summary>
        public int DailyMinutes { get; set; }

        /// <summary>Minutes on Saturday and Sunday. Null is the same as <see cref="DailyMinutes"/>; 0 is no limit.</summary>
        public int? WeekendMinutes { get; set; }

        /// <summary>The language of whoever saved these, for the messages sent to the profile's apps.</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>Whether anything is switched on.</summary>
        [XmlIgnore]
        public bool IsLimited => HoursEnabled || DailyMinutes > 0 || (WeekendMinutes ?? 0) > 0;

        public ViewingRules Copy()
        {
            var copy = (ViewingRules)MemberwiseClone();
            copy.Windows = Windows.Select(w => w.Copy()).ToList();
            return copy;
        }

        public const int MaxWindows = 28;
        public const int MaxMinutes = 1440;
    }

    /// <summary>
    /// One stretch of allowed time on the days in <see cref="Days"/>. An end at or before the
    /// start runs past midnight into the next day ("21:00" to "01:00"); the same start and
    /// end is the whole day.
    /// </summary>
    public class ViewingWindow
    {
        /// <summary>Bit n is <see cref="DayOfWeek"/> n: 1 is Sunday, 2 Monday, 64 Saturday.</summary>
        public int Days { get; set; }

        public string Start { get; set; } = "00:00";

        /// <summary>"24:00" is accepted for midnight at the end of the day.</summary>
        public string End { get; set; } = "24:00";

        public ViewingWindow Copy() => (ViewingWindow)MemberwiseClone();

        public bool AppliesOn(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

        /// <summary>Minutes after midnight, or -1. "24:00" only where <paramref name="allowEndOfDay"/>.</summary>
        public static int Minutes(string? text, bool allowEndOfDay)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 5 || text[2] != ':') return -1;
            if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)) return -1;
            if (!int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return -1;
            if (m > 59) return -1;
            if (h == 24 && m == 0) return allowEndOfDay ? 1440 : -1;
            return h > 23 ? -1 : h * 60 + m;
        }

        public bool IsValid => Days > 0 && Days < 128 && Minutes(Start, false) >= 0 && Minutes(End, true) >= 0;
    }

    /// <summary>
    /// The household's clock. An IANA zone chosen on the main account; until it chooses, the
    /// administrator's default; failing that, the server's own zone. Docker images usually run
    /// in UTC, which is why the first two exist.
    /// </summary>
    public static class HouseholdTime
    {
        private static IReadOnlyList<string>? _zones;

        /// <summary>The zone for a household. Never throws: an id this server doesn't know falls through.</summary>
        public static TimeZoneInfo Resolve(string? household, string? serverDefault)
        {
            if (TryFind(household, out var zone)) return zone;
            if (TryFind(serverDefault, out zone)) return zone;
            return TimeZoneInfo.Local;
        }

        /// <summary>Finds a zone by IANA (or Windows) id.</summary>
        public static bool TryFind(string? id, out TimeZoneInfo zone)
        {
            zone = TimeZoneInfo.Utc;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var found) || found == null) return false;
            zone = found;
            return true;
        }

        /// <summary>The IANA id of a zone, which is what clients and the dashboard show.</summary>
        public static string IdOf(TimeZoneInfo zone)
        {
            if (zone.HasIanaId) return zone.Id;
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && !string.IsNullOrEmpty(iana) ? iana : zone.Id;
        }

        /// <summary>Every zone this server knows, as IANA ids, sorted.</summary>
        public static IReadOnlyList<string> Zones()
        {
            if (_zones != null) return _zones;
            var ids = new SortedSet<string>(StringComparer.Ordinal) { "UTC" };
            foreach (var z in TimeZoneInfo.GetSystemTimeZones())
            {
                var id = IdOf(z);
                if (TryFind(id, out _)) ids.Add(id);
            }
            _zones = ids.ToList();
            return _zones;
        }
    }

    /// <summary>Where a profile stands right now. See <see cref="ViewingClock.Evaluate"/>.</summary>
    public sealed class ViewingState
    {
        public bool Limited { get; set; }
        public bool Allowed { get; set; } = true;

        /// <summary>Why playback is refused: <see cref="ViewingClock.Hours"/>, <see cref="ViewingClock.Limit"/>, or empty.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>What ends the current allowance first: hours, limit, extra, or empty for nothing.</summary>
        public string Binding { get; set; } = string.Empty;

        /// <summary>Minutes until playback is refused, or null for no end in sight.</summary>
        public int? MinutesLeft { get; set; }

        /// <summary>When the allowed hours, or the extra time, end. Null when neither binds.</summary>
        public DateTime? UntilUtc { get; set; }

        /// <summary>While refused: when playback is next allowed, or null for not in the coming week.</summary>
        public DateTime? NextStartUtc { get; set; }

        public int UsedMinutes { get; set; }
        public int? LimitMinutes { get; set; }
        public DateTime? ExtraUntilUtc { get; set; }

        /// <summary>The household's local date, "yyyy-MM-dd": the day usage is counted against.</summary>
        public string Day { get; set; } = string.Empty;
    }

    /// <summary>
    /// The arithmetic, with no clock or storage of its own, so a test can put it on either
    /// side of a clock change. Walls are compared in local time and only turned into UTC to
    /// report them, which is what keeps 20:00 at 20:00 through daylight saving.
    /// </summary>
    public static class ViewingClock
    {
        public const string Hours = "hours";
        public const string Limit = "limit";
        public const string Extra = "extra";

        public static DateTime Local(TimeZoneInfo zone, DateTime nowUtc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);

        public static string DayOf(TimeZoneInfo zone, DateTime nowUtc) =>
            Local(zone, nowUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Today's limit in minutes, or null for none.</summary>
        public static int? LimitFor(ViewingRules rules, DayOfWeek day)
        {
            var weekend = day == DayOfWeek.Saturday || day == DayOfWeek.Sunday;
            var minutes = weekend && rules.WeekendMinutes.HasValue ? rules.WeekendMinutes.Value : rules.DailyMinutes;
            return minutes > 0 ? minutes : null;
        }

        public static ViewingState Evaluate(ViewingRules? rules, TimeZoneInfo zone, double usedSeconds, DateTime? extraUntilUtc, DateTime nowUtc)
        {
            nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            var local = Local(zone, nowUtc);
            var state = new ViewingState { Day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            if (rules == null || !rules.IsLimited) return state;

            state.Limited = true;
            state.UsedMinutes = (int)Math.Floor(Math.Max(0, usedSeconds) / 60);
            var limit = LimitFor(rules, local.DayOfWeek);
            state.LimitMinutes = limit;

            var windows = rules.HoursEnabled ? rules.Windows.Where(w => w.IsValid).ToList() : null;
            DateTime? hoursEnd = null;
            var hoursOk = true;
            if (windows != null)
            {
                hoursEnd = RunEnd(windows, local);
                // The clock goes back and an hour of the wall happens twice. A window ends at
                // the first time the wall reaches its end; once that has passed, the second
                // 01:10 is not inside a window that ended at 01:30.
                if (hoursEnd != null && zone.IsAmbiguousTime(hoursEnd.Value) && FirstUtc(zone, hoursEnd.Value) <= nowUtc)
                    hoursEnd = null;
                hoursOk = hoursEnd != null;
            }

            var limitOk = limit == null || usedSeconds < limit.Value * 60.0;
            var extra = extraUntilUtc.HasValue && extraUntilUtc.Value > nowUtc ? extraUntilUtc : null;
            state.ExtraUntilUtc = extra;

            // What the rules alone allow.
            int? normalLeft = null;
            var binding = string.Empty;
            DateTime? until = null;
            if (hoursOk && limitOk)
            {
                if (hoursEnd != null)
                {
                    var endUtc = zone.IsAmbiguousTime(hoursEnd.Value) ? FirstUtc(zone, hoursEnd.Value) : ToUtc(zone, hoursEnd.Value);
                    normalLeft = CeilMinutes(endUtc - nowUtc);
                    binding = Hours;
                    until = endUtc;
                }
                if (limit != null)
                {
                    var left = CeilMinutes(TimeSpan.FromSeconds(limit.Value * 60.0 - usedSeconds));
                    if (normalLeft == null || left < normalLeft)
                    {
                        normalLeft = left;
                        binding = Limit;
                        until = null;
                    }
                }
            }

            if (extra != null)
            {
                var extraLeft = CeilMinutes(extra.Value - nowUtc);
                state.Allowed = true;
                if (hoursOk && limitOk && (normalLeft == null || normalLeft >= extraLeft))
                {
                    state.MinutesLeft = normalLeft;
                    state.Binding = binding;
                    state.UntilUtc = until;
                }
                else
                {
                    state.MinutesLeft = extraLeft;
                    state.Binding = Extra;
                    state.UntilUtc = extra;
                }
                return state;
            }

            if (hoursOk && limitOk)
            {
                state.MinutesLeft = normalLeft;
                state.Binding = binding;
                state.UntilUtc = until;
                return state;
            }

            state.Allowed = false;
            state.Reason = hoursOk ? Limit : Hours;

            // When it opens again: the next window, but not today if today's minutes are spent.
            DateTime? next;
            var tomorrow = local.Date.AddDays(1);
            if (!limitOk)
            {
                next = windows == null ? tomorrow : (RunEnd(windows, tomorrow) != null ? tomorrow : NextStart(windows, tomorrow));
            }
            else
            {
                next = NextStart(windows!, local);
            }
            state.NextStartUtc = next.HasValue ? ToUtc(zone, next.Value) : null;
            return state;
        }

        private static int CeilMinutes(TimeSpan span) => Math.Max(0, (int)Math.Ceiling(span.TotalMinutes - 1e-9));

        /// <summary>Every stretch a window covers that could contain a time on <paramref name="day"/>.</summary>
        private static IEnumerable<(DateTime Start, DateTime End)> Spans(List<ViewingWindow> windows, DateTime day)
        {
            foreach (var w in windows)
            {
                if (!w.AppliesOn(day.DayOfWeek)) continue;
                var s = ViewingWindow.Minutes(w.Start, false);
                var e = ViewingWindow.Minutes(w.End, true);
                var start = day.AddMinutes(s);
                var end = e > s ? day.AddMinutes(e) : day.AddDays(1).AddMinutes(e);
                yield return (start, end);
            }
        }

        /// <summary>The latest end of a window <paramref name="t"/> is inside, or null.</summary>
        private static DateTime? InsideUntil(List<ViewingWindow> windows, DateTime t)
        {
            DateTime? best = null;
            for (var d = t.Date.AddDays(-1); d <= t.Date; d = d.AddDays(1))
            {
                foreach (var (start, end) in Spans(windows, d))
                {
                    if (start <= t && t < end && (best == null || end > best)) best = end;
                }
            }
            return best;
        }

        /// <summary>The end of the run of windows <paramref name="t"/> is inside, following
        /// windows that touch or overlap (Friday until 24:00, Saturday from 00:00), or null.</summary>
        private static DateTime? RunEnd(List<ViewingWindow> windows, DateTime t)
        {
            var end = InsideUntil(windows, t);
            for (var i = 0; end != null && i < 16; i++)
            {
                var further = InsideUntil(windows, end.Value);
                if (further == null || further <= end) break;
                end = further;
            }
            return end;
        }

        /// <summary>The first window start after <paramref name="t"/> within a week, or null.</summary>
        private static DateTime? NextStart(List<ViewingWindow> windows, DateTime t)
        {
            DateTime? best = null;
            for (var d = t.Date.AddDays(-1); d <= t.Date.AddDays(7); d = d.AddDays(1))
            {
                foreach (var (start, _) in Spans(windows, d))
                {
                    if (start > t && (best == null || start < best)) best = start;
                }
            }
            return best;
        }

        /// <summary>A wall time the clock shows twice, as the first of the two instants.
        /// ConvertTimeToUtc would pick the later one.</summary>
        private static DateTime FirstUtc(TimeZoneInfo zone, DateTime local)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            var first = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - first, DateTimeKind.Utc);
        }

        /// <summary>A wall-clock time to UTC. One that a clock change skips is moved past the gap.</summary>
        public static DateTime ToUtc(TimeZoneInfo zone, DateTime local)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            for (var i = 0; i < 8 && zone.IsInvalidTime(local); i++) local = local.AddMinutes(15);
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
    }
}
