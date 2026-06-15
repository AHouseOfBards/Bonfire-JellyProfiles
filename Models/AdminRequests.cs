using System;

namespace Jellyfin.Profiles.Models
{
    public class DeleteDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
    }

    public class SetProfileLimitRequest
    {
        public Guid UserId { get; set; }
        public int? MaxProfiles { get; set; }
    }
}
