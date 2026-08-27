using System;

namespace Jellyfin.Profiles.Models
{
    public class DeleteDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
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
        public bool? RequireMasterPinForCreation { get; set; }
        public bool? DisallowCustomAvatarUploads { get; set; }
        public bool? DefaultAskOnStartup { get; set; }
        public string? DefaultSwitcherLocation { get; set; }
        public string? IndexInjectionMode { get; set; }
    }
}
