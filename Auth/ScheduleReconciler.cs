using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Decides what access schedule a profile's account gets at a sign-in (R-1345).
    ///
    /// <para>Bonfire used to write the main account's schedule over the profile's at every
    /// sign-in, so a narrower one an administrator set in Dashboard -> Users was lost. The
    /// fix is the one used for libraries (R-1042): the mapping records what Bonfire last
    /// wrote (<c>LastAppliedSchedules</c>). An account that still matches the record has not
    /// been touched; one that differs was changed by an administrator, and that is adopted
    /// as the profile's own (<c>OwnSchedules</c>). The own copy is needed because Bonfire's
    /// next write would otherwise be mistaken for the main account's.</para>
    ///
    /// <para><b>Never wider than the main account's.</b> A schedule lists the hours a
    /// profile may watch; none means always. The adopted hours are cut down to those inside
    /// the main account's, and a main account with none leaves them as they are. If nothing
    /// is left after the cut, the main account's own hours are used: an empty list would
    /// read as "always".</para>
    ///
    /// <para><b>A reset is repaired, not adopted.</b> Jellyfin's default is no schedule, so
    /// an account that was emptied when the record says it had hours looks like a reset and
    /// is written back. The cost is that an administrator cannot clear a profile's schedule
    /// here; they clear it in the profile's own settings or the main account's.</para>
    ///
    /// <para>No record (a configuration from before this) is today's rule once: the main
    /// account's hours, then recorded. XmlSerializer hands back an empty list, not null, for an
    /// element that is absent from the file (as it does for the libraries' record), so an
    /// empty list is "no record" and "the main account had no hours" is recorded as
    /// <see cref="NoneMarker"/>.</para>
    /// </summary>
    internal static class ScheduleReconciler
    {
        internal readonly struct Window
        {
            public Window(DynamicDayOfWeek day, double start, double end) { Day = day; Start = start; End = end; }
            public DynamicDayOfWeek Day { get; }
            public double Start { get; }
            public double End { get; }
        }

        internal sealed class Result
        {
            /// <summary>The rows to write to the account.</summary>
            public List<Window> Applied { get; init; } = new();

            /// <summary>The profile's own hours after this sign-in; empty when it follows the main account.</summary>
            public List<string> Own { get; init; } = new();

            /// <summary>What was written, as the record for the next sign-in.</summary>
            public List<string> Record { get; init; } = new();
        }

        public static string Key(Window w) =>
            ((int)w.Day).ToString(CultureInfo.InvariantCulture) + ":"
            + w.Start.ToString("R", CultureInfo.InvariantCulture) + "-"
            + w.End.ToString("R", CultureInfo.InvariantCulture);

        public static Window Of(AccessSchedule s) => new Window(s.DayOfWeek, s.StartHour, s.EndHour);

        private static Window? Parse(string key)
        {
            var colon = key.IndexOf(':');
            var dash = key.IndexOf('-', colon + 1);
            if (colon < 1 || dash < 0) return null;
            if (!int.TryParse(key.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)) return null;
            if (!double.TryParse(key.Substring(colon + 1, dash - colon - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) return null;
            if (!double.TryParse(key.Substring(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) return null;
            return new Window((DynamicDayOfWeek)day, start, end);
        }

        private static List<Window> ParseAll(IEnumerable<string>? keys) =>
            (keys ?? Enumerable.Empty<string>()).Select(Parse).Where(w => w.HasValue).Select(w => w!.Value).ToList();

        /// <summary>The record of "no hours at all", since an empty list reads as no record.</summary>
        internal const string NoneMarker = "none";

        /// <summary>What to store as the record of <paramref name="applied"/>.</summary>
        internal static List<string> RecordOf(IEnumerable<Window> applied)
        {
            var keys = Keys(applied);
            return keys.Count == 0 ? new List<string> { NoneMarker } : keys;
        }

        private static List<string> Keys(IEnumerable<Window> windows) =>
            windows.Select(Key).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();

        private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
            new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);

        /// <summary>Sunday = 0 ... Saturday = 6, which is how Jellyfin numbers them.</summary>
        private static IEnumerable<int> Days(DynamicDayOfWeek d)
        {
            switch ((int)d)
            {
                case 7: return new[] { 0, 1, 2, 3, 4, 5, 6 };
                case 8: return new[] { 1, 2, 3, 4, 5 };
                case 9: return new[] { 0, 6 };
                default: return (int)d >= 0 && (int)d <= 6 ? new[] { (int)d } : Array.Empty<int>();
            }
        }

        /// <summary>The hours that are inside both, a day at a time.</summary>
        internal static List<Window> Within(IEnumerable<Window> own, IEnumerable<Window> main)
        {
            var result = new List<Window>();
            var mainList = main.ToList();
            foreach (var o in own)
            {
                foreach (var m in mainList)
                {
                    var start = Math.Max(o.Start, m.Start);
                    var end = Math.Min(o.End, m.End);
                    if (end <= start) continue;
                    var days = Days(o.Day).Intersect(Days(m.Day)).ToList();
                    if (days.Count == 0) continue;
                    // Keep the label when both rows name the same days, so a result that
                    // changed nothing is not rewritten as seven rows.
                    if (o.Day == m.Day) result.Add(new Window(o.Day, start, end));
                    else foreach (var d in days) result.Add(new Window((DynamicDayOfWeek)d, start, end));
                }
            }
            return result;
        }

        public static Result Resolve(
            IEnumerable<Window> main, IEnumerable<Window> account,
            IReadOnlyCollection<string>? lastApplied, IReadOnlyCollection<string>? own)
        {
            var mainList = main.ToList();
            var accountKeys = Keys(account);
            var ownKeys = Keys(ParseAll(own));

            var recorded = lastApplied == null || lastApplied.Count == 0
                ? null
                : lastApplied.Where(k => k != NoneMarker).ToList();

            if (recorded != null && !SameSet(accountKeys, recorded)
                && accountKeys.Count > 0)
            {
                // Somebody changed it. (Emptied is the shape of a reset, handled by keeping
                // the own hours as they were.)
                ownKeys = accountKeys;
            }

            var ownWindows = ParseAll(ownKeys);
            List<Window> applied;
            if (ownWindows.Count == 0) applied = mainList;
            else if (mainList.Count == 0) applied = ownWindows;
            else
            {
                applied = Within(ownWindows, mainList);
                if (applied.Count == 0) applied = mainList;
            }

            return new Result { Applied = applied, Own = ownKeys, Record = RecordOf(applied) };
        }
    }
}
