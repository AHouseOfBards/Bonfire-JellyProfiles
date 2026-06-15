using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Bonfire group endpoints — create/join/leave/kick/delete/settings.
    /// All helpers are inherited from ProfilesBaseController.
    /// </summary>
    [Route("plugins/profiles")]
    public class BonfireController : ProfilesBaseController
    {
        public BonfireController(
            IUserManager userManager,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            INetworkManager networkManager,
            ILogger<BonfireController> logger)
            : base(userManager, sessionManager, libraryManager, networkManager, logger)
        {
        }

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
                HideOthersSubProfilesFromMe = masterMapping?.HideOthersSubProfilesFromMe ?? false
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

            lock (config)
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

            lock (config)
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

                // Remove from any previously joined group first
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

            lock (config)
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

            lock (config)
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

            lock (config)
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

            lock (config)
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
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }
    }
}
