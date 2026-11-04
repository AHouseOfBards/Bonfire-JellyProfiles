using System;

namespace Jellyfin.Profiles.Models
{
    /// <summary>POST /admin/email. Every field is optional; only what is sent changes.</summary>
    public class AdminEmailRequest
    {
        public string? Host { get; set; }
        public int? Port { get; set; }

        /// <summary><c>starttls</c> or <c>none</c>.</summary>
        public string? Security { get; set; }

        public string? Username { get; set; }

        /// <summary>Null keeps the stored one; empty clears it. Never sent back.</summary>
        public string? Password { get; set; }

        public string? FromAddress { get; set; }
        public string? FromName { get; set; }
    }

    /// <summary>POST /admin/email/test.</summary>
    public class EmailTestRequest
    {
        public string To { get; set; } = string.Empty;
    }

    /// <summary>POST /admin/account-email/remove.</summary>
    public class RemoveAccountEmailRequest
    {
        public Guid UserId { get; set; }
    }

    /// <summary>POST /account/email: send a code to a new address.</summary>
    public class AccountEmailRequest
    {
        public string Address { get; set; } = string.Empty;

        /// <summary>The language to write the mail in, as the client is showing it.</summary>
        public string? Language { get; set; }

        public string? MasterPin { get; set; }
    }

    /// <summary>POST /account/email/confirm.</summary>
    public class AccountEmailConfirmRequest
    {
        public string Code { get; set; } = string.Empty;
        public string? Language { get; set; }
        public string? MasterPin { get; set; }
    }

    /// <summary>POST /account/email/notifications. Only what is sent changes.</summary>
    public class AccountEmailNotificationsRequest
    {
        public bool? NotifyLockout { get; set; }
        public bool? NotifyNewDevice { get; set; }
        public bool? NotifyTimeAsked { get; set; }
        public bool? NotifyLimitReached { get; set; }
        public string? MasterPin { get; set; }
    }

    /// <summary>POST /account/email/remove.</summary>
    public class AccountEmailRemoveRequest
    {
        public string? MasterPin { get; set; }
    }

    /// <summary>POST /account/pin-reset/send.</summary>
    public class PinResetSendRequest
    {
        public string? Language { get; set; }
    }

    /// <summary>POST /account/pin-reset.</summary>
    public class PinResetRequest
    {
        public string Code { get; set; } = string.Empty;
        public string NewPin { get; set; } = string.Empty;
    }
}
