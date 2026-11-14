namespace Jellyfin.Profiles.Models
{
    public class AddAvatarRequest
    {
        /// <summary>Full-size rendering as a <c>data:image/…;base64,…</c> URL.</summary>
        public string? Image { get; set; }

        /// <summary>
        /// Small rendering of the same image, used by pickers and switcher cards. Optional —
        /// the full-size image is served in its place when absent, which costs bandwidth but
        /// never breaks the picture.
        /// </summary>
        public string? Thumb { get; set; }

        /// <summary>Label shown under the image. Trimmed to 60 characters.</summary>
        public string? DisplayName { get; set; }
    }
}
