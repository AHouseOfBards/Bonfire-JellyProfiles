namespace Jellyfin.Profiles.Models
{
    /// <summary>
    /// What a profile may change about itself: its name and its picture, nothing else. There is
    /// no profile id (the caller is the profile) and no field for a rating, PIN, library, limit or
    /// device. A body carrying any of those is read without them.
    /// </summary>
    public class UpdateOwnProfileRequest
    {
        /// <summary>Null leaves the name as it is.</summary>
        public string? ProfileName { get; set; }
        /// <summary>Null leaves it as it is.</summary>
        public string? AvatarColor { get; set; }
        /// <summary>Hides the avatar colour behind the picture. Null leaves it as it is.</summary>
        public bool? TransparentAvatar { get; set; }
        /// <summary>A picture to upload, as <c>/update</c> takes it. Null leaves it; "" removes it.</summary>
        public string? ProfileImage { get; set; }
        /// <summary>Small rendering of <see cref="ProfileImage"/>. Optional.</summary>
        public string? ProfileImageThumb { get; set; }
        /// <summary>A picture from the server's library; wins over <see cref="ProfileImage"/>.</summary>
        public string? AvatarLibraryId { get; set; }
    }
}
