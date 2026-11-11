using System;
using System.Threading.Tasks;
using Campground.Shared.Contracts;
using Campground.Shared.Registry;
using Jellyfin.Profiles.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Bonfire's answer to other Campground plugins asking "may this user play now?"
    /// (<c>campground.limits.v1</c>, Campground-Coordination #32). A game in GameTent asks it at
    /// launch and about once a minute.
    ///
    /// <para>It answers from the rules the playback gate uses (<see cref="ViewingTracker"/>), so
    /// the two cannot disagree. A user with no rule, an administrator, a main account and a user
    /// Bonfire does not know are all allowed: only a limited profile is ever refused. The
    /// emergency disable empties <see cref="ViewingTracker.Index"/>, which reads as allowed.</para>
    ///
    /// <para><b>Grace.</b> The contract starts grace when the answer turns to no, so a refusal
    /// carries how long a running activity may carry on: what is left of the grace Bonfire already
    /// gave a video, or Bonfire's five minutes. A game and a film stop together. The answer is
    /// <i>not</i> held at "allowed" through the grace: the caller would then wait out its own
    /// grace on top of it.</para>
    ///
    /// <para><b>Never waits.</b> The call can arrive on any thread. It reads the rules from memory
    /// and takes only the short locks the playback gate takes; it asks Jellyfin nothing.</para>
    ///
    /// <para><b>Maintenance</b> is not answered here: it needs to know whether the user is an
    /// administrator, which is a Jellyfin lookup (see the report on #32).</para>
    /// </summary>
    internal static class LimitsProvider
    {
        /// <summary>The minor of <c>campground.limits.v1</c> this build offers.</summary>
        internal const int Minor = 0;

        /// <summary>
        /// Offers the capability. Safe to call more than once. Not before the Campground generation
        /// is open, or the offer is swept: see <see cref="LimitsStartupFilter"/>.
        /// </summary>
        internal static void Register()
            => Limits.Provide(PluginIds.Bonfire, Minor, (userId, _, _) => Task.FromResult(Decide(userId, ViewingTracker.Now())));

        internal static LimitsDecision Decide(Guid userId, DateTime nowUtc)
        {
            var index = ViewingTracker.Index();
            if (!index.TryGetValue(userId, out var limited)) return Limits.Allowed;

            var state = ViewingTracker.State(limited, nowUtc);
            if (state.Allowed)
            {
                return new LimitsDecision(true, null, state.UntilUtc.HasValue ? Utc(state.UntilUtc.Value) : null, 0);
            }

            var reason = state.Reason == ViewingClock.Limit ? Limits.Reasons.DailyLimitReached : Limits.Reasons.OutsideViewingHours;
            var grace = ViewingTracker.GraceMinutes * 60;
            DateTime? endsUtc;
            lock (ViewingTracker.UsageLock) endsUtc = ViewingTracker.EntryFor(userId).GraceEndsUtc;
            if (endsUtc.HasValue && endsUtc.Value > nowUtc && endsUtc.Value <= nowUtc.AddMinutes(ViewingTracker.GraceMinutes))
            {
                grace = (int)Math.Ceiling((endsUtc.Value - nowUtc).TotalSeconds);
            }

            return new LimitsDecision(false, reason, state.NextStartUtc.HasValue ? Utc(state.NextStartUtc.Value) : null, grace);
        }

        private static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    /// <summary>
    /// Offers <see cref="LimitsProvider"/> once the Campground generation is open. That happens when
    /// the coordinator's startup filter configures the pipeline, and an offer made before it can be
    /// swept as the old host's. This filter is registered after the coordinator's, so its
    /// configuration runs after it; it adds no middleware.
    /// </summary>
    internal sealed class LimitsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                try { LimitsProvider.Register(); }
                catch (Exception) { /* Never the reason Jellyfin fails to start: callers treat no provider as allowed. */ }
                next(app);
            };
    }
}
