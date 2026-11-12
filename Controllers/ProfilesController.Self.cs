using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// A profile's own name and picture (P2-713). A partial of <see cref="ProfilesController"/>,
    /// so tests/js/apidocs.js reads this route with the rest.
    /// <para>
    /// <c>update</c> is the main account's, with the main PIN, and rewrites the whole profile.
    /// This is the other way in: the person signed in as the profile, changing how it looks,
    /// and nothing it is allowed to do. The caller is the profile by construction. The
    /// request has no profile id to point elsewhere, and no field for a rating, PIN, library,
    /// limit, device or theme, so a body that carries one is read without it.
    /// </para>
    /// </summary>
    public partial class ProfilesController
    {
        /// <summary>
        /// Changes the signed-in profile's own name and picture. A main account is refused:
        /// its name is its username, and it has <c>update</c>.
        /// </summary>
        [HttpPost("me")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> UpdateOwnProfile([FromBody] UpdateOwnProfileRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var callerId = GetCurrentUserId();
            if (callerId == null) return Unauthorized();
            if (request == null) return BadRequest("Missing request body.");

            // Exactly a sub-profile: a row of its own, under a main account that is not itself.
            var mapping = FindMapping(callerId.Value);
            if (mapping == null || mapping.MasterUserId == Guid.Empty || mapping.MasterUserId == callerId.Value)
                return Unauthorized("Only a profile can change its own name and picture.");

            var user = _userManager.GetUserById(callerId.Value);
            var mainUser = _userManager.GetUserById(mapping.MasterUserId);
            if (user == null || mainUser == null) return NotFound("Profile not found.");

            // Everything that can be refused is checked before anything is written, so a
            // refusal leaves the profile as it was (the picture used to be written first, ST-11).
            string? newName = null;
            string? newUsername = null;
            if (request.ProfileName != null)
            {
                var nameError = ValidateProfileName(request.ProfileName);
                if (nameError != null) return BadRequest(nameError);
                newName = request.ProfileName.Trim();
                newUsername = $"{mainUser.Username}_{newName.Replace(" ", "")}";
            }

            // The row's existence and the free-name checks are read under the lock, before the
            // rename or the picture is written: outside it, a delete or a rename by someone
            // else between the check and the write left this call writing over their result.
            lock (ConfigLock)
            {
                var current = config.Mappings.FirstOrDefault(m => m.ProfileUserId == callerId.Value);
                if (current == null) return NotFound("Profile not found.");
                if (newName != null)
                {
                    if (!string.Equals(current.ProfileName, newName, StringComparison.OrdinalIgnoreCase))
                    {
                        var takenError = ValidateProfileNameFree(_userManager, newName, callerId.Value);
                        if (takenError != null) return BadRequest(takenError);
                    }

                    if (!string.Equals(user.Username, newUsername, StringComparison.OrdinalIgnoreCase)
                        && GetAllUsers().Any(u => u.Id != user.Id && string.Equals(u.Username, newUsername, StringComparison.OrdinalIgnoreCase)))
                    {
                        return BadRequest("A profile with this name already exists.");
                    }
                }
            }

            string? newImageUrl = null;
            var imageChanged = false;
            if (!string.IsNullOrEmpty(request.AvatarLibraryId))
            {
                var copied = CopyLibraryAvatarToProfile(callerId.Value, request.AvatarLibraryId);
                if (copied.Error != null) return BadRequest(copied.Error);
                newImageUrl = copied.Url;
                imageChanged = true;
            }
            else if (request.ProfileImage != null)
            {
                var saved = SaveProfileImage(callerId.Value, request.ProfileImage, request.ProfileImageThumb, mapping.ProfileImage);
                if (saved.Error != null) return BadRequest(saved.Error);
                newImageUrl = saved.Url;
                imageChanged = true;
            }

            if (newUsername != null && !string.Equals(user.Username, newUsername, StringComparison.OrdinalIgnoreCase))
            {
                user.Username = newUsername;
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }

            string? renamedTo = null;
            var householdOf = Guid.Empty;
            lock (ConfigLock)
            {
                var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == callerId.Value);
                if (row == null) return NotFound("Profile not found.");
                householdOf = row.MasterUserId;
                if (newName != null)
                {
                    if (!string.Equals(row.ProfileName, newName, StringComparison.Ordinal)) renamedTo = newName;
                    row.ProfileName = newName;
                }
                if (request.AvatarColor != null) row.AvatarColor = SanitizeAvatarColor(request.AvatarColor);
                if (request.TransparentAvatar.HasValue) row.TransparentAvatar = request.TransparentAvatar.Value;
                if (imageChanged) row.ProfileImage = newImageUrl;
                Plugin.Instance?.SaveConfiguration();
            }

            if (renamedTo != null) BonfireCampground.RaiseRenamed(householdOf, callerId.Value, renamedTo);

            return Ok();
        }
    }
}
