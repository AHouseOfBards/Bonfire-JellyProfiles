using System;

namespace Jellyfin.Profiles.Models
{
    public class AuditLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string MasterUsername { get; set; } = string.Empty;
        public string TargetUsername { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Client { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
    }
}
