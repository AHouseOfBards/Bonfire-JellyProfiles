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
}
