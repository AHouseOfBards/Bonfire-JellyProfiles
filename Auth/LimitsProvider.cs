using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Campground.Shared.Contracts;
using Campground.Shared.Registry;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// <para><b>Maintenance</b> refuses everyone but administrators, with <c>Until</c> when the
    /// administrator set an end. Whether a user is an administrator is a database query in
    /// Jellyfin, so the call reads a set of administrator ids that is refreshed off it.</para>
    /// </summary>
    internal static class LimitsProvider
    {
        /// <summary>The minor of <c>campground.limits.v1</c> this build offers.</summary>
        internal const int Minor = 0;

        /// <summary>
        /// Offers the capability. Safe to call more than once. Not before the Campground generation
        /// is open, or the offer is swept: see <see cref="LimitsStartupFilter"/>.
        /// </summary>
        internal static void Register() => Register(null);

        internal static void Register(IUserManager? users)
        {
            if (users != null) _users = users;
            // Said once: with no user manager the administrators are never known, so
            // maintenance reads as "allowed" for every Campground caller, by design but not
            // quietly (R2-02-04).
            if (_users == null && Interlocked.Exchange(ref _saidNoUsers, 1) == 0)
            {
                ViewingTracker.Log?.LogWarning("ProfilesPlugin: no user manager, so maintenance is not enforced for other Campground plugins.");
            }

            Limits.Provide(PluginIds.Bonfire, Minor, (userId, _, _) => Task.FromResult(Decide(userId, ViewingTracker.Now())));
            RefreshAdmins();
            // Who is limited, for a caller that gets no answer from this provider. Written now so
            // the file is current after a restart, and again whenever a save changes it.
            ViewingTracker.RulesChanged();
        }

        // ── Maintenance ─────────────────────────────────────────────────────────────

        // Jellyfin's UserManager.GetUserById is a database query (release-10.11.z opens a
        // context and runs FirstOrDefault), so the answer cannot ask it. The ids of the
        // administrators are kept here instead and refreshed off the call: on startup, and
        // whenever a call during maintenance finds the set older than a minute. Until the first
        // refresh lands the set is null and maintenance reads as "allowed", the contract's
        // fail-open, rather than refusing an administrator on a guess.
        private static readonly TimeSpan AdminsFresh = TimeSpan.FromSeconds(60);
        private static volatile HashSet<Guid>? _admins;
        private static long _adminsAtTicks;
        private static int _refreshing;
        private static IUserManager? _users;
        private static int _saidNoUsers;
        private static int _saidRefreshFailed;

        /// <summary>Replaces the administrator set. For the refresh, and for tests.</summary>
        internal static void SetAdmins(IEnumerable<Guid> ids, DateTime atUtc)
        {
            _admins = new HashSet<Guid>(ids);
            Interlocked.Exchange(ref _adminsAtTicks, atUtc.Ticks);
        }

        /// <summary>Forgets the set, so maintenance reads as allowed until the next refresh. For tests.</summary>
        internal static void ForgetAdmins()
        {
            _admins = null;
            Interlocked.Exchange(ref _adminsAtTicks, 0);
        }

        /// <summary>Starts a refresh on the thread pool, if there is a user manager and none is running.</summary>
        private static void RefreshAdmins()
        {
            var users = _users;
            if (users == null || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
            // BackgroundWork, not Task.Run, so a server stop does not leave this running
            // (tests/cs/shutdown). The user list goes through AllUsersOf: users.Users is not on
            // 10.11.7 and later, and its MissingMethodException fired before this try block,
            // leaving _refreshing set for good.
            _ = BackgroundWork.Run(_ =>
            {
                try
                {
                    var ids = new List<Guid>();
                    foreach (var user in Controllers.ProfilesBaseController.AllUsersOf(users))
                    {
                        if (Jellyfin.Data.UserEntityExtensions.HasPermission(user, Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
                        {
                            ids.Add(user.Id);
                        }
                    }

                    SetAdmins(ids, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    // Keeps the old set, or none. Tried again a minute on, not by the very next
                    // call: stamping the failure as a refresh made every call during
                    // maintenance start another one (R2-02-04). Said once.
                    Interlocked.Exchange(ref _adminsAtTicks, DateTime.UtcNow.Ticks);
                    if (Interlocked.Exchange(ref _saidRefreshFailed, 1) == 0)
                    {
                        ViewingTracker.Log?.LogWarning(ex, "ProfilesPlugin: could not read the administrators, so maintenance is not enforced for other Campground plugins until it can.");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        }

        /// <summary>
        /// The refusal for a non-administrator while maintenance is on, or null when it does not
        /// apply: maintenance is off or has lapsed, the emergency disable is on, the user is an
        /// administrator, or the administrators are not known yet. Reads memory only.
        /// </summary>
        private static LimitsDecision? Maintenance(Guid userId, DateTime nowUtc)
        {
            if (Plugin.IsOff) return null;
            var m = Plugin.Instance?.Configuration?.Maintenance;
            if (m == null || !m.Enabled || m.HasLapsed(nowUtc)) return null;

            if (new DateTime(Interlocked.Read(ref _adminsAtTicks), DateTimeKind.Utc) + AdminsFresh < DateTime.UtcNow) RefreshAdmins();
            var admins = _admins;
            if (admins == null || admins.Contains(userId)) return null;

            var until = m.UntilUtc.HasValue && m.UntilUtc.Value > nowUtc ? Utc(m.UntilUtc.Value) : (DateTimeOffset?)null;
            return new LimitsDecision(false, Limits.Reasons.Maintenance, until, ViewingTracker.GraceMinutes * 60);
        }

        internal static LimitsDecision Decide(Guid userId, DateTime nowUtc)
        {
            var closed = Maintenance(userId, nowUtc);
            if (closed != null) return closed;

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
                try { LimitsProvider.Register(app.ApplicationServices.GetService<IUserManager>()); }
                catch (Exception) { /* Never the reason Jellyfin fails to start: callers treat no provider as allowed. */ }
                next(app);
            };
    }
}
