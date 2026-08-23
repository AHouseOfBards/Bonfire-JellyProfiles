namespace Jellyfin.Profiles.Models
{
    /// <summary>
    /// Sets one profile's tile artwork for one library (GitHub issue #19).
    /// </summary>
    public class LibraryArtworkRequest
    {
        /// <summary>The profile whose view of the library is being changed.</summary>
        public string? ProfileId { get; set; }

        /// <summary>The library. Jellyfin's virtual-folder item id.</summary>
        public string? LibraryId { get; set; }

        /// <summary>
        /// <c>inherit</c>, <c>custom</c> or <c>none</c> — see
        /// <see cref="Configuration.LibraryArtworkModes"/>. Anything else normalises to
        /// <c>inherit</c> rather than being rejected.
        /// </summary>
        public string? Mode { get; set; }

        /// <summary>Full-size rendering as a <c>data:image/…;base64,…</c> URL. Only read when the mode is <c>custom</c>.</summary>
        public string? Image { get; set; }

        /// <summary>Small rendering of the same image. Optional.</summary>
        public string? Thumb { get; set; }

        /// <summary>
        /// Id of a picture from the administrator's avatar library, used instead of
        /// <see cref="Image"/>. Copied server-side, which is what makes the "only allow
        /// avatars from this library" setting enforceable rather than advisory.
        /// </summary>
        public string? AvatarLibraryId { get; set; }

        /// <summary>Master PIN, when the master account has one.</summary>
        public string? MasterPin { get; set; }
    }
}
