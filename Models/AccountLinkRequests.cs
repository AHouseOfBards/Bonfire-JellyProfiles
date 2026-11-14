using System;
using System.Collections.Generic;

namespace Jellyfin.Profiles.Models
{
    /// <summary>POST /accounts/links/add. The account's own password is the consent.</summary>
    public class AddAccountLinkRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? MasterPin { get; set; }
    }

    /// <summary>
    /// POST /accounts/links/remove. From the household: <see cref="UserId"/> is the account to
    /// take off its switcher. From the account: <see cref="HouseholdId"/> is the household to
    /// leave.
    /// </summary>
    public class RemoveAccountLinkRequest
    {
        public Guid UserId { get; set; }
        public Guid HouseholdId { get; set; }
        public string? MasterPin { get; set; }
    }

    /// <summary>POST /admin/links and /admin/links/remove.</summary>
    public class AdminAccountLinkRequest
    {
        public Guid HouseholdId { get; set; }
        public Guid UserId { get; set; }
    }

    /// <summary>POST /admin/make-profile.</summary>
    public class MakeProfileRequest
    {
        /// <summary>The account to make a profile.</summary>
        public Guid UserId { get; set; }

        /// <summary>The main account it becomes a profile of.</summary>
        public Guid HouseholdId { get; set; }

        /// <summary>The name on its tile. Empty keeps its username.</summary>
        public string? ProfileName { get; set; }

        /// <summary>
        /// What deleting the profile later does to the account. False (the default) keeps it,
        /// with its watch history, as an account of its own; true deletes it with the profile.
        /// </summary>
        public bool DeleteWithProfile { get; set; }
    }

    /// <summary>GET /accounts/links.</summary>
    public class AccountLinksResponse
    {
        /// <summary>Other accounts shown in this household's switcher.</summary>
        public List<LinkedAccountView> Added { get; set; } = new();

        /// <summary>Households this account is shown in.</summary>
        public List<LinkedAccountView> ShownIn { get; set; } = new();
    }

    public class LinkedAccountView
    {
        /// <summary>The account, in <see cref="AccountLinksResponse.Added"/>; the household's main account, in <see cref="AccountLinksResponse.ShownIn"/>.</summary>
        public Guid UserId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Opening it asks for its PIN.</summary>
        public bool HasPin { get; set; }

        public bool ByAdministrator { get; set; }

        public DateTime LinkedUtc { get; set; }
    }
}
