using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// Another real Jellyfin account shown as a tile in a household's switcher (issue #39).
    /// Opening the tile signs into that account itself, so there is one watch history and
    /// nothing to keep in step. Bonfire never changes the account: not its policy, its PIN
    /// or its libraries.
    ///
    /// <para><b>Not a Bonfire.</b> A Bonfire joins two households both ways and shows each
    /// one's profiles to the other. A link is one account, shown in one household, one
    /// way: the household sees the account, the account sees nothing of the household.
    /// The account's own profiles are not shown either.</para>
    ///
    /// <para><b>Consent.</b> Made by the household's main account typing the account's
    /// password once, or by an administrator. Either the household or the account can
    /// remove it, and one account can be shown in several households.</para>
    ///
    /// <para><b>Opening it.</b> With the account's own Bonfire PIN if it has one, or the
    /// LAN consent its owner already gives Bonfires (<see cref="ProfileMapping.AllowHouseholdLanBypass"/>);
    /// with nothing if it has no PIN, which is what Logan asked for: the password was the
    /// consent. An administrator account is the exception: it opens only with its PIN,
    /// on any network, and can't be added without one.</para>
    /// </summary>
    public class LinkedAccount
    {
        /// <summary>The main account of the household the tile is shown in.</summary>
        public Guid HouseholdId { get; set; }

        /// <summary>The account the tile opens.</summary>
        public Guid UserId { get; set; }

        public DateTime LinkedUtc { get; set; }

        /// <summary>Added by an administrator rather than with the account's password.</summary>
        public bool ByAdministrator { get; set; }

        public LinkedAccount Copy() => (LinkedAccount)MemberwiseClone();
    }

    /// <summary>The rules about links, kept apart from the routes so they can be tested alone.</summary>
    public static class AccountLinks
    {
        /// <summary>Per household. Each is a tile, and a switcher is a screen.</summary>
        public const int MaxPerHousehold = 10;

        /// <summary>The accounts shown in <paramref name="householdId"/>, oldest first.</summary>
        public static List<Guid> In(PluginConfiguration config, Guid householdId) =>
            (config.LinkedAccounts ?? new List<LinkedAccount>())
                .Where(l => l.HouseholdId == householdId && l.UserId != householdId)
                .OrderBy(l => l.LinkedUtc)
                .Select(l => l.UserId)
                .Distinct()
                .ToList();

        /// <summary>The households <paramref name="userId"/> is shown in.</summary>
        public static List<Guid> ShownIn(PluginConfiguration config, Guid userId) =>
            (config.LinkedAccounts ?? new List<LinkedAccount>())
                .Where(l => l.UserId == userId && l.HouseholdId != userId)
                .Select(l => l.HouseholdId)
                .Distinct()
                .ToList();

        /// <summary>
        /// Why <paramref name="userId"/> can't be shown in <paramref name="householdId"/>, or
        /// null when it can. Everything but the password and who is asking.
        /// </summary>
        public static string? Refusal(PluginConfiguration config, Guid householdId, Guid userId, bool isAdministrator)
        {
            if (userId == householdId) return OwnAccountMessage;
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            if (row != null && row.MasterUserId != Guid.Empty && row.MasterUserId != userId) return ProfileMessage;
            if ((config.LinkedAccounts ?? new List<LinkedAccount>()).Any(l => l.HouseholdId == householdId && l.UserId == userId))
                return AlreadyHereMessage;
            if (In(config, householdId).Count >= MaxPerHousehold) return TooManyMessage;
            if (isAdministrator && string.IsNullOrEmpty(row?.PinHash)) return AdminNeedsPinMessage;
            return null;
        }

        internal const string OwnAccountMessage = "That's your own account.";
        internal const string ProfileMessage = "That's a profile. Add its main account instead.";
        internal const string AlreadyHereMessage = "That account is already here.";
        internal const string TooManyMessage = "A household can show up to 10 other accounts.";
        internal const string AdminNeedsPinMessage = "An administrator account needs a PIN before it can be added.";

        /// <summary>
        /// Drops every link to or from <paramref name="userId"/>: it was deleted, or is now a
        /// profile. The caller holds the lock and saves. True if anything went.
        /// </summary>
        public static bool Forget(PluginConfiguration config, Guid userId) =>
            (config.LinkedAccounts ?? new List<LinkedAccount>()).RemoveAll(l => l.UserId == userId || l.HouseholdId == userId) > 0;
    }
}
