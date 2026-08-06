namespace Jellyfin.Profiles.Models
{
    public class UpdatePreferencesRequest
    {
        /// <summary>
        /// "gate" or "native". Nullable so a caller can post other preferences later without
        /// having to know this one; anything unrecognised is normalised to "gate".
        /// </summary>
        public string? SwitcherMode { get; set; }
    }
}
