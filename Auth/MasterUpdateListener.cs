using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Passes a change made to a main account on to its profiles when it is made, not the next
    /// time one is opened (R-1301).
    ///
    /// <para>A library taken away in Dashboard → Users, or a lower parental rating, reached a
    /// profile only through <see cref="ProfilePolicySync"/> at sign-in, so a profile left signed
    /// in on a television kept the old limits until somebody opened it again.
    /// <see cref="IUserManager.OnUserUpdated"/> is raised after every account save, in 10.11.5
    /// and 12.0 alike (<c>EventHandler&lt;GenericEventArgs&lt;User&gt;&gt;</c>), so this
    /// subscribes to it.</para>
    ///
    /// <para><b>Off the event thread.</b> The event is raised inside the save that caused it, and
    /// a policy write takes Jellyfin's per-user lock, so writing from there waits on itself: the
    /// same hang that rules out writing from the PIN provider (see
    /// <see cref="ProfilePolicySync"/>). The handler only looks the account up in the mappings
    /// and queues the work through <see cref="Configuration.BackgroundWork"/>.</para>
    ///
    /// <para><b>It cannot trigger itself.</b> Our writes go to profile accounts, which are no
    /// one's main account, so the handler returns at the lookup. If a main account's event
    /// arrives while its profiles are being written (an administrator saving, or another
    /// plugin), it asks for one more pass and no more: events during that second pass are
    /// dropped, so a source that keeps writing the main account costs two passes per outside
    /// change, not an endless run. A pass leaves a correct account alone, so one with nothing to
    /// change writes and raises nothing.</para>
    /// </summary>
    public class MasterUpdateListener : IHostedService
    {
        private enum Pass { Running, Again, Last }

        private readonly IUserManager _userManager;
        private readonly ILogger<MasterUpdateListener> _logger;
        private readonly IServiceProvider? _services;

        // Which main accounts are being passed on, and whether another pass was asked for.
        private static readonly object PassLock = new();
        private readonly Dictionary<Guid, Pass> _passes = new();

        /// <param name="services">
        /// Asked for the library manager only when there is work, so building this listener cannot
        /// fail the server's start (as in <see cref="BonfireSessionListener"/>).
        /// </param>
        public MasterUpdateListener(
            IUserManager userManager,
            ILogger<MasterUpdateListener> logger,
            IServiceProvider? services = null)
        {
            _userManager = userManager;
            _logger = logger;
            _services = services;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _userManager.OnUserUpdated += OnUserUpdated;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _userManager.OnUserUpdated -= OnUserUpdated;
            lock (PassLock) _passes.Clear();
            return Task.CompletedTask;
        }

        private void OnUserUpdated(object? sender, Jellyfin.Data.Events.GenericEventArgs<User> e)
        {
            try
            {
                var id = e?.Argument?.Id ?? Guid.Empty;
                if (id == Guid.Empty || !HasProfiles(id)) return;

                lock (PassLock)
                {
                    if (_passes.TryGetValue(id, out var state))
                    {
                        // Already being passed on: one more pass picks this change up. During
                        // that pass, drop it (see the class comment).
                        if (state == Pass.Running) _passes[id] = Pass.Again;
                        return;
                    }
                    _passes[id] = Pass.Running;
                }

                try
                {
                    _ = Configuration.BackgroundWork.Run(async stopping =>
                {
                    var forgotten = false;
                    try
                    {
                        forgotten = await RunPassesAsync(id).ConfigureAwait(false);
                        AfterPasses?.Invoke();
                    }
                    catch (OperationCanceledException)
                    {
                        // The server is stopping. The next sign-in applies it again.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "ProfilesPlugin: could not pass a change to account {UserId} on to its profiles. "
                            + "They are updated again the next time one is opened.",
                            id);
                    }
                    finally
                    {
                        // Normally RunPassesAsync already forgot the run, under the lock that
                        // found nothing more to do; forgetting it again could erase a newer
                        // run's marker.
                        if (!forgotten) lock (PassLock) _passes.Remove(id);
                    }
                });
                }
                catch (Exception ex)
                {
                    // Starting the work threw, so nothing will end this run. It is not left
                    // marked as running, or every later change would be dropped.
                    lock (PassLock) _passes.Remove(id);
                    _logger.LogError(ex, "ProfilesPlugin: could not start passing a change to account {UserId} on to its profiles.", id);
                }
            }
            catch (Exception ex)
            {
                // On Jellyfin's own dispatch: nothing may escape.
                _logger.LogError(ex, "ProfilesPlugin: could not check an account update.");
            }
        }

        /// <summary>For the harness: called when the passes are done, before the caller's clean-up.</summary>
        internal static Action? AfterPasses = null;

        /// <summary>
        /// True when the run was forgotten here, in the same lock that found nothing more to do,
        /// so a change cannot arrive in between and be marked "again" for a run that is over.
        /// </summary>
        private async Task<bool> RunPassesAsync(Guid master)
        {
            var libraryManager = _services?.GetService(typeof(ILibraryManager)) as ILibraryManager;
            if (libraryManager == null) return false;

            while (true)
            {
                // The library list and the main account's policy are the same for every profile
                // in a pass, so they are read once. A new pass ("again" below) reads them anew.
                var pass = new ProfilePolicySync.Pass(_userManager, libraryManager);
                foreach (var profile in ProfilesOf(master))
                {
                    // One profile failing must not leave the rest on the old limits.
                    try
                    {
                        await ProfilePolicySync.ApplyInPassAsync(_userManager, libraryManager, _logger, profile, pass).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "ProfilesPlugin: could not pass a change to account {UserId} on to profile {ProfileId}. "
                            + "It is updated again the next time it is opened.",
                            master,
                            profile);
                    }
                }

                lock (PassLock)
                {
                    if (!_passes.TryGetValue(master, out var state) || state != Pass.Again)
                    {
                        _passes.Remove(master);
                        return true;
                    }

                    _passes[master] = Pass.Last;
                }
            }
        }

        // Under ConfigLock, and a few comparisons: this is asked on every save of every
        // account, and most are not a main account with profiles.
        private static bool HasProfiles(Guid userId)
        {
            lock (ProfilesBaseController.ConfigLock)
            {
                var mappings = Plugin.Instance?.Configuration?.Mappings;
                return mappings != null && mappings.Any(m => m.MasterUserId == userId && m.ProfileUserId != userId);
            }
        }

        private static List<Guid> ProfilesOf(Guid master)
        {
            lock (ProfilesBaseController.ConfigLock)
            {
                var mappings = Plugin.Instance?.Configuration?.Mappings;
                return mappings == null
                    ? new List<Guid>()
                    : mappings.Where(m => m.MasterUserId == master && m.ProfileUserId != master)
                        .Select(m => m.ProfileUserId).ToList();
            }
        }
    }
}
