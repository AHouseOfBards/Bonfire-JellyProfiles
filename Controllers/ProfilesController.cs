using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Net;
using System.Net;

namespace Jellyfin.Profiles.Controllers
{

    [Route("plugins/profiles")]
    public class ProfilesController : ProfilesBaseController
    {
        public ProfilesController(
            IUserManager userManager,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            INetworkManager networkManager,
            ILogger<ProfilesController> logger)
            : base(userManager, sessionManager, libraryManager, networkManager, logger)
        {
        }


        [HttpGet("list")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<object>> GetProfiles()
        {
            _logger.LogDebug("ProfilesPlugin: GetProfiles endpoint called.");

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // Resolve Master ID from caller claims context (preventing spoofing)
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            var masterUser = _userManager.GetUserById(masterUserId);
            if (masterUser == null) return NotFound("Master user not found.");

            RecordDeviceActivity();

            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            bool isLocal = remoteIp != null && _networkManager.IsInLocalNetwork(remoteIp);

            var localMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            bool localHidesOthers = localMapping?.HideOthersSubProfilesFromMe ?? false;

            var linkedMasterIds = GetLinkedMasterUserIds(masterUserId, config);
            var profileList = new List<object>();

            foreach (var linkedId in linkedMasterIds)
            {
                var linkedUser = _userManager.GetUserById(linkedId);
                if (linkedUser == null) continue;

                var linkedMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == linkedId);
                bool masterRequiresPin = linkedMapping != null && !string.IsNullOrEmpty(linkedMapping.PinHash);

                // Mirror the switch endpoint exactly — reporting a PIN-less switch the server
                // then rejects strands the user on a screen with nothing to do.
                //
                //  - Your own account: BypassPinOnLocalNetwork, your own convenience setting.
                //  - A linked account: only AllowHouseholdLanBypass, which belongs to that
                //    account's owner. Your own bypass setting never reaches across a link.
                if (isLocal && linkedMapping != null)
                {
                    bool lanUnlocked = linkedId == masterUserId
                        ? linkedMapping.BypassPinOnLocalNetwork
                        : linkedMapping.AllowHouseholdLanBypass;

                    if (lanUnlocked) masterRequiresPin = false;
                }

                profileList.Add(new
                {
                    ProfileUserId = linkedId,
                    ProfileName = linkedUser.Username,
                    AvatarInitial = string.IsNullOrEmpty(linkedUser.Username) ? "M" : linkedUser.Username.Substring(0, 1).ToUpper(),
                    AvatarColor = linkedMapping?.AvatarColor ?? "#00A4DC",
                    TransparentAvatar = linkedMapping?.TransparentAvatar ?? false,
                    RequiresPin = masterRequiresPin,
                    HasPin = !string.IsNullOrEmpty(linkedMapping?.PinHash),
                    IsMaster = true,
                    LockoutMinutes = linkedMapping?.LockoutMinutes ?? 5,
                    MaxSubProfiles = GetMaxProfilesForUser(linkedId, config),
                    BypassPinOnLocalNetwork = linkedMapping?.BypassPinOnLocalNetwork ?? false,
                    AllowedDeviceIds = linkedMapping?.AllowedDeviceIds ?? new List<string>(),
                    IsBonfire = (linkedId != masterUserId),
                    ProfileImage = linkedMapping?.ProfileImage,
                    MasterUserId = linkedId
                });

                bool shouldAddShadowProfiles = true;
                if (linkedId != masterUserId)
                {
                    bool linkedHidesOwn = linkedMapping?.HideMySubProfilesFromOthers ?? false;
                    if (localHidesOthers || linkedHidesOwn)
                    {
                        shouldAddShadowProfiles = false;
                    }
                }

                if (shouldAddShadowProfiles)
                {
                    // Add all shadow profiles for this master
                    var shadowProfiles = config.Mappings
                        .Where(m => m.MasterUserId == linkedId && m.ProfileUserId != linkedId)
                        .Select(m => {
                            bool requiresPin = !string.IsNullOrEmpty(m.PinHash);
                            if (isLocal && m.BypassPinOnLocalNetwork)
                            {
                                requiresPin = false;
                            }
                            return new
                            {
                                m.ProfileUserId,
                                m.ProfileName,
                                AvatarInitial = string.IsNullOrEmpty(m.ProfileName) ? "?" : m.ProfileName.Substring(0, 1).ToUpper(),
                                m.AvatarColor,
                                m.TransparentAvatar,
                                RequiresPin = requiresPin,
                                // Whether a PIN EXISTS, independent of whether one will be
                                // asked for right now. RequiresPin goes false on the LAN when
                                // BypassPinOnLocalNetwork is set, so the edit form cannot use
                                // it to decide if a PIN is configured — doing so made a saved
                                // PIN look like it had never been stored.
                                HasPin = !string.IsNullOrEmpty(m.PinHash),
                                IsMaster = false,
                                m.LockoutMinutes,
                                EnabledFolders = m.EnabledFolders ?? new List<Guid>(),
                                BlockedTags = m.BlockedTags ?? new List<string>(),
                                AllowedTags = m.AllowedTags ?? new List<string>(),
                                BypassPinOnLocalNetwork = m.BypassPinOnLocalNetwork,
                                AllowedDeviceIds = m.AllowedDeviceIds ?? new List<string>(),
                                IsBonfire = (linkedId != masterUserId),
                                m.ProfileImage,
                                m.MasterUserId
                            };
                        });

                    profileList.AddRange(shadowProfiles);
                }
            }

            return Ok(profileList);
        }

        [HttpGet("libraries")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IEnumerable<object>> GetLibraries()
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var caller = _userManager.GetUserById(currentUserId);
            if (caller == null) return NotFound("Calling user not found.");

            var folders = _libraryManager.GetVirtualFolders();

            // Filter folders by caller's user policy
            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.EnableAllFolders)
            {
                var enabled = callerDto.Policy.EnabledFolders ?? Array.Empty<Guid>();
                var blocked = callerDto.Policy.BlockedMediaFolders ?? Array.Empty<Guid>();
                folders = folders.Where(f => Guid.TryParse(f.ItemId, out var id) && enabled.Contains(id) && !blocked.Contains(id)).ToList();
            }

            var libraries = folders.Select(f => new
            {
                Id = f.ItemId,
                Name = f.Name,
                CollectionType = f.CollectionType
            });
            return Ok(libraries);
        }

