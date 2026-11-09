using System;

namespace Jellyfin.Profiles.Models
{
    public class DeleteDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
    }

    /// <summary>Folds <see cref="FromDeviceId"/> into <see cref="IntoDeviceId"/>.</summary>
    public class MergeDevicesRequest
    {
        /// <summary>The record to drop. Its whitelist entries move to the other device.</summary>
        public string FromDeviceId { get; set; } = string.Empty;

        /// <summary>The record to keep.</summary>
        public string IntoDeviceId { get; set; } = string.Empty;
    }

    public class RenameDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
    }

    public class SetProfileLimitRequest
    {
        public Guid UserId { get; set; }
        public int? MaxProfiles { get; set; }
    }

    /// <summary>
    /// The six server-wide settings the plugin's settings page owns.
    /// <para>
    /// Every field is nullable and only the ones actually sent are applied. That is what
    /// makes this endpoint safe where the old path was not: the page used to GET the whole
    /// PluginConfiguration, change six fields on the copy, and PUT all of it back — so a
    /// profile created, a device seen, or an avatar uploaded between the GET and the PUT was
    /// silently overwritten with the state from before it happened. Sending only what the
    /// page owns means nothing else can be lost by saving it.
    /// </para>
    /// </summary>
    public class AdminSettingsRequest
    {
        public int? MaxProfilesPerUser { get; set; }
        /// <summary>Wrong PIN tries an account may have in a day, 1 to 20. Null leaves it alone.</summary>
        public int? PinGuessesPerDay { get; set; }
        public bool? RequireMainPin { get; set; }
        public bool? DisallowCustomAvatarUploads { get; set; }
        public bool? EnableStarterAvatars { get; set; }
        public bool? DefaultAskOnStartup { get; set; }
        public string? DefaultSwitcherLocation { get; set; }
        /// <summary>One of the theme ids. Null leaves it alone; anything unknown is refused.</summary>
        public string? DefaultTheme { get; set; }
        /// <summary>The time zone households read viewing hours in until they pick one. Empty is the server's own.</summary>
        public string? DefaultTimeZone { get; set; }
        public bool? AllowProfileThemes { get; set; }

        /// <summary>Themes with a special animation may use it. Null leaves it alone.</summary>
        public bool? RichThemeMotion { get; set; }
        public bool? EnableClientPinLogin { get; set; }
        public bool? EnableClientProfileList { get; set; }

        /// <summary>PIN-less and device-restricted profiles may sign in on other apps from outside the home network. Null leaves it alone.</summary>
        public bool? ClientPinLoginFromAnywhere { get; set; }

        public bool? SkipQuickConnectOnKnownDevices { get; set; }

        /// <summary>Keys from <c>ProxyClients.Catalogue</c>. Unknown keys are dropped. Null leaves it alone.</summary>
        public System.Collections.Generic.List<string>? ProxyClients { get; set; }

        /// <summary>See <c>ImageQualities</c>: standard, high or maximum. Null leaves it alone.</summary>
        public string? ProfileImageQuality { get; set; }

        /// <summary>First-time setup on or off. The first time it goes on, accounts that
        /// already exist are counted as having seen it.</summary>
        public bool? OnboardingEnabled { get; set; }

        /// <summary>The administrator has answered the new-install question about first-time
        /// setup. Null leaves it alone.</summary>
        public bool? OnboardingAsked { get; set; }

        /// <summary>Step ids to offer (<c>OnboardingSteps</c>). Unknown ids are refused.</summary>
        public System.Collections.Generic.List<string>? OnboardingSteps { get; set; }

        /// <summary>Plain text for the first screen. Empty for none.</summary>
        public string? OnboardingWelcomeText { get; set; }

        /// <summary>Main accounts may reset a forgotten PIN by email. Refused while email
        /// isn't working.</summary>
        public bool? EnablePinResetByEmail { get; set; }
    }

    /// <summary>Identifies one signed-in device belonging to one user.</summary>
    public class SignOutSessionRequest
    {
        public Guid UserId { get; set; }
        public string DeviceId { get; set; } = string.Empty;
    }
}
