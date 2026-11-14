using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// A device that opened another household's account or profile through a Bonfire or a
    /// linked account (R-1302).
    ///
    /// <para><b>Why it is recorded.</b> /switch hands back an ordinary Jellyfin token, and Jellyfin's
    /// tokens do not expire. Kick, leave, delete-group and removing a link took the door away and
    /// left everyone already through it inside: a removed member kept the other household's profile
    /// on their television, and a changed PIN made no difference. With LAN switching on, one visit
    /// to the house was a permanent token that worked from anywhere.</para>
    ///
    /// <para><b>Why not RevokeUserTokens.</b> It signs the account out everywhere, its own
    /// household's televisions included. Only the devices that came in from outside are signed out,
    /// and this remembers which they were.</para>
    ///
    /// <para><b>When an entry goes.</b> After every change that can take a door away, each entry is
    /// asked what /switch asks (<c>EvaluateSwitch</c>); one that would now be refused is signed out
    /// and forgotten.</para>
    /// </summary>
    public class CrossHouseholdEntry
    {
        /// <summary>The main account of the household that came in.</summary>
        public Guid EnteredBy { get; set; }

        /// <summary>The account or profile it opened.</summary>
        public Guid ProfileUserId { get; set; }

        /// <summary>The device the token was issued to.</summary>
        public string DeviceId { get; set; } = string.Empty;

        public DateTime EnteredUtc { get; set; }

        /// <summary>
        /// Came in without its PIN, on the owner's home-network consent. Signed out too when
        /// that consent is withdrawn, since nothing else was ever proved.
        /// </summary>
        public bool WithoutPin { get; set; }

        public CrossHouseholdEntry Copy() => (CrossHouseholdEntry)MemberwiseClone();
    }

    /// <summary>The list's rules, apart from the routes.</summary>
    public static class CrossHouseholdEntries
    {
        /// <summary>
        /// Oldest dropped past this. An entry is one (device, account) pair that crossed a
        /// household line; a server with more than this many is far outside what a Bonfire
        /// is for, and the file is rewritten whole on every save.
        /// </summary>
        public const int Max = 500;

        /// <summary>
        /// Notes an entry; one per (device, account), the newest kept. The caller holds
        /// ConfigLock and saves. False if the device id is empty, which /switch never sends.
        /// </summary>
        public static bool Record(PluginConfiguration config, Guid enteredBy, Guid profileUserId, string deviceId, bool withoutPin, DateTime nowUtc, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(deviceId) || profileUserId == Guid.Empty) return false;
            config.CrossHouseholdEntries ??= new List<CrossHouseholdEntry>();
            config.CrossHouseholdEntries.RemoveAll(e => e.ProfileUserId == profileUserId
                && string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal));
            config.CrossHouseholdEntries.Add(new CrossHouseholdEntry
            {
                EnteredBy = enteredBy,
                ProfileUserId = profileUserId,
                DeviceId = deviceId,
                EnteredUtc = nowUtc,
                WithoutPin = withoutPin,
            });
            // The dropped entry is the record that a device came in from another household, so
            // it can no longer be signed out when that door is taken away. Said once per drop.
            while (config.CrossHouseholdEntries.Count > Max)
            {
                var dropped = config.CrossHouseholdEntries[0];
                config.CrossHouseholdEntries.RemoveAt(0);
                logger?.LogWarning(
                    "ProfilesPlugin: More than {Max} cross-household sign-ins are recorded, so the oldest was dropped: device {DeviceId} in profile {ProfileId}. That device can no longer be signed out when its access is removed.",
                    Max, dropped.DeviceId, dropped.ProfileUserId);
            }
            return true;
        }
    }
}
