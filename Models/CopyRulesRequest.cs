using System;

namespace Jellyfin.Profiles.Models
{
    /// <summary>Body of <c>POST /plugins/profiles/copy-rules</c>.</summary>
    public class CopyRulesRequest
    {
        /// <summary>The profile whose rules are copied. Left as it is.</summary>
        public Guid SourceProfileId { get; set; }

        /// <summary>The profile that gets them. Its rating limit, tags, libraries and viewing limits are replaced.</summary>
        public Guid TargetProfileId { get; set; }

        public string? MasterPin { get; set; }
    }
}
