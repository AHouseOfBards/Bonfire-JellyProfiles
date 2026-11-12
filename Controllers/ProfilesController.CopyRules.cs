using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Auth;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Copy one profile's rules onto another in the same household. A partial of
    /// <see cref="ProfilesController"/>, so tests/js/apidocs.js reads the route with the rest.
    /// </summary>
    public partial class ProfilesController
    {
        /// <summary>
        /// Gives the target profile the source's rating limit, tags, libraries and viewing limits.
        /// Its devices, name, picture and PIN are not touched.
        /// <para>
        /// A route that changes what a profile can watch, so it is held to what <c>/update</c> is
        /// held to: the main account only, with the main PIN, and both profiles must be this
        /// household's. A profile that is not (another household's, the main account's own, a
        /// standalone user's, one that does not exist) is refused with one answer, so the route
        /// cannot be used to find out which. The values go through the same helpers as an edit,
        /// so the target ends as if its owner had set them on the form: never above the main
        /// account's rating, never a library the main account cannot reach, and a library
        /// another plugin manages is neither copied nor changed.</para>
        /// </summary>
        [HttpPost("copy-rules")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> CopyRules([FromBody] CopyRulesRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = FindMapping(currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;
            if (currentUserId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal)) return pinRefusal!;

            // Whose profiles these are is read once, here, and read again under the lock before
            // anything is stored. A row that is not a sub-profile of this caller, for whatever
            // reason, is the same answer.
            if (request.SourceProfileId == request.TargetProfileId) return BadRequest("Pick two different profiles.");
            ProfileMapping? source;
            ProfileMapping? target;
            lock (ConfigLock)
            {
                source = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.SourceProfileId);
                target = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.TargetProfileId);
                if (!IsSubProfileOf(source, masterUserId) || !IsSubProfileOf(target, masterUserId))
                {
                    return Unauthorized(OtherHouseholdMessage);
                }

                // A snapshot: everything below reads these while Jellyfin is awaited, outside the lock.
                source = source!.CopyForReading();
            }

            var sourceUser = _userManager.GetUserById(request.SourceProfileId);
            var targetUser = _userManager.GetUserById(request.TargetProfileId);
            var masterUser = _userManager.GetUserById(masterUserId);
            if (sourceUser == null || targetUser == null || masterUser == null) return NotFound("Target user not found.");

            var masterPolicy = _userManager.GetUserDto(masterUser, string.Empty).Policy;
            var sourcePolicy = _userManager.GetUserDto(sourceUser, string.Empty).Policy;

            // What the source has, as the form would have sent it.
            var blockedTags = NormalizeTags(source.BlockedTags);
            var allowedTags = NormalizeTags(source.AllowedTags);
            var allowedTagsError = ValidateAllowedTags(masterPolicy, allowedTags);
            if (allowedTagsError != null) return BadRequest(allowedTagsError);

            var viewing = source.Viewing != null && (source.Viewing.IsLimited || source.Viewing.Windows.Count > 0)
                ? source.Viewing.Copy()
                : null;
            // Limits with no main PIN to switch them off again are refused, as /limits/{id} refuses them.
            if (viewing != null && viewing.IsLimited && !MainHasPin(masterUserId, config)) return BadRequest(MainPinRequiredMessage);

            // The rating as the source holds it. A source that follows the main account has no
            // rating of its own, and the target follows too: copying what the source's account
            // happens to carry would pin the target to today's value (r2-11-03). A source with
            // no record at all (its rating was set in Dashboard -> Users) has only the account
            // to copy from.
            var sourceOwn = source.OwnRating;
            bool followsMain = sourceOwn == null && source.LastAppliedRating != null;
            int? requestedRating;
            int? requestedSubRating;
            if (sourceOwn != null)
            {
                requestedRating = sourceOwn.Score;
                requestedSubRating = sourceOwn.SubScore;
            }
            else if (followsMain)
            {
                requestedRating = masterPolicy.MaxParentalRating;
                requestedSubRating = masterPolicy.MaxParentalSubRating;
            }
            else
            {
                requestedRating = sourcePolicy.MaxParentalRating;
                requestedSubRating = sourcePolicy.MaxParentalSubRating;
            }
            var allLibraries = source.AllLibraries
                || (source.EnabledFolders == null && sourcePolicy.EnableAllFolders);
            var listedFolders = source.EnabledFolders ?? (sourcePolicy.EnabledFolders ?? Array.Empty<Guid>()).ToList();

            // The same gate as an edit: a policy sync must not land between the read and the write.
            using var syncGate = await ProfilePolicySync.HoldAsync().ConfigureAwait(false);

            var targetPolicy = _userManager.GetUserDto(targetUser, string.Empty).Policy;
            ProfileRating? ratingChosen = null;
            if (sourceOwn != null || followsMain)
            {
                targetPolicy.MaxParentalRating = requestedRating;
                targetPolicy.MaxParentalSubRating = requestedSubRating;
                ratingChosen = sourceOwn?.Copy();
            }
            else if (requestedRating != targetPolicy.MaxParentalRating)
            {
                targetPolicy.MaxParentalRating = requestedRating;
                targetPolicy.MaxParentalSubRating = requestedSubRating;
                ratingChosen = new ProfileRating { Score = requestedRating, SubScore = requestedSubRating };
            }

            (targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating) = ClampRating(
                targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating,
                masterPolicy.MaxParentalRating, masterPolicy.MaxParentalSubRating);
            var ratingWritten = new ProfileRating { Score = targetPolicy.MaxParentalRating, SubScore = targetPolicy.MaxParentalSubRating };

            var (resolvedBlocked, resolvedAllowed) = ResolveTagPolicy(masterPolicy, blockedTags, allowedTags);
            targetPolicy.BlockedTags = resolvedBlocked;
            targetPolicy.AllowedTags = resolvedAllowed;

            var validatedFolders = allLibraries
                ? ApplyInheritedFolders(_libraryManager, masterPolicy, targetPolicy, keepOwned: true)
                : ApplyListedFolders(_libraryManager, masterPolicy, listedFolders, targetPolicy, keepOwned: true);

            // Checked again, with the gate held and just before the account is written: a
            // profile deleted or moved since the first look must not be changed (r2-11-04).
            // The check under the lock below stays, for what happens during the write.
            lock (ConfigLock)
            {
                var again = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.TargetProfileId);
                if (!IsSubProfileOf(again, masterUserId)) return Unauthorized(OtherHouseholdMessage);
            }

            await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);

            lock (ConfigLock)
            {
                var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.TargetProfileId);
                if (!IsSubProfileOf(row, masterUserId)) return Unauthorized(OtherHouseholdMessage);

                if (ratingChosen != null || followsMain)
                {
                    // Null for a target that follows the main account, as the source does.
                    row!.OwnRating = ratingChosen;
                    row.LastAppliedRating = ratingWritten;
                }
                else if (row!.LastAppliedRating != null)
                {
                    row.LastAppliedRating = ratingWritten;
                }

                row.EnabledFolders = validatedFolders;
                row.AllLibraries = allLibraries;
                row.LastAppliedFolders = allLibraries ? null : validatedFolders.ToList();
                row.BlockedTags = blockedTags;
                row.AllowedTags = allowedTags;
                row.Viewing = viewing;

                Plugin.Instance?.SaveConfiguration();
            }

            ViewingTracker.Invalidate();
            if (viewing == null || !viewing.IsLimited) ViewingTracker.ForgetUnlessRecent(request.TargetProfileId);
            LogActivity("Rules copied from " + source.ProfileName + " to " + targetUser.Username, "CopyRules", request.TargetProfileId);
            return Ok();
        }

        /// <summary>A profile row that belongs to <paramref name="mainId"/> and is not the main account itself.</summary>
        private static bool IsSubProfileOf(ProfileMapping? row, Guid mainId) =>
            row != null && row.MasterUserId == mainId && row.ProfileUserId != mainId;
    }
}
