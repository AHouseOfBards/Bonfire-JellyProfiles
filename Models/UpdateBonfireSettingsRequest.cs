namespace Jellyfin.Profiles.Models
{
    public class UpdateBonfireSettingsRequest
    {
        public bool HideMySubProfilesFromOthers { get; set; }
        public bool HideOthersSubProfilesFromMe { get; set; }
    }
}
