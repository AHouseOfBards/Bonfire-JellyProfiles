using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Campground.Shared.Registry;
using Campground.Shared.Storage;
using Jellyfin.Data.Events.Users;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// What Bonfire keeps about a Jellyfin user, and taking it out once Jellyfin no longer has
    /// the user. Two callers with one set of rules: the administrator's Leftover records scan
    /// (<c>admin/cleanup-orphans</c>), and Campground's user clean-up (Campground.Shared 1.0.1),
    /// which runs when a user is deleted in Dashboard → Users and again at every start for a
    /// clean-up that failed or was missed while Bonfire wasn't running.
    /// <para>
    /// Only Bonfire's own records go, never a Jellyfin account. A profile whose main account
    /// was deleted keeps its Jellyfin user, hidden and out of every household, exactly as the
    /// scan has always left it. Devices are not touched: a device outliving the profile that
    /// used it is normal, and dropping one another profile still names would empty that
    /// whitelist, which means "any device" (see RemoveStaleDevices). Nor are
    /// CrossHouseholdEntries: an entry whose household is gone is what signs the foreign
    /// device out at the next re-evaluation, and forgetting it would leave the token working.
    /// </para>
    /// </summary>
    internal static class UserRecords
    {
        /// <summary>What one removal took out, for the events raised and the scan's counts.</summary>
        internal sealed class Removed
        {
            internal List<(Guid Main, Guid Profile, string Name)> Profiles { get; } = new();
            internal int Groups { get; set; }
            internal int Members { get; set; }
            internal int Other { get; set; }
            internal bool Any => Profiles.Count > 0 || Groups > 0 || Members > 0 || Other > 0;
        }

        /// <summary>Every user id Bonfire holds a record for. The caller holds ConfigLock.</summary>
        internal static HashSet<Guid> StoredUserIds(PluginConfiguration config)
        {
            var seen = new HashSet<Guid>();
            foreach (var m in config.Mappings) { seen.Add(m.ProfileUserId); seen.Add(m.MasterUserId); }
            foreach (var g in config.BonfireGroups)
            {
                seen.Add(g.OwnerUserId);
                foreach (var id in g.MemberUserIds ?? new List<Guid>()) seen.Add(id);
            }
            if (config.LinkedAccounts != null) foreach (var l in config.LinkedAccounts) { seen.Add(l.UserId); seen.Add(l.HouseholdId); }
            if (config.Onboarding?.Seen != null) foreach (var s in config.Onboarding.Seen) seen.Add(s.UserId);
            if (config.AccountEmails != null) foreach (var a in config.AccountEmails) seen.Add(a.UserId);
            if (config.UserProfileLimitOverrides != null) foreach (var o in config.UserProfileLimitOverrides) seen.Add(o.UserId);
            if (config.TrustedAddressEntries != null) foreach (var t in config.TrustedAddressEntries) foreach (var id in t.UserIds ?? new List<Guid>()) seen.Add(id);
            return seen;
        }

        /// <summary>
        /// Takes out every record naming a user <paramref name="exists"/> says is gone. The caller
        /// holds ConfigLock and saves; then calls <see cref="After"/> with none held.
        /// </summary>
        internal static Removed Remove(PluginConfiguration config, Func<Guid, bool> exists)
        {
            var removed = new Removed();

            // A profile row goes when either its own account or its main account is gone.
            foreach (var m in config.Mappings.Where(m => !exists(m.ProfileUserId) || !exists(m.MasterUserId)))
            {
                removed.Profiles.Add((m.MasterUserId, m.ProfileUserId, m.ProfileName));
            }
            config.Mappings.RemoveAll(m => !exists(m.ProfileUserId) || !exists(m.MasterUserId));

            // Bonfire groups outlive their owner the same way. A group whose owner is alive can
            // still list members who are not; a group going takes its members with it, so they
            // are not counted twice.
            removed.Groups = config.BonfireGroups.RemoveAll(g => !exists(g.OwnerUserId));
            foreach (var g in config.BonfireGroups)
            {
                removed.Members += g.MemberUserIds?.RemoveAll(id => !exists(id)) ?? 0;
            }

            // Housekeeping with nothing for an administrator to review, so the scan doesn't
            // count it: a linked account where either side is gone (a tile for a missing
            // account is never drawn), first-time setup's "has seen it" rows (R-1075), a
            // confirmed address and a profile-limit override, which are personal data in the
            // export (R-1311), and an account an address skips PINs for.
            removed.Other += config.LinkedAccounts?.RemoveAll(l => !exists(l.UserId) || !exists(l.HouseholdId)) ?? 0;
            removed.Other += config.Onboarding?.Seen?.RemoveAll(s => !exists(s.UserId)) ?? 0;
            removed.Other += config.AccountEmails?.RemoveAll(a => !exists(a.UserId)) ?? 0;
            removed.Other += config.UserProfileLimitOverrides?.RemoveAll(o => !exists(o.UserId)) ?? 0;
            if (config.TrustedAddressEntries != null)
            {
                foreach (var t in config.TrustedAddressEntries)
                {
                    removed.Other += t.UserIds?.RemoveAll(id => !exists(id)) ?? 0;
                }
            }
            return removed;
        }

        /// <summary>What follows a removal, outside ConfigLock: the tracker's own lock is never taken inside it.</summary>
        internal static void After(Removed removed)
        {
            foreach (var (_, profile, _) in removed.Profiles)
            {
                Auth.ViewingTracker.Forget(profile);   // R-1023
                // Its image is often a photo of the person. Nothing can show it once the row is
                // gone, and the scan used to leave it on disk for good.
                ProfilesBaseController.DeleteProfileImage(profile);
            }
            if (removed.Profiles.Count > 0) Auth.ViewingTracker.RulesChanged();
            foreach (var (main, profile, name) in removed.Profiles) BonfireCampground.RaiseDeleted(main, profile, name);

            // A main account whose profiles all went is no longer one the PIN provider should hold.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();
        }

        /// <summary>
        /// Campground's cleaner for one deleted user: that user's records, and nobody else's.
        /// Throws if the configuration can't be saved, so Campground keeps it pending and the
        /// next start tries again.
        /// </summary>
        internal static Task CleanAsync(Guid userId, CancellationToken ct)
        {
            if (userId == Guid.Empty) return Task.CompletedTask;
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Bonfire's configuration isn't loaded yet.");
            Removed removed;
            lock (ProfilesBaseController.ConfigLock)
            {
                removed = Remove(config, id => id != userId);
                if (removed.Any) Plugin.Instance!.SaveConfiguration();
            }
            if (removed.Any) After(removed);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Registers Bonfire's cleaner with Campground and runs the start-up sweep (shared/README,
    /// step 4). The sweep cleans every user Bonfire holds a record for that Jellyfin no longer
    /// has, and retries a clean-up that failed before.
    /// </summary>
    public sealed class UserRecordsStartup : IHostedService
    {
        private readonly IUserManager _userManager;
        private readonly ILogger<UserRecordsStartup> _logger;

        /// <summary>Initializes a new instance of the <see cref="UserRecordsStartup"/> class.</summary>
        public UserRecordsStartup(IUserManager userManager, ILogger<UserRecordsStartup> logger)
        {
            _userManager = userManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            UserDataCleanup.Register(PluginIds.Bonfire, UserRecords.CleanAsync);
            var config = Plugin.Instance?.Configuration;
            if (config == null) return Task.CompletedTask;
            HashSet<Guid> stored;
            lock (ProfilesBaseController.ConfigLock)
            {
                stored = UserRecords.StoredUserIds(config);
            }
            stored.Remove(Guid.Empty);
            // Not awaited: a sweep must never hold up the server's start. Its failures stay
            // pending in Campground's file for the next start.
            _ = SweepAsync(stored);
            return Task.CompletedTask;
        }

        private async Task SweepAsync(HashSet<Guid> stored)
        {
            try
            {
                var left = await UserDataCleanup.SweepAsync(PluginIds.Bonfire, stored, id => _userManager.GetUserById(id) != null).ConfigureAwait(false);
                if (left.Count > 0)
                {
                    _logger.LogWarning("ProfilesPlugin: could not clean up {Count} deleted users; trying again at the next start.", left.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: the start-up clean-up of deleted users failed.");
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A user deleted in Dashboard → Users: every Campground plugin's cleaner runs for them, once
    /// (Campground makes sure of the once).
    /// </summary>
    public sealed class UserRecordsDeleted : IEventConsumer<UserDeletedEventArgs>
    {
        /// <inheritdoc />
        public Task OnEvent(UserDeletedEventArgs eventArgs) => UserDataCleanup.RunAsync(eventArgs.Argument.Id);
    }
}
