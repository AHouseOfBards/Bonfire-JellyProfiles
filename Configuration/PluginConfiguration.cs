using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Profiles.Configuration
{
    public static class ClientInjectionModes
    {
        public const string Direct = "direct";
        public const string FileTransformation = "fileTransformation";

        public static string Normalize(string? mode) =>
            string.Equals(mode, FileTransformation, StringComparison.OrdinalIgnoreCase)
                ? FileTransformation
                : Direct;
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ClientInjectionMode { get; set; } = ClientInjectionModes.Direct;
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
        /// <para>
        /// Guid.Empty means "recorded before this field existed". Unowned records are never
        /// listed for anyone — they are claimed first, either by a live session for the
        /// household or by already appearing on one of its whitelists. Treating Guid.Empty as
        /// visible would expose every device on the server to every account, since on an
        /// existing install every record starts out unowned.
        /// </para>
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

    /// <summary>
    /// Valid values for <see cref="ProfileMapping.SwitcherMode"/>. Kept as constants rather
    /// than an enum so the stored configuration stays readable and an unrecognised value
    /// degrades to the default instead of failing to deserialise.
    /// </summary>
    public static class SwitcherModes
    {
        /// <summary>Forced "Who's Watching?" screen on the home page. The original behaviour.</summary>
        public const string Gate = "gate";

        /// <summary>No forced screen; the switcher lives in Jellyfin's user menu and profile page.</summary>
        public const string Native = "native";

        /// <summary>Maps arbitrary input to a known mode, falling back to <see cref="Gate"/>.</summary>
        public static string Normalize(string? mode) =>
            string.Equals(mode, Native, System.StringComparison.OrdinalIgnoreCase) ? Native : Gate;
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
        /// <summary>
        /// Set on a master account by its own owner: allows other members of a shared Bonfire
        /// to switch into this account from the local network without entering its PIN, and —
        /// if no PIN is set at all — to enter it from the local network at all.
        /// <para>
        /// Default false, so upgrading never widens access on an existing install. Consent has
        /// to come from the account being entered: <see cref="BypassPinOnLocalNetwork"/> is the
        /// owner's convenience for their own household and deliberately does not carry across a
        /// Bonfire link.
        /// </para>
        /// <para>
        /// "Local" is decided by Jellyfin's own network settings and is relative to the
        /// <em>server</em>. Behind a reverse proxy missing from Known Proxies, every request
        /// looks local — the UI warns about this.
        /// </para>
        /// </summary>
        public bool AllowHouseholdLanBypass { get; set; } = false;
        public List<string> AllowedDeviceIds { get; set; } = new List<string>();
        public string? ProfileImage { get; set; }
        public bool HideMySubProfilesFromOthers { get; set; } = false;
        public bool HideOthersSubProfilesFromMe { get; set; } = false;
        /// <summary>
        /// How this account reaches the profile switcher. <c>"gate"</c> (the default) shows the
        /// forced "Who's Watching?" screen on the home page; <c>"native"</c> drops the gate and
        /// puts the switcher in Jellyfin's own user menu and profile page instead.
        /// <para>
        /// Deliberately a per-account preference rather than a server setting: it is a matter
        /// of taste, and one household's answer should not be imposed on everyone on the server.
        /// Only ever read from the master account's mapping — sub-profiles inherit it.
        /// </para>
        /// </summary>
        public string SwitcherMode { get; set; } = SwitcherModes.Gate;
    }

    public class UserProfileLimitOverride
    {
        public Guid UserId { get; set; }
        public int MaxProfiles { get; set; }
    }

}

