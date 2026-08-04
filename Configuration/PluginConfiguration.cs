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
        public List<KnownDevice> KnownDevices { get; set; } = new List<KnownDevice>();
        public List<BonfireGroup> BonfireGroups { get; set; } = new List<BonfireGroup>();
        public List<UserProfileLimitOverride> UserProfileLimitOverrides { get; set; } = new List<UserProfileLimitOverride>();
        // AuditLogs have been moved to a separate audit_log.json file so they no longer
        // cause a full PluginConfiguration.xml rewrite on every profile switch.
    }

    public class KnownDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Client { get; set; } = string.Empty;
        public DateTime LastSeen { get; set; }
        /// <summary>
        /// Master account this device was last seen on. Used to scope the device picker so one
        /// household never sees another's hardware.
        /// <para>
        /// Ownership is recorded rather than inferred from live sessions: a device that is
        /// simply switched off must still appear in the picker, otherwise editing a profile
        /// would silently drop it from that profile's whitelist.
        /// </para>
        /// <para>Guid.Empty means "recorded before this field existed" — treated as unowned
        /// and claimed by the next account that uses the device.</para>
        /// </summary>
        public Guid MasterUserId { get; set; }
    }

    public class BonfireGroup
    {
        // Initialized to empty so deserialization doesn't regenerate it on every load.
        // The controller sets this explicitly when creating a new group.
        public string GroupId { get; set; } = string.Empty;
        public string BonfireCode { get; set; } = string.Empty; // 6-character alphanumeric code
        public Guid OwnerUserId { get; set; }
        public List<Guid> MemberUserIds { get; set; } = new List<Guid>();
    }

    public class ProfileMapping
    {
        public Guid ProfileUserId { get; set; }
        public Guid MasterUserId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string? PinHash { get; set; }
        public string AvatarColor { get; set; } = "#1F77B4";
        public bool IsHidden { get; set; } = true;
        /// <summary>
        /// Minutes of inactivity before auto-lock. 0 = never. Default 5.
        /// Only honoured when the profile has a PIN set.
        /// </summary>
        public int LockoutMinutes { get; set; } = 5;
        /// <summary>
        /// The plugin's ground-truth list of library GUIDs this profile can access.
        /// Stored here so it survives Jellyfin server restarts that may reset user policies.
        /// Empty list = no library access. Null = not yet set (legacy; falls back to Jellyfin policy).
        /// </summary>
        public List<Guid>? EnabledFolders { get; set; }
        /// <summary>
        /// Tags this profile must never see. Stored as the profile's own list — the master's
        /// blocked tags are merged in when the policy is applied, so unblocking a tag on the
        /// master flows through to sub-profiles instead of staying baked in here.
        /// Null or empty = block nothing.
        /// </summary>
        public List<string>? BlockedTags { get; set; }
        /// <summary>
        /// When non-empty, this profile can only see items carrying at least one of these tags.
        /// Jellyfin matches against an item's *inherited* tags, so tagging a series or a whole
        /// library cascades to everything inside it. Null or empty = no allow-list.
        /// </summary>
        public List<string>? AllowedTags { get; set; }
        public bool BypassPinOnLocalNetwork { get; set; } = false;
        public List<string> AllowedDeviceIds { get; set; } = new List<string>();
        public string? ProfileImage { get; set; }
        public bool HideMySubProfilesFromOthers { get; set; } = false;
        public bool HideOthersSubProfilesFromMe { get; set; } = false;
    }

    public class UserProfileLimitOverride
    {
        public Guid UserId { get; set; }
        public int MaxProfiles { get; set; }
    }

}

