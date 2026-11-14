#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Campground.Shared.Contracts;
using Campground.Shared.Registry;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// What Bonfire offers the other Campground plugins on this server:
    /// <c>campground.household.v1</c> (minor 1) and <c>campground.device.v1</c>, and the events
    /// that go with the household one. Design: Campground-Coordination design/bonfire/campground.md.
    /// <para>
    /// Every answer is built from a snapshot of the configuration taken under
    /// <see cref="ProfilesBaseController.ConfigLock"/> and released before anything else, so a caller
    /// never holds the lock while Jellyfin is asked for a name and a slow caller cannot hold up a save.</para>
    /// <para>
    /// <b>While Bonfire is off or emergency-disabled</b> (<see cref="Plugin.IsOff"/>) it answers
    /// nothing and raises nothing. <c>getHousehold</c> gives the household of one the contract
    /// already defines for an unknown user, <c>getDevice</c> the library's own fallback key, and
    /// <c>getSignInPolicy</c> a caller that fails open. A plugin that pooled a household's data on
    /// Bonfire's word must not keep pooling it while the owner is switching Bonfire off. The shared
    /// file or environment brake is not consulted: it is for what a browser sees ("Server-side
    /// features keep running"), and these are server-side answers.</para>
    /// </summary>
    internal static class BonfireCampground
    {
        /// <summary>
        /// The household capability's minor: 1 added <c>SharingGroup</c>, and <c>getSignInPolicy</c>
        /// came with it.
        /// </summary>
        internal const int HouseholdMinor = 1;

        internal const int DeviceMinor = 0;

        /// <summary>
        /// Offers both capabilities. <paramref name="nameOf"/> is the Jellyfin username of an
        /// account, or null; it must not block (Jellyfin's user cache is in memory).
        /// </summary>
        internal static void Provide(Func<Guid, string?> nameOf)
        {
            HouseholdClient.Provide(
                PluginIds.Bonfire,
                HouseholdMinor,
                (userId, ct) => Task.FromResult(Plugin.IsOff
                    ? null
                    : BuildHousehold(ProfilesBaseController.ReadConfigSnapshot(), userId, nameOf)),
                (userId, ct) => Task.FromResult(Plugin.IsOff
                    ? null
                    : BuildSignInPolicy(ProfilesBaseController.ReadConfigSnapshot())));

            DeviceClient.Provide(
                PluginIds.Bonfire,
                DeviceMinor,
                (q, ct) => Task.FromResult(Plugin.IsOff
                    ? null
                    : BuildDevice(ProfilesBaseController.ReadConfigSnapshot(), q)));
        }

        // ── household.v1 ─────────────────────────────────────────────────────────

        /// <summary>
        /// The household of <paramref name="userId"/>, or null for a user Bonfire holds no record of
        /// (a household of one, which the library answers itself).
        /// <para>
        /// <b>Most main accounts have no mapping row of their own</b>: one is written lazily, the first
        /// time switcher preferences are saved. A user with no row of its own and rows naming it as
        /// <c>MasterUserId</c> is the head of a household, never a household of one.</para>
        /// </summary>
        internal static Household? BuildHousehold(PluginConfiguration? config, Guid userId, Func<Guid, string?> nameOf)
        {
            if (config == null || userId == Guid.Empty) return null;

            var rows = config.Mappings ?? new List<ProfileMapping>();
            var own = rows.FirstOrDefault(m => m.ProfileUserId == userId);
            var isProfile = own != null && own.MasterUserId != Guid.Empty && own.MasterUserId != userId;
            var main = isProfile ? own!.MasterUserId : userId;

            var profiles = rows
                .Where(m => m.MasterUserId == main && m.ProfileUserId != main && m.ProfileUserId != Guid.Empty)
                .GroupBy(m => m.ProfileUserId)
                .Select(g => g.First())
                .ToList();

            var group = (config.BonfireGroups ?? new List<BonfireGroup>())
                .FirstOrDefault(g => g.OwnerUserId == main || (g.MemberUserIds?.Contains(main) ?? false));

            // No row of its own, nobody pointing at it, in no group: Bonfire has no record of it.
            if (own == null && profiles.Count == 0 && group == null) return null;

            var mainRow = main == userId ? own : rows.FirstOrDefault(m => m.ProfileUserId == main);
            var mainName = nameOf(main) ?? mainRow?.ProfileName ?? string.Empty;

            var members = new List<HouseholdMember> { new(main, mainName, false) };
            members.AddRange(profiles.Select(p => new HouseholdMember(p.ProfileUserId, p.ProfileName ?? string.Empty, true)));

            var displayName = isProfile
                ? (string.IsNullOrWhiteSpace(own!.ProfileName) ? nameOf(userId) ?? string.Empty : own.ProfileName)
                : mainName;

            SharingGroup? sharing = null;
            if (group != null)
            {
                // The owner is a member in the contract's words; the code is a secret and is never read here.
                var memberIds = new List<Guid>();
                if (group.OwnerUserId != Guid.Empty) memberIds.Add(group.OwnerUserId);
                foreach (var id in group.MemberUserIds ?? new List<Guid>())
                {
                    if (id != Guid.Empty && !memberIds.Contains(id)) memberIds.Add(id);
                }

                sharing = new SharingGroup(group.GroupId ?? string.Empty, group.OwnerUserId, memberIds);
            }

            return new Household(userId, main, isProfile, displayName, members, true, sharing);
        }

        /// <summary>
        /// Whether a profile with no PIN, or limited to some devices, opens from outside the home
        /// network. False when PIN login on other apps is off, because then nothing opens at all.
        /// </summary>
        internal static SignInPolicy? BuildSignInPolicy(PluginConfiguration? config)
            => config == null
                ? null
                : new SignInPolicy(config.EnableClientPinLogin && config.ClientPinLoginFromAnywhere);

        // ── device.v1 ────────────────────────────────────────────────────────────

        /// <summary>
        /// The device Bonfire has on record for <c>DeviceId</c>, or null (the library then builds its
        /// own key from the app, the model and the user).
        /// <para>
        /// <b>A grouping key, nothing more.</b> <c>StableKey</c> is the recorded id after the merges an
        /// administrator made, so two ids of one browser answer alike. Device restrictions never read
        /// it: they match the id the client sent
        /// (<see cref="ProfilesBaseController.EvaluateDeviceRestriction"/>), and tests/cs/household fails
        /// if that code starts to.</para>
        /// <para>
        /// A name a household chose is given only to that household. If the caller says whose request it
        /// is and the device was never that household's, the client's own name comes back.</para>
        /// </summary>
        internal static DeviceIdentity? BuildDevice(PluginConfiguration? config, DeviceQuery query)
        {
            var row = Auth.DeviceRegistry.Row(config?.KnownDevices, query.DeviceId);
            var key = row?.DeviceId?.Trim();
            if (row == null || string.IsNullOrEmpty(key)) return null;

            var mayShowName = query.UserId is not { } who || who == Guid.Empty
                || row.OwnedBy(Auth.DeviceRegistry.HouseholdOf(config, who));
            var chosen = row.NameIsCustom && mayShowName ? row.DeviceName : null;
            var reported = string.IsNullOrWhiteSpace(query.DeviceName) ? row.DeviceName : query.DeviceName;
            var name = string.IsNullOrWhiteSpace(chosen) ? reported : chosen;
            return new DeviceIdentity(key, string.IsNullOrWhiteSpace(name) ? null : name!.Trim(), true);
        }

        // ── Events ───────────────────────────────────────────────────────────────
        // Raised after the change is saved, never inside ConfigLock: the library runs
        // each subscriber on the publishing thread, so a slow one would hold the lock.

        internal static void RaiseCreated(Guid mainUserId, Guid profileUserId, string? name)
            => Publish(EventNames.ProfileCreated, new ProfileEvent(mainUserId, profileUserId, name, null, null));

        internal static void RaiseRenamed(Guid mainUserId, Guid profileUserId, string? name)
            => Publish(EventNames.ProfileRenamed, new ProfileEvent(mainUserId, profileUserId, name, null, null));

        internal static void RaiseDeleted(Guid mainUserId, Guid profileUserId, string? name)
            => Publish(EventNames.ProfileDeleted, new ProfileEvent(mainUserId, profileUserId, name, null, null));

        // One switch is heard twice: the switch route knows who it came from, and the session it
        // starts reaches BonfireSessionListener (the only place a native app is seen). The route
        // says so before it signs in, because the session starts inside that call, and the
        // listener then stays quiet for the same profile on the same device. The note is kept for
        // SwitchWindow, not removed, because Jellyfin reports a handover on an open session on the
        // next request, which can be seconds later.
        private static readonly TimeSpan SwitchWindow = TimeSpan.FromSeconds(30);
        private static readonly ConcurrentDictionary<string, (Guid Profile, DateTime At)> ExpectedSwitches
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Called by the switch route just before it signs in.</summary>
        internal static void ExpectSwitch(Guid profileUserId, string? deviceId)
        {
            if (ExpectedSwitches.Count > 1000)
            {
                var stale = DateTime.UtcNow - SwitchWindow;
                foreach (var pair in ExpectedSwitches.Where(p => p.Value.At < stale).ToList())
                {
                    ExpectedSwitches.TryRemove(pair.Key, out _);
                }
            }

            ExpectedSwitches[deviceId ?? string.Empty] = (profileUserId, DateTime.UtcNow);
        }

        /// <summary>The switch route, once the session exists.</summary>
        internal static void RaiseSwitched(Guid mainUserId, Guid profileUserId, string? name, string? deviceId, Guid? previousUserId)
            => Publish(EventNames.ProfileSwitched, new ProfileEvent(mainUserId, profileUserId, name, deviceId, previousUserId));

        /// <summary>The session listener: a sign-in on a household's account, from any client.</summary>
        internal static void RaiseSwitchedFromSession(Guid mainUserId, Guid profileUserId, string? name, string? deviceId, Guid? previousUserId)
        {
            if (ExpectedSwitches.TryGetValue(deviceId ?? string.Empty, out var expected)
                && expected.Profile == profileUserId
                && DateTime.UtcNow - expected.At < SwitchWindow)
            {
                return;
            }

            RaiseSwitched(mainUserId, profileUserId, name, deviceId, previousUserId);
        }

        private static void Publish(string eventName, ProfileEvent payload)
        {
            if (Plugin.IsOff) return;
            try
            {
                CampgroundEvents.Publish(eventName, payload);
            }
            catch (Exception)
            {
                // Telling the other plugins is never worth failing the change that was already saved.
            }
        }
    }

    /// <summary>
    /// Offers Bonfire's capabilities once the host exists. A hosted service rather than the
    /// service registration, because joining the lifetime (which stamps and later withdraws what
    /// is offered) needs the running host.
    /// </summary>
    internal sealed class CampgroundProvidersTask : Microsoft.Extensions.Hosting.IHostedService
    {
        private readonly Microsoft.Extensions.Hosting.IHostApplicationLifetime _lifetime;
        private readonly MediaBrowser.Controller.Library.IUserManager _users;

        public CampgroundProvidersTask(
            Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime,
            MediaBrowser.Controller.Library.IUserManager users)
        {
            _lifetime = lifetime;
            _users = users;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                CampgroundLifetime.Register(PluginIds.Bonfire, _lifetime);
                BonfireCampground.Provide(id => id == Guid.Empty ? null : _users.GetUserById(id)?.Username);
            }
            catch (Exception)
            {
                // Other plugins losing Bonfire's answers must never stop Bonfire starting.
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
