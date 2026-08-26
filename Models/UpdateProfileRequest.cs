using System;
using System.Collections.Generic;

namespace Jellyfin.Profiles.Models
{
    public class UpdateProfileRequest
    {
        public Guid ProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string? Pin { get; set; }
        public string AvatarColor { get; set; } = "#1F77B4";
        /// <summary>
        /// Hides the avatar colour behind the picture. Null = leave unchanged.
        /// </summary>
        public bool? TransparentAvatar { get; set; }
        public string? MaxParentalRating { get; set; }
        public List<Guid>? EnabledFolders { get; set; }
        /// <summary>Tags this profile is blocked from seeing. Null = leave unchanged.</summary>
        public List<string>? BlockedTags { get; set; }
        /// <summary>When non-empty, restricts this profile to items carrying one of these tags. Null = leave unchanged.</summary>
        public List<string>? AllowedTags { get; set; }
        public string? MasterPin { get; set; }
        /// <summary>Minutes of inactivity before auto-lock. 0 = never. Null = leave unchanged.</summary>
        public int? LockoutMinutes { get; set; }
        public bool? BypassPinOnLocalNetwork { get; set; }
        public List<string>? AllowedDeviceIds { get; set; }
        public string? ProfileImage { get; set; }
        /// <summary>
        /// Small rendering of <see cref="ProfileImage"/>, produced by the client. Optional —
        /// the full-size image is served in its place when absent.
        /// </summary>
        public string? ProfileImageThumb { get; set; }
        /// <summary>
        /// Id of an avatar from the server's library. When set it wins over
        /// <see cref="ProfileImage"/> and the file is copied server-side, which is the only
        /// way to set a picture on a server that has disabled custom avatars.
        /// </summary>
        public string? AvatarLibraryId { get; set; }
    }
}
