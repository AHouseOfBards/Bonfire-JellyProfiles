using System;
using System.Collections.Generic;

namespace Jellyfin.Profiles.Models
{
    public class CreateProfileRequest
    {
        public string ProfileName { get; set; } = string.Empty;
        public string? Pin { get; set; }
        /// <summary>Null picks the next colour the household isn't using.</summary>
        public string? AvatarColor { get; set; }
        /// <summary>
        /// Hides the avatar colour behind the picture. Null = false (paint the colour).
        /// </summary>
        public bool? TransparentAvatar { get; set; }
        /// <summary>
        /// One of the theme ids. Null or "" = follow the server default. Ignored while the
        /// server does not let profiles choose.
        /// </summary>
        public string? Theme { get; set; }
        /// <summary>A rating score such as "14". Null or "" = no limit of its own.</summary>
        public string? MaxParentalRating { get; set; }
        /// <summary>
        /// True gives every library the main account has, including ones added later, the
        /// same as leaving <see cref="EnabledFolders"/> out on create. Accepted here so a
        /// client can say it the same way on /create and /update (R-1144).
        /// </summary>
        public bool? AllLibraries { get; set; }
        /// <summary>
        /// Library ids, as strings, parsed by the controller (ProfilesBaseController.ParseFolderIds).
        /// <para>
        /// Typed as Guid, a single entry that was not a Guid failed the whole request: Jellyfin's Guid
        /// converter throws a FormatException, not a JsonException, so model binding reported "The
        /// supplied value is invalid" against no field and handed the action a null request (issue #33).
        /// A library with no item id, which /libraries used to list, put exactly such an entry in the form.
        /// </para>
        /// </summary>
        public List<string>? EnabledFolders { get; set; }
        /// <summary>Tags this profile is blocked from seeing. Null = block nothing.</summary>
        public List<string>? BlockedTags { get; set; }
        /// <summary>When non-empty, restricts this profile to items carrying one of these tags.</summary>
        public List<string>? AllowedTags { get; set; }
        public string? MasterPin { get; set; }
        /// <summary>Minutes of inactivity before auto-lock. 0 = never. Null = use default (5).</summary>
        public int? LockoutMinutes { get; set; }
        public bool? BypassPinOnLocalNetwork { get; set; }
        public List<string>? AllowedDeviceIds { get; set; }
        public string? ProfileImage { get; set; }
        /// <summary>
        /// Small rendering of <see cref="ProfileImage"/>, produced by the client. Optional; the
        /// full-size image is served in its place when absent.
        /// </summary>
        public string? ProfileImageThumb { get; set; }
        /// <summary>
        /// Id of an avatar from the server's library. When set it wins over
        /// <see cref="ProfileImage"/> and the file is copied server-side, the only way to set a picture
        /// on a server that has disabled custom avatars.
        /// </summary>
        public string? AvatarLibraryId { get; set; }
    }
}
