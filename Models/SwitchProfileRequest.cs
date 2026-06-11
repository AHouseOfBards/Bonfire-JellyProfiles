using System;

namespace Jellyfin.Profiles.Models
{
    public class SwitchProfileRequest
    {
        public Guid ProfileId { get; set; }
        public string? Pin { get; set; }
    }
}
