using System;

namespace Jellyfin.Profiles.Models
{
    public class DeleteProfileRequest
    {
        public Guid ProfileId { get; set; }
        public string? MasterPin { get; set; }
    }
}
