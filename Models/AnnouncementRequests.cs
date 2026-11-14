using System;

namespace Jellyfin.Profiles.Models
{
    /// <summary>Create an announcement, or replace one when <see cref="Id"/> is given.</summary>
    public class SaveAnnouncementRequest
    {
        public Guid? Id { get; set; }

        public string? Message { get; set; }

        /// <summary>info, warning or critical. Defaults to info.</summary>
        public string? Severity { get; set; }

        /// <summary>When it starts showing. Null means now.</summary>
        public DateTime? StartsUtc { get; set; }

        /// <summary>When it stops showing. Null means until deleted.</summary>
        public DateTime? EndsUtc { get; set; }
    }

    public class DeleteAnnouncementRequest
    {
        public Guid Id { get; set; }
    }

    /// <summary>Turns a profile into an account of its own.</summary>
    public class StandaloneRequest
    {
        public Guid ProfileId { get; set; }

        /// <summary>The new account's username. Blank keeps the one it has.</summary>
        public string? Username { get; set; }

        /// <summary>Required: a profile has no password anyone knows.</summary>
        public string? Password { get; set; }
    }

    /// <summary>Opens or closes the server for maintenance.</summary>
    public class MaintenanceRequest
    {
        public bool Enabled { get; set; }

        /// <summary>Shown to everyone turned away. Blank for the default.</summary>
        public string? Message { get; set; }

        /// <summary>When it is expected to end. Shown; it ends by itself only if
        /// <see cref="ReopenAutomatically"/> is set.</summary>
        public DateTime? UntilUtc { get; set; }

        /// <summary>Open again at <see cref="UntilUtc"/>, with nobody there. Needs an end time.</summary>
        public bool ReopenAutomatically { get; set; }

        /// <summary>Stop what is playing now, and refuse video and audio while closed.</summary>
        public bool StopPlayback { get; set; }
    }
}
