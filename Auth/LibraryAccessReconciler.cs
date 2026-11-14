using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Decides which libraries a profile gets when its stored list and the account's list disagree.
    ///
    /// <para><b>Issue #30.</b> The switch path re-applied the profile's stored copy of its folders
    /// unconditionally, so a library granted in Dashboard → Users was undone the next time anyone
    /// entered the profile. The write persisted and <c>GET /Users/{id}</c> agreed; entering the
    /// profile put the old list back. Nothing errored and polling an idle server showed a stable
    /// policy, which ruled out scheduled tasks. The trigger is a person using the feature.</para>
    ///
    /// <para><b>Why the stored copy cannot simply lose.</b> A reset policy looks the same as an
    /// account that differs, and healing resets is why the stored copy exists.</para>
    ///
    /// <para><b>Direction separates them.</b> A reset can only lose grants, never invent one:</para>
    ///
    /// <list type="bullet">
    /// <item><description>A folder the <b>account has and the profile does not</b> was granted by
    /// somebody, so it is adopted.</description></item>
    /// <item><description>A folder the <b>profile has and the account does not</b> could be a
    /// revocation or a reset, so the profile's list wins.</description></item>
    /// </list>
    ///
    /// <para>The cost: removing a library in Dashboard → Users did not stick (R-1042).
    /// <see cref="Reconcile"/> still works this way, and is what an older configuration gets
    /// once.</para>
    ///
    /// <para><b>R-1042.</b> Bonfire now records the list it last wrote
    /// (<c>ProfileMapping.LastAppliedFolders</c>), as <see cref="InheritedLimits"/> does for
    /// limits. An account that matches the record is untouched; one that differs was changed by
    /// somebody, and the change is adopted in both directions. See
    /// <see cref="ReconcileWithLastApplied"/>.</para>
    ///
    /// <para>Pure and static so a harness can drive it directly.</para>
    /// </summary>
    public static class LibraryAccessReconciler
    {
        /// <summary>The result of comparing a profile's stored list with the account's.</summary>
        public sealed class Reconciliation
        {
            /// <summary>The list to apply to the account.</summary>
            public List<Guid> Authority { get; init; } = new();

            /// <summary>
            /// Folders the account had that the profile did not, now adopted. Non-empty means somebody
            /// granted a library outside the plugin.
            /// </summary>
            public List<Guid> Granted { get; init; } = new();

            /// <summary>
            /// Folders the profile has that the account lost. Not adopted, since a reset looks the same,
            /// but reported because it reads as a failed save.
            /// </summary>
            public List<Guid> Missing { get; init; } = new();

            /// <summary>
            /// Folders taken off the account since Bonfire last wrote it, now removed from the profile
            /// too. Only set by <see cref="ReconcileWithLastApplied"/>.
            /// </summary>
            public List<Guid> Removed { get; init; } = new();

            /// <summary>True when <see cref="Granted"/> or <see cref="Removed"/> changed the stored list.</summary>
            public bool StoredListChanged => Granted.Count > 0 || Removed.Count > 0;
        }

        /// <summary>
        /// Reconciles the stored list against the account's, using what Bonfire last wrote there
        /// (R-1042).
        /// <list type="bullet">
        /// <item><description><b>No record</b> (older configuration): same as <see cref="Reconcile"/>.</description></item>
        /// <item><description><b>Account on "all libraries"</b>: a reset leaves this shape, with no
        /// list to read. The stored list is re-applied.</description></item>
        /// <item><description><b>Account matches the record</b>: untouched. The stored list is
        /// re-applied, which also picks up libraries the main account gained.</description></item>
        /// <item><description><b>It differs</b>: somebody changed it in Dashboard → Users. Additions
        /// join the stored list and removals leave it. Only the difference from the record counts, so
        /// a stored library the account never carried is kept for when it gets it.</description></item>
        /// </list>
        /// A reset to an explicit empty list reads as "everything removed". That shows less, never
        /// more, and Jellyfin's default is "all libraries", handled above.
        /// </summary>
        public static Reconciliation ReconcileWithLastApplied(
            IEnumerable<Guid>? stored,
            IEnumerable<Guid>? accountCurrent,
            bool accountEnableAllFolders,
            IEnumerable<Guid>? lastApplied)
        {
            if (lastApplied == null) return Reconcile(stored, accountCurrent, accountEnableAllFolders);

            var storedList = Clean(stored);
            if (accountEnableAllFolders) return new Reconciliation { Authority = storedList };

            var accountList = Clean(accountCurrent);
            var lastList = Clean(lastApplied);

            var granted = accountList.Where(id => !lastList.Contains(id)).ToList();
            var removed = lastList.Where(id => !accountList.Contains(id)).ToList();
            if (granted.Count == 0 && removed.Count == 0) return new Reconciliation { Authority = storedList };

            return new Reconciliation
            {
                Authority = storedList.Where(id => !removed.Contains(id))
                    .Concat(granted.Where(id => !storedList.Contains(id)))
                    .ToList(),
                Granted = granted.Where(id => !storedList.Contains(id)).ToList(),
                Removed = removed.Where(id => storedList.Contains(id)).ToList(),
            };
        }

        private static List<Guid> Clean(IEnumerable<Guid>? ids) =>
            (ids ?? Enumerable.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();

        /// <summary>Reconciles a profile's stored list against the account's current one.</summary>
        /// <param name="stored">
        /// The profile's own list. Never null here: null means a profile that predates the field,
        /// which the caller migrates.
        /// </param>
        /// <param name="accountCurrent">The account's live <c>Policy.EnabledFolders</c>.</param>
        /// <param name="accountEnableAllFolders">
        /// The account's live <c>Policy.EnableAllFolders</c>. When set, nothing is adopted: an
        /// all-libraries account has an empty <c>EnabledFolders</c>, so there is no grant to read, and
        /// a reset leaves the same shape. The switch path forces it off regardless, because a
        /// sub-profile that sees everything is not a profile.
        /// </param>
        public static Reconciliation Reconcile(
            IEnumerable<Guid>? stored,
            IEnumerable<Guid>? accountCurrent,
            bool accountEnableAllFolders)
        {
            // Guid.Empty is what an unparseable folder id becomes elsewhere; granting it would
            // grant nothing under a real-looking name.
            var storedList = (stored ?? Enumerable.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            var accountList = (accountCurrent ?? Enumerable.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (accountEnableAllFolders)
            {
                return new Reconciliation { Authority = storedList };
            }

            var granted = accountList.Where(id => !storedList.Contains(id)).ToList();
            var missing = storedList.Where(id => !accountList.Contains(id)).ToList();

            return new Reconciliation
            {
                Authority = storedList.Concat(granted).ToList(),
                Granted = granted,
                Missing = missing,
            };
        }
    }
}
