using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Profiles.Configuration;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Bitrate, session count, forced transcoding, SyncPlay and blocked unrated items: the
    /// limits a profile takes as the stricter of its own and its master's.
    ///
    /// <para><b>What went wrong.</b> "Its own" meant whatever was on the profile's account,
    /// which is mostly what Bonfire wrote last time, the master's value included. Tightening a
    /// master tightened its profiles, and loosening it never reached them: each switch took
    /// the stricter of the new master value and the old one Bonfire had written. A master set
    /// to one stream for an evening left every profile on one stream for good, with nothing in
    /// the profile form to undo it.</para>
    ///
    /// <para><b>The fix</b> is to remember which part is the profile's. Bonfire records what it
    /// last wrote (<see cref="ProfileMapping.LastAppliedLimits"/>). An account field that still
    /// equals the record is Bonfire's own write and says nothing about the profile. A field
    /// that differs was changed by somebody else, such as an administrator in Dashboard ->
    /// Users, and becomes the profile's own (<see cref="ProfileMapping.OwnLimits"/>).</para>
    ///
    /// <para><b>Profiles from before this</b> have no record, so Bonfire's writes cannot be
    /// told from an administrator's. A field equal to the master's is taken as inherited: the
    /// result is the same today, and a later loosening flows through. A field stricter than
    /// the master's is kept as the profile's own, the reading that never loosens anything on
    /// upgrade.</para>
    ///
    /// <para>Pure and static so tests/cs/switchpolicy can drive every case.</para>
    /// </summary>
    internal static class InheritedLimits
    {
        /// <summary>The value of each field that means "no limit".</summary>
        public static ProfileLimits None() => new ProfileLimits();

        public sealed class Outcome
        {
            /// <summary>What to write to the profile's account.</summary>
            public ProfileLimits Applied { get; init; } = new();

            /// <summary>The profile's own limits, to store on its mapping.</summary>
            public ProfileLimits Own { get; init; } = new();
        }

        /// <param name="master">The master's limits as they are now.</param>
        /// <param name="account">The profile account's limits as they are now, before anything is written.</param>
        /// <param name="ownBefore">The profile's stored own limits; null for a profile with no record.</param>
        /// <param name="lastApplied">What Bonfire last wrote; null for a profile with no record.</param>
        public static Outcome Resolve(
            ProfileLimits master, ProfileLimits account, ProfileLimits? ownBefore, ProfileLimits? lastApplied)
        {
            var own = new ProfileLimits();

            if (lastApplied == null)
            {
                own.RemoteClientBitrateLimit = account.RemoteClientBitrateLimit == master.RemoteClientBitrateLimit
                    ? 0 : account.RemoteClientBitrateLimit;
                own.MaxActiveSessions = account.MaxActiveSessions == master.MaxActiveSessions
                    ? 0 : account.MaxActiveSessions;
                own.ForceRemoteSourceTranscoding = account.ForceRemoteSourceTranscoding
                    && !master.ForceRemoteSourceTranscoding;
                own.SyncPlayAccess = account.SyncPlayAccess == master.SyncPlayAccess
                    ? default : account.SyncPlayAccess;
                own.BlockUnratedItems = Set(account.BlockUnratedItems).Except(Set(master.BlockUnratedItems)).ToList();
            }
            else
            {
                var before = ownBefore ?? None();
                own.RemoteClientBitrateLimit = account.RemoteClientBitrateLimit != lastApplied.RemoteClientBitrateLimit
                    ? account.RemoteClientBitrateLimit : before.RemoteClientBitrateLimit;
                own.MaxActiveSessions = account.MaxActiveSessions != lastApplied.MaxActiveSessions
                    ? account.MaxActiveSessions : before.MaxActiveSessions;
                own.ForceRemoteSourceTranscoding = account.ForceRemoteSourceTranscoding != lastApplied.ForceRemoteSourceTranscoding
                    ? account.ForceRemoteSourceTranscoding : before.ForceRemoteSourceTranscoding;
                own.SyncPlayAccess = account.SyncPlayAccess != lastApplied.SyncPlayAccess
                    ? account.SyncPlayAccess : before.SyncPlayAccess;
                // A set, read as a diff: what somebody added since Bonfire's write joins the
                // profile's own, and what they removed leaves it. Taking the whole account set
                // would adopt the master's old items as the profile's and bring the ratchet
                // back.
                var now = Set(account.BlockUnratedItems);
                var wrote = Set(lastApplied.BlockUnratedItems);
                var ownSet = Set(before.BlockUnratedItems);
                ownSet.UnionWith(now.Except(wrote));
                ownSet.ExceptWith(wrote.Except(now));
                own.BlockUnratedItems = ownSet.ToList();
            }

            var applied = new ProfileLimits
            {
                RemoteClientBitrateLimit = Stricter(master.RemoteClientBitrateLimit, own.RemoteClientBitrateLimit),
                MaxActiveSessions = Stricter(master.MaxActiveSessions, own.MaxActiveSessions),
                ForceRemoteSourceTranscoding = master.ForceRemoteSourceTranscoding || own.ForceRemoteSourceTranscoding,
                // CreateAndJoinGroups, JoinGroups, None: a larger value allows less.
                SyncPlayAccess = (int)master.SyncPlayAccess >= (int)own.SyncPlayAccess ? master.SyncPlayAccess : own.SyncPlayAccess,
                BlockUnratedItems = Set(master.BlockUnratedItems).Union(Set(own.BlockUnratedItems)).ToList(),
            };

            return new Outcome { Applied = applied, Own = own };
        }

        /// <summary>The smaller of two limits where zero (or less) means "none".</summary>
        public static int Stricter(int a, int b)
        {
            if (a <= 0) return Math.Max(b, 0);
            if (b <= 0) return a;
            return Math.Min(a, b);
        }

        public static bool Same(ProfileLimits? a, ProfileLimits? b)
        {
            if (a == null || b == null) return a == b;
            return a.RemoteClientBitrateLimit == b.RemoteClientBitrateLimit
                && a.MaxActiveSessions == b.MaxActiveSessions
                && a.ForceRemoteSourceTranscoding == b.ForceRemoteSourceTranscoding
                && a.SyncPlayAccess == b.SyncPlayAccess
                && Set(a.BlockUnratedItems).SetEquals(Set(b.BlockUnratedItems));
        }

        private static HashSet<T> Set<T>(IEnumerable<T>? items) => new HashSet<T>(items ?? Enumerable.Empty<T>());
    }
}
