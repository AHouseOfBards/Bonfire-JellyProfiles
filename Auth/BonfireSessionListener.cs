using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Remembers which household a device belongs to, when somebody signs in on it.
    ///
    /// <para><b>Only a password makes a device known.</b> A PIN works only on a device its
    /// household signed in on with the password, so a session a PIN, an empty box or a switch
    /// started may refresh a record (last seen, name) and never makes one or adds a household
    /// to one: otherwise the PIN would vouch for its own device. The sign-in request says
    /// which it was (<see cref="RequestDevice.SignIn"/>), and Jellyfin raises these events
    /// through Task.Run, which carries that request's context here. A session that starts
    /// outside a sign-in request is a token being used, and every token began with a sign-in
    /// that was checked: that is how a television's later, derived id (Android TV's
    /// sha1(id + "+" + account), Roku's appended name) joins the record its password
    /// sign-in made.</para>
    ///
    /// <para><b>Why this exists.</b> <see cref="DeviceRegistry"/> is the store, and it has
    /// existed since the device-restrictions work — but the only two things that ever wrote
    /// to it were authenticated Bonfire routes, and those are reached from the web switcher
    /// alone. A television never calls Bonfire's own API at all, so the map was populated
    /// exclusively by the one client that had no use for it. This is the writer that every
    /// client reaches, because every client authenticates.</para>
    ///
    /// <para><b>Why <see cref="ISessionManager.SessionStarted"/>.</b> It is the narrowest
    /// event that means "somebody signed in here": one per new session, whether or not an
    /// account is on it yet (a session with none is ignored). <c>SessionActivity</c>
    /// is raised far more often (up to once every ten seconds per session), so the work is
    /// never done from there unless the account on the session has changed; see below. The
    /// trade-off is that a household already signed in when the plugin
    /// updates is not recorded until their next sign-in — which is self-healing, and is the
    /// same sign-in the feature asks for anyway.</para>
    ///
    /// <para><b>Why <c>SessionActivity</c> as well (R-1008).</b> SessionStarted alone misses
    /// a second account signing in on the same app and device without signing out first.
    /// Jellyfin keys a session by app name + device id (<c>SessionManager.GetSessionKey</c>,
    /// release-10.11.z); <c>GetSessionInfo</c> raises SessionStarted only when
    /// <c>GetOrAdd</c> creates the record, and otherwise writes the new account's UserId and
    /// UserName over the old ones in place. A profile signing in on a television somebody
    /// else last used got no policy sync and no device row. A handover does go through
    /// <c>LogSessionActivity</c>, which raises SessionActivity at most once per ten seconds
    /// per session, so that is watched, and the work is one dictionary compare: a change of
    /// account on a session already seen is treated as a sign-in.</para>
    ///
    /// <para>The events are identical in 10.11.5 and 12.0 —
    /// <c>event EventHandler&lt;SessionEventArgs&gt;</c> — so one assembly serves both
    /// servers.</para>
    /// </summary>
    public class BonfireSessionListener : IHostedService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<BonfireSessionListener> _logger;

        /// <param name="services">
        /// Asked for the user and library managers only when a profile signs in, rather than
        /// injected with them, so nothing about building this listener can fail the server's
        /// start — the same reason the PIN provider takes it.
        /// </param>
        public BonfireSessionListener(
            ISessionManager sessionManager,
            ILogger<BonfireSessionListener> logger,
            IServiceProvider? services = null)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _services = services;
        }

        private readonly IServiceProvider? _services;

        /// <summary>
        /// The account last seen on each session, by session id, so a handover can be told
        /// from more activity by the same account. Emptied as sessions end.
        /// </summary>
        private readonly ConcurrentDictionary<string, Guid> _accountOnSession = new(StringComparer.Ordinal);

        /// <summary>A ceiling, in case sessions stop being reported as ended.</summary>
        private const int MaxTrackedSessions = 10000;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.SessionStarted += OnSessionStarted;
            _sessionManager.SessionActivity += OnSessionActivity;
            _sessionManager.SessionEnded += OnSessionEnded;
            _sessionManager.PlaybackStart += OnPlaybackStart;

            // Said out loud on purpose. Without it, "no profiles appeared" cannot be told
            // apart from "the listener never started", and those have completely different
            // fixes. One line per server start is not a cost.
            _logger.LogInformation(
                "ProfilesPlugin: watching for sign-ins, so the devices people use can be "
                + "offered their household's profiles on a sign-in screen.");

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.SessionStarted -= OnSessionStarted;
            _sessionManager.SessionActivity -= OnSessionActivity;
            _sessionManager.SessionEnded -= OnSessionEnded;
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _accountOnSession.Clear();
            _playerNoted.Clear();
            _stopping.Cancel();
            return Task.CompletedTask;
        }

        /// <summary>Sessions whose device has been noted as a player, so that is done once.</summary>
        private readonly ConcurrentDictionary<string, bool> _playerNoted = new(StringComparer.Ordinal);

        private readonly CancellationTokenSource _stopping = new();

        private static bool ShowsPlayback(SessionInfo session)
            => DeviceRegistry.ReportsPlayback(session.PlayableMediaTypes?.Count ?? 0, session.SupportsMediaControl);

        /// <summary>
        /// Notes <paramref name="session"/>'s device as a player or, after the grace, as a
        /// service (see KnownDevice.CanPlay). The Roku id without the account's name is the
        /// other row that television makes.
        /// </summary>
        private void NoteDevice(SessionInfo session, bool canPlay)
        {
            DeviceRegistry.NotePlayerAndSave(session.DeviceId, canPlay);
            var bare = DeviceRegistry.BareDeviceIdFor(session.DeviceId, session.UserName);
            if (bare != null) DeviceRegistry.NotePlayerAndSave(bare, canPlay);
        }

        /// <summary>
        /// A sign-in has had <see cref="DeviceRegistry.PlayerGrace"/> to show it can play. Apps
        /// post their capabilities just after signing in; a service never does. Judged on the
        /// session object, which Jellyfin updates in place, so it is right even if the
        /// session has since gone.
        /// </summary>
        private void JudgeAfterGrace(SessionInfo session)
        {
            // Through BackgroundWork like SyncProfilePolicy, so a server stop cancels the wait
            // and is not left with it (tests/cs/shutdown). Our own token still ends it when this
            // listener stops alone.
            var own = _stopping.Token;
            _ = Configuration.BackgroundWork.Run(async hostStop =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStop, own);
                    await Task.Delay(DeviceRegistry.PlayerGrace, linked.Token).ConfigureAwait(false);
                    var plays = ShowsPlayback(session);
                    if (plays) _playerNoted[session.Id] = true;
                    NoteDevice(session, plays);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProfilesPlugin: could not tell whether a device can play.");
                }
            });
        }

        /// <summary>
        /// Playing something is the one proof an app that never posts capabilities gives.
        /// Raised per play, not per request.
        /// </summary>
        private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(e?.DeviceId)) DeviceRegistry.NotePlayerAndSave(e.DeviceId, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: could not note a device that started playing.");
            }
        }

        private void OnSessionStarted(object? sender, SessionEventArgs e)
        {
            var session = e?.SessionInfo;
            if (session == null) return;
            Remember(session);
            OnSignedIn(session);
        }

        /// <summary>
        /// Raised from LogSessionActivity, at most once per ten seconds per session. Must stay
        /// a compare: anything more here is in front of every client's requests.
        /// </summary>
        private void OnSessionActivity(object? sender, SessionEventArgs e)
        {
            try
            {
                var session = e?.SessionInfo;
                if (session == null || string.IsNullOrEmpty(session.Id)) return;
                var now = session.UserId;

                // Capabilities that arrived after the grace: a dictionary lookup and two reads.
                if (!_playerNoted.ContainsKey(session.Id) && ShowsPlayback(session))
                {
                    _playerNoted[session.Id] = true;
                    NoteDevice(session, true);
                }

                // First sighting: a session that was open before the plugin started. Only
                // remembered — acting on it would run a sign-in for every open session at
                // once on each server start, and SessionStarted covered it when it began.
                if (!_accountOnSession.TryGetValue(session.Id, out var before))
                {
                    Remember(session);
                    return;
                }

                if (before == now) return;

                // Compare-and-set, so two activity events racing for one handover act once.
                if (!_accountOnSession.TryUpdate(session.Id, now, before)) return;

                // Signed out, or a request with no account: nothing to sign in.
                if (now == Guid.Empty) return;

                _logger.LogInformation(
                    "ProfilesPlugin: {User} took over an open session on device {DeviceId} ({Client}) "
                    + "without a sign-out, which Jellyfin does not report as a sign-in; treated as one.",
                    session.UserName, session.DeviceId, session.Client);
                OnSignedIn(session, before);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: could not check a session for a change of account.");
            }
        }

        private void OnSessionEnded(object? sender, SessionEventArgs e)
        {
            var id = e?.SessionInfo?.Id;
            if (!string.IsNullOrEmpty(id))
            {
                _accountOnSession.TryRemove(id, out _);
                _playerNoted.TryRemove(id, out _);
            }
        }

        private void Remember(SessionInfo session)
        {
            if (string.IsNullOrEmpty(session.Id)) return;
            if (_accountOnSession.Count >= MaxTrackedSessions) _accountOnSession.Clear();
            // Bounded the same way (R2-01-04). Forgetting only means a session is noted again.
            if (_playerNoted.Count >= MaxTrackedSessions) _playerNoted.Clear();
            _accountOnSession[session.Id] = session.UserId;
        }

        /// <summary>Somebody signed in on <paramref name="session"/>: a new session, or a handover.</summary>
        private void OnSignedIn(SessionInfo session, Guid? previousUserId = null)
        {
            // Nothing here may be allowed to escape. This runs on Jellyfin's own event
            // dispatch, not on a request, so an exception is not ours to lose politely —
            // Android TV's LeanbackChannelWorker is the standing example of what an escaped
            // exception on a background path does to an application, and that one only had
            // to brick a launcher row.
            try
            {
                // A session with no account is not a sign-in. Falling on, it would be taken
                // for an account outside every household and note the device's name as
                // shared, which turns the Quick Connect skip off for that model.
                if (session.UserId == Guid.Empty) return;

                SyncProfilePolicy(session.UserId);

                if (string.IsNullOrWhiteSpace(session.DeviceId)) return;

                // A snapshot taken under the lock. This walks Mappings, and an administrator
                // saving a profile at the same moment can change that list mid-walk: the
                // enumeration throws, the catch below logs it, and this sign-in's device is
                // never recorded - so its television later lacks the household's profiles.
                var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();
                if (config == null) return;

                // Only households Bonfire actually runs. Every other account on the server
                // signs in here too, and recording them would fill an administrator's device
                // list with hardware that has nothing to do with any profile — and grow a
                // configuration file that is rewritten in full every time it is saved.
                //
                // Asked as its own question rather than read off HouseholdOf, which returns
                // the account itself when it knows of no mapping and so is never empty.
                var master = DeviceRegistry.IsHousehold(config, session.UserId)
                    ? DeviceRegistry.HouseholdOf(config, session.UserId)
                    : Guid.Empty;

                if (master == Guid.Empty)
                {
                    // Debug: every other account on the server signs in here, and one line per
                    // sign-in in the Information log is noise for an administrator.
                    _logger.LogDebug(
                        "ProfilesPlugin: {User} signed in on device {DeviceId} ({Client}), which is not a "
                        + "Bonfire household, so nothing was noted for its sign-in screen.",
                        session.UserName, session.DeviceId, session.Client);

                    // But its television's NAME is noted, so the Quick Connect skip never takes
                    // Quick Connect away from this person because a household owns the same
                    // model. See QuickConnectGate.NoteSharedName. Names only, and only for the
                    // clients the skip applies to.
                    if (QuickConnectGate.NoteSharedNameAndSave(session.DeviceName, session.Client))
                    {
                        _logger.LogInformation(
                            "ProfilesPlugin: {Device} is also used by an account outside any household, so "
                            + "Quick Connect is no longer skipped on televisions of that name.",
                            session.DeviceName);
                    }

                    return;
                }

                // A household's account signed in on a device: for the other Campground plugins that is
                // a profile switch, and on a native app this is the only place it is seen. The switch
                // route raises its own, and BonfireCampground keeps the two from being counted twice.
                BonfireCampground.RaiseSwitchedFromSession(
                    master,
                    session.UserId,
                    config.Mappings.FirstOrDefault(m => m.ProfileUserId == session.UserId)?.ProfileName ?? session.UserName,
                    session.DeviceId,
                    previousUserId == Guid.Empty ? null : previousUserId);

                // A PIN, an empty box or a switch refreshes, never makes a device known.
                // A sign-in with no note at all (SSO's own endpoint) counts as a password
                // one. See RequestDevice.MayMakeDeviceKnown.
                //
                // The note is there only while this runs inside the sign-in's own request
                // (SessionStarted, through Task.Run). A handover noticed from SessionActivity,
                // up to ten seconds later on another request, has none and counts as a
                // password one, so it may grant (R2-01-02). Left so on purpose: refusing it
                // would stop a television taken over by a profile ever being recorded.
                var mayGrant = RequestDevice.MayMakeDeviceKnown()
                    // A profile approving a Quick Connect code vouches for a device its household
                    // never signed in on. A main account approving is a real sign-in (Android TV).
                    && !(RequestDevice.IsQuickConnectAuthorize
                         && config.Mappings.Any(m => m.ProfileUserId == session.UserId && m.MasterUserId != m.ProfileUserId));

                DeviceRegistry.RecordSightingAndSave(
                    session.DeviceId,
                    session.DeviceName,
                    session.Client,
                    master,
                    _logger,
                    mayGrant);

                // Roku signs in with one id and browses with another: it appends the
                // account's name to its device id once a session exists, so the id on its
                // sign-in screen is the prefix of the id every later request carries. Record
                // that prefix too, or the screen we are trying to put profiles on is the one
                // id we never noted.
                //
                // Reversed here rather than derived at lookup because this is the only place
                // that knows WHICH name the client used — Roku uses the real username after
                // a token restore and the household's name after a PIN login, and only one
                // of those is in our configuration. See DeviceRegistry.BareDeviceIdFor.
                //
                // It is a second row in the administrator's device list for one physical
                // television, which is honest: two device ids really are in use. Recording
                // never reassigns a row another household owns, so a coincidental strip
                // cannot take a device away from anyone.
                var bare = DeviceRegistry.BareDeviceIdFor(session.DeviceId, session.UserName);

                // Whether this is somebody's player or a service signing in as them.
                if (!string.IsNullOrEmpty(session.Id)) JudgeAfterGrace(session);

                if (bare != null)
                {
                    DeviceRegistry.RecordSightingAndSave(
                        bare,
                        session.DeviceName,
                        session.Client,
                        master,
                        _logger,
                        mayGrant);

                    _logger.LogInformation(
                        "ProfilesPlugin: device {DeviceId} carries {User}'s name appended to it, so "
                        + "{Bare} was noted as well — that is what this app sends on its sign-in screen.",
                        session.DeviceId, session.UserName, bare);
                }

                // Information, not Debug. This is the half of the feature that WRITES, and
                // shipping it silent in 1.6.1.7 repeated the exact mistake 1.6.1.6 existed to
                // fix on the reading half: a device that was never recorded and a device that
                // was recorded and then not read produce the same visible nothing.
                //
                // SessionStarted fires once per new session, not per request, so this is a
                // handful of lines a day even on a busy server.
                _logger.LogInformation(
                    "ProfilesPlugin: {User} signed in on device {DeviceId} ({Client}); household "
                    + "{Master} noted, so its profiles can be offered on this device's sign-in screen.",
                    session.UserName, session.DeviceId, session.Client, master);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ProfilesPlugin: could not record the device a session started on. "
                    + "Profiles may not appear on that client's sign-in screen.");
            }
        }

        /// <summary>
        /// How long after a sign-in the policy is applied a second time. See
        /// <see cref="SyncProfilePolicy"/>.
        /// </summary>
        private static readonly TimeSpan SecondPass = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Applies a sub-profile's master policy when it signs in anywhere. Until this, only
        /// the web switch did it, so a profile used only on a television never heard about a
        /// library taken from its master or a lowered rating. See <see cref="ProfilePolicySync"/>.
        ///
        /// <para><b>Why twice.</b> The event is raised from inside
        /// <c>SessionManager.LogSessionActivity</c>, which, straight after, saves the
        /// <c>User</c> it was handed — read before this sign-in, so before anything we write —
        /// and Jellyfin's <c>UpdateUserAsync</c> writes that object's permissions and
        /// preferences back whole (verified in release-10.11.z
        /// Jellyfin.Server.Implementations/Users/UserManager.cs). A policy written in that
        /// window is reverted. The event is queued on the thread pool, so which comes first is
        /// a race. The first pass covers the requests the television makes straight away; the
        /// second runs after that save has happened, and is a no-op when nothing was reverted
        /// (ProfilePolicySync leaves an account that is already right alone). After that save
        /// the activity date is fresh, and Jellyfin does not save the user again for a minute.</para>
        ///
        /// <para>A web switch applies the policy itself before the session exists, and then
        /// lands here as well. The runs are serialized and the repeats write nothing.</para>
        /// </summary>
        private void SyncProfilePolicy(Guid userId)
        {
            if (userId == Guid.Empty) return;

            // A snapshot, taken under ConfigLock, for the same reason as OnSessionStarted:
            // walking the live list while a save replaces it can throw.
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();
            var isSubProfile = config?.Mappings?.Any(m => m.ProfileUserId == userId && m.MasterUserId != userId) == true;
            if (!isSubProfile) return;

            // Through BackgroundWork, so a server stop cancels the second pass instead of
            // leaving it to wake after a restart and write with the old host's managers
            // (Campground-Coordination #33). The first pass is not cancelled midway: it is one
            // policy write, and the stop waits for it.
            _ = Configuration.BackgroundWork.Run(async stopping =>
            {
                try
                {
                    var userManager = _services?.GetService(typeof(MediaBrowser.Controller.Library.IUserManager))
                        as MediaBrowser.Controller.Library.IUserManager;
                    var libraryManager = _services?.GetService(typeof(MediaBrowser.Controller.Library.ILibraryManager))
                        as MediaBrowser.Controller.Library.ILibraryManager;
                    if (userManager == null || libraryManager == null) return;

                    await ProfilePolicySync.ApplyAsync(userManager, libraryManager, _logger, userId).ConfigureAwait(false);
                    await Task.Delay(SecondPass, stopping).ConfigureAwait(false);
                    await ProfilePolicySync.ApplyAsync(userManager, libraryManager, _logger, userId).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The server is stopping. The next sign-in applies it again.
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "ProfilesPlugin: could not apply the main account's limits to profile {ProfileId} "
                        + "after it signed in. They are applied again the next time it is opened.",
                        userId);
                }
            });
        }
    }
}
