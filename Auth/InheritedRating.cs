using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// A profile's parental rating: the stricter of its own and its main account's, where
    /// "its own" is remembered rather than read off the account (R-1045).
    ///
    /// <para><b>What went wrong.</b> The profile's own rating was whatever was on its account,
    /// which is mostly what Bonfire wrote last time: the main account's, clamped. A main
    /// account set to PG for an evening left every profile at PG for good, with nothing in the
    /// profile form to undo it. This is the ratchet <see cref="InheritedLimits"/> removed for
    /// the five limits, resolved the same way: Bonfire records what it last wrote
    /// (<see cref="ProfileMapping.LastAppliedRating"/>). An account rating equal to it says
    /// nothing about the profile; one that differs was set by somebody, the profile's editor
    /// or an administrator in Dashboard -> Users, and becomes the profile's own
    /// (<see cref="ProfileMapping.OwnRating"/>).</para>
    ///
    /// <para><b>A profile from before the record</b> cannot tell Bonfire's write from a choice.
    /// A rating equal to the main account's is taken as inherited, so a later relaxing reaches
    /// it; a different one is kept as its own, which never loosens anything on upgrade. Either
    /// way it is never wider than the main account's
    /// (<see cref="ProfilesBaseController.ClampRating"/>).</para>
    ///
    /// <para>Pure and static so tests/cs/policysync can drive every case.</para>
    /// </summary>
    internal static class InheritedRating
    {
        public sealed class Outcome
        {
            /// <summary>What to write to the profile's account.</summary>
            public ProfileRating Applied { get; init; } = new();

            /// <summary>The profile's own rating, to store on its mapping.</summary>
            public ProfileRating Own { get; init; } = new();
        }

        /// <param name="master">The main account's rating as it is now.</param>
        /// <param name="account">The profile account's rating as it is now, before anything is written.</param>
        /// <param name="ownBefore">The profile's stored own rating; null for a profile with no record.</param>
        /// <param name="lastApplied">What Bonfire last wrote; null for a profile with no record.</param>
        public static Outcome Resolve(ProfileRating master, ProfileRating account, ProfileRating? ownBefore, ProfileRating? lastApplied)
        {
            ProfileRating own;
            if (lastApplied == null)
            {
                own = Same(account, master) ? new ProfileRating() : account.Copy();
            }
            else
            {
                own = Same(account, lastApplied) ? (ownBefore ?? new ProfileRating()).Copy() : account.Copy();
            }

            var (score, sub) = ProfilesBaseController.ClampRating(own.Score, own.SubScore, master.Score, master.SubScore);
            return new Outcome { Applied = new ProfileRating { Score = score, SubScore = sub }, Own = own };
        }

        public static bool Same(ProfileRating? a, ProfileRating? b)
        {
            if (a == null || b == null) return a == b;
            return a.Score == b.Score && a.SubScore == b.SubScore;
        }
    }
}
