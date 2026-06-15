using System;

namespace Jellyfin.Profiles.Models
{
    public class JoinBonfireRequest
    {
        public string Code { get; set; } = string.Empty;
    }

    public class KickBonfireRequest
    {
        public Guid MemberId { get; set; }
    }
}
