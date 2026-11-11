using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// At server start, ends the profile sessions the picker made before the last stop
    /// (<see cref="PickerMarks.SweepAsync"/>). Jellyfin tokens do not expire, and the picker
    /// forgets a profile's session when the server restarts, so nothing else would ever sign
    /// them out.
    ///
    /// <para>Run a little after start rather than in it, and never the reason the server does
    /// not start: the managers are asked for here, not injected, as the session listener does.</para>
    /// </summary>
    public sealed class ProxyPickerStartup : IHostedService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<ProxyPickerStartup> _logger;

        public ProxyPickerStartup(IServiceProvider services, ILogger<ProxyPickerStartup> logger)
        {
            _services = services;
            _logger = logger;
            PickerMarks.Log = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // The wait used StartAsync's token, which only covers starting up: a restart inside
            // those twenty seconds left the old copy to wake up and sweep with the old host's
            // disposed managers (Campground-Coordination #33). BackgroundWork's token is
            // cancelled when the server stops.
            _ = Configuration.BackgroundWork.Run(async stopping =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), stopping).ConfigureAwait(false);
                    await PickerMarks.SweepAsync(
                        _services.GetService(typeof(ISessionManager)) as ISessionManager,
                        _services.GetService(typeof(IDeviceManager)) as IDeviceManager,
                        _logger).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "ProfilesPlugin: the check of profile sessions from before the restart failed.");
                }
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
