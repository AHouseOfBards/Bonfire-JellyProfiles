using System;

namespace Jellyfin.Profiles.Models
{
    /// <summary>Body of <c>POST /plugins/profiles/theme</c>.</summary>
    public class SetProfileThemeRequest
    {
        public Guid ProfileId { get; set; }

        /// <summary>A theme id, or empty for "follow the server default".</summary>
        public string? Theme { get; set; }

        public string? MasterPin { get; set; }
    }
}
