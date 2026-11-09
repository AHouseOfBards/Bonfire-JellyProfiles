using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Queries;
using Jellyfin.Extensions.Json;
using Jellyfin.Profiles.Configuration;
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
        /// <param name="logger">
        /// Optional, and the reason this method is not silent. It declines for six different
        /// reasons and used to report none of them, which left "the profiles do not appear"
        /// with no way to tell whether the request arrived, whether the device was
        /// recognised, or whether the household was empty. Every return path now says which
        /// one it took.
        /// </param>
        public static IReadOnlyList<Guid> ResolveHousehold(
            string? authorizationHeader,
            string? embyAuthorizationHeader,
            IDeviceManager deviceManager,
            Configuration.PluginConfiguration? config,
            ILogger? logger = null)
        {
            var none = Array.Empty<Guid>();

            if (config?.Mappings == null || !config.EnableClientProfileList)
            {
                logger?.LogInformation(
                    "ProfilesPlugin: user list requested, but showing profiles on sign-in screens is off.");
                return none;
            }

            var deviceId = ProfilesBaseController.ParseAuthorizationParameter(
                authorizationHeader, embyAuthorizationHeader, "DeviceId");
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                logger?.LogInformation(
                    "ProfilesPlugin: user list requested with no DeviceId in the Authorization header, "
                    + "so the household cannot be identified. Header was: {Header}",
                    string.IsNullOrEmpty(authorizationHeader) ? "(absent)" : "present but carried no DeviceId");
                return none;
            }

            // Our own record first, Jellyfin's second.
            //
            // Jellyfin's Device rows are session artefacts: SessionManager.Logout calls
            // DeleteDevice, and Android TV's "Switch account" logs out BEFORE it opens the
            // picker — so on the client this feature exists for, the action that opens the
            // sign-in screen deletes the row we used to key on. 1.6.1.4 through 1.6.1.6
            // asked IDeviceManager alone and were answered, correctly and uselessly, that
            // nobody had ever signed in on the television somebody had just signed out of.
            //
            // DeviceRegistry survives that because it is ours. IDeviceManager is still
            // consulted afterwards: it covers the still-signed-in case, and it holds
            // devices from before this record existed.
            var seenUserId = DeviceRegistry.FindMaster(config, deviceId);
            var seenFrom = "Bonfire's own record";

            if (seenUserId == Guid.Empty)
            {
                var devices = deviceManager.GetDevices(new DeviceQuery { DeviceId = deviceId });
                var newest = devices?.Items?
                    .OrderByDescending(d => d.DateLastActivity)
                    .FirstOrDefault();
                seenUserId = newest?.UserId ?? Guid.Empty;
                seenFrom = "Jellyfin's device record";
            }

            if (seenUserId == Guid.Empty)
            {
                logger?.LogInformation(
                    "ProfilesPlugin: user list requested by device {DeviceId}, which nobody has signed in on yet. "
                    + "Sign in as the account that owns the profiles once on this device.",
                    deviceId);
                return none;
            }

            // Resolve to the household.
            //
            // A sub-profile carries a mapping row that names its master. A MASTER usually
            // does NOT: creating a profile writes a row for the profile only, and a row for
            // the master itself is written just once, lazily, the first time switcher
            // preferences are saved. Most households therefore have no row for the person
            // who owns the profiles.
            //
            // 1.6.1.4 required one and returned nothing without it, so signing in as
            // yourself on a new television — the very first step of the feature — left the
            // list empty. The sub-profiles pointing AT an account are proof enough that it
            // is a master, so use that instead of demanding a row that may never be written.
            Guid masterId;
            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == seenUserId);
            if (mapping != null)
            {
                masterId = mapping.MasterUserId;
            }
            else if (config.Mappings.Any(m => m.MasterUserId == seenUserId))
            {
                masterId = seenUserId;
            }
            else
            {
                // Nobody Bonfire knows. Every other account on the server lands here, which
                // is what keeps them out of a household they have nothing to do with.
                logger?.LogInformation(
                    "ProfilesPlugin: user list requested by device {DeviceId}, last used by {UserId} "
                    + "(per {Source}), which is not a Bonfire account. Nothing added.",
                    deviceId, seenUserId, seenFrom);
                return none;
            }

            // Only one household, deliberately. Two families sharing a television get the
            // most recent, and share by making a sub-profile or a Bonfire grouping instead —
            // otherwise a guest signing in on your set would leave your profiles on it.
            // A profile limited to particular devices is only offered on one of them. The
            // provider refuses it from anywhere else, so listing it here would be a door
            // that does not open — and would tell whoever holds the television which
            // profiles exist but are barred to it.
            var household = new List<Guid> { masterId };
            foreach (var m in config.Mappings.Where(m => m.MasterUserId == masterId && m.ProfileUserId != masterId))
            {
                var access = ProfilesBaseController.EvaluateDeviceRestriction(
                    m, deviceId, config.KnownDevices, config.Mappings);

                if (access == ProfilesBaseController.DeviceAccess.NotRestricted
                    || access == ProfilesBaseController.DeviceAccess.Allowed)
                {
                    household.Add(m.ProfileUserId);
                    continue;
                }

                // A profile that is missing from a sign-in screen for THIS reason is the
                // working-as-designed one, and it looks identical to a bug from the couch.
                logger?.LogInformation(
                    "ProfilesPlugin: profile {Profile} ({UserId}) is not offered on device {DeviceId}: "
                    + "limited to particular devices and {Reason}.",
                    m.ProfileName, m.ProfileUserId, deviceId, DescribeDenial(access));
            }

            // The sign-in that follows hashes the name typed with THIS id, and this id is
            // not on record when the sign-in is checked (the session it starts is recorded
            // afterwards). See DeviceRegistry.ResolveSignInDevice.
            DeviceRegistry.NoteSignInScreen(deviceId);

            logger?.LogInformation(
                "ProfilesPlugin: user list requested by device {DeviceId}; resolved via {Source} to the "
                + "household of {MasterId} with {Count} member(s).",
                deviceId, seenFrom, masterId, household.Count);

            return household;
        }


        /// <summary>
        /// What a device-restriction refusal means, in words the owner can act on. The
        /// evaluator is the one the switcher and the authentication provider use: one
        /// evaluator, three call sites.
        /// </summary>
        private static string DescribeDenial(ProfilesBaseController.DeviceAccess access)
            => access switch
            {
                ProfilesBaseController.DeviceAccess.DeniedNotOnList
                    => "this device is not on its list (DeniedNotOnList)",
                ProfilesBaseController.DeviceAccess.DeniedListIsStale
                    => "its list names no device Bonfire has on record, so the list may be stale (DeniedListIsStale)",
                ProfilesBaseController.DeviceAccess.DeniedNoDeviceId
                    => "the request carried no device id (DeniedNoDeviceId)",
                _ => access.ToString(),
            };

        /// <summary>
        /// Rewrites the captured <c>/Users/Public</c> body with the household appended, or
        /// returns null to leave it untouched.
        /// </summary>
        /// <param name="config">
        /// Supplies the name each household gave a profile. Passed rather than read from
        /// <c>Plugin.Instance</c> so a harness can drive this without a plugin, and null when
        /// there is nothing to rename.
        /// </param>
        public static byte[]? Inject(
            byte[] produced,
            string? responseContentType,
            IReadOnlyList<Guid> household,
            IUserManager userManager,
            string? remoteEndPoint,
            ILogger logger,
            Configuration.PluginConfiguration? config)
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

            if (root is not JsonArray array)
            {
                logger.LogWarning(
                    "ProfilesPlugin: /Users/Public was not a JSON array, so no profiles were added to it.");
                return null;
            }

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
            int failed = 0;
            foreach (var userId in household)
            {
                if (present.Contains(Normalize(userId.ToString("N")))) continue;

                try
                {
                    // Built by Jellyfin, not by hand. GetUserDto is what fills HasPassword
                    // from the authentication provider — the flag that decides whether the
                    // television shows a password box at all — and hand-rolling this shape
                    // is how a client ends up unable to parse its own login screen.
                    //
                    // Read twice before giving up. Each profile is its own database read,
                    // and a transient failure (SQLite busy while a library scan or another
                    // sign-in holds the file) used to cost exactly that one profile for the
                    // whole request. Clients show the users they have stored and only add
                    // this list on top, so the one profile the television had never signed
                    // in as simply vanished - at random, which is what it looked like.
                    Jellyfin.Database.Implementations.Entities.User? user = null;
                    MediaBrowser.Model.Dto.UserDto? dto = null;
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            user = userManager.GetUserById(userId);
                            dto = user == null ? null : userManager.GetUserDto(user, remoteEndPoint);
                            break;
                        }
                        catch (Exception ex) when (attempt < 2)
                        {
                            logger.LogInformation(
                                "ProfilesPlugin: reading profile {ProfileId} failed ({Reason}); trying once more.",
                                userId, ex.Message);
                        }
                    }

                    if (user == null)
                    {
                        // The mapping outlived the account (deleted in Jellyfin's dashboard).
                        failed++;
                        logger.LogWarning(
                            "ProfilesPlugin: profile {ProfileId} is in the household but Jellyfin has no such "
                            + "user any more, so it was left out of the sign-in list. Remove its mapping.",
                            userId);
                        continue;
                    }

                    var node = Reduced(JsonNode.Parse(JsonSerializer.Serialize(dto, serializerOptions)));
                    if (node == null) continue;

                    // Show the name the household typed, not the system username.
                    //
                    // A profile is created as `<master>_<name>` so two households on one
                    // server can both have a "kids", and that name is right everywhere it
                    // normally appears. A sign-in screen is the one place it is not: nobody
                    // calls their child "Bard_kids". Rewritten here rather than by renaming
                    // the account, because the system username is what keeps the households
                    // apart and what the switcher, the audit log and Jellyfin's dashboard
                    // all show.
                    //
                    // The provider translates it back on the way in - a client puts the
                    // displayed name straight into the username field.
                    var named = ProfileNameRewriter.FriendlyNameFor(config, userId);

                    // Not when the name is another account's username (R-1336). Jellyfin looks
                    // a typed name up as a username first, so a "Bob" tile would sign in as
                    // the real Bob: it could never open, and each wrong PIN would count
                    // against him. The profile keeps its system username, which is its own.
                    // This covers a name chosen before the create/rename check existed and an
                    // account made later. If the lookup itself fails the system username is
                    // shown: a plainer tile, never a lost one.
                    if (named != null)
                    {
                        try
                        {
                            var other = userManager.GetUserByName(named.Trim());
                            if (other != null && other.Id != userId) named = null;
                        }
                        catch (Exception)
                        {
                            named = null;
                        }
                    }

                    if (named != null)
                    {
                        // Whichever casing this response negotiated - missing the other one
                        // would show the raw username on exactly the clients that ask for it.
                        if (node["Name"] != null) node["Name"] = named;
                        else if (node["name"] != null) node["name"] = named;
                    }

                    array.Add(node);
                    added++;
                }
                catch (Exception ex)
                {
                    // One profile failing must not cost the whole login screen.
                    failed++;
                    logger.LogWarning(ex,
                        "ProfilesPlugin: could not add profile {ProfileId} to the user list.", userId);
                }
            }

            if (added == 0)
            {
                logger.LogInformation(
                    "ProfilesPlugin: nothing added to the user list: {Failed} of {Household} household "
                    + "member(s) could not be read, the rest were already present.",
                    failed, household.Count);
                return null;
            }

            logger.LogInformation(
                "ProfilesPlugin: added {Added} profile(s) to the sign-in user list, which now has {Total}"
                + " ({Failed} could not be read).",
                added, array.Count, failed);

            return Encoding.UTF8.GetBytes(array.ToJsonString());
        }

        /// <summary>
        /// What a sign-in screen draws a tile from, and nothing else. This endpoint is
        /// anonymous, and the full UserDto carries the account's Policy (administrator,
        /// folders, tags, schedules), its Configuration and when it last signed in: for each
        /// profile, to anyone who can reach the server (R-1043).
        ///
        /// <para>Checked against the clients, not remembered. jellyfin-androidtv's
        /// <c>UserDto.toPublicUser</c> (util/sdk/ModelExtensions.kt) reads id, name, serverId
        /// and primaryImage's tag and nothing else, but reading is not the bar: the whole list
        /// has to parse first. Swiftfin decodes it through the Swift SDK's <c>UserDto</c>, where
        /// every field is optional (I could not locate Swiftfin's own reads of it, so the
        /// password and auto-login flags are kept, which is what a sign-in screen uses to
        /// decide on a password box).</para>
        ///
        /// <para><b>Check the SDK version the apps ship, not master (#43).</b> On
        /// jellyfin-sdk-kotlin master every flag is optional, and that is what this list was
        /// first checked against. Wholphin and the Android TV 0.19 release use 1.7.1, where
        /// <c>id</c>, <c>hasPassword</c>, <c>hasConfiguredPassword</c> and
        /// <c>hasConfiguredEasyPassword</c> have no default. Leaving out the last one, a dead
        /// flag nothing draws, failed the entire response with "Field
        /// 'HasConfiguredEasyPassword' is required", so those apps had no sign-in screen at
        /// all in 1.7.2.</para>
        ///
        /// <para>Policy is left out, not emptied. The Kotlin SDK's <c>UserPolicy</c> has about
        /// thirty required fields (isAdministrator, isHidden, isDisabled, enableAllFolders,
        /// syncPlayAccess ...): a minimal object that has some of them fails the whole
        /// response, which empties the picker, the way 1.6.1.2's integer enums did. A missing
        /// policy is the nullable case both SDKs accept.</para>
        /// </summary>
        private static readonly HashSet<string> TileFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "Name", "Id", "ServerId", "HasPassword", "HasConfiguredPassword", "HasConfiguredEasyPassword",
            "PrimaryImageTag", "EnableAutoLogin",
        };

        private static JsonNode? Reduced(JsonNode? full)
        {
            if (full is not JsonObject obj) return full;

            var reduced = new JsonObject();
            foreach (var pair in obj)
            {
                if (TileFields.Contains(pair.Key)) reduced[pair.Key] = pair.Value?.DeepClone();
            }

            return reduced;
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