        [HttpPost("create")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<object>> CreateProfile([FromBody] CreateProfileRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            if (currentUserId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            // Verify Master PIN if required and master profile has a PIN
            if (config.RequireMasterPinForCreation)
            {
                var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
                if (masterMapping != null && !string.IsNullOrEmpty(masterMapping.PinHash))
                {
                    if (!VerifyPinAndUpgrade(request.MasterPin, masterMapping, config))
                    {
                        return BadRequest("Invalid Master PIN code.");
                    }
                }
            }

            var masterUser = _userManager.GetUserById(masterUserId);
            if (masterUser == null) return NotFound("Master user not found.");

            // Enforce max profiles limit
            var maxProfiles = GetMaxProfilesForUser(masterUserId, config);
            var existingCount = config.Mappings.Count(m => m.MasterUserId == masterUserId && m.ProfileUserId != masterUserId);
            if (existingCount >= maxProfiles)
            {
                return BadRequest($"Maximum profile limit of {maxProfiles} reached.");
            }

            // PIN validation (4-8 digits numeric)
            if (!string.IsNullOrEmpty(request.Pin))
            {
                if (request.Pin.Length < 4 || request.Pin.Length > 8 || !request.Pin.All(char.IsDigit))
                {
                    return BadRequest("PIN code must be a numeric value between 4 and 8 digits.");
                }
            }

            // Reject an allow-list that shares no tags with the master's, which would leave the
            // profile with an empty library. Checked before the user is created so we don't
            // leave a half-built account behind.
            var allowedTagsError = ValidateAllowedTags(
                _userManager.GetUserDto(masterUser, string.Empty).Policy, request.AllowedTags);
            if (allowedTagsError != null)
            {
                return BadRequest(allowedTagsError);
            }

            // Standardize username to avoid global collisions
            string systemUsername = $"{masterUser.Username}_{request.ProfileName.Replace(" ", "")}";

            // Ensure name uniqueness in system
            var existingUser = GetAllUsers().FirstOrDefault(u => string.Equals(u.Username, systemUsername, StringComparison.OrdinalIgnoreCase));
            if (existingUser != null)
            {
                return BadRequest("A profile with this name already exists.");
            }

            // Create system user
            var targetUser = await _userManager.CreateUserAsync(systemUsername).ConfigureAwait(false);

            // Set high-entropy random password to prevent direct password-based bypass logins
            string securePassword = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await ChangePasswordCompat(targetUser, securePassword).ConfigureAwait(false);

            // Fetch master user's details for inheritance
            var masterUserDto = _userManager.GetUserDto(masterUser, string.Empty);
            var masterPolicy = masterUserDto.Policy;
            var masterConfig = masterUserDto.Configuration;

            // Build target policy from the newly created Jellyfin user so required provider
            // fields (e.g. AuthenticationProviderId) are preserved. Jellyfin 10.11 enforces
            // AuthenticationProviderId as NOT NULL, so using new UserPolicy() would leave it
            // null and cause UpdatePolicyAsync to throw. (Fix by PepeTechs, PR #6)
            var targetUserDto = _userManager.GetUserDto(targetUser, string.Empty);
            var targetPolicy = targetUserDto.Policy;
            CopyUserPolicy(masterPolicy, targetPolicy);
            targetPolicy.IsAdministrator = false;
            targetPolicy.IsHidden = true;
            targetPolicy.IsDisabled = false;

            // Set parental rating limit (enforce parent rating if set)
            if (!string.IsNullOrEmpty(request.MaxParentalRating) && int.TryParse(request.MaxParentalRating, out var rating))
            {
                targetPolicy.MaxParentalRating = rating;
            }
            if (masterPolicy.MaxParentalRating.HasValue)
            {
                if (!targetPolicy.MaxParentalRating.HasValue || targetPolicy.MaxParentalRating.Value > masterPolicy.MaxParentalRating.Value)
                {
                    targetPolicy.MaxParentalRating = masterPolicy.MaxParentalRating.Value;
                }
            }

            // Tag-based filtering. Stored on the mapping as the profile's own lists; the master's
            // tags are merged in here and re-merged on every switch.
            var profileBlockedTags = NormalizeTags(request.BlockedTags);
            var profileAllowedTags = NormalizeTags(request.AllowedTags);
            var (resolvedBlockedTags, resolvedAllowedTags) =
                ResolveTagPolicy(masterPolicy, profileBlockedTags, profileAllowedTags);
            targetPolicy.BlockedTags = resolvedBlockedTags;
            targetPolicy.AllowedTags = resolvedAllowedTags;

            // Library folder filtering (propagate master blocks)
            List<Guid> validatedFolders = new List<Guid>();
            if (request.EnabledFolders != null)
            {
                var masterAccessible = GetMasterAccessibleFolders(masterPolicy);
                validatedFolders = request.EnabledFolders.Where(id => masterAccessible.Contains(id)).ToList();

                targetPolicy.EnableAllFolders = false;
                targetPolicy.EnabledFolders = validatedFolders.ToArray();

                var allFolders = _libraryManager.GetVirtualFolders();
                var blockedMediaFolders = allFolders
                    .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty && !validatedFolders.Contains(id))
                    .ToArray();

                var masterBlocked = masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>();
                targetPolicy.BlockedMediaFolders = blockedMediaFolders.Union(masterBlocked).ToArray();
            }
            else
            {
                // Inherit blocked libraries and folder accessibility from master user
                targetPolicy.EnableAllFolders = masterPolicy.EnableAllFolders;
                targetPolicy.EnabledFolders = masterPolicy.EnabledFolders;
                targetPolicy.BlockedMediaFolders = masterPolicy.BlockedMediaFolders;

                var masterAccessible = GetMasterAccessibleFolders(masterPolicy);
                validatedFolders = masterAccessible.ToList();
            }

            // Clone general non-admin user configurations
            var targetConfig = new UserConfiguration
            {
                AudioLanguagePreference = masterConfig.AudioLanguagePreference,
                SubtitleLanguagePreference = masterConfig.SubtitleLanguagePreference,
                SubtitleMode = masterConfig.SubtitleMode,
                EnableLocalPassword = false
            };

            // Persist the policy and configuration settings to the database
            await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);
            await _userManager.UpdateConfigurationAsync(targetUser.Id, targetConfig).ConfigureAwait(false);

            // Add new mapping entry
            lock (ConfigLock)
            {
                config.Mappings.Add(new ProfileMapping
                {
                    ProfileUserId = targetUser.Id,
                    MasterUserId = masterUserId,
                    ProfileName = request.ProfileName,
                    PinHash = HashPin(request.Pin),
                    AvatarColor = SanitizeAvatarColor(request.AvatarColor),
                    TransparentAvatar = request.TransparentAvatar ?? false,
                    IsHidden = true,
                    LockoutMinutes = request.LockoutMinutes ?? 5,
                    // Store the selected libraries as the plugin's own ground truth
                    EnabledFolders = validatedFolders,
                    BlockedTags = profileBlockedTags,
                    AllowedTags = profileAllowedTags,
                    BypassPinOnLocalNetwork = request.BypassPinOnLocalNetwork ?? false,
                    AllowedDeviceIds = request.AllowedDeviceIds ?? new List<string>(),
                    ProfileImage = !string.IsNullOrEmpty(request.AvatarLibraryId)
                        ? CopyLibraryAvatarToProfile(targetUser.Id, request.AvatarLibraryId)
                        : SaveProfileImage(targetUser.Id, request.ProfileImage, request.ProfileImageThumb)
                });

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok(new
            {
                ProfileUserId = targetUser.Id,
                ProfileName = request.ProfileName
            });
        }

        [HttpPost("delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> DeleteProfile([FromBody] DeleteProfileRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            if (currentUserId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            if (request.ProfileId == masterUserId)
            {
                return BadRequest("Cannot delete the master profile.");
            }

            // Verify Master PIN if master profile has a PIN
            var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            if (masterMapping != null && !string.IsNullOrEmpty(masterMapping.PinHash))
            {
                if (!VerifyPinAndUpgrade(request.MasterPin, masterMapping, config))
                {
                    return BadRequest("Invalid Master PIN code.");
                }
            }

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
            if (mapping == null) return NotFound("Profile not found.");

            // Verify mapping ownership
            if (mapping.MasterUserId != masterUserId)
            {
                return Unauthorized("Unauthorized profile deletion attempt.");
            }

            // Delete underlying native system user.
            // Track whether the Jellyfin user was fully removed so we know whether
            // to remove the plugin mapping (if deletion failed we keep the mapping
            // so the ghost account remains visible and manageable in the dashboard).
            bool userFullyDeleted = false;
            var targetUser = _userManager.GetUserById(request.ProfileId);
            if (targetUser != null)
            {
                try
                {
                    // Terminate all active sessions for this profile user BEFORE calling
                    // DeleteUserAsync. Jellyfin throws an InvalidOperationException when you
                    // try to delete an account that still has a live session, which was the
                    // root cause of users seeing deletion always fail.
                    try
                    {
                        var activeSessions = _sessionManager.Sessions
                            .Where(s => s.UserId == request.ProfileId)
                            .ToList();
                        // Revoke each session's token so Jellyfin considers them dead.
                        // This prevents DeleteUserAsync from throwing due to an active session.
                        foreach (var session in activeSessions)
                        {
                            try
                            {
                                await _sessionManager.RevokeUserTokens(request.ProfileId, null).ConfigureAwait(false);
                                break; // RevokeUserTokens revokes ALL tokens for the user; no need to loop
                            }
                            catch (Exception sessionEx)
                            {
                                _logger.LogDebug(sessionEx,
                                    "ProfilesPlugin: Could not revoke tokens before deleting user {UserId}.",
                                    request.ProfileId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "ProfilesPlugin: Session enumeration failed before deleting {UserId}; attempting deletion anyway.",
                            request.ProfileId);
                    }

                    await _userManager.DeleteUserAsync(targetUser.Id).ConfigureAwait(false);
                    userFullyDeleted = true;
                }
                catch (Exception ex)
                {
                    // Best-effort fallback: disable the account so it cannot log in,
                    // but keep the plugin mapping so the admin can see and retry later.
                    _logger.LogError(ex,
                        "ProfilesPlugin: Error deleting Jellyfin user {UserId}. " +
                        "Disabling the account as a fallback — the plugin mapping is PRESERVED " +
                        "so the profile remains visible in the dashboard for retry.",
                        targetUser.Id);
                    try
                    {
                        var targetUserDto = _userManager.GetUserDto(targetUser, string.Empty);
                        var targetPolicy = targetUserDto.Policy;
                        targetPolicy.IsDisabled = true;
                        await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogError(updateEx, "ProfilesPlugin: Failed to disable underlying user {UserId} as fallback.", targetUser.Id);
                    }
                    // Return an error so the UI knows deletion did not fully complete.
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        "Profile could not be fully deleted. The account has been disabled. " +
                        "Retry deletion after restarting Jellyfin.");
                }
            }
            else
            {
                // Underlying Jellyfin user already gone — treat as a clean delete.
                userFullyDeleted = true;
            }

            if (userFullyDeleted)
            {
                // Clean up static profile image if any
                SaveProfileImage(request.ProfileId, null);

                lock (ConfigLock)
                {
                    var mappingToRemove = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                    if (mappingToRemove != null)
                    {
                        config.Mappings.Remove(mappingToRemove);
                        Plugin.Instance?.SaveConfiguration();
                    }
                }
            }

            return Ok();
        }

        [HttpPost("switch")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<object>> SwitchProfile([FromBody] SwitchProfileRequest request)
        {
            // Switching is the click users perceive as "loading the home screen", so the
            // per-stage cost is logged at Debug. Enable debug logging for Jellyfin.Profiles to
            // see which stage is responsible when a switch feels slow.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tAuth = 0, tPolicy = 0, tSession = 0;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // Master user is always valid. If no mapping exists for master, check if request is master user ID.
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid callerMasterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            ProfileMapping? mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);

            RecordDeviceActivity();

            var linkedMasterIds = GetLinkedMasterUserIds(callerMasterUserId, config);

            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            bool isLocal = remoteIp != null && _networkManager.IsInLocalNetwork(remoteIp);
            var ip = remoteIp?.ToString() ?? "127.0.0.1";

            // True when the target is someone else's master account reached through a Bonfire
            // link, rather than the caller's own account or one of its sub-profiles.
            bool isCrossAccountMasterSwitch = false;

            // Set when the target account's owner has opted into household LAN switching and
            // the request genuinely came from the local network. Relaxes both of the
            // cross-account restrictions below, for that one account.
            bool householdLanBypass = false;

            // Validate switch permissions: must belong to the same master user group or a linked Bonfire group.
            if (request.ProfileId == callerMasterUserId)
            {
                // Switching to own master profile is allowed
            }
            else if (linkedMasterIds.Contains(request.ProfileId))
            {
                isCrossAccountMasterSwitch = true;

                // Switching to a *different* master account via a Bonfire link hands the
                // caller a real session token for that account — including its admin rights
                // if it has any. The owner's PIN is normally the only thing standing between a
                // shared Bonfire code and full account access.
                //
                // The exception is consent from the account being entered: its owner can turn
                // on AllowHouseholdLanBypass, which is what two adults sharing one TV actually
                // want (issue #13). It is deliberately theirs to grant and nobody else's — the
                // caller's own BypassPinOnLocalNetwork still does not carry across a link — and
                // it only applies on the local network, so a leaked code is worth nothing to
                // someone outside the house.
                var linkedMasterMapping = config.Mappings
                    .FirstOrDefault(m => m.ProfileUserId == request.ProfileId);

                bool blockedUnprotected;
                (householdLanBypass, blockedUnprotected) =
                    EvaluateCrossAccountSwitch(linkedMasterMapping, isLocal);

                if (blockedUnprotected)
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: Blocked Bonfire switch from {Caller} into unprotected master account {Target}.",
                        callerMasterUserId, request.ProfileId);
                    return BadRequest(
                        "This account has no PIN set, so it cannot be opened from a shared Bonfire. " +
                        "Its owner must either set a profile PIN, or turn on \"Allow household " +
                        "switching on this network\" in their Bonfire settings.");
                }

                if (householdLanBypass)
                {
                    // Recorded at Information alongside the audit entry: this is the path where
                    // one account is entered without proving anything but network location, so
                    // it needs to be visible in the log when an owner reviews access.
                    _logger.LogInformation(
                        "ProfilesPlugin: Household LAN bypass — {Caller} entered linked account {Target} " +
                        "from {Ip} without a PIN (the target's owner enabled this).",
                        callerMasterUserId, request.ProfileId, ip);
                }
            }
            else
            {
                if (mapping == null || !linkedMasterIds.Contains(mapping.MasterUserId))
                {
                    return Unauthorized("Unauthorized profile switch attempt.");
                }
            }

            // Enforce device restrictions for sub-profiles
            if (mapping != null && mapping.ProfileUserId != mapping.MasterUserId && mapping.AllowedDeviceIds != null && mapping.AllowedDeviceIds.Count > 0)
            {
                var targetDeviceId = GetAuthorizationParameter("DeviceId");
                if (string.IsNullOrEmpty(targetDeviceId) || !mapping.AllowedDeviceIds.Any(id => string.Equals(id, targetDeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    return BadRequest("This profile is not allowed on this device.");
                }
            }

            var rateLimitKey = $"{ip}_{request.ProfileId}";

            // Verify PIN if set
            var pinHashToCheck = mapping?.PinHash;
            if (!string.IsNullOrEmpty(pinHashToCheck))
            {
                bool bypass = CanSkipPin(mapping, isLocal, isCrossAccountMasterSwitch, householdLanBypass);

                if (bypass)
                {
                    // Logged so an administrator can confirm the bypass only ever fires for
                    // genuinely local clients. Behind a reverse proxy that is NOT listed in
                    // Jellyfin's Known Proxies, every request arrives with the proxy's address
                    // and would therefore look local — this line is how that shows up.
                    // The cross-account case has already logged its own, more specific line.
                    if (!isCrossAccountMasterSwitch)
                    {
                        _logger.LogInformation(
                            "ProfilesPlugin: PIN skipped for profile {Profile} — client {Ip} was classified " +
                            "as local by Jellyfin's network settings.",
                            request.ProfileId, ip);
                    }
                }
                else
                {
                    if (RateLimiter.Pin.IsRateLimited(rateLimitKey))
                    {
                        return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed PIN attempts. Please try again in 15 minutes.");
                    }

                    // mapping is non-null here — pinHashToCheck came from it.
                    if (!VerifyPinAndUpgrade(request.Pin, mapping!, config))
                    {
                        RateLimiter.Pin.RecordFailure(rateLimitKey);
                        return BadRequest("Invalid PIN code.");
                    }
                }
            }

            RateLimiter.Pin.Reset(rateLimitKey);
            tAuth = sw.ElapsedMilliseconds;

            var targetUser = _userManager.GetUserById(request.ProfileId);
            if (targetUser == null) return NotFound("Underlying system user missing.");

            // Inherit/synchronize streaming policies and configurations from master user dynamically during switch
            var targetMasterUserId = mapping != null ? mapping.MasterUserId : request.ProfileId;
            var masterUser = _userManager.GetUserById(targetMasterUserId);
            if (masterUser != null && targetUser.Id != callerMasterUserId)
            {
                var masterUserDto = _userManager.GetUserDto(masterUser, string.Empty);
                var masterPolicy = masterUserDto.Policy;

                var targetUserDto = _userManager.GetUserDto(targetUser, string.Empty);
                var targetPolicy = targetUserDto.Policy;

                // Sync streaming, transcoding, and bitrate policies
                var childMaxParentalRating = targetPolicy.MaxParentalRating;
                var childBlockedFolders = targetPolicy.BlockedMediaFolders;
                var childEnableAllFolders = targetPolicy.EnableAllFolders;
                var childEnabledFolders = targetPolicy.EnabledFolders;

                CopyUserPolicy(masterPolicy, targetPolicy);

                // Restore child-specific overrides
                targetPolicy.IsAdministrator = false;
                targetPolicy.IsHidden = true;
                targetPolicy.IsDisabled = false;
                targetPolicy.MaxParentalRating = childMaxParentalRating;

                // Re-apply the profile's tag filters. CopyUserPolicy above just overwrote them with
                // the master's, so without this every profile switch would silently drop them.
                // Resolving from the mapping (rather than the current policy) also heals tags reset
                // by a Jellyfin restart and picks up changes to the master's own tag policy.
                var (reapplyBlockedTags, reapplyAllowedTags) =
                    ResolveTagPolicy(masterPolicy, mapping?.BlockedTags, mapping?.AllowedTags);
                targetPolicy.BlockedTags = reapplyBlockedTags;
                targetPolicy.AllowedTags = reapplyAllowedTags;

                // Determine the authoritative enabled-folder list:
                //  - If the plugin mapping has a stored list (EnabledFolders != null), use it as ground truth.
                //    This survives Jellyfin restarts that reset user policies.
                //  - If EnabledFolders is null (profile predates this field), fall back to the Jellyfin policy
                //    and auto-migrate by saving the list into the mapping now.
                List<Guid> authorityFolders;
                if (mapping?.EnabledFolders != null)
                {
                    authorityFolders = mapping.EnabledFolders;
                }
                else
                {
                    // Legacy profile: read from Jellyfin policy and migrate
                    var legacyEnabled = childEnableAllFolders
                        ? (masterPolicy.EnabledFolders ?? Array.Empty<Guid>()).ToList()
                        : (childEnabledFolders ?? Array.Empty<Guid>()).ToList();

                    // If still empty, derive from BlockedMediaFolders
                    if (legacyEnabled.Count == 0 && childBlockedFolders != null && childBlockedFolders.Length > 0)
                    {
                        var allFolderIds = _libraryManager.GetVirtualFolders()
                            .Select(f => Guid.TryParse(f.ItemId, out var fid) ? fid : Guid.Empty)
                            .Where(fid => fid != Guid.Empty)
                            .ToList();
                        legacyEnabled = allFolderIds.Where(fid => !childBlockedFolders.Contains(fid)).ToList();
                    }

                    authorityFolders = legacyEnabled;

                    // Persist the migration so we never need this fallback again
                    if (mapping != null)
                    {
                        lock (ConfigLock)
                        {
                            mapping.EnabledFolders = authorityFolders;
                            Plugin.Instance?.SaveConfiguration();
                        }
                    }
                }

                // Intersect authorityFolders with the master's current accessible folders
                var masterAccessible = GetMasterAccessibleFolders(masterPolicy);
                authorityFolders = authorityFolders.Where(id => masterAccessible.Contains(id)).ToList();

                // Re-apply the stored library policy (heals resets caused by Jellyfin restarts)
                targetPolicy.EnableAllFolders = false;
                targetPolicy.EnabledFolders = authorityFolders.ToArray();

                var allFolders2 = _libraryManager.GetVirtualFolders();
                var reapplyBlocked = allFolders2
                    .Select(f => Guid.TryParse(f.ItemId, out var id2) ? id2 : Guid.Empty)
                    .Where(id2 => id2 != Guid.Empty && !authorityFolders.Contains(id2))
                    .ToArray();
                var masterBlocked2 = masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>();
                targetPolicy.BlockedMediaFolders = reapplyBlocked.Union(masterBlocked2).ToArray();

                await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);

                // Sync basic configuration settings (language settings, subtitles preference)
                var masterConfig = masterUserDto.Configuration;
                var targetConfig = targetUserDto.Configuration;
                targetConfig.AudioLanguagePreference = masterConfig.AudioLanguagePreference;
                targetConfig.SubtitleLanguagePreference = masterConfig.SubtitleLanguagePreference;
                targetConfig.SubtitleMode = masterConfig.SubtitleMode;

                await _userManager.UpdateConfigurationAsync(targetUser.Id, targetConfig).ConfigureAwait(false);
            }

            var client = GetAuthorizationParameter("Client");
            var device = GetAuthorizationParameter("Device");
            var deviceId = GetAuthorizationParameter("DeviceId");
            var version = GetAuthorizationParameter("Version");

            // Jellyfin 10.11+ requires App, DeviceName, DeviceId, AppVersion to be non-null/non-empty.
            // Provide safe fallbacks in case the Authorization header couldn't be parsed.
            var authRequest = new AuthenticationRequest
            {
                // UserId must be set. Without it SessionManager falls back to a username
                // lookup, and any miss surfaces as "Invalid username or password entered."
                // even though AuthenticateDirect never checks a password (issue #15).
                UserId = targetUser.Id,
                Username = targetUser.Username,
                RemoteEndPoint = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1",
                App = !string.IsNullOrEmpty(client) ? client : "JellyfinWeb",
                DeviceName = !string.IsNullOrEmpty(device) ? device : "Profiles Plugin",
                DeviceId = !string.IsNullOrEmpty(deviceId) ? deviceId : ("profiles-" + targetUser.Id.ToString("N")[..8]),
                AppVersion = !string.IsNullOrEmpty(version) ? version : "1.0.0"
            };
            tPolicy = sw.ElapsedMilliseconds;

            // Authenticate directly bypassing password check (securely validated caller + PIN validation)
            // Never let this throw past us: Jellyfin's exception middleware turns these into a 401,
            // and the client reads a 401 as "the caller's session expired" and signs the master out.
            MediaBrowser.Controller.Authentication.AuthenticationResult session;
            try
            {
                session = await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
            }
            catch (System.Security.SecurityException ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: Session creation refused for {User}.", targetUser.Username);
                var reason = ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase)
                    ? "This profile isn't allowed on this device."
                    : "This profile has too many active sessions.";
                return BadRequest(reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Could not create a session for {User}.", targetUser.Username);
                return BadRequest("Couldn't sign in to that profile. Check the server log.");
            }

            tSession = sw.ElapsedMilliseconds;

            // Record profile switch audit log
            RecordAuditLog(masterUser?.Username ?? "Unknown", targetUser.Username);

            var total = sw.ElapsedMilliseconds;
            // Warn on switches slow enough for a user to notice; otherwise keep it at Debug.
            if (total >= 1000)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Slow profile switch — {Total} ms total " +
                    "(auth/PIN {Auth} ms, policy sync {Policy} ms, session {Session} ms, audit {Audit} ms).",
                    total, tAuth, tPolicy - tAuth, tSession - tPolicy, total - tSession);
            }
            else
            {
                _logger.LogDebug(
                    "ProfilesPlugin: Profile switch took {Total} ms " +
                    "(auth/PIN {Auth} ms, policy sync {Policy} ms, session {Session} ms, audit {Audit} ms).",
                    total, tAuth, tPolicy - tAuth, tSession - tPolicy, total - tSession);
            }

            return Ok(new
            {
                ActiveProfileToken = session.AccessToken,
                JellyfinUserId = targetUser.Id,
                // Carried in the switch response so the client can cache it before the reload.
                // Fetching it afterwards would leave a window in which the home screen shows
                // the library artwork this profile is not supposed to see.
                LibraryArtwork = DescribeLibraryArtwork(config, targetUser.Id)
            });
        }

