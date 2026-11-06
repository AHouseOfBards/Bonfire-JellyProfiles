namespace Jellyfin.Profiles.Models
{
    /// <summary>
    /// POST /bonfire/settings. Every field left out keeps its stored value (R-1159). The two
    /// hide flags were plain bool, so a client sending only the home-network setting switched
    /// both off — showing a household's hidden profiles to every linked Bonfire.
    /// </summary>
    public class UpdateBonfireSettingsRequest
    {
        /// <summary>Null keeps it.</summary>
        public bool? HideMySubProfilesFromOthers { get; set; }
        /// <summary>Null keeps it.</summary>
        public bool? HideOthersSubProfilesFromMe { get; set; }
        /// <summary>
        /// Nullable so a cached older copy of profiles.js — which posts only the two hide
        /// flags — leaves the setting alone instead of silently switching it back off.
        /// </summary>
        public bool? AllowHouseholdLanBypass { get; set; }
    }
}
