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

        /// <summary>
        /// Avatar images the administrator has uploaded for anyone on the server to choose
        /// from. Managed entirely through the plugin settings page — the files are never
        /// meant to be dropped into the folder by hand.
        /// </summary>
        public List<AvatarLibraryItem> AvatarLibrary { get; set; } = new List<AvatarLibraryItem>();

        /// <summary>
        /// When true, profile pictures can only be chosen from <see cref="AvatarLibrary"/>;
        /// uploading an arbitrary image is refused. For households that want a curated,
        /// consistent set rather than whatever each family member picks.
        /// </summary>
        public bool DisallowCustomAvatarUploads { get; set; } = false;

        /// <summary>
        /// What an account gets before it chooses for itself: whether the "Who's Watching?"
        /// screen appears on startup, and where the switcher is reached from.
        /// <para>
        /// Requested in GitHub issue #14 — on a household server the administrator is usually
        /// the only person who will ever open these settings, and setting every account by
        /// hand does not scale. An account that has made its own choice keeps it; these only
        /// fill in the blank. See <see cref="SwitcherLocations.Resolve"/>.
        /// </para>
        /// </summary>
        public bool DefaultAskOnStartup { get; set; } = true;

        /// <summary>Default for <see cref="ProfileMapping.SwitcherLocation"/>. See <see cref="DefaultAskOnStartup"/>.</summary>
        public string DefaultSwitcherLocation { get; set; } = SwitcherLocations.Button;

        /// <summary>
        /// PBKDF2 hash of the emergency disable code, or null when the feature is off (the
        /// default). Entering the code shuts the plugin's client script down until the
        /// server restarts — see <see cref="Plugin.IsPanicDisabled"/>.
        /// <para>
        /// Hashed like a PIN, so the code cannot be read back from it. None of this plugin's
        /// own endpoints return the field — <c>admin/panic-status</c> reports only whether one
        /// is set. Note that it is still part of the plugin configuration, which Jellyfin
        /// exposes to administrators through its own <c>/Plugins/{id}/Configuration</c>
        /// endpoint; that is the same audience that can set it, and only the hash is there.
        /// </para>
        /// </summary>
        public string? PanicCodeHash { get; set; }
    }

    /// <summary>
    /// One administrator-supplied avatar. The image itself lives in the plugin's data
    /// directory under <c>avatars/</c>; only metadata is kept in the configuration.
    /// </summary>
    public class AvatarLibraryItem
    {
        public string Id { get; set; } = string.Empty;
        /// <summary>Shown under the image in the picker. Free text, escaped on render.</summary>
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>File extension including the dot, e.g. ".jpg" — the stored format.</summary>
        public string Extension { get; set; } = ".jpg";
        public DateTime UploadedUtc { get; set; }
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
    /// Legacy single-setting switcher modes, superseded by the independent
    /// <see cref="ProfileMapping.AskOnStartup"/> and <see cref="ProfileMapping.SwitcherLocation"/>
    /// pair. Retained only so 1.3.1-beta installs — and any cached copy of profiles.js still
    /// posting this field — migrate instead of resetting to the default.
    /// </summary>
    public static class SwitcherModes
    {
        /// <summary>"Who's Watching?" on startup, switcher reached from the floating button.</summary>
        public const string Gate = "gate";

        /// <summary>No startup prompt, switcher reached from Jellyfin's own user menu.</summary>
        public const string Native = "native";

        /// <summary>Maps arbitrary input to a known mode, falling back to <see cref="Gate"/>.</summary>
        public static string Normalize(string? mode) =>
            string.Equals(mode, Native, System.StringComparison.OrdinalIgnoreCase) ? Native : Gate;
    }

    /// <summary>
    /// Where an account reaches the profile switcher. Independent of whether the startup
    /// prompt appears — the two were one setting in 1.3.1-beta, which could not express
    /// "ask me on startup, but put the switcher in Jellyfin's menu" (GitHub issue #14).
    /// </summary>
    public static class SwitcherLocations
    {
        /// <summary>Bonfire's own floating button, injected into the client header.</summary>
        public const string Button = "button";

        /// <summary>A "Switch Profile" row in Jellyfin's user menu; no floating button.</summary>
        public const string Menu = "menu";

        /// <summary>Maps arbitrary input to a known location, falling back to <see cref="Button"/>.</summary>
        public static string Normalize(string? location) =>
            string.Equals(location, Menu, System.StringComparison.OrdinalIgnoreCase) ? Menu : Button;

        /// <summary>
        /// Resolves an account's effective preference. Null is what distinguishes "never set"
        /// from "deliberately set to the default", which is why both new fields are nullable:
        /// an account that has chosen keeps its choice, and one that has not inherits the
        /// server-wide default the administrator set.
        /// <para>
        /// The legacy <see cref="ProfileMapping.SwitcherMode"/> still wins over the default,
        /// but only when it says <c>native</c>. It is a non-nullable field that has always
        /// defaulted to <c>gate</c>, so every mapping carries that value whether or not
        /// anybody chose it — reading it as an explicit choice would mean the administrator's
        /// default could never apply to anyone.
        /// </para>
        /// </summary>
        public static (bool AskOnStartup, string Location) Resolve(
            ProfileMapping? mapping,
            bool defaultAskOnStartup = true,
            string? defaultLocation = null)
        {
            string fallbackLocation = Normalize(defaultLocation ?? Button);
            if (mapping == null) return (defaultAskOnStartup, fallbackLocation);

            bool legacyNative = SwitcherModes.Normalize(mapping.SwitcherMode) == SwitcherModes.Native;

            return (
                mapping.AskOnStartup ?? (legacyNative ? false : defaultAskOnStartup),
                Normalize(mapping.SwitcherLocation ?? (legacyNative ? Menu : fallbackLocation))
            );
        }
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
        /// Legacy combined switcher setting. Superseded by <see cref="AskOnStartup"/> and
        /// <see cref="SwitcherLocation"/>; read only by <see cref="SwitcherLocations.Resolve"/>
        /// to migrate accounts configured under 1.3.1-beta. No longer written.
        /// </summary>
        public string SwitcherMode { get; set; } = SwitcherModes.Gate;

        /// <summary>
        /// Whether the "Who's Watching?" screen appears when the client first loads. It is
        /// shown once per browser session, not on every trip to the home screen.
        /// <para>
        /// Null means never explicitly set, in which case <see cref="SwitcherMode"/> decides —
        /// see <see cref="SwitcherLocations.Resolve"/>. That distinction is the whole reason
        /// this is nullable rather than defaulting to true.
        /// </para>
        /// </summary>
        public bool? AskOnStartup { get; set; }

        /// <summary>
        /// Where the switcher is reached from once past the startup prompt — see
        /// <see cref="SwitcherLocations"/>. Null means never explicitly set.
        /// <para>
        /// A per-account preference — it is a matter of taste, and one household's answer
        /// should not be imposed on everyone on the server. The administrator sets only the
        /// starting point, via <see cref="PluginConfiguration.DefaultSwitcherLocation"/>,
        /// which an account overrides the moment it chooses for itself. Only ever read from
        /// the master account's mapping — sub-profiles inherit it.
        /// </para>
        /// </summary>
        public string? SwitcherLocation { get; set; }
    }

    public class UserProfileLimitOverride
    {
        public Guid UserId { get; set; }
        public int MaxProfiles { get; set; }
    }

}