        [HttpPost("verify-pin")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult VerifyPin([FromBody] SwitchProfileRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid callerMasterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            var linkedMasterIds = GetLinkedMasterUserIds(callerMasterUserId, config);

            // Enforce device restrictions for sub-profiles
            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
            if (mapping != null && mapping.ProfileUserId != mapping.MasterUserId && mapping.AllowedDeviceIds != null && mapping.AllowedDeviceIds.Count > 0)
            {
                var deviceId = GetAuthorizationParameter("DeviceId");
                if (string.IsNullOrEmpty(deviceId) || !mapping.AllowedDeviceIds.Any(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    return BadRequest("This profile is not allowed on this device.");
                }
            }

            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            bool isLocal = remoteIp != null && _networkManager.IsInLocalNetwork(remoteIp);
            var ip = remoteIp?.ToString() ?? "127.0.0.1";
            var rateLimitKey = $"{ip}_{request.ProfileId}";

            if (linkedMasterIds.Contains(request.ProfileId))
            {
                // Verify master PIN
                var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                var pinHash = masterMapping?.PinHash;
                if (!string.IsNullOrEmpty(pinHash))
                {
                    // Same rules as /switch, through the same helpers, so this endpoint can
                    // never green-light a switch /switch would then refuse.
                    bool isCrossAccount = request.ProfileId != callerMasterUserId;
                    var (householdLanBypass, _) = EvaluateCrossAccountSwitch(masterMapping, isLocal);
                    bool bypass = CanSkipPin(masterMapping, isLocal, isCrossAccount, householdLanBypass);
                    if (!bypass)
                    {
                        if (RateLimiter.Pin.IsRateLimited(rateLimitKey))
                        {
                            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed PIN attempts. Please try again in 15 minutes.");
                        }

                        // masterMapping is non-null here — pinHash came from it.
                        if (!VerifyPinAndUpgrade(request.Pin, masterMapping!, config))
                        {
                            RateLimiter.Pin.RecordFailure(rateLimitKey);
                            return BadRequest("Invalid PIN.");
                        }
                    }
                }
                RateLimiter.Pin.Reset(rateLimitKey);
                return Ok();
            }
            else
            {
                if (mapping == null || !linkedMasterIds.Contains(mapping.MasterUserId))
                {
                    return Unauthorized("Unauthorized profile PIN verification.");
                }
                var pinHash = mapping.PinHash;
                if (!string.IsNullOrEmpty(pinHash))
                {
                    bool bypass = mapping.BypassPinOnLocalNetwork && isLocal;
                    if (!bypass)
                    {
                        if (RateLimiter.Pin.IsRateLimited(rateLimitKey))
                        {
                            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed PIN attempts. Please try again in 15 minutes.");
                        }

                        if (!VerifyPinAndUpgrade(request.Pin, mapping, config))
                        {
                            RateLimiter.Pin.RecordFailure(rateLimitKey);
                            return BadRequest("Invalid PIN.");
                        }
                    }
                }
                RateLimiter.Pin.Reset(rateLimitKey);
                return Ok();
            }
        }

        [HttpGet("admin/mappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetAdminMappings()
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
            {
                return Unauthorized("Only administrators can view all mappings.");
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var subProfileIds = config.Mappings
                .Where(m => m.ProfileUserId != m.MasterUserId)
                .Select(m => m.ProfileUserId)
                .ToHashSet();

            var masterUsersList = new List<object>();
            var subProfilesList = new List<object>();

            var allUsers = GetAllUsers().ToList();
            foreach (var user in allUsers)
            {
                if (subProfileIds.Contains(user.Id))
                {
                    var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == user.Id);
                    var masterUser = mapping != null ? _userManager.GetUserById(mapping.MasterUserId) : null;
                    subProfilesList.Add(new
                    {
                        ProfileUserId = user.Id,
                        ProfileName = mapping?.ProfileName ?? user.Username,
                        MasterName = masterUser?.Username ?? "Unknown",
                        // Grouping key for the dashboard. Name alone is not safe to group by —
                        // it changes when an account is renamed and is not guaranteed unique.
                        MasterUserId = mapping?.MasterUserId ?? Guid.Empty,
                        RequiresPin = mapping != null && !string.IsNullOrEmpty(mapping.PinHash),
                        // Lets the settings page report pictures whose file has gone missing.
                        ProfileImage = mapping?.ProfileImage
                    });
                }
                else
                {
                    var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == user.Id);
                    var limitOverride = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == user.Id)?.MaxProfiles;
                    masterUsersList.Add(new
                    {
                        ProfileUserId = user.Id,
                        ProfileName = user.Username,
                        RequiresPin = mapping != null && !string.IsNullOrEmpty(mapping.PinHash),
                        MaxProfiles = GetMaxProfilesForUser(user.Id, config),
                        LimitOverride = limitOverride,
                        ProfileImage = mapping?.ProfileImage
                    });
                }
            }

            // Re-read index.html so the dashboard reflects the file as it is right now.
            // These flags used to be a snapshot taken at server boot, which is why the
            // warning banner persisted after users ran the documented fix commands —
            // nothing re-checked until the next full Jellyfin restart.
            ProfilesBootstrapTask.RefreshInjectionStatus();

            return Ok(new
            {
                MasterUsers = masterUsersList,
                SubProfiles = subProfilesList,
                InjectionSucceeded = ProfilesBootstrapTask.InjectionSucceeded,
                IsVersionStale = ProfilesBootstrapTask.IsVersionStale,
                IndexPath = ProfilesBootstrapTask.IndexPath,
                FailureReason = ProfilesBootstrapTask.LastFailureReason,
                // Lets the dashboard emit a permission command naming the exact account
                // Jellyfin runs as, instead of guessing between service and desktop mode.
                ServiceAccount = ProfilesBootstrapTask.RunningAccount,
                IsWindows = OperatingSystem.IsWindows(),
                PluginVersion = GetPluginVersion(),
                Mechanism = DescribeInjectionMechanism()
            });
        }

        /// <summary>
        /// How the client script is reaching the browser, and whether the request-pipeline hook
        /// is doing anything.
        /// <para>
        /// Reported next to every injection status because from 1.4.1 an index.html with no tag
        /// in it is no longer necessarily a failure — it is the expected state when the plugin
        /// is injecting on the fly.
        /// </para>
        /// </summary>
        private static object DescribeInjectionMechanism()
        {
            return new
            {
                Mode = IndexInjectionModes.Normalize(Plugin.Instance?.Configuration?.IndexInjectionMode),
                // Installed or updated on a running server, so this build's pipeline hook
                // was never registered and cannot be until Jellyfin restarts (issue #25).
                RestartRequired = ProfilesBootstrapTask.RestartRequired,
                MiddlewareRegistered = ProfilesIndexMiddleware.IsRegistered,
                MiddlewareActive = ProfilesIndexMiddleware.HasSeenIndexRequest,
                MiddlewareServed = ProfilesIndexMiddleware.ServedCountValue,
                MiddlewareLastServedUtc = ProfilesIndexMiddleware.LastServedUtc,
                MiddlewareError = ProfilesIndexMiddleware.LastError
            };
        }

        /// <summary>
        /// Re-runs the client-script injection on demand. Lets an administrator apply a file
        /// permission fix and click "Retry" instead of restarting the whole Jellyfin server.
        /// </summary>
        [HttpPost("admin/retry-injection")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> RetryInjection()
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
            {
                return Unauthorized("Only administrators can retry script injection.");
            }

            _logger.LogInformation("ProfilesPlugin: Manual injection retry requested by {User}.", caller.Username);
            var succeeded = ProfilesBootstrapTask.RunInjectionNow();

            return Ok(new
            {
                InjectionSucceeded = succeeded,
                IsVersionStale = ProfilesBootstrapTask.IsVersionStale,
                IndexPath = ProfilesBootstrapTask.IndexPath,
                FailureReason = ProfilesBootstrapTask.LastFailureReason,
                // Lets the dashboard emit a permission command naming the exact account
                // Jellyfin runs as, instead of guessing between service and desktop mode.
                ServiceAccount = ProfilesBootstrapTask.RunningAccount,
                IsWindows = OperatingSystem.IsWindows(),
                PluginVersion = GetPluginVersion(),
                Mechanism = DescribeInjectionMechanism()
            });
        }

        /// <summary>
        /// Plugin version for display and cache-busting. Prefers the informational version,
        /// which carries any pre-release label (e.g. "1.2.7-beta") that the numeric assembly
        /// version cannot represent — Jellyfin requires the latter to be purely numeric.
        /// Falls back to the numeric version so this can never go stale the way a hardcoded
        /// literal does.
        /// </summary>
        private static string GetPluginVersion()
        {
            var informational = typeof(ProfilesBootstrapTask).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip a "+<sha>" build-metadata suffix if one ever slips in.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }

            return Plugin.Instance?.Version?.ToString()
                   ?? typeof(ProfilesBootstrapTask).Assembly.GetName().Version?.ToString()
                   ?? "unknown";
        }

