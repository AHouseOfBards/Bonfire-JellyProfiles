using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Decides which libraries a profile should have when the profile's own stored list and
    /// the Jellyfin account's current list disagree.
    ///
    /// <para><b>Issue #30.</b> A profile keeps its own copy of its enabled folders, and the
    /// switch path re-applied that copy unconditionally. So granting a profile a new library
    /// the normal way — Dashboard → Users, tick the box — was silently undone the next time
    /// somebody entered the profile. The write was real and had persisted;
    /// <c>GET /Users/{id}</c> agreed, the view appeared, it survived a refresh. Entering the
    /// profile put it back.</para>
    ///
    /// <para>It read as "my save did not take" rather than "something overwrote this",
    /// because the value it reverted to looked like a deliberate setting — it had been one,
    /// once. Nothing errored, Jellyfin does not log policy writes at all, and polling the
    /// policy with nobody using the server showed it perfectly stable, which ruled out a
    /// scheduled task and made every background job look innocent. The mutation is
    /// event-driven and the event is a person using the feature.</para>
    ///
    /// <para><b>Why the stored copy cannot simply lose.</b> Preferring the account's list
    /// whenever the two differ is the obvious fix and it is wrong, because that is also what
    /// a reset policy looks like — and healing those is the entire reason the stored copy
    /// exists.</para>
    ///
    /// <para><b>What separates them is direction.</b> A reset can only lose grants; it cannot
    /// invent one. So:</para>
    ///
    /// <list type="bullet">
    /// <item><description>A folder the <b>account has and the profile does not</b> can only
    /// have been granted by somebody, so it is adopted into the profile.</description></item>
    /// <item><description>A folder the <b>profile has and the account does not</b> is
    /// ambiguous — a revocation or a reset, indistinguishable — so the profile's list still
    /// wins and the healing behaviour is unchanged.</description></item>
    /// </list>
    ///
    /// <para>The cost of that asymmetry is that removing a library in Dashboard → Users does
    /// not stick. It is reported rather than hidden, and the documented place to take a
    /// library away is the profile's own editor.</para>
    ///
    /// <para>Pure and static so it can be driven directly by a harness: every case below is
    /// a list comparison, and the controller it serves needs six injected services to
    /// reach.</para>
    /// </summary>
    public static class LibraryAccessReconciler
    {
        /// <summary>
        /// The outcome of comparing a profile's stored library list with the account's.
        /// </summary>
        public sealed class Reconciliation
        {
            /// <summary>The list to apply to the account.</summary>
            public List<Guid> Authority { get; init; } = new();

            /// <summary>
            /// Folders the account had that the profile did not, now adopted. Non-empty means
            /// somebody granted a library outside the plugin.
            /// </summary>
            public List<Guid> Granted { get; init; } = new();

            /// <summary>
            /// Folders the profile has that the account has lost. Not adopted — this is the
            /// shape a reset takes — but worth saying out loud, because it is the case that
            /// reads as a failed save.
            /// </summary>
            public List<Guid> Missing { get; init; } = new();

            /// <summary>True when <see cref="Granted"/> changed the stored list.</summary>
            public bool StoredListChanged => Granted.Count > 0;
        }

        /// <summary>
        /// Reconciles a profile's stored list against the account's current one.
        /// </summary>
        /// <param name="stored">
        /// The profile's own list, from its mapping row. Never null here — a null stored list
        /// means a profile that predates the field, which the caller migrates instead.
        /// </param>
        /// <param name="accountCurrent">The account's live <c>Policy.EnabledFolders</c>.</param>
        /// <param name="accountEnableAllFolders">
        /// The account's live <c>Policy.EnableAllFolders</c>. When set, nothing is adopted:
        /// an all-libraries account carries an EMPTY <c>EnabledFolders</c> rather than a list
        /// of everything, so there is no per-library grant to read, and it is the shape a
        /// reset leaves behind. The switch path forces it back off regardless, because a
        /// sub-profile that can see everything is not a profile.
        /// </param>
        public static Reconciliation Reconcile(
            IEnumerable<Guid>? stored,
            IEnumerable<Guid>? accountCurrent,
            bool accountEnableAllFolders)
        {
            // Guid.Empty is what an unparseable folder id degrades to elsewhere in the
            // plugin, and granting it would mean granting nothing under a real-looking name.
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
