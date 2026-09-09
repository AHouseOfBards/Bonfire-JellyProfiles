using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Queries;
using Jellyfin.Extensions.Json;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Adds a household's profiles to the user list a client paints its login screen from.
    ///
    /// <para>
    /// Sub-profiles are created hidden, so <c>GET /Users/Public</c> never returns them —
    /// and on the many servers that hide every user it returns nothing at all. That empty
    /// array is the right starting point: this <b>adds</b> to it rather than filtering it,
    /// so no other account on the server needs its policy changed and none can ever appear
    /// here by accident.
    /// </para>
    ///
    /// <para><b>Which household.</b> The request is anonymous — that is what a login screen
    /// is — so Jellyfin builds no claims from it and there is no signed-in user to key on.
    /// But the client still sends its <c>Authorization</c> header, and this middleware reads
    /// raw requests, so the <c>DeviceId</c> is there. Jellyfin's own <c>Device</c> records
    /// say who has signed in on it; the most recent one names the household.</para>
    ///
    /// <para><b>Most recent, resolved to a household.</b> Taking the newest device row's user
    /// literally would break the first time somebody switched into a sub-profile, because a
    /// sub-profile has no sub-profiles of its own and the list would empty. The user is
    /// resolved to its master first, so a household stays current on a television for as
    /// long as anyone in it is watching.</para>
    ///
    /// <para><b>This is tidiness, not a boundary.</b> A device id is client-supplied and can
    /// be forged, which would reveal one household's profile <i>names</i> against a baseline
    /// of nothing. Entry still needs the PIN, and library and rating enforcement is
    /// server-side regardless.</para>
    /// </summary>
    public static class PublicUserInjector
    {
        /// <summary>
        /// The users to add for this request, or an empty list to leave the response alone.
        /// Separated from the JSON work so a harness can drive the decision without a body.
        /// </summary>
        public static IReadOnlyList<Guid> ResolveHousehold(
            string? authorizationHeader,
            string? embyAuthorizationHeader,
            IDeviceManager deviceManager,
            Configuration.PluginConfiguration? config)
        {
            var none = Array.Empty<Guid>();

            if (config?.Mappings == null || !config.EnableClientProfileList) return none;

            var deviceId = ProfilesBaseController.ParseAuthorizationParameter(
                authorizationHeader, embyAuthorizationHeader, "DeviceId");
            if (string.IsNullOrWhiteSpace(deviceId)) return none;

            // Everyone who has ever authenticated on this device, newest first. A device
            // nobody has signed in on yields nothing, which is the correct answer for a
            // television that has never seen this household.
            var devices = deviceManager.GetDevices(new DeviceQuery { DeviceId = deviceId });
            var newest = devices?.Items?
                .OrderByDescending(d => d.DateLastActivity)
                .FirstOrDefault();
            if (newest == null || newest.UserId == Guid.Empty) return none;

            // Resolve to the household. A sub-profile maps to its master; a master maps to
            // itself; an account Bonfire does not know maps to nothing.
            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == newest.UserId);
            if (mapping == null) return none;
            var masterId = mapping.MasterUserId;

            // Only one household, deliberately. Two families sharing a television get the
            // most recent, and share by making a sub-profile or a Bonfire grouping instead —
            // otherwise a guest signing in on your set would leave your profiles on it.
            var household = new List<Guid> { masterId };
            household.AddRange(config.Mappings
                .Where(m => m.MasterUserId == masterId && m.ProfileUserId != masterId)
                .Select(m => m.ProfileUserId));

            return household;
        }

        /// <summary>
        /// Rewrites the captured <c>/Users/Public</c> body with the household appended, or
        /// returns null to leave it untouched.
        /// </summary>
        public static byte[]? Inject(
            byte[] produced,
            string? responseContentType,
            IReadOnlyList<Guid> household,
            IUserManager userManager,
            string? remoteEndPoint,
            ILogger logger)
        {
            if (household.Count == 0) return null;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(Encoding.UTF8.GetString(produced));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "ProfilesPlugin: /Users/Public was not JSON we could read; left unchanged.");
                return null;
            }

            if (root is not JsonArray array) return null;

            // JELLYFIN'S OWN SETTINGS, NOT OURS. 1.6.1.2 built a JsonSerializerOptions here
            // by hand — naming policy and ignore-nulls, which looked like the whole job —
            // and it crashed the Android TV app the moment a profile was actually added.
            // JsonDefaults carries nine converters, and two of them are load-bearing for
            // this DTO:
            //
            //   JsonStringEnumConverter  UserPolicy.SyncPlayAccess, BlockUnratedItems and
            //                            UserConfiguration.SubtitleMode are enums. Without
            //                            it they serialize as integers, where every client
            //                            on the server expects strings.
            //   JsonGuidConverter        Jellyfin writes Guids as "N" — dashless. The
            //                            default writes them with dashes, so our entries
            //                            disagreed with every other id the client had seen.
            //
            // Getting the DTO from Jellyfin and then serializing it ourselves was the right
            // lesson applied one level too shallow. Take both from Jellyfin.
            //
            // The casing is still ours to choose, because Jellyfin serves this endpoint in
            // both by content negotiation — application/json; profile="CamelCase" or
            // "PascalCase" — so it is read off the response we are editing rather than
            // assumed. JsonDefaults exposes one prepared set for each.
            var serializerOptions = UseCamelCase(responseContentType)
                ? JsonDefaults.CamelCaseOptions
                : JsonDefaults.PascalCaseOptions;

            // Whatever Jellyfin already returned stays exactly as it is — on a server that
            // shows its users publicly the master is likely already in here.
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array)
            {
                var id = item?["Id"]?.GetValue<string>() ?? item?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) present.Add(Normalize(id));
            }

            int added = 0;
            foreach (var userId in household)
            {
                if (present.Contains(Normalize(userId.ToString("N")))) continue;

                try
                {
                    var user = userManager.GetUserById(userId);
                    if (user == null) continue;

                    // Built by Jellyfin, not by hand. GetUserDto is what fills HasPassword
                    // from the authentication provider — the flag that decides whether the
                    // television shows a password box at all — and hand-rolling this shape
                    // is how a client ends up unable to parse its own login screen.
                    var dto = userManager.GetUserDto(user, remoteEndPoint);
                    var node = JsonNode.Parse(JsonSerializer.Serialize(dto, serializerOptions));
                    if (node == null) continue;

                    array.Add(node);
                    added++;
                }
                catch (Exception ex)
                {
                    // One profile failing must not cost the whole login screen.
                    logger.LogWarning(ex,
                        "ProfilesPlugin: could not add profile {ProfileId} to the user list.", userId);
                }
            }

            if (added == 0) return null;

            return Encoding.UTF8.GetBytes(array.ToJsonString());
        }

        /// <summary>
        /// True when the response was negotiated as camelCase. Jellyfin's default is
        /// PascalCase, so anything unrecognised is treated as that.
        /// </summary>
        private static bool UseCamelCase(string? contentType)
            => !string.IsNullOrEmpty(contentType)
               && contentType.Contains("CamelCase", StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string guid)
            => guid.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