        [HttpPost("admin/reset-pin")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult ResetPinAdmin([FromBody] DeleteProfileRequest request)
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
            {
                return Unauthorized("Only administrators can reset PINs.");
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            lock (ConfigLock)
            {
                var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                if (mapping == null) return NotFound("Profile mapping not found.");

                mapping.PinHash = string.Empty;
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }



        [HttpPost("update")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null)
            {
                return Unauthorized();
            }
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            if (currentUserId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            // Verify Master PIN if master profile has a PIN
            var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            if (masterMapping != null && !string.IsNullOrEmpty(masterMapping.PinHash))
            {
                if (!VerifyPinAndUpgrade(request.MasterPin, masterMapping, config))
                {
                    return BadRequest("Invalid Master PIN code.");
                }
            }

            // Enforce ownership: the profile being edited must belong to the caller's master account.
            if (request.ProfileId != masterUserId)
            {
                var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                if (mapping == null || mapping.MasterUserId != masterUserId)
                {
                    return Unauthorized("Unauthorized profile update attempt.");
                }
            }

            // PIN validation (4-8 digits numeric if provided)
            if (!string.IsNullOrEmpty(request.Pin))
            {
                if (request.Pin.Length < 4 || request.Pin.Length > 8 || !request.Pin.All(char.IsDigit))
                {
                    return BadRequest("PIN code must be a numeric value between 4 and 8 digits.");
                }
            }

            var targetUser = _userManager.GetUserById(request.ProfileId);
            if (targetUser == null) return NotFound("Target user not found.");

            var masterUser = _userManager.GetUserById(masterUserId);
            if (masterUser == null) return NotFound("Master user not found.");
            
            var masterUserDto = _userManager.GetUserDto(masterUser, string.Empty);
            var masterPolicy = masterUserDto.Policy;

            var allowedTagsError = ValidateAllowedTags(masterPolicy, request.AllowedTags);
            if (allowedTagsError != null)
            {
                return BadRequest(allowedTagsError);
            }

            // Tag filters: a null list means "leave unchanged", so fall back to what's stored.
            var existingMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
            List<string>? profileBlockedTags = request.BlockedTags != null
                ? NormalizeTags(request.BlockedTags)
                : existingMapping?.BlockedTags;
            List<string>? profileAllowedTags = request.AllowedTags != null
                ? NormalizeTags(request.AllowedTags)
                : existingMapping?.AllowedTags;

            // Renaming logic
            if (request.ProfileId != masterUserId)
            {
                string systemUsername = $"{masterUser.Username}_{request.ProfileName.Replace(" ", "")}";
                if (!string.Equals(targetUser.Username, systemUsername, StringComparison.OrdinalIgnoreCase))
                {
                    var existingUser = GetAllUsers().FirstOrDefault(u => string.Equals(u.Username, systemUsername, StringComparison.OrdinalIgnoreCase));
                    if (existingUser != null)
                    {
                        return BadRequest("A profile with this name already exists.");
                    }
                    targetUser.Username = systemUsername;
                    await _userManager.UpdateUserAsync(targetUser).ConfigureAwait(false);
                }
            }

            // Update policy for sub-profiles
            List<Guid>? validatedFolders = null;
            if (request.ProfileId != masterUserId)
            {
                var targetUserDto = _userManager.GetUserDto(targetUser, string.Empty);
                var targetPolicy = targetUserDto.Policy;

                // Set parental rating
                if (!string.IsNullOrEmpty(request.MaxParentalRating) && int.TryParse(request.MaxParentalRating, out var rating))
                {
                    targetPolicy.MaxParentalRating = rating;
                }
                else
                {
                    targetPolicy.MaxParentalRating = null;
                }

                if (masterPolicy.MaxParentalRating.HasValue)
                {
                    if (!targetPolicy.MaxParentalRating.HasValue || targetPolicy.MaxParentalRating.Value > masterPolicy.MaxParentalRating.Value)
                    {
                        targetPolicy.MaxParentalRating = masterPolicy.MaxParentalRating.Value;
                    }
                }

                // Tag-based filtering (clamped so it can never exceed the master's)
                var (resolvedBlockedTags, resolvedAllowedTags) =
                    ResolveTagPolicy(masterPolicy, profileBlockedTags, profileAllowedTags);
                targetPolicy.BlockedTags = resolvedBlockedTags;
                targetPolicy.AllowedTags = resolvedAllowedTags;

                // Library access propagation
                if (request.EnabledFolders != null)
                {
                    var masterAccessible = GetMasterAccessibleFolders(masterPolicy);
                    validatedFolders = request.EnabledFolders.Where(id => masterAccessible.Contains(id)).ToList();

                    targetPolicy.EnableAllFolders = false;
                    targetPolicy.EnabledFolders = validatedFolders.ToArray();

                    var allFolders = _libraryManager.GetVirtualFolders();
                    var blockedMediaFolders = allFolders
                        .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
                        .Where(id => id != Guid.Empty && !validatedFolders.Contains(id))
                        .ToArray();

                    var masterBlocked = masterPolicy.BlockedMediaFolders ?? Array.Empty<Guid>();
                    targetPolicy.BlockedMediaFolders = blockedMediaFolders.Union(masterBlocked).ToArray();
                }
                else
                {
                    targetPolicy.EnableAllFolders = masterPolicy.EnableAllFolders;
                    targetPolicy.EnabledFolders = masterPolicy.EnabledFolders;
                    targetPolicy.BlockedMediaFolders = masterPolicy.BlockedMediaFolders;

                    var masterAccessible = GetMasterAccessibleFolders(masterPolicy);
                    validatedFolders = masterAccessible.ToList();
                }

                await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);
            }

            lock (ConfigLock)
            {
                // Fetch or create mapping for this profile inside the lock
                var mappingEntry = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                if (mappingEntry == null && request.ProfileId == masterUserId)
                {
                    mappingEntry = new ProfileMapping
                    {
                        ProfileUserId = masterUserId,
                        MasterUserId = masterUserId,
                        ProfileName = masterUser.Username,
                        IsHidden = false
                    };
                    config.Mappings.Add(mappingEntry);
                }

                if (mappingEntry != null)
                {
                    // Update basic mapping properties (only for sub-profiles; master name is read-only)
                    if (request.ProfileId != masterUserId)
                    {
                        mappingEntry.ProfileName = request.ProfileName;
                    }

                    mappingEntry.AvatarColor = SanitizeAvatarColor(request.AvatarColor);

                    // Null leaves it alone, so a caller that predates the field — or one
                    // sending a partial update — cannot silently switch the background off.
                    if (request.TransparentAvatar.HasValue)
                    {
                        mappingEntry.TransparentAvatar = request.TransparentAvatar.Value;
                    }

                    if (!string.IsNullOrEmpty(request.AvatarLibraryId))
                    {
                        mappingEntry.ProfileImage = CopyLibraryAvatarToProfile(request.ProfileId, request.AvatarLibraryId);
                    }
                    else if (request.ProfileImage != null)
                    {
                        mappingEntry.ProfileImage = SaveProfileImage(request.ProfileId, request.ProfileImage, request.ProfileImageThumb);
                    }

                    // Handle PIN updates
                    if (request.Pin == string.Empty)
                    {
                        mappingEntry.PinHash = string.Empty;
                    }
                    else if (request.Pin != null)
                    {
                        mappingEntry.PinHash = HashPin(request.Pin);
                    }

                    // Handle lockout timer update
                    if (request.LockoutMinutes.HasValue)
                    {
                        mappingEntry.LockoutMinutes = request.LockoutMinutes.Value;
                    }

                    // Update stored library list (plugin's ground truth)
                    if (validatedFolders != null)
                    {
                        mappingEntry.EnabledFolders = validatedFolders;
                    }

                    // Store the profile's own tag lists, not the master-merged result, so later
                    // changes to the master's tags flow through instead of staying baked in.
                    // Sub-profiles only: the master's tag policy is managed in Jellyfin itself,
                    // and the block above never applies these to the master's user account.
                    if (request.ProfileId != masterUserId)
                    {
                        if (request.BlockedTags != null)
                        {
                            mappingEntry.BlockedTags = NormalizeTags(request.BlockedTags);
                        }
                        if (request.AllowedTags != null)
                        {
                            mappingEntry.AllowedTags = NormalizeTags(request.AllowedTags);
                        }
                    }

                    if (request.BypassPinOnLocalNetwork.HasValue)
                    {
                        mappingEntry.BypassPinOnLocalNetwork = request.BypassPinOnLocalNetwork.Value;
                    }

                    if (request.AllowedDeviceIds != null)
                    {
                        mappingEntry.AllowedDeviceIds = request.AllowedDeviceIds;
                    }
                }

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        /// <summary>
        /// Script served when the emergency disable is active. It only undoes what a
        /// previously loaded copy may have left behind — the overlay, the scroll lock, the
        /// injected button — and then does nothing at all.
        /// </summary>
        private const string InertProfilesJs =
            "/* Bonfire: emergency disable active. Restart Jellyfin to restore the plugin. */\n" +
            "(function(){try{\n" +
            "  var o=document.getElementById('profiles-gate-overlay'); if(o)o.remove();\n" +
            "  var b=document.getElementById('profiles-floating-bubble'); if(b)b.remove();\n" +
            "  var s=document.getElementById('profiles-sidebar-link'); if(s)s.remove();\n" +
            "  document.body.classList.remove('profiles-no-scroll');\n" +
            "  document.documentElement.classList.remove('profiles-no-scroll');\n" +
            "  document.documentElement.style.removeProperty('opacity');\n" +
            "  localStorage.removeItem('jpf-sw');\n" +
            "  console.warn('Bonfire is disabled until the server restarts.');\n" +
            "}catch(e){}})();\n";

        [HttpGet("profiles.js")]
        [Produces("application/javascript")]
        public ActionResult GetProfilesJs()
        {
            // Emergency disable: serve an inert script instead of the switcher. Answered
            // before the cache and the ETag so a browser holding a 304-able copy of the real
            // script still gets this one — the whole point is to recover a client that the
            // plugin has made unusable, and a conditional request must not defeat that.
            if (Plugin.IsPanicDisabled)
            {
                Response.Headers["Cache-Control"] = "no-store";
                return Content(InertProfilesJs, "application/javascript");
            }

            // CachedProfilesJs lives on ProfilesBaseController as a static field.
            // It is loaded once per app lifetime — no reason to read the embedded
            // resource on every browser page load.
            if (CachedProfilesJs == null)
            {
                lock (JsCacheLock)
                {
                    if (CachedProfilesJs == null)
                    {
                        var assembly = typeof(Plugin).Assembly;
                        using var stream = assembly.GetManifestResourceStream("Jellyfin.Profiles.Web.profiles.js");
                        if (stream == null) return NotFound();
                        using var reader = new StreamReader(stream);
                        CachedProfilesJs = PublishLocales(reader.ReadToEnd());
                    }
                }
            }

            // The script URL normally carries a ?v={version} cache-buster, but when the plugin
            // cannot rewrite index.html (read-only web root) that query string stays pinned to
            // an older version. A long immutable cache would then serve stale client code
            // indefinitely and new features would silently never appear.
            //
            // Tagging the response with the plugin version and asking for revalidation means a
            // stale URL still picks up new code on its own within max-age, while unchanged
            // content costs only a 304.
            var etag = "\"" + GetPluginVersion() + "\"";
            Response.Headers["ETag"] = etag;
            Response.Headers["Cache-Control"] = "public, max-age=300, must-revalidate";

            if (string.Equals(Request.Headers["If-None-Match"].ToString(), etag, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(CachedProfilesJs, "application/javascript");
        }

        /// <summary>
        /// Fills in the client's list of available translations as the script is served.
        /// <para>
        /// profiles.js ships with an empty list and a marker comment. Rewriting it here
        /// means the browser knows which languages exist without a request that every
        /// English reader would also pay for, and — the point of it — without a
        /// contributor having to edit JavaScript to register a file they just added.
        /// </para>
        /// <para>
        /// A plain string replace on an exact literal, not a regex over the whole file:
        /// this runs on the script every client loads, and the failure mode of a clever
        /// pattern here is a corrupted script rather than a missing translation. If the
        /// marker is ever edited away the replace simply does nothing, the list stays
        /// empty, and every client keeps rendering English.
        /// </para>
        /// </summary>
        internal static string PublishLocales(string js)
        {
            const string marker = "let SUPPORTED_LOCALES = []; // __BONFIRE_LOCALES__";

            var codes = EmbeddedLocales.Value.OrderBy(c => c, StringComparer.Ordinal);
            var list = string.Join(", ", codes.Select(c => "'" + c + "'"));

            return js.Replace(
                marker,
                "let SUPPORTED_LOCALES = [" + list + "]; // __BONFIRE_LOCALES__",
                StringComparison.Ordinal);
        }

        // ── Translations ────────────────────────────────────────────────────────────
        // English ships inline in profiles.js — it is the fallback every client already
        // has, so it is never requested here. This endpoint only ever serves the other
        // languages, each embedded as its own Web/i18n/{locale}.json resource, fetched
        // by the browser once it has decided (from navigator.languages) that it wants
        // one.
        //
        // Adding a language is one JSON file in Web/i18n and nothing else: the .csproj
        // embeds that folder by wildcard, the set below is read back out of the assembly
        // rather than written by hand, and GetProfilesJs tells the client what it found.
        // See docs/developer-api.md, "Adding a translation".

        /// <summary>
        /// Locale codes with a translation file embedded in the assembly, discovered from
        /// the resource names rather than maintained as a list.
        /// <para>
        /// A hand-kept list is a second place to edit and therefore a place to forget: a
        /// contributor who added the file and not the entry got a file that was shipped,
        /// served, and never requested — working code that does nothing, with no error to
        /// point at it.
        /// </para>
        /// </summary>
        internal static IReadOnlyCollection<string> SupportedI18nLocales => EmbeddedLocales.Value;

        private const string I18nResourcePrefix = "Jellyfin.Profiles.Web.i18n.";

        /// <summary>A BCP-47-ish tag: "fr", "pt-BR", "zh-Hans". Deliberately narrow.</summary>
        private static readonly System.Text.RegularExpressions.Regex LocaleCodeRegex =
            new("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly Lazy<HashSet<string>> EmbeddedLocales = new(() =>
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // typeof(ProfilesController), not typeof(Plugin): same assembly, but naming
            // Plugin forces its base type out of MediaBrowser.Common to load, which makes
            // this unreachable from a test harness that has only the plugin DLL.
            foreach (var name in typeof(ProfilesController).Assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(I18nResourcePrefix, StringComparison.Ordinal)) continue;
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var code = name.Substring(
                    I18nResourcePrefix.Length,
                    name.Length - I18nResourcePrefix.Length - ".json".Length);

                // The name comes from the build, not from a request, but a malformed one
                // would still be published to every client as a locale to go and fetch.
                if (LocaleCodeRegex.IsMatch(code)) found.Add(code);
            }
            return found;
        });


        private static readonly ConcurrentDictionary<string, string?> CachedI18nJson = new();

        [HttpGet("i18n/{locale}")]
        [Produces("application/json")]
        public ActionResult GetI18n(string locale)
        {
            // profiles.js requests "fr.json"; strip the extension so the lookup key
            // matches SupportedI18nLocales and the embedded resource name either way.
            var code = locale.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? locale[..^5]
                : locale;

            // Checked before the cache is touched, so an unknown code cannot grow the
            // dictionary — the key would otherwise be whatever the caller sent.
            if (!EmbeddedLocales.Value.Contains(code)) return NotFound();

            // Loaded once per app lifetime, the same reasoning as CachedProfilesJs above.
            var json = CachedI18nJson.GetOrAdd(code, key =>
            {
                var assembly = typeof(Plugin).Assembly;
                using var stream = assembly.GetManifestResourceStream($"Jellyfin.Profiles.Web.i18n.{key}.json");
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });

            if (json == null) return NotFound();

            // Same cache contract as profiles.js: the plugin version is the cache-buster,
            // so a stale copy still picks up an updated translation within max-age.
            var etag = "\"" + GetPluginVersion() + "-" + code + "\"";
            Response.Headers["ETag"] = etag;
            Response.Headers["Cache-Control"] = "public, max-age=300, must-revalidate";

            if (string.Equals(Request.Headers["If-None-Match"].ToString(), etag, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(json, "application/json");
        }

        // ── Bonfire Codes ──────────────────────────────────────────────────────────

        [HttpGet("bonfire/status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetBonfireStatus()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            Guid masterId = currentMapping != null ? currentMapping.MasterUserId : masterUserId;

            var ownedGroup = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
            var joinedGroup = config.BonfireGroups.FirstOrDefault(g => g.MemberUserIds.Contains(masterId));
            var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);

            return Ok(new
            {
                IsOwner = ownedGroup != null,
                OwnedCode = ownedGroup?.BonfireCode,
                OwnedMembers = ownedGroup != null ? GetBonfireGroupMembers(ownedGroup, config) : null,
                IsMember = joinedGroup != null,
                JoinedOwnerName = joinedGroup != null ? (_userManager.GetUserById(joinedGroup.OwnerUserId)?.Username ?? "Unknown") : null,
                JoinedOwnerId = joinedGroup?.OwnerUserId,
                HideMySubProfilesFromOthers = masterMapping?.HideMySubProfilesFromOthers ?? false,
                HideOthersSubProfilesFromMe = masterMapping?.HideOthersSubProfilesFromMe ?? false,
                AllowHouseholdLanBypass = masterMapping?.AllowHouseholdLanBypass ?? false,
                // Both drive the wording of the warning next to the LAN-bypass toggle: entering
                // an administrator account hands over server management, and an account with no
                // PIN has nothing else protecting it once the bypass is on.
                IsAdministrator = IsUserAdministrator(masterId),
                HasPin = !string.IsNullOrEmpty(masterMapping?.PinHash)
            });
        }

        [HttpPost("bonfire/generate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GenerateBonfireCode()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            if (currentMapping != null && currentMapping.MasterUserId != masterUserId)
                return Unauthorized("Only the master profile can manage Bonfire groups.");

            string groupId;
            string bonfireCode;
            List<object> members;

            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterUserId);
                if (group == null)
                {
                    group = new BonfireGroup
                    {
                        GroupId = Guid.NewGuid().ToString("N").Substring(0, 8),
                        OwnerUserId = masterUserId,
                        BonfireCode = GenerateSecureCode()
                    };
                    config.BonfireGroups.Add(group);
                }
                else
                {
                    if (string.IsNullOrEmpty(group.GroupId))
                        group.GroupId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    if (string.IsNullOrEmpty(group.BonfireCode))
                        group.BonfireCode = GenerateSecureCode();
                }

                Plugin.Instance?.SaveConfiguration();
                groupId = group.GroupId;
                bonfireCode = group.BonfireCode;
                members = GetBonfireGroupMembers(group, config);
            }

            return Ok(new { GroupId = groupId, BonfireCode = bonfireCode, Members = members });
        }

        [HttpPost("bonfire/join")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult JoinBonfire([FromBody] JoinBonfireRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            Guid masterId = currentMapping != null ? currentMapping.MasterUserId : masterUserId;

            if (currentMapping != null && currentMapping.MasterUserId != masterUserId)
                return Unauthorized("Only the master profile can join Bonfire groups.");

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            if (RateLimiter.Bonfire.IsRateLimited(ip))
                return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed attempts. Please try again in 15 minutes.");

            var code = request.Code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                RateLimiter.Bonfire.RecordFailure(ip);
                return BadRequest("Invalid code format.");
            }

            Guid ownerUserId;
            bool newlyJoined = false;

            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g =>
                    string.Equals(g.BonfireCode, code, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    RateLimiter.Bonfire.RecordFailure(ip);
                    return BadRequest("Invalid Bonfire Code.");
                }

                if (group.OwnerUserId == masterId)
                    return BadRequest("You cannot join your own Bonfire group.");

                if (group.MemberUserIds.Contains(masterId))
                    return Ok(new { Message = "Already a member of this group." });

                foreach (var g in config.BonfireGroups)
                    g.MemberUserIds.Remove(masterId);

                group.MemberUserIds.Add(masterId);
                Plugin.Instance?.SaveConfiguration();
                ownerUserId = group.OwnerUserId;
                newlyJoined = true;
            }

            if (newlyJoined)
                RateLimiter.Bonfire.Reset(ip);

            return Ok(new
            {
                Message = "Successfully joined Bonfire group.",
                OwnerName = _userManager.GetUserById(ownerUserId)?.Username ?? "Unknown"
            });
        }

        [HttpPost("bonfire/kick")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult KickBonfireMember([FromBody] KickBonfireRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var callerMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (callerMapping != null && callerMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can manage Bonfire groups.");

            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
                if (group == null) return BadRequest("You do not own a Bonfire group.");

                if (group.MemberUserIds.Contains(request.MemberId))
                {
                    group.MemberUserIds.Remove(request.MemberId);
                    Plugin.Instance?.SaveConfiguration();
                    return Ok();
                }
            }

            return NotFound("Member not found in your Bonfire group.");
        }

        [HttpPost("bonfire/leave")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult LeaveBonfire()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            lock (ConfigLock)
            {
                var joinedGroup = config.BonfireGroups.FirstOrDefault(g => g.MemberUserIds.Contains(masterId));
                if (joinedGroup != null)
                {
                    joinedGroup.MemberUserIds.Remove(masterId);
                    Plugin.Instance?.SaveConfiguration();
                    return Ok();
                }
            }

            return BadRequest("You are not in any Bonfire group.");
        }

        [HttpPost("bonfire/delete-group")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult DeleteBonfireGroup()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
                if (group != null)
                {
                    config.BonfireGroups.Remove(group);
                    Plugin.Instance?.SaveConfiguration();
                    return Ok();
                }
            }

            return BadRequest("You do not own a Bonfire group.");
        }

        [HttpPost("bonfire/settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult UpdateBonfireSettings([FromBody] UpdateBonfireSettingsRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            if (currentMapping != null && currentMapping.MasterUserId != masterUserId)
                return Unauthorized("Only the master profile can update Bonfire settings.");

            lock (ConfigLock)
            {
                var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
                if (masterMapping == null)
                {
                    masterMapping = new ProfileMapping
                    {
                        ProfileUserId = masterUserId,
                        MasterUserId = masterUserId,
                        ProfileName = _userManager.GetUserById(masterUserId)?.Username ?? "Master",
                        IsHidden = false
                    };
                    config.Mappings.Add(masterMapping);
                }

                masterMapping.HideMySubProfilesFromOthers = request.HideMySubProfilesFromOthers;
                masterMapping.HideOthersSubProfilesFromMe = request.HideOthersSubProfilesFromMe;

                if (request.AllowHouseholdLanBypass.HasValue
                    && request.AllowHouseholdLanBypass.Value != masterMapping.AllowHouseholdLanBypass)
                {
                    masterMapping.AllowHouseholdLanBypass = request.AllowHouseholdLanBypass.Value;

                    // Logged at Information because this is the one Bonfire setting that widens
                    // access to the account rather than narrowing what others can see.
                    _logger.LogInformation(
                        "ProfilesPlugin: Household LAN bypass {State} for account {Account} by its owner.",
                        masterMapping.AllowHouseholdLanBypass ? "enabled" : "disabled",
                        masterUserId);
                }

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        // ── Per-account preferences ────────────────────────────────────────────────
        // Settings that are a matter of taste rather than policy, chosen by the account
        // holder and not by the server administrator. Stored on the master's own mapping so
        // they follow the user to every device they sign in on.

        [HttpGet("preferences")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetPreferences()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid currentUserId = currentUserIdVal.Value;

            // A sub-profile reads its master's preference: the switcher behaves the same way
            // everywhere in the household rather than changing as you move between profiles.
            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            var (askOnStartup, location) = SwitcherLocations.Resolve(masterMapping, config.DefaultAskOnStartup, config.DefaultSwitcherLocation);

            return Ok(new
            {
                AskOnStartup = askOnStartup,
                SwitcherLocation = location,
                // Derived, for any client still reading the 1.3.1-beta field. It cannot
                // express "ask on startup + menu", which is the whole point of the split, so
                // such a client sees the nearest equivalent rather than something incoherent.
                SwitcherMode = askOnStartup ? SwitcherModes.Gate : SwitcherModes.Native,
                // The client caches these in localStorage to decide whether to raise the gate
                // before this call returns. Echoing the account they belong to lets it throw
                // the cache away when a different user signs in on the same browser.
                MasterUserId = masterUserId,
                // Whether the switcher should offer the emergency disable link at all. Told
                // to signed-in users only, and it says nothing about what the code is.
                EmergencyCodeConfigured = !string.IsNullOrEmpty(config.PanicCodeHash)
            });
        }

        [HttpPost("preferences")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid currentUserId = currentUserIdVal.Value;

            // Unlike reads, only the account holder may write — a sub-profile changing this
            // would silently rewrite the whole household's experience.
            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            if (currentMapping != null && currentMapping.MasterUserId != currentUserId)
                return Unauthorized("Only the master profile can change switcher preferences.");

            bool askOnStartup;
            string location;
            lock (ConfigLock)
            {
                var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
                if (masterMapping == null)
                {
                    masterMapping = new ProfileMapping
                    {
                        ProfileUserId = currentUserId,
                        MasterUserId = currentUserId,
                        ProfileName = _userManager.GetUserById(currentUserId)?.Username ?? "Master",
                        IsHidden = false
                    };
                    config.Mappings.Add(masterMapping);
                }

                // A cached 1.3.1-beta script posts only switcherMode. Expand it, but let the
                // newer fields win when both arrive, so a client that knows about the split
                // is never overruled by a legacy value it sent for compatibility.
                if (request.SwitcherMode != null)
                {
                    bool native = SwitcherModes.Normalize(request.SwitcherMode) == SwitcherModes.Native;
                    masterMapping.AskOnStartup = !native;
                    masterMapping.SwitcherLocation = native ? SwitcherLocations.Menu : SwitcherLocations.Button;
                }

                if (request.AskOnStartup.HasValue)
                    masterMapping.AskOnStartup = request.AskOnStartup.Value;

                if (request.SwitcherLocation != null)
                    masterMapping.SwitcherLocation = SwitcherLocations.Normalize(request.SwitcherLocation);

                (askOnStartup, location) = SwitcherLocations.Resolve(masterMapping, config.DefaultAskOnStartup, config.DefaultSwitcherLocation);
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok(new
            {
                AskOnStartup = askOnStartup,
                SwitcherLocation = location,
                SwitcherMode = askOnStartup ? SwitcherModes.Gate : SwitcherModes.Native
            });
        }

        /// <summary>
        /// Lists the pictures in a folder on the server, so an administrator with a prepared
        /// set can import it instead of uploading file by file (GitHub issue #14).
        /// <para>
        /// Listing and reading are split from importing on purpose. The plugin has no
        /// server-side image library and deliberately keeps it that way — every resize in
        /// Bonfire happens on a canvas in the browser. So the browser reads each file through
        /// <c>admin/avatars/scan/file</c>, runs it through the same pipeline an upload uses,
        /// and posts the result back to <c>admin/avatars</c>. The server never decodes an
        /// image, and one code path produces every stored avatar.
        /// </para>
        /// </summary>
        [HttpGet("admin/avatars/scan")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> ScanAvatarFolder([FromQuery] string? path)
        {
            var adminError = RequireAdministrator("import avatars from a folder");
            if (adminError != null) return adminError;

            if (string.IsNullOrWhiteSpace(path)) return BadRequest("Enter a folder path.");

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: Avatar scan path could not be resolved.");
                return BadRequest("That path is not valid.");
            }

            if (!Directory.Exists(full)) return BadRequest("No folder there. Check the path as the server sees it.");

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(full)
                    .Where(f => StorableImageExtensions.Contains(
                        Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxScanFiles + 1)
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return BadRequest("Jellyfin cannot read that folder.");
            }
            catch (IOException ex)
            {
                return BadRequest("That folder could not be read: " + ex.Message);
            }

            bool truncated = files.Count > MaxScanFiles;
            if (truncated) files = files.Take(MaxScanFiles).ToList();

            return Ok(new
            {
                Folder = full,
                Truncated = truncated,
                // Length is read defensively: the folder is on a live filesystem and a
                // file removed between the listing and here would otherwise throw out of
                // the handler as a 500, past the error messages above.
                Files = files.Select(f => new
                {
                    Name = Path.GetFileName(f),
                    Size = FileLengthOrZero(f)
                }).ToList()
            });
        }

        /// <summary>
        /// Returns one file from a scanned folder so the browser can resize it. Administrator
        /// only, and the name is treated as a name rather than a path.
        /// </summary>
        [HttpGet("admin/avatars/scan/file")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult ReadAvatarFolderFile([FromQuery] string? path, [FromQuery] string? name)
        {
            var adminError = RequireAdministrator("import avatars from a folder");
            if (adminError != null) return adminError;

            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name)) return BadRequest("Missing file.");

            // A name, not a path. Rejecting separators outright is clearer than normalising
            // them away, and the containment check below is the belt to this pair of braces.
            if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..", StringComparison.Ordinal))
            {
                return BadRequest("Invalid file name.");
            }

            var extension = Path.GetExtension(name);
            if (!StorableImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest("Not a supported image.");
            }

            string folder, file;
            try
            {
                folder = Path.GetFullPath(path);
                file = Path.GetFullPath(Path.Combine(folder, name));
            }
            catch
            {
                return BadRequest("That path is not valid.");
            }

            // The resolved file must still be inside the resolved folder.
            var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid file name.");
            if (!System.IO.File.Exists(file)) return NotFound();

            var info = new FileInfo(file);
            if (info.Length > MaxScanFileBytes) return BadRequest("That image is too large to import.");

            return File(System.IO.File.ReadAllBytes(file), ContentTypeForExtension(extension));
        }

        // ── Library tile artwork (GitHub issue #19) ────────────────────────────────
        //
        // Jellyfin builds one image per library and caches it on the folder, from a query
        // that has no user attached — so a profile restricted to children's films still gets
        // a tile drawn from whatever else lives in that library. There is no per-user image
        // for the server to hand out, so the client substitutes; these endpoints are the
        // storage and the source of truth behind that.

        /// <summary>
        /// The calling profile's own artwork choices. Only entries that change something are
        /// returned — an untouched library inherits Jellyfin's artwork and is simply absent.
        /// </summary>
        [HttpGet("library-artwork")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<object>> GetMyLibraryArtwork()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            return Ok(DescribeLibraryArtwork(config, currentUserIdVal.Value));
        }

        /// <summary>
        /// One profile's choices, for the edit form. Restricted to that profile's master, the
        /// same rule that governs every other profile setting.
        /// </summary>
        [HttpGet("library-artwork/{profileId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<object>> GetProfileLibraryArtwork(Guid profileId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var denied = DenyUnlessProfileOwner(config, currentUserIdVal.Value, profileId);
            if (denied != null) return denied;

            return Ok(DescribeLibraryArtwork(config, profileId));
        }

        /// <summary>Sets or clears one profile's artwork for one library.</summary>
        [HttpPost("library-artwork")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> SetLibraryArtwork([FromBody] LibraryArtworkRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid currentUserId = currentUserIdVal.Value;

            if (!Guid.TryParse(request.ProfileId, out var profileId)) return BadRequest("Missing profile.");
            if (!Guid.TryParse(request.LibraryId, out var libraryId)) return BadRequest("Missing library.");

            var denied = DenyUnlessProfileOwner(config, currentUserId, profileId);
            if (denied != null) return denied;

            // The master PIN guards profile edits, and artwork is one of them.
            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;
            var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            if (masterMapping != null && !string.IsNullOrEmpty(masterMapping.PinHash))
            {
                if (!VerifyPinAndUpgrade(request.MasterPin, masterMapping, config))
                {
                    return BadRequest("Invalid Master PIN code.");
                }
            }

            // The library has to be one Jellyfin actually knows about. Without this the id is
            // just a string that ends up in a filename.
            bool known = _libraryManager.GetVirtualFolders()
                .Any(f => Guid.TryParse(f.ItemId, out var id) && id == libraryId);
            if (!known) return BadRequest("That library no longer exists.");

            string mode = LibraryArtworkModes.Normalize(request.Mode);
            string baseName = LibraryArtName(profileId, libraryId);

            if (mode == LibraryArtworkModes.Custom)
            {
                if (!string.IsNullOrEmpty(request.AvatarLibraryId))
                {
                    if (!CopyLibraryAvatar(request.AvatarLibraryId, LibraryArtFolder, baseName))
                    {
                        return BadRequest("That picture is no longer available.");
                    }
                }
                else if (!string.IsNullOrEmpty(request.Image))
                {
                    // Same rule as profile pictures: when the administrator has curated a set,
                    // an arbitrary upload is not an option here either.
                    if (AreCustomAvatarsBlocked())
                    {
                        return BadRequest("Only pictures from the avatar library can be used.");
                    }

                    Directory.CreateDirectory(LibraryArtFolder);
                    var ext = WriteImageFiles(LibraryArtFolder, baseName, request.Image, request.Thumb,
                        "library artwork for profile " + profileId);
                    if (ext == null) return BadRequest("That image could not be saved.");
                }
                else if (FindImageFile(LibraryArtFolder, baseName, false) == null)
                {
                    // Nothing supplied now and nothing stored before.
                    return BadRequest("Choose a picture first.");
                }
            }
            else
            {
                // Inherit and none both mean "stop serving a picture for this pair".
                DeleteImageFiles(LibraryArtFolder, baseName);
            }

            lock (ConfigLock)
            {
                var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
                if (mapping == null)
                {
                    // A master account has no mapping row until one of its settings is
                    // changed for the first time. Refusing here meant a master could never
                    // set artwork for its own libraries, and every attempt left the image
                    // files behind. Sub-profiles always have a row, so this only ever
                    // creates the master one.
                    var owner = _userManager.GetUserById(profileId);
                    if (owner == null) return NotFound("Profile not found.");

                    mapping = new ProfileMapping
                    {
                        ProfileUserId = profileId,
                        MasterUserId = profileId,
                        ProfileName = owner.Username
                    };
                    config.Mappings.Add(mapping);
                }

                if (mapping.LibraryArtwork == null) mapping.LibraryArtwork = new List<LibraryArtwork>();
                var entry = mapping.LibraryArtwork.FirstOrDefault(a => a.LibraryId == libraryId);

                if (mode == LibraryArtworkModes.Inherit)
                {
                    // Inherit is the default, so it is stored as absence rather than as a row.
                    if (entry != null) mapping.LibraryArtwork.Remove(entry);
                }
                else if (entry == null)
                {
                    mapping.LibraryArtwork.Add(new LibraryArtwork { LibraryId = libraryId, Mode = mode });
                }
                else
                {
                    entry.Mode = mode;
                }

                Plugin.Instance?.SaveConfiguration();
            }

            _logger.LogInformation(
                "ProfilesPlugin: Library artwork for profile {Profile}, library {Library} set to {Mode}.",
                profileId, libraryId, mode);

            return Ok(new { Mode = mode, Url = LibraryArtUrl(profileId, libraryId, mode) });
        }

        /// <summary>Serves a stored library picture. Anonymous, like the other image routes.</summary>
        [HttpGet("library-art/{profileId}/{libraryId}")]
        public ActionResult GetLibraryArtImage(Guid profileId, Guid libraryId, [FromQuery] string? size = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return NotFound();

            // Serve only what the configuration says is in use. The ids are parsed as GUIDs,
            // so they cannot carry a path, but a file left behind by an old choice should not
            // stay reachable either.
            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            var entry = mapping?.LibraryArtwork?.FirstOrDefault(a => a.LibraryId == libraryId);
            if (entry == null || LibraryArtworkModes.Normalize(entry.Mode) != LibraryArtworkModes.Custom)
            {
                return NotFound();
            }

            bool wantThumb = string.Equals(size, "thumb", StringComparison.OrdinalIgnoreCase);
            var found = FindImageFile(LibraryArtFolder, LibraryArtName(profileId, libraryId), wantThumb);
            if (found == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Library artwork for profile {Profile}, library {Library} is set but its file is missing.",
                    profileId, libraryId);
                return NotFound();
            }

            return File(System.IO.File.ReadAllBytes(found.Value.Path), found.Value.ContentType);
        }

        /// <summary>
        /// Only the master of a profile may read or change its settings — the same rule the
        /// create, update and delete endpoints apply. A master is its own owner.
        /// </summary>
        private ActionResult? DenyUnlessProfileOwner(PluginConfiguration config, Guid callerId, Guid profileId)
        {
            var callerMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == callerId);
            Guid masterUserId = callerMapping != null ? callerMapping.MasterUserId : callerId;

            if (callerId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            if (profileId == masterUserId) return null;

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            if (mapping == null || mapping.MasterUserId != masterUserId)
            {
                return Unauthorized("That profile does not belong to this account.");
            }

            return null;
        }

        private static long FileLengthOrZero(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static string? LibraryArtUrl(Guid profileId, Guid libraryId, string mode)
            => mode == LibraryArtworkModes.Custom
                ? "/plugins/profiles/library-art/" + profileId + "/" + libraryId + "?v=" + DateTime.UtcNow.Ticks
                : null;

        private static IEnumerable<object> DescribeLibraryArtwork(PluginConfiguration config, Guid profileId)
        {
            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            var entries = mapping?.LibraryArtwork ?? new List<LibraryArtwork>();

            return entries.Select(a =>
            {
                string mode = LibraryArtworkModes.Normalize(a.Mode);
                return (object)new
                {
                    LibraryId = a.LibraryId,
                    Mode = mode,
                    Url = LibraryArtUrl(profileId, a.LibraryId, mode)
                };
            }).ToList();
        }

        // ── Device Management ──────────────────────────────────────────────────────

        [HttpGet("devices")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<KnownDevice>> GetDevices()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid currentUserId = currentUserIdVal.Value;

            // Resolve to the master account (a sub-profile calling this should see
            // its master's devices, not its own — sub-profiles don't have sessions).
            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            // Every device already whitelisted on one of this account's profiles MUST appear in
            // the picker, whether or not it is currently switched on. The edit form rebuilds
            // AllowedDeviceIds from the checkboxes it rendered, so any whitelisted device that
            // is missing from this response gets silently dropped on the next save — and a
            // whitelist that empties out stops restricting anything at all.
            var whitelistedDeviceIds = config.Mappings
                .Where(m => m.MasterUserId == masterUserId && m.AllowedDeviceIds != null)
                .SelectMany(m => m.AllowedDeviceIds!)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Claim ownership of records written before MasterUserId existed, using this
            // household's live sessions. KnownDevices is a single server-wide list, so
            // unowned records must be attributed here rather than shown to everyone —
            // treating Guid.Empty as "visible to all" leaked every device on the server to
            // every account, because on an existing install *every* record is Guid.Empty.
            var householdUserIds = new HashSet<Guid> { masterUserId };
            foreach (var m in config.Mappings.Where(m => m.MasterUserId == masterUserId))
            {
                householdUserIds.Add(m.ProfileUserId);
            }

            var sessionDeviceIds = _sessionManager.Sessions
                .Where(s => householdUserIds.Contains(s.UserId))
                .Select(s => s.DeviceId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            lock (ConfigLock)
            {
                var claimed = false;
                foreach (var d in config.KnownDevices)
                {
                    if (d.MasterUserId == Guid.Empty
                        && (sessionDeviceIds.Contains(d.DeviceId) || whitelistedDeviceIds.Contains(d.DeviceId)))
                    {
                        d.MasterUserId = masterUserId;
                        claimed = true;
                    }
                }
                if (claimed) Plugin.Instance?.SaveConfiguration();
            }

            List<KnownDevice> devices;
            lock (ConfigLock)
            {
                devices = ScopeDevicesToHousehold(config.KnownDevices, masterUserId, whitelistedDeviceIds);
            }

            return Ok(devices);
        }

        [HttpPost("devices/delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult DeleteDevice([FromBody] DeleteDeviceRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can delete devices.");

            if (string.IsNullOrEmpty(request.DeviceId))
                return BadRequest("DeviceId is required.");

            lock (ConfigLock)
            {
                var toRemove = config.KnownDevices
                    .Where(d => string.Equals(d.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var d in toRemove)
                    config.KnownDevices.Remove(d);

                foreach (var mapping in config.Mappings)
                    mapping.AllowedDeviceIds?.RemoveAll(id =>
                        string.Equals(id, request.DeviceId, StringComparison.OrdinalIgnoreCase));

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        // ── Profile Image (unauthenticated) ────────────────────────────────────────

        /// <summary>
        /// Serves a profile's stored avatar image.
        ///
        /// Intentionally unauthenticated: the URL is used directly as an &lt;img src&gt;, and
        /// browsers do not attach the Authorization header to image requests. This mirrors
        /// how Jellyfin serves its own user images (/Users/{id}/Images/Primary) — the GUID
        /// is the capability, and the content is a low-sensitivity avatar.
        /// </summary>
        /// <param name="size">Pass <c>thumb</c> for the small variant used by grids.</param>
        [HttpGet("image/{profileId}")]
        public ActionResult GetProfileImage(Guid profileId, [FromQuery] string? size = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return NotFound();

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            if (mapping == null || string.IsNullOrEmpty(mapping.ProfileImage)) return NotFound();

            if (Plugin.Instance == null) return NotFound();

            bool wantThumb = string.Equals(size, "thumb", StringComparison.OrdinalIgnoreCase);
            var found = FindImageFile(ProfileImageFolder, profileId.ToString(), wantThumb);

            if (found == null)
            {
                // The mapping says there is a picture but the file is gone — a manual
                // deletion, a failed restore, or a half-migrated data directory. Logged so
                // the cause is discoverable; the dashboard surfaces a count separately, and
                // the client falls back to the initial-and-colour tile.
                _logger.LogWarning(
                    "ProfilesPlugin: Profile {Id} references an image that is missing from {Folder}.",
                    profileId, ProfileImageFolder);
                return NotFound();
            }

            // No redirect for externally hosted images: this endpoint is anonymous, so
            // forwarding to a stored URL would turn it into an open redirect. Clients render
            // http(s) avatars straight from the URL in the profile list instead.
            return File(System.IO.File.ReadAllBytes(found.Value.Path), found.Value.ContentType);
        }

        // ── Emergency disable ──────────────────────────────────────────────────────
        // If the switcher breaks badly it can make the Jellyfin web interface hard to use —
        // including the settings page needed to uninstall the plugin. This is an escape
        // hatch for that: an administrator sets a long code in advance, and entering it
        // anywhere in the client shuts the plugin's script down until Jellyfin restarts.
        //
        // What it does NOT do is widen anyone's access to content. Library access, parental
        // ratings and tag filters are all enforced in Jellyfin's own user policy server-side,
        // and /switch still demands the target profile's PIN. The real exposure is narrower
        // and worth stating plainly: the gate is what stands between "signed in as the master
        // account" and "must pick a profile", so anyone who knows the code can skip that
        // prompt on a device already signed in to the master account.
        //
        // Hence: off by default, hashed like a PIN, validated only here, rate limited hard,
        // and logged loudly.

        /// <summary>
        /// Validates an emergency disable code and, if it matches, disables the plugin's
        /// client script until the server restarts.
        /// <para>
        /// Deliberately unauthenticated. The whole point is to work when the interface is
        /// broken — an administrator locked behind a failed switcher may be holding a
        /// sub-profile's token, or none at all, so requiring admin rights here would make the
        /// feature useless exactly when it is needed.
        /// </para>
        /// </summary>
        [HttpPost("panic")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<object> Panic([FromBody] PanicRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

            // Rate limiting comes first, before the configured/not-configured branch below.
            // Checking it afterwards would make an armed server answer 429 on the sixth
            // attempt while an unarmed one kept answering 400 — the very oracle the matching
            // error messages exist to close.
            if (RateLimiter.Panic.IsRateLimited(ip))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Emergency disable code rate limit hit from {Ip}. Someone is guessing.", ip);
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    "Too many attempts. Try again in an hour, or restart Jellyfin.");
            }

            // Nothing to match against. Answer exactly as a wrong code does, so the response
            // cannot be used to discover whether a server has the feature armed.
            if (string.IsNullOrEmpty(config.PanicCodeHash) || !VerifyPinHash(request.Code, config.PanicCodeHash))
            {
                RateLimiter.Panic.RecordFailure(ip);
                return BadRequest("Incorrect code.");
            }

            RateLimiter.Panic.Reset(ip);
            Plugin.TripPanicDisable();

            // Error level so it stands out in the log without anyone having to know what to
            // look for: this is a deliberate, unauthenticated shutdown of a security feature.
            _logger.LogError(
                "ProfilesPlugin: EMERGENCY DISABLE activated from {Ip} (device '{Device}', client '{Client}'). " +
                "The profile switcher is now inert and will stay that way until Jellyfin is restarted.",
                ip,
                GetAuthorizationParameter("Device") ?? "unknown",
                GetAuthorizationParameter("Client") ?? "unknown");

            return Ok(new { Disabled = true });
        }

        /// <summary>
        /// Whether the plugin is currently disabled. Unauthenticated and deliberately
        /// minimal: it reveals only what is already obvious from the client's behaviour, and
        /// nothing about whether a code is configured.
        /// <para>
        /// Exists because profiles.js is served with a five-minute cache. A browser reloading
        /// inside that window can re-run the real script from cache after a disable, so the
        /// script asks this on startup whenever it finds its own local disable marker — and
        /// clears the marker when the server says the plugin is back.
        /// </para>
        /// </summary>
        [HttpGet("panic-state")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetPanicState()
        {
            Response.Headers["Cache-Control"] = "no-store";
            return Ok(new { Disabled = Plugin.IsPanicDisabled });
        }

        /// <summary>Whether a code is configured, and whether it has been used this run.</summary>
        [HttpGet("admin/panic-status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetPanicStatus()
        {
            var adminError = RequireAdministrator("view the emergency disable settings");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // The code itself is never returned — it is stored as a PBKDF2 hash and cannot be
            // read back even here.
            return Ok(new
            {
                IsConfigured = !string.IsNullOrEmpty(config.PanicCodeHash),
                IsCurrentlyDisabled = Plugin.IsPanicDisabled
            });
        }

        [HttpPost("admin/panic-code")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult SetPanicCode([FromBody] PanicRequest request)
        {
            var adminError = RequireAdministrator("change the emergency disable code");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // Empty clears it, which is how the feature is turned back off.
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                lock (ConfigLock)
                {
                    config.PanicCodeHash = null;
                    Plugin.Instance?.SaveConfiguration();
                }
                _logger.LogInformation("ProfilesPlugin: Emergency disable code cleared.");
                return Ok();
            }

            var code = request.Code.Trim();

            // Long, because this is submitted without authentication. The rate limiter caps
            // guessing at five an hour, but a four-digit code would still be reachable.
            if (code.Length < MinPanicCodeLength)
                return BadRequest($"The code must be at least {MinPanicCodeLength} characters.");

            if (code.Length > 128)
                return BadRequest("The code is too long.");

            lock (ConfigLock)
            {
                config.PanicCodeHash = HashPin(code);
                Plugin.Instance?.SaveConfiguration();
            }

            _logger.LogInformation("ProfilesPlugin: Emergency disable code set by an administrator.");
            return Ok();
        }

        // ── Avatar library ─────────────────────────────────────────────────────────
        // A set of pictures the administrator uploads once for everyone on the server to
        // choose from. The motivating case is TV: <input type="file"> is unusable on a
        // television, so before this the only ways to set a picture there were pasting a
        // URL with an on-screen keyboard, or giving up.

        /// <summary>
        /// The avatars anyone may choose from, plus whether custom uploads are still allowed.
        /// Metadata only — the images themselves come from <c>avatars/{id}</c>.
        /// </summary>
        [HttpGet("avatars")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetAvatarLibrary()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (GetCurrentUserId() == null) return Unauthorized();

            return Ok(new
            {
                AllowCustomUploads = !config.DisallowCustomAvatarUploads,
                Avatars = config.AvatarLibrary.Select(a => new
                {
                    a.Id,
                    a.DisplayName,
                    Url = $"/plugins/profiles/avatars/{a.Id}",
                    ThumbUrl = $"/plugins/profiles/avatars/{a.Id}?size=thumb"
                }).ToList()
            });
        }

        /// <summary>
        /// Serves a library avatar. Unauthenticated for the same reason as
        /// <see cref="GetProfileImage"/>: it is rendered as an &lt;img src&gt;, and browsers
        /// do not attach Authorization headers to image requests. These are pictures the
        /// administrator published to every user on the server, so there is nothing here
        /// that an authenticated user could not already fetch.
        /// </summary>
        [HttpGet("avatars/{id}")]
        public ActionResult GetLibraryAvatar(string id, [FromQuery] string? size = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return NotFound();

            // Look the id up rather than trusting it as a filename — it arrives from the
            // URL, and joining unvalidated input to a path is how directory traversal works.
            var item = config.AvatarLibrary.FirstOrDefault(a =>
                string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item == null) return NotFound();

            bool wantThumb = string.Equals(size, "thumb", StringComparison.OrdinalIgnoreCase);
            var found = FindImageFile(AvatarLibraryFolder, item.Id, wantThumb);
            if (found == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Library avatar {Id} ({Name}) is listed but its file is missing from {Folder}.",
                    item.Id, item.DisplayName, AvatarLibraryFolder);
                return NotFound();
            }

            return File(System.IO.File.ReadAllBytes(found.Value.Path), found.Value.ContentType);
        }

        // ── Admin Endpoints ────────────────────────────────────────────────────────

        /// <summary>
        /// Adds an image to the shared avatar library. The client supplies both a full-size
        /// and a thumbnail rendering; nothing is resized server-side.
        /// </summary>
        [HttpPost("admin/avatars")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> AddLibraryAvatar([FromBody] AddAvatarRequest request)
        {
            var adminError = RequireAdministrator("manage the avatar library");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            if (string.IsNullOrWhiteSpace(request.Image))
                return BadRequest("No image supplied.");

            // Hex rather than a raw GUID so the id reads cleanly in a URL and on disk.
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);

            string? extension;
            try
            {
                extension = WriteImageFiles(AvatarLibraryFolder, id, request.Image, request.Thumb, $"library avatar {id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProfilesPlugin: Failed to write library avatar {Id}.", id);
                return BadRequest("Could not save that image. Check the Jellyfin log for details.");
            }

            if (extension == null)
                return BadRequest("That image could not be read, or it is larger than the 2 MB limit.");

            var name = string.IsNullOrWhiteSpace(request.DisplayName)
                ? "Avatar"
                : request.DisplayName.Trim();
            if (name.Length > 60) name = name.Substring(0, 60);

            var item = new AvatarLibraryItem
            {
                Id = id,
                DisplayName = name,
                Extension = extension,
                UploadedUtc = DateTime.UtcNow
            };

            lock (ConfigLock)
            {
                config.AvatarLibrary.Add(item);
                Plugin.Instance?.SaveConfiguration();
            }

            _logger.LogInformation("ProfilesPlugin: Added library avatar {Id} ({Name}).", id, name);

            return Ok(new
            {
                item.Id,
                item.DisplayName,
                Url = $"/plugins/profiles/avatars/{item.Id}",
                ThumbUrl = $"/plugins/profiles/avatars/{item.Id}?size=thumb"
            });
        }

        [HttpDelete("admin/avatars/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteLibraryAvatar(string id)
        {
            var adminError = RequireAdministrator("manage the avatar library");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            lock (ConfigLock)
            {
                var item = config.AvatarLibrary.FirstOrDefault(a =>
                    string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item == null) return NotFound("No such avatar.");

                try
                {
                    DeleteImageFiles(AvatarLibraryFolder, item.Id);
                }
                catch (Exception ex)
                {
                    // Losing the file but keeping the entry would leave a permanently broken
                    // tile in the picker, so drop the entry either way and log the orphan.
                    _logger.LogWarning(ex,
                        "ProfilesPlugin: Could not delete files for library avatar {Id}; removing the entry anyway.",
                        item.Id);
                }

                config.AvatarLibrary.Remove(item);
                Plugin.Instance?.SaveConfiguration();
                _logger.LogInformation("ProfilesPlugin: Removed library avatar {Id} ({Name}).", item.Id, item.DisplayName);
            }

            // Profiles that chose this avatar keep their own copy of the image, so nothing
            // they see changes — only the picker loses the option.
            return Ok();
        }

        [HttpPost("admin/avatars/settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult UpdateAvatarSettings([FromBody] AvatarSettingsRequest request)
        {
            var adminError = RequireAdministrator("change avatar settings");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            lock (ConfigLock)
            {
                if (request.DisallowCustomAvatarUploads.HasValue)
                    config.DisallowCustomAvatarUploads = request.DisallowCustomAvatarUploads.Value;
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        /// <summary>
        /// Saves the six server-wide settings the plugin's settings page owns.
        /// </summary>
        /// <remarks>
        /// This exists because the settings page used to save through Jellyfin's generic
        /// plugin-configuration API: GET the entire PluginConfiguration, change six fields on
        /// the copy in the browser, PUT the whole thing back. Everything else in that document
        /// — every profile mapping, every known device, every Bonfire group, the avatar
        /// library and the emergency-disable hash — went along for the ride. A profile created
        /// while the settings page sat open was reverted the moment an administrator pressed
        /// Save, with no error and nothing in the log to connect the two.
        ///
        /// It also happened to be the one call that made Jellyfin replace the configuration
        /// instance, which is what orphaned every lock taken on it. Both problems have the
        /// same cure: send the six fields, mutate in place under ConfigLock.
        /// </remarks>
        [HttpPost("admin/settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult UpdateAdminSettings([FromBody] AdminSettingsRequest request)
        {
            var adminError = RequireAdministrator("change plugin settings");
            if (adminError != null) return adminError;

            if (request == null) return BadRequest("No settings were sent.");

            // Validate everything before touching anything, so a request with one bad field
            // is rejected whole rather than half-applied.
            if (request.MaxProfilesPerUser.HasValue)
            {
                var limitError = ValidateProfileLimit(request.MaxProfilesPerUser.Value);
                if (limitError != null) return BadRequest(limitError);
            }

            // Normalize() silently falls back to a default, which is right when reading a
            // configuration written by an older version and wrong when an administrator is
            // telling us what they want: saving "middleware" because they typed something
            // unrecognised looks like the setting did not take.
            if (request.DefaultSwitcherLocation != null
                && !string.Equals(request.DefaultSwitcherLocation, SwitcherLocations.Button, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.DefaultSwitcherLocation, SwitcherLocations.Menu, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"Switcher location must be '{SwitcherLocations.Button}' or '{SwitcherLocations.Menu}'.");
            }

            if (request.IndexInjectionMode != null
                && !string.Equals(request.IndexInjectionMode, IndexInjectionModes.File, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.IndexInjectionMode, IndexInjectionModes.Middleware, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.IndexInjectionMode, IndexInjectionModes.Both, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"Injection method must be '{IndexInjectionModes.File}', "
                                + $"'{IndexInjectionModes.Middleware}' or '{IndexInjectionModes.Both}'.");
            }

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                if (request.MaxProfilesPerUser.HasValue)
                    config.MaxProfilesPerUser = request.MaxProfilesPerUser.Value;
                if (request.RequireMasterPinForCreation.HasValue)
                    config.RequireMasterPinForCreation = request.RequireMasterPinForCreation.Value;
                if (request.DisallowCustomAvatarUploads.HasValue)
                    config.DisallowCustomAvatarUploads = request.DisallowCustomAvatarUploads.Value;
                if (request.DefaultAskOnStartup.HasValue)
                    config.DefaultAskOnStartup = request.DefaultAskOnStartup.Value;
                if (request.DefaultSwitcherLocation != null)
                    config.DefaultSwitcherLocation = SwitcherLocations.Normalize(request.DefaultSwitcherLocation);
                if (request.IndexInjectionMode != null)
                    config.IndexInjectionMode = IndexInjectionModes.Normalize(request.IndexInjectionMode);

                Plugin.Instance?.SaveConfiguration();
            }

            _logger.LogInformation("ProfilesPlugin: Plugin settings updated by an administrator.");
            return Ok();
        }

        [HttpPost("admin/set-profile-limit")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult SetProfileLimit([FromBody] SetProfileLimitRequest request)
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
                return Unauthorized("Only administrators can update profile limits.");

            // Checked before the lock, and against both bounds. This used to test only
            // `< 1`, so an override of two billion was accepted and then handed to the gate
            // as the number of tiles to lay out.
            if (request.MaxProfiles.HasValue)
            {
                var limitError = ValidateProfileLimit(request.MaxProfiles.Value);
                if (limitError != null) return BadRequest(limitError);
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            lock (ConfigLock)
            {
                if (request.MaxProfiles.HasValue)
                {
                    var existing = config.UserProfileLimitOverrides.FirstOrDefault(o => o.UserId == request.UserId);
                    if (existing != null)
                        existing.MaxProfiles = request.MaxProfiles.Value;
                    else
                        config.UserProfileLimitOverrides.Add(new UserProfileLimitOverride
                        {
                            UserId = request.UserId,
                            MaxProfiles = request.MaxProfiles.Value
                        });
                }
                else
                {
                    config.UserProfileLimitOverrides.RemoveAll(o => o.UserId == request.UserId);
                }

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        [HttpGet("admin/audit-logs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<AuditLogEntry>> GetAuditLogs()
        {
            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            var caller = _userManager.GetUserById(currentUserIdVal.Value);
            if (caller == null) return Unauthorized();

            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.IsAdministrator)
                return Unauthorized("Only administrators can view audit logs.");

            lock (AuditLogLock)
            {
                // Served from the in-memory cache so the dashboard reflects entries whose
                // background write may not have landed yet.
                return Ok(GetAuditLogSnapshot().OrderByDescending(l => l.Timestamp).ToList());
            }
        }

    }
}
