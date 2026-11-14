using System;
using System.Collections.Generic;

namespace Jellyfin.Profiles.Models
{
    // Typed so Jellyfin's OpenAPI document describes these routes, and a client can be
    // generated from it. Every field here is part of the API: see "Stability" in
    // docs/developer-api.md before renaming or removing one.

    /// <summary>GET /info: what this server's Bonfire is and does. Anonymous.</summary>
    public class InfoResponse
    {
        public string Name { get; set; } = "Bonfire";

        /// <summary>The plugin's version, with any pre-release label.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Goes up only when a released route changes in a way that breaks callers.</summary>
        public int ApiVersion { get; set; }

        /// <summary>The emergency disable is on: show no Bonfire screens.</summary>
        public bool Disabled { get; set; }

        public InfoFeatures Features { get; set; } = new InfoFeatures();

        /// <summary>Null unless the server is closed for maintenance.</summary>
        public InfoMaintenance? Maintenance { get; set; }
    }

    /// <summary>Server-wide settings a client acts on. Never anything about a household.</summary>
    public class InfoFeatures
    {
        public bool ClientPinLogin { get; set; }
        public bool ClientProfileList { get; set; }
        public bool SkipQuickConnectOnKnownDevices { get; set; }
        public bool ProfileThemes { get; set; }
        public List<string> ProxyClients { get; set; } = new List<string>();
        public bool RequireMainPin { get; set; }

        /// <summary>Outgoing mail is set up and tested, so addresses can be added.</summary>
        public bool Email { get; set; }

        /// <summary>A main account with a confirmed address can reset its PIN by email.</summary>
        public bool PinResetByEmail { get; set; }
    }

    public class InfoMaintenance
    {
        public string Message { get; set; } = string.Empty;
        public DateTime? UntilUtc { get; set; }
    }

    /// <summary>GET /me: who the caller is, in Bonfire's terms.</summary>
    public class MeResponse
    {
        /// <summary>The signed-in Jellyfin user.</summary>
        public Guid UserId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>True for a profile, false for a main account.</summary>
        public bool IsProfile { get; set; }

        /// <summary>The household's main account. The caller's own id when it is one.</summary>
        public Guid MainAccountId { get; set; }

        public string MainAccountName { get; set; } = string.Empty;

        /// <summary>The caller is a Jellyfin administrator.</summary>
        public bool IsAdministrator { get; set; }

        /// <summary>The main account has a PIN.</summary>
        public bool MainHasPin { get; set; }

        /// <summary>How many profiles the household has, not counting the main account.</summary>
        public int ProfileCount { get; set; }

        /// <summary>How many it may have.</summary>
        public int MaxProfiles { get; set; }

        /// <summary>This main account can reset its PIN by email now: the feature is on and
        /// it has a confirmed address. Always false for a profile.</summary>
        public bool PinResetByEmail { get; set; }
    }

    /// <summary>GET /account/email: the main account's address and notices.</summary>
    public class AccountEmailResponse
    {
        /// <summary>Email is set up on this server. Without it nothing else here works.</summary>
        public bool Available { get; set; }

        /// <summary>The confirmed address, or empty.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>An address waiting for its code, or empty.</summary>
        public string PendingAddress { get; set; } = string.Empty;

        public bool NotifyLockout { get; set; } = true;
        public bool NotifyNewDevice { get; set; } = true;
        public bool NotifyTimeAsked { get; set; } = true;
        public bool NotifyLimitReached { get; set; }

        /// <summary>PIN reset by email is on for this server.</summary>
        public bool PinResetEnabled { get; set; }
    }

    /// <summary>GET /admin/email. The password is never included, only whether one is set.</summary>
    public class AdminEmailResponse
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Security { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public bool HasPassword { get; set; }
        public string FromAddress { get; set; } = string.Empty;

        /// <summary>Enough is filled in to try.</summary>
        public bool Configured { get; set; }

        /// <summary>A test has gone through since anything was last changed.</summary>
        public bool Verified { get; set; }

        public bool PinResetByEmail { get; set; }

        /// <summary>Main accounts with a confirmed address.</summary>
        public List<AdminAccountEmail> Accounts { get; set; } = new();
    }

    public class AdminAccountEmail
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public DateTime ConfirmedUtc { get; set; }
    }

    /// <summary>GET /onboarding: the first-time setup this account should be shown.</summary>
    public class OnboardingResponse
    {
        /// <summary>Show it now. False for a profile, when it is off, or once seen.</summary>
        public bool Pending { get; set; }

        /// <summary>Send this back with /onboarding/done.</summary>
        public int Generation { get; set; }

        /// <summary>In order. Only steps the server can offer right now.</summary>
        public System.Collections.Generic.List<OnboardingStepInfo> Steps { get; set; } = new();
    }

    public class OnboardingStepInfo
    {
        /// <summary>welcome, mainPin, firstProfile, switcher or theme.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Already true of this account: a PIN set, a profile made. Show it or not.</summary>
        public bool Done { get; set; }
    }
}
