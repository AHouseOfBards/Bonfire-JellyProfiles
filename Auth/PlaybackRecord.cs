using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// What <see cref="ViewingGate"/> and <see cref="MaintenanceGate"/> let start, per account:
    /// the whole meaning of "already playing" for both (R-1297).
    ///
    /// <para>It used to be judged from <c>session.NowPlayingItem</c>, which Jellyfin sets on
    /// <c>POST /Sessions/Playing</c> without checking that anything allowed the item. A
    /// limited profile could report an item as playing first, and the gate then let it
    /// stream as "already playing". The session's word is the client's; this record is ours.</para>
    ///
    /// <para>Noted when a PlaybackInfo, or a video stream, got past the gates. Memory only:
    /// after a restart nothing counts as already playing, and a stream in progress is
    /// refused at its next segment. That is the price of not trusting the client, and it is
    /// paid once. Bounded and expiring: an item stays for <see cref="Lifetime"/> after it
    /// was last allowed. Streams are noted only while some profile is limited (it costs a
    /// token lookup per segment), so otherwise it is the PlaybackInfo that starts the clock.</para>
    /// </summary>
    internal static class PlaybackRecord
    {
        /// <summary>Long enough for the longest film with a long pause in it.</summary>
        internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

        internal const int MaxEntries = 4096;

        /// <summary>Replaced by tests.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        private static readonly ConcurrentDictionary<(Guid User, Guid Item), DateTime> Allowed = new();

        internal static int Count() => Allowed.Count;

        /// <summary>The gate let <paramref name="item"/> start (or go on) for <paramref name="user"/>.</summary>
        internal static void Note(Guid user, Guid item)
        {
            if (user == Guid.Empty || item == Guid.Empty) return;
            var now = Now();
            Allowed[(user, item)] = now + Lifetime;
            if (Allowed.Count > MaxEntries) Trim(now);
        }

        /// <summary>True if the gate let <paramref name="item"/> start for <paramref name="user"/> and it has not expired.</summary>
        internal static bool Has(Guid user, Guid item)
        {
            if (!Allowed.TryGetValue((user, item), out var until)) return false;
            var now = Now();
            if (until <= now)
            {
                Allowed.TryRemove((user, item), out _);
                return false;
            }

            return true;
        }

        private static void Trim(DateTime now)
        {
            foreach (var pair in Allowed.ToArray())
            {
                if (pair.Value <= now) Allowed.TryRemove(pair.Key, out _);
            }

            // Still over: the oldest go first, enough to leave room.
            if (Allowed.Count <= MaxEntries) return;
            foreach (var pair in Allowed.ToArray().OrderBy(p => p.Value).Take(Allowed.Count - MaxEntries + MaxEntries / 8))
            {
                Allowed.TryRemove(pair.Key, out _);
            }
        }
    }
}
