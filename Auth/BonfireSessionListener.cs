using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Remembers which household a device belongs to, every time anyone signs in on it.
    ///
    /// <para><b>Why this exists.</b> <see cref="DeviceRegistry"/> is the store, and it has
    /// existed since the device-restrictions work — but the only two things that ever wrote
    /// to it were authenticated Bonfire routes, and those are reached from the web switcher
    /// alone. A television never calls Bonfire's own API at all, so the map was populated
    /// exclusively by the one client that had no use for it. This is the writer that every
    /// client reaches, because every client authenticates.</para>
    ///
    /// <para><b>Why <see cref="ISessionManager.SessionStarted"/>.</b> It is the narrowest
    /// event that means "somebody signed in here": one per sign-in, rather than
    /// <c>SessionActivity</c>, which fires on every request and would put this work in front
    /// of all of them. The trade-off is that a household already signed in when the plugin
    /// updates is not recorded until their next sign-in — which is self-healing, and is the
    /// same sign-in the feature asks for anyway.</para>
    ///
    /// <para>The event is identical in 10.11.5 and 12.0 —
    /// <c>event EventHandler&lt;SessionEventArgs&gt; SessionStarted</c> — so one assembly
    /// serves both servers.</para>
    /// </summary>
    public class BonfireSessionListener : IHostedService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<BonfireSessionListener> _logger;

        public BonfireSessionListener(
            ISessionManager sessionManager,
            ILogger<BonfireSessionListener> logger)
        {
            _sessionManager = sessionManager;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.SessionStarted += OnSessionStarted;

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
            return Task.CompletedTask;
        }

        private void OnSessionStarted(object? sender, SessionEventArgs e)
        {
            // Nothing here may be allowed to escape. This runs on Jellyfin's own event
            // dispatch, not on a request, so an exception is not ours to lose politely —
            // Android TV's LeanbackChannelWorker is the standing example of what an escaped
            // exception on a background path does to an application, and that one only had
            // to brick a launcher row.
            try
            {
                var session = e?.SessionInfo;
                if (session == null || string.IsNullOrWhiteSpace(session.DeviceId)) return;

                var config = Plugin.Instance?.Configuration;
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
                    _logger.LogInformation(
                        "ProfilesPlugin: {User} signed in on device {DeviceId} ({Client}), which is not a "
                        + "Bonfire household, so nothing was noted for its sign-in screen.",
                        session.UserName, session.DeviceId, session.Client);
                    return;
                }

                DeviceRegistry.RecordAndSave(
                    session.DeviceId,
                    session.DeviceName,
                    session.Client,
                    master,
                    _logger);

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
                if (bare != null)
                {
                    DeviceRegistry.RecordAndSave(
                        bare,
                        session.DeviceName,
                        session.Client,
                        master,
                        _logger);

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
    }
}
