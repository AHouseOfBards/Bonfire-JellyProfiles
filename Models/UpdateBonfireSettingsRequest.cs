namespace Jellyfin.Profiles.Models
{
    public class UpdateBonfireSettingsRequest
    {
        public bool HideMySubProfilesFromOthers { get; set; }
        public bool HideOthersSubProfilesFromMe { get; set; }
        /// <summary>
        /// Nullable so a cached older copy of profiles.js — which posts only the two hide
        /// flags — leaves the setting alone instead of silently switching it back off.
        /// </summary>
        public bool? AllowHouseholdLanBypass { get; set; }
    }
}
