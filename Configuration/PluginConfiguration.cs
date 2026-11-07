using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Profiles.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public int MaxProfilesPerUser { get; set; } = 5;

        /// <summary>
        /// Wrong PIN guesses an account may have in a day, across every door (PinBudget).
        /// A security limit an administrator may make stricter, never looser: anything above
        /// <c>PinBudget.DailyLimit</c> counts as that (R-1002, Logan's decision). Advanced.
        /// </summary>
        public int PinGuessesPerDay { get; set; } = 20;

        // RequireMasterPinForCreation was here until 1.7.2. Creating a profile now needs the
        // main PIN like every other change, so it had nothing left to do. A saved
        // configuration that still has the element loads fine: the serializer skips it.

        /// <summary>
        /// A main account can't change its household (profiles, devices, sharing, switcher
        /// settings) until it has a PIN. Off by default. Without it, a main account with no
        /// PIN leaves all of that open to anyone who can reach its switcher.
        /// </summary>
        public bool RequireMainPin { get; set; }
        public List<ProfileMapping> Mappings { get; set; } = new List<ProfileMapping>();
        public List<KnownDevice> KnownDevices { get; set; } = new List<KnownDevice>();
        public List<BonfireGroup> BonfireGroups { get; set; } = new List<BonfireGroup>();

        /// <summary>Other accounts shown in a household's switcher (issue #39). See <see cref="LinkedAccount"/>.</summary>
        public List<LinkedAccount> LinkedAccounts { get; set; } = new List<LinkedAccount>();

        /// <summary>Devices signed into another household's account. See <see cref="CrossHouseholdEntry"/>.</summary>
        public List<CrossHouseholdEntry> CrossHouseholdEntries { get; set; } = new List<CrossHouseholdEntry>();
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
        /// A copy to read from outside <c>ConfigLock</c>, taken while holding it
        /// (<c>ProfilesBaseController.ReadConfigSnapshot</c>). Never save one.
        /// <para>
        /// For the code that runs on Jellyfin's own requests rather than on Bonfire's: the
        /// authentication provider, and the sign-in list, own-name and Quick Connect rewrites.
        /// Those walked <see cref="Mappings"/> and <see cref="KnownDevices"/> unlocked, and a
        /// television signing in is exactly when the session listener appends a device row,
        /// so a sign-in could meet "collection was modified" — an InvalidOperationException
        /// Jellyfin does not catch from a provider, which turns a refusal into a 500.
        /// </para>
        /// <para>
        /// The lists are copied, and so is each mapping, because a profile's
        /// <see cref="ProfileMapping.AllowedDeviceIds"/> is edited in place too. Everything
        /// else is a value or an immutable string and is shared.
        /// </para>
        /// </summary>
        public PluginConfiguration SnapshotForReading()
        {
            var copy = (PluginConfiguration)MemberwiseClone();
            copy.Mappings = Mappings?.Select(m => m.CopyForReading()).ToList() ?? new List<ProfileMapping>();
            copy.KnownDevices = KnownDevices?.ToList() ?? new List<KnownDevice>();
            copy.BonfireGroups = BonfireGroups?.ToList() ?? new List<BonfireGroup>();
            copy.LinkedAccounts = LinkedAccounts?.Select(l => l.Copy()).ToList() ?? new List<LinkedAccount>();
            copy.CrossHouseholdEntries = CrossHouseholdEntries?.Select(e => e.Copy()).ToList() ?? new List<CrossHouseholdEntry>();
            copy.UserProfileLimitOverrides = UserProfileLimitOverrides?.ToList() ?? new List<UserProfileLimitOverride>();
            copy.AvatarLibrary = AvatarLibrary?.ToList() ?? new List<AvatarLibraryItem>();
            copy.QuickConnectSharedDeviceNames = QuickConnectSharedDeviceNames?.ToList() ?? new List<string>();
            copy.Onboarding = Onboarding?.CopyForReading() ?? new OnboardingSettings();
            copy.Email = Email?.CopyForReading() ?? new EmailSettings();
            copy.AccountEmails = AccountEmails?.Select(a => a.Copy()).ToList() ?? new List<AccountEmail>();
            return copy;
        }

        /// <summary>
        /// When true, profile pictures can only be chosen from <see cref="AvatarLibrary"/>;
        /// uploading an arbitrary image is refused. For households that want a curated,
        /// consistent set rather than whatever each family member picks.
        /// </summary>
        public bool DisallowCustomAvatarUploads { get; set; } = false;

        /// <summary>
        /// Offers the thirty built-in <see cref="StarterAvatars"/> alongside
        /// <see cref="AvatarLibrary"/>. On by default, including for a configuration saved by
        /// an older version: the serializer leaves a missing element at this initializer.
        /// Turning it off removes them from the picker; profiles that already chose one keep
        /// their own copy.
        /// </summary>
        public bool EnableStarterAvatars { get; set; } = true;

        /// <summary>
        /// Television names an account outside every household has signed in on, so
        /// <see cref="SkipQuickConnectOnKnownDevices"/> leaves them alone. See
        /// <c>Auth.QuickConnectGate</c>: a Quick Connect request names only the device, and
        /// a model name such as "SHIELD Android TV" is shared by every set of that model on
        /// the server, so without this a household's television took Quick Connect away from
        /// everyone else's of the same make. Names only, Android TV only, capped.
        /// </summary>
        public List<string> QuickConnectSharedDeviceNames { get; set; } = new List<string>();

        /// <summary>
        /// How large profile pictures, library avatars and library artwork are stored. See
        /// <see cref="ImageQualities"/>. Applies to pictures saved from now on; pictures
        /// already stored keep the size they were saved at.
        /// </summary>
        public string ProfileImageQuality { get; set; } = ImageQualities.Standard;

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

        /// <summary>
        /// Lets a sub-profile be entered with its PIN from any Jellyfin client, including
        /// the native ones that never load the web client and so never see the switcher.
        /// <para>
        /// Off by default, and deliberately so. Turning it on binds sub-profiles to
        /// Bonfire's own authentication provider, which is a change to how the server
        /// authenticates — not something to acquire by upgrading.
        /// </para>
        /// </summary>
        public bool EnableClientPinLogin { get; set; }

        /// <summary>
        /// Lets a profile with no PIN, or one limited to particular devices, be signed in on
        /// another app from outside the home network (R-1334). Off by default: those two
        /// rest on a device id the client supplies, which anyone who has seen one can send,
        /// so they open only for a caller Jellyfin itself places on the local network. A
        /// profile with a PIN and no device list is unaffected: the PIN is the credential.
        /// <para>
        /// A setting rather than a fixed limit because households differ (a PIN-less
        /// profile on a TV the family also uses away from home is a choice some make), and
        /// under Advanced because a wrong value lets a stranger into a profile.
        /// </para>
        /// </summary>
        public bool ClientPinLoginFromAnywhere { get; set; }

        /// <summary>
        /// Adds this household's profiles to the user list a client paints its login screen
        /// from, on a device the household has already signed in on at least once.
        /// <para>
        /// Off by default, and separate from <see cref="EnableClientPinLogin"/> on purpose.
        /// This one edits <c>/Users/Public</c>, the endpoint every client on the server uses
        /// to sign in, so it carries a blast radius the PIN provider does not.
        /// </para>
        /// </summary>
        public bool EnableClientProfileList { get; set; }

        /// <summary>
        /// Send an app straight to its password field, instead of Quick Connect, once a
        /// household has signed in on that device.
        /// <para>
        /// Off by default, like the other two. It changes the response to
        /// <c>POST /QuickConnect/Initiate</c> for matching devices, which is a request every
        /// client on the server can make — see <see cref="Auth.QuickConnectGate"/> for why
        /// this cannot be decided per profile and why it keys on the device name.
        /// </para>
        /// </summary>
        public bool SkipQuickConnectOnKnownDevices { get; set; }

        /// <summary>
        /// Apps that get the profile picker served as their own home screen, by key from
        /// <see cref="Auth.ProxyClients.Catalogue"/>. Empty by default: nothing is proxied
        /// until an administrator ticks an app. See <see cref="Auth.ProxySwitcher"/>.
        /// </summary>
        public List<string> ProxyClients { get; set; } = new();

        /// <summary>
        /// Messages from the administrator, shown on the profile screen. Replaced as a whole
        /// on every change, never edited in place, so a reader can hold the list it was
        /// handed without the lock. See <see cref="Announcements"/>.
        /// </summary>
        public List<Announcement> Announcements { get; set; } = new();

        /// <summary>
        /// Closed for maintenance. Replaced as a whole when it changes, like
        /// <see cref="Announcements"/>, so the request gate can read it without the lock.
        /// See <see cref="Auth.MaintenanceGate"/>.
        /// </summary>
        public MaintenanceMode Maintenance { get; set; } = new();

        /// <summary>
        /// First-time setup for main accounts. Edited in place under ConfigLock; readers
        /// outside the lock take a snapshot (ReadConfigSnapshot). See <see cref="OnboardingSettings"/>.
        /// </summary>
        public OnboardingSettings Onboarding { get; set; } = new();

        /// <summary>Outgoing mail. See <see cref="EmailSettings"/>.</summary>
        public EmailSettings Email { get; set; } = new();

        /// <summary>Main accounts' confirmed addresses, one row each. See <see cref="AccountEmail"/>.</summary>
        public List<AccountEmail> AccountEmails { get; set; } = new();

        /// <summary>
        /// A main account may reset a forgotten PIN with a code sent to its confirmed
        /// address. Off by default, and refused until email works (<see cref="Features"/>).
        /// Resets the main account's PIN only: a profile's PIN is the main account's to reset.
        /// </summary>
        public bool EnablePinResetByEmail { get; set; }

        /// <summary>Default for <see cref="ProfileMapping.SwitcherLocation"/>. See <see cref="DefaultAskOnStartup"/>.</summary>
        public string DefaultSwitcherLocation { get; set; } = SwitcherLocations.Button;

        /// <summary>
        /// The theme every account sees until it picks its own: a built-in that is not hidden,
        /// or one of <see cref="CustomThemes"/>. See <see cref="Themes.IsKnown"/>.
        /// </summary>
        public string DefaultTheme { get; set; } = Themes.Bonfire;

        /// <summary>
        /// Themes an administrator made or imported (1.7.2). Each is one theme file, schema 1,
        /// kept as the text it was saved as. Validated by <see cref="ThemeCatalog.Validate"/>
        /// on the way in and again on the way out, so a configuration edited by hand cannot
        /// put an address or an unreadable colour in front of anyone.
        /// </summary>
        public List<CustomTheme> CustomThemes { get; set; } = new();

        /// <summary>
        /// Built-in themes taken out of every picker. Never Bonfire, which is what everything
        /// falls back to, and never the current <see cref="DefaultTheme"/>. A profile that chose
        /// one sees the default instead; its choice is kept for if the theme comes back.
        /// </summary>
        public List<string> HiddenThemes { get; set; } = new();

        /// <summary>
        /// Fonts an administrator uploaded: WOFF2 files kept in the plugin's data folder
        /// (<see cref="ThemeCatalog.CustomFontFolder"/>), named by their contents' hash.
        /// </summary>
        public List<CustomFont> CustomFonts { get; set; } = new();

        /// <summary>
        /// The logo image (1.7.2): a file in <see cref="ThemeLogo.Folder"/>, named by its
        /// contents' hash. Null for none. An image wins over <see cref="LogoText"/>.
        /// </summary>
        public string? LogoImage { get; set; }

        /// <summary>A line of text shown where a logo image would be, when there is none.</summary>
        public string? LogoText { get; set; }

        /// <summary>What a screen reader says for the logo image.</summary>
        public string? LogoAlt { get; set; }

        /// <summary>
        /// The time zone a household's viewing hours are read in until its main account picks
        /// one. Empty is the server's own zone. See <see cref="HouseholdTime"/>.
        /// </summary>
        public string DefaultTimeZone { get; set; } = string.Empty;

        /// <summary>
        /// Whether each profile may pick its own theme. Off means everyone sees
        /// <see cref="DefaultTheme"/>; choices already made are kept, not cleared, so turning
        /// this back on gives them back. See <see cref="Themes.Resolve"/>.
        /// </summary>
        public bool AllowProfileThemes { get; set; } = true;

        /// <summary>
        /// How the client script gets into Jellyfin’s index.html. One of the
        /// <see cref="IndexInjectionModes"/> values.
        /// <para>
        /// Every release up to 1.4.0 rewrote index.html on disk, which is the single cause
        /// behind issues #17, #11 and #3: it needs write permission to a directory the
        /// package manager owns, it has to be reapplied after every Jellyfin update, and when
        /// the file being served is not the file being patched there is no way to tell from
        /// the outside. From 1.4.1 the plugin can instead inject on the fly, serving
        /// index.html from the request pipeline and leaving the file alone.
        /// </para>
        /// </summary>
        public string IndexInjectionMode { get; set; } = IndexInjectionModes.Middleware;

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

        /// <summary>
        /// Every household that has signed in on this device, <see cref="MasterUserId"/>
        /// (the one seen last) among them (R-1050).
        /// <para>
        /// One owner per id did not fit a browser or a Roku that two households share: the
        /// first to sign in kept it for good, so the second could not restrict a profile to
        /// it, and its PINs were refused there once a PIN needed a household device
        /// (R-1344). Ownership is what grants things; <see cref="MasterUserId"/> only
        /// decides whose profiles a sign-in screen shows, since nobody is signed in to ask.
        /// </para>
        /// <para>
        /// Replaced, never edited in place: a configuration snapshot shares these objects
        /// and reads them without the lock.
        /// </para>
        /// </summary>
        public List<Guid> Owners { get; set; } = new List<Guid>();

        /// <summary>
        /// Other ids this device has been seen under, folded in by a merge (R-1049, C-1003).
        /// <para>
        /// A merge took the old id off every device list and deleted its record. The old id
        /// was not gone, it was the same machine at another address (the documented case is
        /// a LAN address and a domain), so its next request made a fresh record and a
        /// restricted profile was refused there. An alias is looked up as this record.
        /// </para>
        /// <para>Replaced, never edited in place, for the same reason as <see cref="Owners"/>.</para>
        /// </summary>
        public List<string> Aliases { get; set; } = new List<string>();

        /// <summary>Most aliases a record keeps; the oldest goes first.</summary>
        public const int MaxAliases = 16;

        /// <summary>True if <paramref name="id"/> is this device's id or one of its aliases.</summary>
        public bool Answers(string? id)
        {
            var wanted = id?.Trim();
            if (string.IsNullOrEmpty(wanted)) return false;
            if (string.Equals(DeviceId?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return Aliases?.Any(a => string.Equals(a?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) ?? false;
        }

        /// <summary>True if <paramref name="householdId"/> has signed in on this device.</summary>
        public bool OwnedBy(Guid householdId)
            => householdId != Guid.Empty && (MasterUserId == householdId || (Owners?.Contains(householdId) ?? false));

        /// <summary>
        /// True once an administrator has renamed this device by hand, which stops the name
        /// being overwritten by whatever the client sends on its next request.
        /// <para>
        /// Without this the rename is pointless: the device reports "Chrome" on every call,
        /// so a name typed in the picker survives until the next page load. Renaming is the
        /// only way to tell apart two records that are genuinely one computer reached at two
        /// addresses, so it has to stick.
        /// </para>
        /// </summary>
        public bool NameIsCustom { get; set; }
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
    /// Where the client script tag comes from. See
    /// <see cref="PluginConfiguration.IndexInjectionMode"/>.
    /// </summary>
    public static class IndexInjectionModes
    {
        /// <summary>Patch index.html on disk only — the behaviour of 1.4.0 and earlier.</summary>
        public const string File = "file";

        /// <summary>
        /// Inject on the fly only, never writing to index.html. What the middleware is for;
        /// this is the setting that actually tests it.
        /// </summary>
        public const string Middleware = "middleware";

        /// <summary>
        /// Both. Was the default while the middleware was new; kept as a belt-and-braces
        /// option. The file gets patched as it always did, and the middleware finds the tags
        /// already present and steps aside.
        /// </summary>
        public const string Both = "both";

        /// <summary>
        /// Falls back to <see cref="Middleware"/> for anything unrecognised, including the
        /// empty value on a configuration written before 1.4.1.
        /// <para>
        /// This was <see cref="Both"/> through the 1.4.x betas, so that upgrading changed
        /// nothing until someone opted in. Now that the middleware is confirmed working on
        /// real hardware, not writing to index.html is the behaviour worth defaulting to:
        /// it is the whole reason issues #17, #11 and #3 exist.
        /// </para>
        /// </summary>
        public static string Normalize(string? value)
        {
            var v = (value ?? string.Empty).Trim().ToLowerInvariant();
            return v == File || v == Both ? v : Middleware;
        }

        /// <summary>True when this mode should rewrite index.html on disk.</summary>
        public static bool PatchesFile(string? value) => Normalize(value) != Middleware;

        /// <summary>True when this mode should inject from the request pipeline.</summary>
        public static bool UsesMiddleware(string? value) => Normalize(value) != File;
    }

    /// <summary>
    /// The looks Bonfire's own screens can take: the switcher, the PIN pad and the profile
    /// forms. Never the rest of Jellyfin — a server's own theme is the administrator's, and
    /// re-skinning jellyfin-web would mean chasing its markup every release.
    /// <para>
    /// The ids are lowercase, stored in configuration, and are also the value of the
    /// <c>data-jpf-theme</c> attribute the generated CSS keys on, so the three lists — this
    /// one, <c>THEMES</c> in profiles.js and the files in Web/themes/ — must agree.
    /// tests/js/themes.js fails if they do not.
    /// </para>
    /// <para>
    /// Named for how each one looks, not for any service it resembles. The one exception is
    /// Jellyfin Dark and Jellyfin Light, which match the server's own web client: that is
    /// the project Bonfire runs inside, not another service. No other brand names, logos or
    /// brand typefaces: every font is one Bonfire serves under the OFL (fonts.json).
    /// </para>
    /// </summary>
    public static class Themes
    {
        /// <summary>Bonfire's own look: a fixed one since 1.7.2, not the server's accent.</summary>
        public const string Bonfire = "bonfire";
        public const string Crimson = "crimson";
        public const string Starlight = "starlight";
        public const string Harbor = "harbor";
        public const string Nightfall = "nightfall";
        public const string Glass = "glass";
        public const string Harvest = "harvest";
        public const string Snowfall = "snowfall";
        public const string Bloom = "bloom";
        public const string JellyfinDark = "jellyfin-dark";
        public const string JellyfinLight = "jellyfin-light";
        public const string HighContrast = "high-contrast";

        public static readonly string[] All =
        {
            Bonfire, Crimson, Starlight, Harbor, Nightfall, Glass, Harvest, Snowfall, Bloom,
            JellyfinDark, JellyfinLight, HighContrast,
        };

        /// <summary>True for one of <see cref="All"/>, in any case, hidden or not.</summary>
        public static bool IsBuiltIn(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && All.Contains(value.Trim().ToLowerInvariant());

        /// <summary>
        /// True for a theme this server offers: a built-in the administrator has not hidden
        /// (Bonfire always), or one of its <see cref="PluginConfiguration.CustomThemes"/>.
        /// The configuration is required, not optional: before 1.7.2 this took only the value,
        /// and a call left without it would quietly have refused every custom theme.
        /// </summary>
        public static bool IsKnown(string? value, PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var id = value.Trim().ToLowerInvariant();
            if (All.Contains(id)) return id == Bonfire || !config.HiddenThemes.Contains(id);
            return config.CustomThemes.Any(c => c.Id == id);
        }

        /// <summary>Maps arbitrary input to a theme this server offers, falling back to <see cref="Bonfire"/>.</summary>
        public static string Normalize(string? value, PluginConfiguration config) =>
            IsKnown(value, config) ? value!.Trim().ToLowerInvariant() : Bonfire;

        /// <summary>
        /// The theme one account sees: its own choice when the server lets accounts choose and
        /// it has made one the server still offers, otherwise the server's default. A stored
        /// choice is kept while choosing is turned off, or while its theme is hidden or
        /// deleted, so undoing either gives everybody their pick back.
        /// </summary>
        public static string Resolve(ProfileMapping? mapping, PluginConfiguration config) =>
            config.AllowProfileThemes && IsKnown(mapping?.Theme, config)
                ? Normalize(mapping!.Theme, config)
                : Normalize(config.DefaultTheme, config);

        /// <summary>Every theme the pickers offer, Bonfire first, built-ins before custom.</summary>
        public static List<string> Offered(PluginConfiguration config) =>
            All.Where(id => IsKnown(id, config)).Concat(config.CustomThemes.Select(c => c.Id)).ToList();

        /// <summary>
        /// Sets one profile's theme for the account that owns it: the rule behind
        /// <c>POST /plugins/profiles/theme</c>, kept here so it can be tested without a server.
        /// The caller holds <c>ConfigLock</c> and saves. Empty or null means "follow the
        /// server default". Nothing is changed unless the answer is <see cref="ThemeChange.Changed"/>.
        /// </summary>
        public static ThemeChange SetFor(PluginConfiguration config, System.Guid masterUserId, System.Guid profileId, string? theme, string? ownName = null)
        {
            if (!config.AllowProfileThemes) return ThemeChange.NotAllowed;
            if (!string.IsNullOrWhiteSpace(theme) && !IsKnown(theme, config)) return ThemeChange.UnknownTheme;

            // The master's own row, or one of its profiles. A linked Bonfire's profiles have
            // a different master, so they are someone else's to change.
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);

            // A main account has no row until something creates one, and first-time setup is
            // exactly when a new one picks its theme: it was refused as "not in household" and
            // got a 401 (R-1157). Only its own row is made here, the way UpdatePreferences
            // makes it, and only now that every refusal above has been passed.
            if (row == null && profileId == masterUserId)
            {
                row = new ProfileMapping
                {
                    ProfileUserId = masterUserId,
                    MasterUserId = masterUserId,
                    ProfileName = string.IsNullOrEmpty(ownName) ? "Master" : ownName,
                    IsHidden = false
                };
                config.Mappings.Add(row);
            }

            if (row == null || (row.MasterUserId != masterUserId && row.ProfileUserId != masterUserId))
                return ThemeChange.NotInHousehold;

            row.Theme = IsKnown(theme, config) ? Normalize(theme, config) : null;
            return ThemeChange.Changed;
        }
    }

    /// <summary>One administrator's theme: <see cref="PluginConfiguration.CustomThemes"/>.</summary>
    public class CustomTheme
    {
        /// <summary>The theme's id, the same as the <c>id</c> inside <see cref="Json"/>.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The theme file, as saved.</summary>
        public string Json { get; set; } = string.Empty;
    }

    /// <summary>One uploaded font: <see cref="PluginConfiguration.CustomFonts"/>.</summary>
    public class CustomFont
    {
        /// <summary>The family name read out of the file's own name table.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>The file in <see cref="ThemeCatalog.CustomFontFolder"/>: its hash and .woff2.</summary>
        public string File { get; set; } = string.Empty;

        /// <summary>The lightest and heaviest weight the file draws, read from it.</summary>
        public int WeightMin { get; set; } = 400;

        /// <summary>See <see cref="WeightMin"/>.</summary>
        public int WeightMax { get; set; } = 400;

        /// <summary>sans-serif or serif: what to draw with where the file cannot be used.</summary>
        public string Fallback { get; set; } = "sans-serif";
    }

    /// <summary>What <see cref="Themes.SetFor"/> did.</summary>
    public enum ThemeChange
    {
        Changed,
        NotAllowed,
        UnknownTheme,
        NotInHousehold
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

    /// <summary>
    /// The sizes pictures are stored at. Every resize happens in the browser, on a canvas —
    /// the plugin has no server-side image library and keeps it that way — so this decides
    /// what the browser is told to render, and the server enforces the matching byte limit.
    /// <para>
    /// Standard is what every picture was before this setting existed: 512 pixels square,
    /// which is sharp on a phone and soft on a large television or a high-density screen.
    /// The larger tiers cost disk and bandwidth, and every switcher on the server loads the
    /// full-size picture of whoever is shown large, so they are opt-in.
    /// </para>
    /// </summary>
    public static class ImageQualities
    {
        public const string Standard = "standard";
        public const string High = "high";
        public const string Maximum = "maximum";

        /// <summary>What one tier means.</summary>
        public sealed record Spec(int MasterSize, int ThumbSize, double JpegQuality, int MaxBytes);

        private static readonly Spec StandardSpec = new(512, 128, 0.85, 2 * 1024 * 1024);
        private static readonly Spec HighSpec = new(1024, 256, 0.9, 4 * 1024 * 1024);
        private static readonly Spec MaximumSpec = new(2048, 256, 0.92, 8 * 1024 * 1024);

        /// <summary>Whether this is one of the three names, exactly (case aside).</summary>
        public static bool IsKnown(string? quality)
            => string.Equals(quality, Standard, System.StringComparison.OrdinalIgnoreCase)
               || string.Equals(quality, High, System.StringComparison.OrdinalIgnoreCase)
               || string.Equals(quality, Maximum, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Maps arbitrary input to a known tier, falling back to <see cref="Standard"/>.</summary>
        public static string Normalize(string? quality)
        {
            if (string.Equals(quality, High, System.StringComparison.OrdinalIgnoreCase)) return High;
            if (string.Equals(quality, Maximum, System.StringComparison.OrdinalIgnoreCase)) return Maximum;
            return Standard;
        }

        public static Spec For(string? quality) => Normalize(quality) switch
        {
            High => HighSpec,
            Maximum => MaximumSpec,
            _ => StandardSpec
        };

        /// <summary>
        /// The largest picture the server accepts under any tier. A picture saved while a
        /// larger tier was set must stay readable after it is lowered, so reading never
        /// checks against the current tier — only saving does.
        /// </summary>
        public static int LargestMaxBytes => MaximumSpec.MaxBytes;
    }

    /// <summary>
    /// What a profile sees as a library's tile artwork.
    /// <para>
    /// Jellyfin builds one image per library and caches it on the folder
    /// (<c>CollectionFolderImageProvider</c>, a collage of up to eight random items). The
    /// query behind it has no user, so the artwork cannot respect who is asking: a profile
    /// restricted to children's films still gets a tile drawn from whatever else is in the
    /// library. That is GitHub issue #19, and it is why the substitution has to happen in
    /// the client — there is no per-user image for the server to hand out.
    /// </para>
    /// <para>
    /// One setting with three values rather than an override plus a separate "hide artwork"
    /// switch: two mechanisms would have to negotiate which wins, and this way choosing a
    /// picture and refusing one are the same decision.
    /// </para>
    /// </summary>
    public static class LibraryArtworkModes
    {
        /// <summary>Jellyfin's own artwork. The default, and what every profile had before 1.4.</summary>
        public const string Inherit = "inherit";

        /// <summary>A picture stored by this plugin for this profile and library.</summary>
        public const string Custom = "custom";

        /// <summary>No artwork at all — the tile falls back to its icon and name.</summary>
        public const string None = "none";

        /// <summary>Maps arbitrary input to a known mode, falling back to <see cref="Inherit"/>.</summary>
        public static string Normalize(string? mode)
        {
            if (string.Equals(mode, Custom, System.StringComparison.OrdinalIgnoreCase)) return Custom;
            if (string.Equals(mode, None, System.StringComparison.OrdinalIgnoreCase)) return None;
            return Inherit;
        }
    }

    /// <summary>
    /// One profile's artwork choice for one library. Absent means <see cref="LibraryArtworkModes.Inherit"/>,
    /// so a profile that has never been configured stores nothing at all.
    /// </summary>
    public class LibraryArtwork
    {
        public Guid LibraryId { get; set; }

        /// <summary>See <see cref="LibraryArtworkModes"/>.</summary>
        public string Mode { get; set; } = LibraryArtworkModes.Inherit;
    }

    /// <summary>
    /// The five limits a profile takes as the stricter of its own and its master's. Each
    /// field's "no limit" value — 0, false, CreateAndJoinGroups, empty — is also what an
    /// unset field means, so a profile with no limits of its own is simply the default.
    /// See <see cref="Auth.InheritedLimits"/>.
    /// </summary>
    public class ProfileLimits
    {
        public int RemoteClientBitrateLimit { get; set; }
        public int MaxActiveSessions { get; set; }
        public bool ForceRemoteSourceTranscoding { get; set; }

        // The two Jellyfin enums are stored as plain text, under the element names they always
        // had, and converted here where they are used (R-1127). They were stored as the enum
        // itself, so a Jellyfin that renamed or dropped a member made the whole configuration
        // fail to load, every household's, not this one limit. XmlSerializer refuses an
        // unknown name outright; text it reads, and a name this build doesn't know is kept as
        // written (the server that wrote it may know it) and ignored where it is used.
        // Files written before the change load unchanged: the same elements, the same text.
        // (tests/golden holds one, and tests/cs/goldenconfig reads it.)
        //
        // Jellyfin 10.11.5 and master have the same members (SyncPlayUserAccessType:
        // CreateAndJoinGroups, JoinGroups, None; UnratedItem: Movie, Trailer, Series, Music,
        // Book, LiveTvChannel, LiveTvProgram, ChannelContent, Other), so nothing is lost
        // today. This is for the release that changes them.

        /// <summary>The stored text of <see cref="SyncPlayAccess"/>.</summary>
        [System.Xml.Serialization.XmlElement("SyncPlayAccess")]
        [System.Text.Json.Serialization.JsonIgnore]
        public string SyncPlayAccessName { get; set; } = nameof(Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.CreateAndJoinGroups);

        /// <summary>The stored text of <see cref="BlockUnratedItems"/>.</summary>
        [System.Xml.Serialization.XmlArray("BlockUnratedItems")]
        [System.Xml.Serialization.XmlArrayItem("UnratedItem")]
        [System.Text.Json.Serialization.JsonIgnore]
        public List<string> BlockUnratedItemNames { get; set; } = new();

        /// <summary>A name this build doesn't know reads as the enum's first value, which is "no limit".</summary>
        [System.Xml.Serialization.XmlIgnore]
        public Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType SyncPlayAccess
        {
            get => Enum.TryParse<Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType>(SyncPlayAccessName, true, out var v) ? v : default;
            set => SyncPlayAccessName = value.ToString();
        }

        /// <summary>
        /// A new list on every read, with the names this build doesn't know left out: assign
        /// to change it, adding to the one returned changes nothing.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public List<Jellyfin.Data.Enums.UnratedItem> BlockUnratedItems
        {
            get => (BlockUnratedItemNames ?? new List<string>())
                .Select(n => Enum.TryParse<Jellyfin.Data.Enums.UnratedItem>(n, true, out var v) ? (Jellyfin.Data.Enums.UnratedItem?)v : null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            set => BlockUnratedItemNames = (value ?? new List<Jellyfin.Data.Enums.UnratedItem>()).Select(v => v.ToString()).ToList();
        }

        public ProfileLimits Copy()
        {
            var copy = (ProfileLimits)MemberwiseClone();
            copy.BlockUnratedItemNames = BlockUnratedItemNames?.ToList() ?? new();
            return copy;
        }
    }

    public class ProfileMapping
    {
        public Guid ProfileUserId { get; set; }
        public Guid MasterUserId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string? PinHash { get; set; }
        public string AvatarColor { get; set; } = "#00A4DC";
        /// <summary>
        /// Suppresses <see cref="AvatarColor"/> behind the profile picture, for pictures
        /// that are cut out rather than rectangular — a circular avatar over a coloured
        /// square shows the colour in the corners (issue #23).
        /// </summary>
        /// <remarks>
        /// The client sets this automatically when it detects transparent corners in an
        /// uploaded picture, and the edit form can override it either way. It has no
        /// effect when no picture is set: the colour is the entire background then.
        /// </remarks>
        public bool TransparentAvatar { get; set; }
        public bool IsHidden { get; set; } = true;

        /// <summary>
        /// Set on a profile that was a real account (made a profile by an administrator): deleting
        /// the profile removes it from the household and leaves the account, with its watch
        /// history, as an account of its own (R-1134). Not set on a profile Bonfire created, whose
        /// account exists only for it and is deleted with it. The person who makes the profile
        /// chooses; keep is the default.
        /// <para>
        /// Nothing recorded which profiles were made from accounts before this field, so those
        /// read false and are deleted with their account, as they always were. They can't be told
        /// apart from a created profile, and guessing wrong would leave an orphaned account behind
        /// for every profile ever created.
        /// </para>
        /// </summary>
        public bool KeepAccountOnDelete { get; set; }

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
        /// Main accounts only, set by their owner. False opens the main account from its own
        /// household's switcher without the PIN, on any network; the PIN then only guards
        /// managing the household (profiles, sharing, switcher settings).
        /// <para>
        /// True by default, so upgrading changes nothing. It never reaches across a Bonfire
        /// link: another household opening this account still needs the PIN, or the owner's
        /// <see cref="AllowHouseholdLanBypass"/>. Ignored on sub-profiles. A native app's
        /// sign-in is unaffected too: there the main account still types its PIN or password,
        /// because a blank password on a sign-in form would open it on any device.
        /// </para>
        /// </summary>
        public bool AskPinToOpen { get; set; } = true;

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

        /// <summary>
        /// The profile was saved with no library ticked, which the form promises means "every
        /// library your account does". <see cref="EnabledFolders"/> still holds the list as it
        /// was at that save, but it is not what is applied: the master's libraries are, as they
        /// are when the profile is opened, so a library added later reaches it too.
        /// <para>
        /// Default false, so a profile saved before this existed keeps its fixed list. Nothing
        /// in an old configuration can tell "ticked nothing" apart from "ticked everything",
        /// and guessing the first would widen the second.
        /// </para>
        /// </summary>
        public bool AllLibraries { get; set; } = false;

        /// <summary>
        /// Limits that belong to the profile itself rather than to its master — set on the
        /// profile's own account in Dashboard -> Users. Null until the profile is first opened
        /// on this version. See <see cref="Auth.InheritedLimits"/>.
        /// </summary>
        public ProfileLimits? OwnLimits { get; set; }

        /// <summary>
        /// What Bonfire last wrote for those limits, so a later change on the account can be
        /// told apart from Bonfire's own earlier write. See <see cref="Auth.InheritedLimits"/>.
        /// </summary>
        public ProfileLimits? LastAppliedLimits { get; set; }

        /// <summary>
        /// The library list Bonfire last wrote to the profile's account, so a change made on
        /// the account in Dashboard -> Users can be told apart from Bonfire's own write and
        /// from a reset (R-1042). Null on a configuration from before this existed: the first
        /// sign-in then behaves as it always did and records it. See
        /// <see cref="Auth.LibraryAccessReconciler.ReconcileWithLastApplied"/>.
        /// </summary>
        public List<Guid>? LastAppliedFolders { get; set; }

        /// <summary>
        /// The access schedule Bonfire last wrote to the profile's account, one entry per row
        /// as "day:start-end" (R-1345). The same job as <see cref="LastAppliedFolders"/>: an
        /// account that still matches it has not been touched, one that differs was changed
        /// in Dashboard -> Users. Null on a configuration from before this existed; the first
        /// sign-in then behaves as it always did (the main account's schedule) and records it.
        /// XmlSerializer loads an absent element as an empty list, so empty also means no
        /// record; "the main account had no schedule" is stored as ScheduleReconciler.NoneMarker.
        /// </summary>
        public List<string>? LastAppliedSchedules { get; set; }

        /// <summary>
        /// The schedule an administrator set on the profile's account, adopted at a sign-in
        /// (R-1345). It is kept here because Bonfire writes the account's schedule from the
        /// main account's at every sign-in; without this copy the next sign-in would see its
        /// own write and fall back to the main account's. Empty or null: the profile has none
        /// of its own and follows the main account. Never wider than the main account's.
        /// </summary>
        public List<string>? OwnSchedules { get; set; }

        /// <summary>A copy for <see cref="PluginConfiguration.SnapshotForReading"/>.</summary>
        public ProfileMapping CopyForReading()
        {
            var copy = (ProfileMapping)MemberwiseClone();
            copy.OwnLimits = OwnLimits?.Copy();
            copy.LastAppliedLimits = LastAppliedLimits?.Copy();
            copy.AllowedDeviceIds = AllowedDeviceIds?.ToList() ?? new List<string>();
            copy.EnabledFolders = EnabledFolders?.ToList();
            copy.LastAppliedFolders = LastAppliedFolders?.ToList();
            copy.LastAppliedSchedules = LastAppliedSchedules?.ToList();
            copy.OwnSchedules = OwnSchedules?.ToList();
            copy.BlockedTags = BlockedTags?.ToList();
            copy.AllowedTags = AllowedTags?.ToList();
            copy.LibraryArtwork = LibraryArtwork?.ToList() ?? new List<LibraryArtwork>();
            copy.Viewing = Viewing?.Copy();
            return copy;
        }

        /// <summary>
        /// When this profile may watch, and for how long (1.7.2). Null is no limits. Only ever
        /// set on a profile's row, never a main account's. See <see cref="ViewingRules"/>.
        /// </summary>
        public ViewingRules? Viewing { get; set; }

        /// <summary>
        /// The household's IANA time zone, read from the main account's row only. Null or empty
        /// follows <see cref="PluginConfiguration.DefaultTimeZone"/>. See <see cref="HouseholdTime"/>.
        /// </summary>
        public string? TimeZone { get; set; }

        public string? ProfileImage { get; set; }
        /// <summary>
        /// Per-library tile artwork for this profile. Only libraries the profile has been
        /// given a choice for appear here; anything absent inherits Jellyfin's own artwork.
        /// The picture itself lives on disk under <c>libraryart/</c>, named from the profile
        /// and library ids, so no separate image identifier has to be stored or kept in step.
        /// </summary>
        public List<LibraryArtwork> LibraryArtwork { get; set; } = new List<LibraryArtwork>();
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
        /// Null means never explicitly set, in which case the server-wide
        /// <see cref="PluginConfiguration.DefaultAskOnStartup"/> applies — unless
        /// <see cref="SwitcherMode"/> carries a real 1.3.1-beta choice. See
        /// <see cref="SwitcherLocations.Resolve"/>. That distinction is the whole reason this
        /// is nullable rather than defaulting to true.
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

        /// <summary>
        /// This profile's own theme, one of <see cref="Themes.All"/>. Null means it follows
        /// the server's <see cref="PluginConfiguration.DefaultTheme"/>.
        /// <para>
        /// Per profile, unlike <see cref="SwitcherLocation"/>, and read from this row rather
        /// than the master's: a child's profile can look different from its parent's. Set by
        /// whoever manages the profile, from its edit form.
        /// </para>
        /// </summary>
        public string? Theme { get; set; }
    }

    public class UserProfileLimitOverride
    {
        public Guid UserId { get; set; }
        public int MaxProfiles { get; set; }
    }

}

