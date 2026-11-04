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
    }
}
