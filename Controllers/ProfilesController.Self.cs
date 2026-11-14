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
    /// This is the other way in: the person signed in as the profile changing how it looks, and
    /// nothing it may do. The caller is the profile by construction: the request has no profile
    /// id to point elsewhere and no field for a rating, PIN, library, limit, device or theme, so a
    /// body carrying one is read without it.
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

            // Everything that can be refused is checked before anything is written, so a refusal leaves
            // the profile as it was (the picture used to be written first, ST-11).
            string? newName = null;
            string? newUsername = null;
            if (request.ProfileName != null)
            {
                var nameError = ValidateProfileName(request.ProfileName);
                if (nameError != null) return BadRequest(nameError);
                newName = request.ProfileName.Trim();
                newUsername = $"{mainUser.Username}_{newName.Replace(" ", "")}";
            }

            // The row is read again under the lock, for its current name and picture. The account
            // lookups stay outside it: they are database reads, and the lock is the whole plugin's
            // (r2-11-05). This narrows the window for a delete or rename by someone else; it does not
            // close it, so the write below looks again and takes back a picture it wrote for a profile
            // that is gone (r3-03-01).
            string? currentName;
            string? currentImage;
            lock (ConfigLock)
            {
                var current = config.Mappings.FirstOrDefault(m => m.ProfileUserId == callerId.Value);
                if (current == null) return NotFound("Profile not found.");
                currentName = current.ProfileName;
                currentImage = current.ProfileImage;
            }
            if (newName != null)
            {
                if (!string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase))
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

            string? newImageUrl = null;
            var imageChanged = false;
            // As in UpdateProfile: a file written under this profile's name by this request.
            var imageWritten = false;
            if (!string.IsNullOrEmpty(request.AvatarLibraryId))
            {
                var copied = CopyLibraryAvatarToProfile(callerId.Value, request.AvatarLibraryId);
                if (copied.Error != null) return BadRequest(copied.Error);
                newImageUrl = copied.Url;
                imageChanged = true;
                imageWritten = true;
            }
            else if (request.ProfileImage != null)
            {
                var saved = SaveProfileImage(callerId.Value, request.ProfileImage, request.ProfileImageThumb, currentImage);
                if (saved.Error != null) return BadRequest(saved.Error);
                newImageUrl = saved.Url;
                imageChanged = true;
                imageWritten = request.ProfileImage.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && newImageUrl != null;
            }

            if (newUsername != null && !string.Equals(user.Username, newUsername, StringComparison.OrdinalIgnoreCase))
            {
                user.Username = newUsername;
                try
                {
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }
                catch
                {
                    DiscardNewImage(callerId.Value, imageWritten);
                    throw;
                }
            }

            string? renamedTo = null;
            var householdOf = Guid.Empty;
            var gone = false;
            lock (ConfigLock)
            {
                var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == callerId.Value);
                gone = row == null;
                if (row != null)
                {
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
            }
            if (gone)
            {
                // Deleted while this ran. Its picture went with it, so the one just written would be a
                // file nothing points to.
                DiscardNewImage(callerId.Value, imageWritten);
                return NotFound("Profile not found.");
            }

            if (renamedTo != null) BonfireCampground.RaiseRenamed(householdOf, callerId.Value, renamedTo);

            return Ok();
        }
    }
}
