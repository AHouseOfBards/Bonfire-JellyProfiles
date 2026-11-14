using System;

namespace Jellyfin.Profiles.Models
{
    public class SwitchProfileRequest
    {
        public Guid ProfileId { get; set; }
        public string? Pin { get; set; }

        /// <summary>
        /// /switch only. When true, the access token that made this request is ended once the
        /// new one exists, so a client need not call POST /Sessions/Logout itself. Default false.
        /// </summary>
        public bool EndPreviousSession { get; set; }
    }
}
