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
                if (!DeviceRegistry.IsHousehold(config, session.UserId)) return;

                var master = DeviceRegistry.HouseholdOf(config, session.UserId);
                if (master == Guid.Empty) return;

                DeviceRegistry.RecordAndSave(
                    session.DeviceId,
                    session.DeviceName,
                    session.Client,
                    master,
                    _logger);

                _logger.LogDebug(
                    "ProfilesPlugin: {User} signed in on device {DeviceId} ({Client}); "
                    + "household {Master} noted for its sign-in screen.",
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
