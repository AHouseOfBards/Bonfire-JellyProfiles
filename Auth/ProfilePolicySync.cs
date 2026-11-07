using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Writes a sub-profile's account policy from its master's, as it is now.
    ///
    /// <para><b>Why it is its own class.</b> This was a block inside the web switch, and the
    /// web switch was the only place it ran. A profile opened anywhere else — the sign-in
    /// screen of Android TV, Roku, Swiftfin, anything using the PIN provider — kept whatever
    /// policy it had the last time somebody happened to pick it in a browser. Taking a library
    /// away from a master, or lowering its rating, did not reach a profile that only ever lives
    /// on the living-room television. "A profile never gets more than its main account" was
    /// true in one client. It now runs from the switch and from every sign-in
    /// (<see cref="BonfireSessionListener"/>).</para>
    ///
    /// <para><b>Why not from the PIN provider</b>, which would apply it before the session
    /// exists: Jellyfin holds its per-user lock across the whole of
    /// <c>UserManager.AuthenticateUser</c>, provider call included, and
    /// <c>UpdatePolicyAsync</c> takes the same lock. Called from there it waits on itself and
    /// every PIN sign-in hangs.</para>
    ///
    /// <para>Only ever for a genuine sub-profile; see
    /// <see cref="ProfilesBaseController.ShouldInheritMasterPolicy"/> for what this did to a
    /// main account when it ran there (issue #27).</para>
    /// </summary>
    internal static class ProfilePolicySync
    {
        /// <summary>
        /// One at a time, server-wide. Two runs for one profile overlap routinely — the web
        /// switch applies it and then its own new session fires the sign-in listener — and the
        /// limits bookkeeping reads the account, compares it with what was last written, and
        /// writes. Interleaved, the second run reads the account from before the first one's
        /// write and the record from after it, and takes Bonfire's own old value for an
        /// administrator's. A run is a few database calls, so one lock for all costs nothing.
        /// </summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Applies the master's policy to <paramref name="profileUserId"/>. False when it is not a sub-profile.</summary>
        public static async Task<bool> ApplyAsync(
            IUserManager userManager, ILibraryManager libraryManager, ILogger logger, Guid profileUserId)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ApplyCoreAsync(userManager, libraryManager, logger, profileUserId).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<bool> ApplyCoreAsync(
            IUserManager userManager, ILibraryManager libraryManager, ILogger logger, Guid profileUserId)
        {
            ProfileMapping? mapping;
            lock (ProfilesBaseController.ConfigLock)
            {
                mapping = Plugin.Instance?.Configuration?.Mappings?
                    .FirstOrDefault(m => m.ProfileUserId == profileUserId)?.CopyForReading();
            }

            // A master's own row, or no row: a real account that owns its policy.
            if (mapping == null || mapping.ProfileUserId == mapping.MasterUserId) return false;

            var targetUser = userManager.GetUserById(profileUserId);
            var masterUser = userManager.GetUserById(mapping.MasterUserId);
            if (targetUser == null || masterUser == null) return false;

            // Inherit/synchronize streaming policies and configurations from master user
            var masterPolicy = userManager.GetUserDto(masterUser, string.Empty).Policy;
            var targetPolicy = userManager.GetUserDto(targetUser, string.Empty).Policy;
            var before = Fingerprint(targetPolicy);

            var childMaxParentalRating = targetPolicy.MaxParentalRating;
            var childMaxParentalSubRating = targetPolicy.MaxParentalSubRating;
            var childBlockedFolders = targetPolicy.BlockedMediaFolders;
            var childEnableAllFolders = targetPolicy.EnableAllFolders;
            var childEnabledFolders = targetPolicy.EnabledFolders;
            var accountSchedules = (targetPolicy.AccessSchedules ?? Array.Empty<Jellyfin.Database.Implementations.Entities.AccessSchedule>())
                .Select(ScheduleReconciler.Of).ToList();
            var accountLimits = LimitsOf(targetPolicy);

            ProfilesBaseController.CopyUserPolicy(masterPolicy, targetPolicy, targetUser.Id);

            // The five "stricter of the two" limits, worked out from what is the profile's own
            // rather than from whatever is on the account. CopyUserPolicy compares with the
            // account, which is mostly Bonfire's previous write — see InheritedLimits.
            var limits = InheritedLimits.Resolve(
                LimitsOf(masterPolicy), accountLimits, mapping.OwnLimits, mapping.LastAppliedLimits);
            targetPolicy.RemoteClientBitrateLimit = limits.Applied.RemoteClientBitrateLimit;
            targetPolicy.MaxActiveSessions = limits.Applied.MaxActiveSessions;
            targetPolicy.ForceRemoteSourceTranscoding = limits.Applied.ForceRemoteSourceTranscoding;
            targetPolicy.SyncPlayAccess = limits.Applied.SyncPlayAccess;
            targetPolicy.BlockUnratedItems = limits.Applied.BlockUnratedItems.ToArray();

            // Restore child-specific overrides. The profile keeps its own rating, but
            // compared with the master's as it is NOW: restoring it unchecked meant
            // tightening a master's rating never reached profiles it already had.
            targetPolicy.IsAdministrator = false;
            targetPolicy.IsHidden = true;
            // IsDisabled is left as it is. CopyUserPolicy does not touch it, the web switch
            // refuses a disabled account before getting here, and Jellyfin refuses one at
            // sign-in. This used to write IsDisabled = false, which turned a profile switched
            // off in Dashboard -> Users back on the next time anyone picked it.
            (targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating) = ProfilesBaseController.ClampRating(
                childMaxParentalRating, childMaxParentalSubRating,
                masterPolicy.MaxParentalRating, masterPolicy.MaxParentalSubRating);

            // Re-apply the profile's tag filters. CopyUserPolicy above just overwrote them with
            // the master's, so without this every profile switch would silently drop them.
            // Resolving from the mapping (rather than the current policy) also heals tags reset
            // by a Jellyfin restart and picks up changes to the master's own tag policy.
            var (reapplyBlockedTags, reapplyAllowedTags) =
                ProfilesBaseController.ResolveTagPolicy(masterPolicy, mapping.BlockedTags, mapping.AllowedTags);
            targetPolicy.BlockedTags = reapplyBlockedTags;
            targetPolicy.AllowedTags = reapplyAllowedTags;

            // CopyUserPolicy just replaced the profile's access schedule with the main account's,
            // which lost a narrower one an administrator set in Dashboard -> Users (R-1345).
            // Same fix as the libraries' (R-1042): what Bonfire last wrote is on the mapping, an
            // account that differs from it was changed by somebody and is adopted, never wider
            // than the main account's. See ScheduleReconciler.
            var schedules = ScheduleReconciler.Resolve(
                (masterPolicy.AccessSchedules ?? Array.Empty<Jellyfin.Database.Implementations.Entities.AccessSchedule>())
                    .Select(ScheduleReconciler.Of),
                accountSchedules, mapping.LastAppliedSchedules, mapping.OwnSchedules);
            targetPolicy.AccessSchedules = schedules.Applied
                .Select(w => new Jellyfin.Database.Implementations.Entities.AccessSchedule(w.Day, w.Start, w.End, targetUser.Id))
                .ToArray();

            var masterAccessible = ProfilesBaseController.MasterAccessibleFolders(libraryManager, masterPolicy);

            // Determine the authoritative enabled-folder list:
            //  - "Every library": whatever the master can reach now, so one added since is included.
            //  - If the plugin mapping has a stored list (EnabledFolders != null), use it as ground truth.
            //    This survives Jellyfin restarts that reset user policies.
            //  - If EnabledFolders is null (profile predates this field), fall back to the Jellyfin policy
            //    and auto-migrate by saving the list into the mapping now.
            List<Guid> authorityFolders;
            List<Guid>? migratedFolders = null;
            List<Guid>? grantedFolders = null;
            List<Guid>? removedFolders = null;
            if (mapping.AllLibraries)
            {
                authorityFolders = masterAccessible.ToList();
            }
            else if (mapping.EnabledFolders != null)
            {
                // Issue #30: a library granted in Dashboard → Users is not a stale value.
                // Applying the stored list unconditionally silently reverted it.
                //
                // R-1042: nor is one removed there. Without a record of what Bonfire last
                // wrote, a removal looks exactly like a reset, so only grants were adopted
                // and a library an administrator took away came back at the next sign-in.
                // With the record (LastAppliedFolders) both directions are adopted; a
                // configuration from before it gets the old rule once and is then recorded.
                // See LibraryAccessReconciler.
                //
                // Empty counts as no record. Jellyfin's XmlSerializer loads an absent list
                // element as an empty list, not null, so that is what an old configuration
                // arrives as. Nothing is lost: against an empty record every folder on the
                // account reads as granted and none as removed, which is the old rule exactly.
                var reconciled = mapping.LastAppliedFolders == null || mapping.LastAppliedFolders.Count == 0
                    ? LibraryAccessReconciler.Reconcile(
                        mapping.EnabledFolders, childEnabledFolders, childEnableAllFolders)
                    : LibraryAccessReconciler.ReconcileWithLastApplied(
                        mapping.EnabledFolders, childEnabledFolders, childEnableAllFolders, mapping.LastAppliedFolders);

                authorityFolders = reconciled.Authority;

                if (reconciled.StoredListChanged)
                {
                    grantedFolders = reconciled.Granted;
                    removedFolders = reconciled.Removed;

                    // Said out loud because the absence of any trail is most of what made
                    // this expensive to find: nothing errored, and Jellyfin does not log
                    // policy writes at all.
                    logger.LogInformation(
                        "ProfilesPlugin: profile {ProfileId} was granted {Granted} and lost {Removed} "
                        + "library/libraries outside the plugin since it was last entered; adopted, "
                        + "so entering the profile no longer reverts them.",
                        targetUser.Id,
                        reconciled.Granted.Count,
                        reconciled.Removed.Count);
                }
                else if (reconciled.Missing.Count > 0)
                {
                    // The other direction, which is the one that reads as "my save did
                    // not take". Only reached without a record of what Bonfire last wrote
                    // (an older configuration, once): then a removal and a reset look the
                    // same, so it is not adopted, but it must not be silent. With a record
                    // the removal is adopted above.
                    logger.LogInformation(
                        "ProfilesPlugin: profile {ProfileId} is missing {Count} library/libraries "
                        + "that the profile itself is set to have, so the profile's own list was "
                        + "re-applied. To take a library away from a profile, edit the profile "
                        + "rather than the account in Dashboard -> Users.",
                        targetUser.Id,
                        reconciled.Missing.Count);
                }
            }
            else
            {
                // Legacy profile: read from Jellyfin policy and migrate.
                //
                // An all-libraries profile takes what its master can reach, worked out the
                // same way as everywhere else. This used to read the master's
                // EnabledFolders, which is EMPTY for an all-libraries master — Jellyfin
                // stores "all" as the flag, not as a list — so the commonest legacy profile
                // was migrated to no libraries at all, and the empty list was saved.
                var legacyEnabled = childEnableAllFolders
                    ? masterAccessible.ToList()
                    : (childEnabledFolders ?? Array.Empty<Guid>()).ToList();

                // If still empty, derive from BlockedMediaFolders
                if (legacyEnabled.Count == 0 && childBlockedFolders != null && childBlockedFolders.Length > 0)
                {
                    legacyEnabled = AllFolderIds(libraryManager).Where(fid => !childBlockedFolders.Contains(fid)).ToList();
                }

                authorityFolders = legacyEnabled;
                migratedFolders = legacyEnabled;
            }

            // Intersect authorityFolders with the master's current accessible folders
            authorityFolders = authorityFolders.Where(id => masterAccessible.Contains(id)).ToList();

            // Re-apply the stored library policy (heals resets caused by Jellyfin restarts)
            targetPolicy.EnableAllFolders = false;
            targetPolicy.EnabledFolders = authorityFolders.ToArray();
            var reapplyBlocked = AllFolderIds(libraryManager).Where(id => !authorityFolders.Contains(id));
            var masterBlocked = masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>();
            targetPolicy.BlockedMediaFolders = reapplyBlocked.Union(masterBlocked).ToArray();

            // Every sign-in runs this, twice (see BonfireSessionListener), so an account that is
            // already right is left alone rather than rewritten.
            if (Fingerprint(targetPolicy) != before)
            {
                await userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);
            }

            // Language and subtitle preferences are seeded from the master when the profile is
            // created and are the profile's own after that. Copying them again here would undo
            // whatever was set on the profile (e.g. a child's Polish audio vs. an English master).

            // The mapping is updated on the live row, re-resolved inside the lock, because
            // Jellyfin replaces the whole configuration object when an administrator saves
            // the plugin's settings and the copy read above may belong to the one it replaced.
            lock (ProfilesBaseController.ConfigLock)
            {
                var live = Plugin.Instance?.Configuration?.Mappings?
                    .FirstOrDefault(m => m.ProfileUserId == profileUserId);
                if (live != null)
                {
                    var changed = false;

                    if (migratedFolders != null && live.EnabledFolders == null)
                    {
                        live.EnabledFolders = migratedFolders;
                        changed = true;
                    }

                    if (grantedFolders != null && live.EnabledFolders != null)
                    {
                        var merged = live.EnabledFolders.Concat(grantedFolders).Distinct().ToList();
                        if (merged.Count != live.EnabledFolders.Count)
                        {
                            live.EnabledFolders = merged;
                            changed = true;
                        }
                    }

                    if (removedFolders != null && removedFolders.Count > 0 && live.EnabledFolders != null)
                    {
                        var kept = live.EnabledFolders.Where(id => !removedFolders.Contains(id)).ToList();
                        if (kept.Count != live.EnabledFolders.Count)
                        {
                            live.EnabledFolders = kept;
                            changed = true;
                        }
                    }

                    // What was just written, so the next sign-in can tell an administrator's
                    // change from this write (R-1042). Recorded on every path, the "every
                    // library" one included, so a profile later given a fixed list starts
                    // with a record.
                    if (live.LastAppliedFolders == null
                        || !live.LastAppliedFolders.ToHashSet().SetEquals(authorityFolders))
                    {
                        live.LastAppliedFolders = authorityFolders.ToList();
                        changed = true;
                    }

                    // What was just written, so the next sign-in can tell an administrator's
                    // change from this write (R-1345). Own is what the profile keeps of its
                    // own; null while it has none, so a configuration stays as it was.
                    if (live.LastAppliedSchedules == null
                        || !live.LastAppliedSchedules.ToHashSet().SetEquals(schedules.Record))
                    {
                        live.LastAppliedSchedules = schedules.Record.ToList();
                        changed = true;
                    }

                    var ownNow = schedules.Own.Count > 0 ? schedules.Own.ToList() : null;
                    if ((live.OwnSchedules?.Count ?? 0) != (ownNow?.Count ?? 0)
                        || (ownNow != null && !ownNow.ToHashSet().SetEquals(live.OwnSchedules!)))
                    {
                        live.OwnSchedules = ownNow;
                        changed = true;
                    }

                    if (!InheritedLimits.Same(live.OwnLimits, limits.Own)
                        || !InheritedLimits.Same(live.LastAppliedLimits, limits.Applied))
                    {
                        live.OwnLimits = limits.Own;
                        live.LastAppliedLimits = limits.Applied;
                        changed = true;
                    }

                    if (changed) Plugin.Instance?.SaveConfiguration();
                }
            }

            return true;
        }

        internal static ProfileLimits LimitsOf(UserPolicy policy) => new ProfileLimits
        {
            RemoteClientBitrateLimit = policy.RemoteClientBitrateLimit,
            MaxActiveSessions = policy.MaxActiveSessions,
            ForceRemoteSourceTranscoding = policy.ForceRemoteSourceTranscoding,
            SyncPlayAccess = policy.SyncPlayAccess,
            BlockUnratedItems = (policy.BlockUnratedItems ?? Array.Empty<Jellyfin.Data.Enums.UnratedItem>()).ToList(),
        };

        private static IEnumerable<Guid> AllFolderIds(ILibraryManager libraryManager) =>
            libraryManager.GetVirtualFolders()
                .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty);

        /// <summary>
        /// The policy as a string, for "did anything change". Access schedules are compared by
        /// their day and hours alone: CopyUserPolicy builds new rows for them every time, and a
        /// new row has no database id, so comparing whole rows would always differ.
        /// </summary>
        private static string Fingerprint(UserPolicy policy)
        {
            var node = JsonSerializer.SerializeToNode(policy) as JsonObject;
            node?.Remove("AccessSchedules");
            var schedules = (policy.AccessSchedules ?? Array.Empty<Jellyfin.Database.Implementations.Entities.AccessSchedule>())
                .Select(s => s.DayOfWeek + ":" + s.StartHour + "-" + s.EndHour)
                .OrderBy(s => s, StringComparer.Ordinal);
            return (node?.ToJsonString() ?? string.Empty) + "|" + string.Join(",", schedules);
        }
    }
}
