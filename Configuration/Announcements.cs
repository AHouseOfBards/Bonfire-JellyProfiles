using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>One message from the administrator.</summary>
    public class Announcement
    {
        public Guid Id { get; set; }

        /// <summary>What the household reads. Plain text: never rendered as HTML.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>One of <see cref="Announcements.Severities"/>.</summary>
        public string Severity { get; set; } = Announcements.Info;

        /// <summary>When it starts showing. Null means as soon as it is saved.</summary>
        public DateTime? StartsUtc { get; set; }

        /// <summary>When it stops showing. Null means until it is deleted.</summary>
        public DateTime? EndsUtc { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>
    /// The rules for <see cref="Announcement"/>: what is valid, and what is showing now.
    ///
    /// <para><b>Severity, not colour.</b> An announcement says how much it matters, and each
    /// theme decides what that looks like. An administrator picking colours would get a red
    /// banner that is unreadable in half the themes.</para>
    ///
    /// <para><b>Plain text.</b> The message reaches every household on the server, and an
    /// app may draw it natively. HTML would be an injection point in the web client and
    /// meaningless everywhere else.</para>
    /// </summary>
    public static class Announcements
    {
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Critical = "critical";

        public static readonly IReadOnlyList<string> Severities = new[] { Info, Warning, Critical };

        /// <summary>Longest message accepted. A banner, not a letter.</summary>
        public const int MaxMessageLength = 280;

        /// <summary>Most announcements kept at once, expired ones included.</summary>
        public const int MaxCount = 50;

        /// <summary>How long an expired announcement stays in the admin list before it is dropped.</summary>
        public static readonly TimeSpan KeepExpiredFor = TimeSpan.FromDays(30);

        /// <summary>Why <paramref name="a"/> cannot be saved, or null.</summary>
        public static string? Validate(Announcement a)
        {
            if (string.IsNullOrWhiteSpace(a.Message)) return "Enter a message for the announcement.";
            if (a.Message.Trim().Length > MaxMessageLength)
                return $"An announcement can be at most {MaxMessageLength} characters.";
            if (!Severities.Contains(a.Severity)) return "Announcement severity must be info, warning or critical.";
            if (a.StartsUtc.HasValue && a.EndsUtc.HasValue && a.EndsUtc <= a.StartsUtc)
                return "An announcement must end after it starts.";
            return null;
        }

        /// <summary>True while <paramref name="a"/> should be shown.</summary>
        public static bool IsActive(Announcement a, DateTime nowUtc)
            => (!a.StartsUtc.HasValue || a.StartsUtc.Value <= nowUtc)
               && (!a.EndsUtc.HasValue || a.EndsUtc.Value > nowUtc);

        /// <summary>What is showing now: critical first, then warning, then info, newest first within each.</summary>
        public static List<Announcement> Active(IEnumerable<Announcement>? all, DateTime nowUtc)
            => (all ?? Enumerable.Empty<Announcement>())
                .Where(a => IsActive(a, nowUtc))
                .OrderByDescending(a => Rank(a.Severity))
                .ThenByDescending(a => a.StartsUtc ?? a.CreatedUtc)
                .ToList();

        /// <summary>
        /// The list with <paramref name="saved"/> added or replaced, and long-expired entries
        /// dropped. A new list: the old one is never touched.
        /// </summary>
        public static List<Announcement> With(IEnumerable<Announcement>? all, Announcement saved, DateTime nowUtc)
        {
            var list = (all ?? Enumerable.Empty<Announcement>())
                .Where(a => a.Id != saved.Id)
                .Where(a => !a.EndsUtc.HasValue || nowUtc - a.EndsUtc.Value < KeepExpiredFor)
                .ToList();
            list.Add(saved);
            return list;
        }

        /// <summary>Normalises a date sent by a client to UTC.</summary>
        public static DateTime? ToUtc(DateTime? value)
        {
            if (!value.HasValue) return null;
            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
            };
        }

        private static int Rank(string severity) => severity switch
        {
            Critical => 2,
            Warning => 1,
            _ => 0,
        };
    }
}
