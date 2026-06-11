using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Profiles.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public int MaxProfilesPerUser { get; set; } = 5;
        public bool RequireMasterPinForCreation { get; set; } = true;
        public List<ProfileMapping> Mappings { get; set; } = new List<ProfileMapping>();
    }

    public class ProfileMapping
    {
        public Guid ProfileUserId { get; set; }
        public Guid MasterUserId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string? PinHash { get; set; }
        public string AvatarColor { get; set; } = "#1F77B4";
        public bool IsHidden { get; set; } = true;
    }
}
