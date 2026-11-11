using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Devices;
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
    public partial class ProfilesController : ProfilesBaseController
    {
        public ProfilesController(
            IUserManager userManager,
            ISessionManager sessionManager,
            IDeviceManager deviceManager,
            ILibraryManager libraryManager,
            INetworkManager networkManager,
            ILogger<ProfilesController> logger)
            : base(userManager, sessionManager, deviceManager, libraryManager, networkManager, logger)
        {
        }


        [HttpGet("list")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<object>> GetProfiles()
        {
            _logger.LogDebug("ProfilesPlugin: GetProfiles endpoint called.");

            // A copy: this route only reads, and walks several lists while another request
            // may be editing them. See ReadConfigSnapshot.
            var config = ReadConfigSnapshot();
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
            // Remembered so the screens only offer "skip the PIN on my home network" to a
            // household it can apply to.
            bool seenLocally = NoteHouseholdSeenLocally(masterUserId, isLocal, config);

            var localMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            bool localHidesOthers = localMapping?.HideOthersSubProfilesFromMe ?? false;

            var linkedMasterIds = GetLinkedMasterUserIds(masterUserId, config);
            var addedAccounts = LinkedAccountsFor(masterUserId, config);
            var profileList = new List<object>();

            foreach (var linkedId in linkedMasterIds)
            {
                var linkedUser = _userManager.GetUserById(linkedId);
                if (linkedUser == null) continue;

                var linkedMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == linkedId);
                // Mirror the switch endpoint exactly, through the same function: reporting a
                // PIN-less switch the server then rejects strands the user on a screen with
                // nothing to do.
                //
                //  - Your own account: BypassPinOnLocalNetwork, or AskPinToOpen turned off.
                //  - A linked account: only AllowHouseholdLanBypass, which belongs to that
                //    account's owner. Your own settings never reach across a link.
                //  - An account also added to this household (issue #39): the added account's
                //    rules, which is what /switch applies to it.
                bool crossHousehold = linkedId != masterUserId;
                bool added = crossHousehold && addedAccounts.ContainsKey(linkedId);
                var addedRules = added
                    ? EvaluateSwitch(config.Mappings, masterUserId, linkedId, linkedMasterIds, isLocal, addedAccounts)
                    : null;
                bool masterRequiresPin = linkedMapping != null
                    && !string.IsNullOrEmpty(linkedMapping.PinHash)
                    && !CanSkipPin(linkedMapping, isLocal, crossHousehold,
                        householdLanBypass: addedRules != null
                            ? addedRules.HouseholdLanBypass
                            : isLocal && linkedMapping.AllowHouseholdLanBypass);

                profileList.Add(new
                {
                    ProfileUserId = linkedId,
                    ProfileName = linkedUser.Username,
                    AvatarInitial = string.IsNullOrEmpty(linkedUser.Username) ? "M" : linkedUser.Username.Substring(0, 1).ToUpper(),
                    AvatarColor = linkedMapping?.AvatarColor ?? "#00A4DC",
                    TransparentAvatar = linkedMapping?.TransparentAvatar ?? false,
                    // The stored choice, not the resolved one: the form shows "server default"
                    // for null. Owner only, like the other settings below.
                    Theme = currentUserId == linkedId ? linkedMapping?.Theme : null,
                    RequiresPin = masterRequiresPin,
                    HasPin = !string.IsNullOrEmpty(linkedMapping?.PinHash),
                    IsMaster = true,
                    // Only so the PIN field can warn that this PIN opens an account which
                    // can reach server settings. Sent for masters only; a sub-profile is
                    // never created with administrator rights.
                    IsAdministrator = IsAdministratorAccount(linkedUser),
                    LockoutMinutes = linkedMapping?.LockoutMinutes ?? 5,
                    MaxSubProfiles = GetMaxProfilesForUser(linkedId, config),
                    BypassPinOnLocalNetwork = linkedMapping?.BypassPinOnLocalNetwork ?? false,
                    SeenOnLocalNetwork = linkedId == masterUserId && seenLocally,
                    // Owner only. Another household is always asked for the PIN regardless.
                    AskPinToOpen = currentUserId == linkedId ? (linkedMapping?.AskPinToOpen ?? true) : (bool?)null,
                    AllowedDeviceIds = currentUserId == linkedId
                        ? (linkedMapping?.AllowedDeviceIds ?? new List<string>())
                        : new List<string>(),
                    IsBonfire = (linkedId != masterUserId),
                    // Shown with its Bonfire, profiles and all, even when also added here:
                    // only its PIN rule is the added account's.
                    IsLinked = false,
                    ProfileImage = ImageForViewer(linkedMapping?.ProfileImage, linkedId == masterUserId),
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
                    // The profile's restrictions are for the edit form, which only its own
                    // master can open. Everyone else — the household's other profiles, and
                    // every member of a linked Bonfire — used to receive them too, device ids
                    // included, and a device id is what a PIN-less profile is opened with on
                    // another app's sign-in screen.
                    bool ownerView = currentUserId == linkedId;

                    var mappingsSnapshot = config.Mappings.ToList();

                    // Add all shadow profiles for this master
                    var shadowProfiles = config.Mappings
                        .Where(m => m.MasterUserId == linkedId && m.ProfileUserId != linkedId)
                        .Select(m => (Row: m, Rules: EvaluateSwitch(
                            mappingsSnapshot, masterUserId, m.ProfileUserId, linkedMasterIds, isLocal, addedAccounts)))
                        // Mirror /switch exactly. A tile it would refuse — hidden by either
                        // household, or PIN-less in another household without its owner's
                        // consent — is a door that does not open.
                        .Where(x => !x.Rules.Unauthorized && x.Rules.Refusal == null)
                        .Select(x => {
                            var m = x.Row;
                            bool requiresPin = !string.IsNullOrEmpty(m.PinHash)
                                && !CanSkipPin(m, isLocal, x.Rules.CrossHousehold, x.Rules.HouseholdLanBypass);
                            return new
                            {
                                m.ProfileUserId,
                                m.ProfileName,
                                AvatarInitial = string.IsNullOrEmpty(m.ProfileName) ? "?" : m.ProfileName.Substring(0, 1).ToUpper(),
                                m.AvatarColor,
                                m.TransparentAvatar,
                                Theme = ownerView ? m.Theme : null,
                                RequiresPin = requiresPin,
                                // Whether a PIN EXISTS, independent of whether one will be
                                // asked for right now. RequiresPin goes false on the LAN when
                                // BypassPinOnLocalNetwork is set, so the edit form cannot use
                                // it to decide if a PIN is configured — doing so made a saved
                                // PIN look like it had never been stored.
                                HasPin = !string.IsNullOrEmpty(m.PinHash),
                                IsMaster = false,
                                m.LockoutMinutes,
                                EnabledFolders = ownerView ? (m.EnabledFolders ?? new List<Guid>()) : new List<Guid>(),
                                AllLibraries = ownerView && m.AllLibraries,
                                BlockedTags = ownerView ? (m.BlockedTags ?? new List<string>()) : new List<string>(),
                                AllowedTags = ownerView ? (m.AllowedTags ?? new List<string>()) : new List<string>(),
                                BypassPinOnLocalNetwork = m.BypassPinOnLocalNetwork,
                                SeenOnLocalNetwork = linkedId == masterUserId && seenLocally,
                                // Only so the delete confirmation can say the account stays.
                                KeepAccountOnDelete = ownerView && m.KeepAccountOnDelete,
                                AllowedDeviceIds = ownerView ? (m.AllowedDeviceIds ?? new List<string>()) : new List<string>(),
                                IsBonfire = (linkedId != masterUserId),
                                ProfileImage = ImageForViewer(m.ProfileImage, linkedId == masterUserId),
                                m.MasterUserId,
                                // Viewing hours and limits, for the tile's "45 min left". Own
                                // household only: another household's bedtimes are its own.
                                Viewing = linkedId == masterUserId ? ViewingSummary(m, config) : null
                            };
                        });

                    profileList.AddRange(shadowProfiles);
                }
            }

            // Accounts added to this household (issue #39), after its own and its Bonfire's.
            // The account only: its own profiles belong to its own household. One that is
            // also in a Bonfire with this household is already above.
            foreach (var addedId in addedAccounts.Keys.Where(id => !linkedMasterIds.Contains(id)))
            {
                var addedUser = _userManager.GetUserById(addedId);
                if (addedUser == null) continue;
                var rules = EvaluateSwitch(config.Mappings, masterUserId, addedId, linkedMasterIds, isLocal, addedAccounts);
                // A tile /switch would refuse is a door that does not open.
                if (rules.Unauthorized || rules.Refusal != null) continue;
                var row = rules.Target;
                bool hasPin = !string.IsNullOrEmpty(row?.PinHash);
                profileList.Add(new
                {
                    ProfileUserId = addedId,
                    ProfileName = addedUser.Username,
                    AvatarInitial = string.IsNullOrEmpty(addedUser.Username) ? "?" : addedUser.Username.Substring(0, 1).ToUpper(),
                    AvatarColor = row?.AvatarColor ?? "#00A4DC",
                    TransparentAvatar = row?.TransparentAvatar ?? false,
                    Theme = (string?)null,
                    RequiresPin = hasPin && !CanSkipPin(row, isLocal, true, rules.HouseholdLanBypass),
                    HasPin = hasPin,
                    IsMaster = true,
                    IsAdministrator = addedAccounts[addedId],
                    LockoutMinutes = row?.LockoutMinutes ?? 5,
                    BypassPinOnLocalNetwork = false,
                    AllowedDeviceIds = new List<string>(),
                    IsBonfire = true,
                    IsLinked = true,
                    ProfileImage = ImageForViewer(row?.ProfileImage, false),
                    MasterUserId = addedId
                });
            }

            // Apps ask for this every time the profile screen opens, and on a television
            // that is most of what they ask. An unchanged list costs them a 304.
            return ETagged(profileList);
        }

        /// <summary>
        /// A profile picture as another household may see it. A picture can be any http(s)
        /// address, and the gate draws it straight from there — so a linked household could
        /// point one at a server of its own and learn the address, browser and hours of
        /// everyone who opened the switcher. Pictures stored on this server are unaffected;
        /// an outside address is only drawn for its own household.
        /// </summary>
        private static string? ImageForViewer(string? image, bool ownHousehold)
            => ownHousehold || string.IsNullOrEmpty(image)
               || !(image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ? image
                : null;

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

            // A library another plugin manages access to is not Bonfire's to list: a profile form offering
            // it would grant or block it, which is the owner's to do.
            var folders = _libraryManager.GetVirtualFolders()
                .Where(f => !Auth.LibraryOwnership.ManagedByOwner(_libraryManager, f)).ToList();

            // Filter folders by caller's user policy
            var callerDto = _userManager.GetUserDto(caller, string.Empty);
            if (!callerDto.Policy.EnableAllFolders)
            {
                var enabled = callerDto.Policy.EnabledFolders ?? Array.Empty<Guid>();
                var blocked = callerDto.Policy.BlockedMediaFolders ?? Array.Empty<Guid>();
                folders = folders.Where(f => Guid.TryParse(f.ItemId, out var id) && enabled.Contains(id) && !blocked.Contains(id)).ToList();
            }

            // A library with no item id — never scanned, or its folder is gone — cannot be
            // granted to anybody, and listing it put an "undefined" checkbox in the form that
            // failed the whole create the moment it was ticked (issue #33).
            var libraries = folders
                .Where(f => Guid.TryParse(f.ItemId, out var parsed) && parsed != Guid.Empty)
                .Select(f => new
            {
                Id = f.ItemId,
                Name = f.Name,
                CollectionType = f.CollectionType
            });
            return Ok(libraries);
        }

        /// <summary>
        /// The rating limits the profile form offers: the server's Preferred metadata
        /// country's own ratings, one per age. See <see cref="RatingSystems"/> for why this
        /// does not just pass /Localization/ParentalRatings through.
        /// <para>
        /// The two services are asked for here rather than injected, so the controller's
        /// constructor — which every harness builds — does not change for one read-only
        /// route, and a server that cannot supply one still answers.
        /// </para>
        /// </summary>
        [HttpGet("ratings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetRatings()
        {
            if (GetCurrentUserId() == null) return Unauthorized();

            var services = HttpContext?.RequestServices;
            var localization = services?.GetService(typeof(MediaBrowser.Model.Globalization.ILocalizationManager));
            var serverConfig = services?.GetService(typeof(MediaBrowser.Controller.Configuration.IServerConfigurationManager))
                as MediaBrowser.Controller.Configuration.IServerConfigurationManager;
            var country = serverConfig?.Configuration?.MetadataCountryCode;

            var ratings = localization == null
                ? new List<RatingChoice>()
                : RatingSystems.ForCountry(localization.GetType().Assembly, country, _logger);

            return Ok(new { Country = country, Ratings = ratings });
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

            // The main PIN, like every other change to the household. There used to be an
            // administrator setting to let creation skip it; once every other route asked,
            // all it did was leave this one door open.
            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal)) return pinRefusal!;

            var nameError = ValidateProfileName(request.ProfileName) ?? ValidateProfileNameFree(_userManager, request.ProfileName);
            if (nameError != null) return BadRequest(nameError);

            // One creation at a time. The limit is counted before the account is created and
            // the mapping written after it, so two requests arriving together both counted
            // the same number and both got past a limit meant to stop the second.
            await CreateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await CreateProfileCore(request, config, masterUserId, request.ProfileName.Trim())
                    .ConfigureAwait(false);
            }
            finally
            {
                CreateGate.Release();
            }
        }

        private static readonly System.Threading.SemaphoreSlim CreateGate = new(1, 1);

        private async Task<ActionResult<object>> CreateProfileCore(
            CreateProfileRequest request, PluginConfiguration config, Guid masterUserId, string profileName)
        {
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
                if (!IsValidPin(request.Pin))
                {
                    return BadRequest("PIN code must be a numeric value between 4 and 8 digits.");
                }
            }

            var themeError = ValidateTheme(request.Theme, config);
            if (themeError != null) return BadRequest(themeError);

            // The same answers /update takes (R-1144). Checked before the account exists. A
            // rating that isn't a score was quietly read as no limit here too.
            if (request.AllLibraries == true && request.EnabledFolders != null)
                return BadRequest(LibrariesTwiceMessage);
            if (!string.IsNullOrEmpty(request.MaxParentalRating) && !int.TryParse(request.MaxParentalRating, out _))
                return BadRequest(RatingNotScoreMessage);

            // Reject an allow-list that shares no tags with the master's, which would leave the
            // profile with an empty library. Checked before the user is created so we don't
            // leave a half-built account behind.
            var allowedTagsError = ValidateAllowedTags(
                _userManager.GetUserDto(masterUser, string.Empty).Policy, request.AllowedTags);
            if (allowedTagsError != null)
            {
                return BadRequest(allowedTagsError);
            }

            // A picture that would be refused is refused now, before the account exists,
            // rather than after it: it used to be dropped silently and the profile made
            // without one (R-1108).
            var pictureError = !string.IsNullOrEmpty(request.AvatarLibraryId)
                ? CheckLibraryAvatar(request.AvatarLibraryId)
                : CheckProfileImage(Guid.Empty, request.ProfileImage, null);
            if (pictureError != null) return BadRequest(pictureError);

            // Standardize username to avoid global collisions
            string systemUsername = $"{masterUser.Username}_{profileName.Replace(" ", "")}";

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
            CopyUserPolicy(masterPolicy, targetPolicy, targetUser.Id);
            targetPolicy.IsAdministrator = false;
            targetPolicy.IsHidden = true;
            targetPolicy.IsDisabled = false;

            // The requested rating, never above the master's.
            if (!string.IsNullOrEmpty(request.MaxParentalRating) && int.TryParse(request.MaxParentalRating, out var rating))
            {
                targetPolicy.MaxParentalRating = rating;
                targetPolicy.MaxParentalSubRating = null;
            }
            (targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating) = ClampRating(
                targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating,
                masterPolicy.MaxParentalRating, masterPolicy.MaxParentalSubRating);

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
                // A new account has no access to a library another plugin manages, and gets none.
                validatedFolders = ApplyListedFolders(
                    _libraryManager, masterPolicy, ParseFolderIds(request.EnabledFolders) ?? new List<Guid>(), targetPolicy, keepOwned: false);
            }
            else
            {
                // Inherit blocked libraries and folder accessibility from master user
                validatedFolders = ApplyInheritedFolders(_libraryManager, masterPolicy, targetPolicy, keepOwned: false);
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
                    ProfileName = profileName,
                    PinHash = HashPin(request.Pin),
                    AvatarColor = IsAvatarColor(request.AvatarColor)
                        ? request.AvatarColor!.Trim()
                        : NextAvatarColor(masterUserId, config),
                    TransparentAvatar = request.TransparentAvatar ?? false,
                    // Only while profiles may choose. Stored otherwise, it would surface the
                    // day choosing is turned on, as a choice nobody remembers making.
                    Theme = config.AllowProfileThemes ? ThemeToStore(request.Theme, config) : null,
                    IsHidden = true,
                    LockoutMinutes = ClampLockoutMinutes(request.LockoutMinutes ?? 5),
                    // Store the selected libraries as the plugin's own ground truth
                    EnabledFolders = validatedFolders,
                    // Nothing ticked means every library the master has, then and later.
                    // On create, leaving the list out is "every library" too: there is nothing
                    // to keep. allLibraries: true says the same thing explicitly.
                    AllLibraries = request.EnabledFolders == null,
                    // What was just written to the account, when it was a list. Without it the
                    // next sign-in would read this write as an administrator's change (R-1042).
                    LastAppliedFolders = request.EnabledFolders != null ? validatedFolders.ToList() : null,
                    // The rating asked for is the profile's own; none asked for follows the main
                    // account. What went to the account is recorded beside it (R-1045).
                    OwnRating = new ProfileRating
                    {
                        Score = int.TryParse(request.MaxParentalRating, out var ownScore) ? ownScore : null,
                    },
                    LastAppliedRating = new ProfileRating
                    {
                        Score = targetPolicy.MaxParentalRating,
                        SubScore = targetPolicy.MaxParentalSubRating,
                    },
                    BlockedTags = profileBlockedTags,
                    AllowedTags = profileAllowedTags,
                    BypassPinOnLocalNetwork = request.BypassPinOnLocalNetwork ?? false,
                    AllowedDeviceIds = request.AllowedDeviceIds ?? new List<string>(),
                    ProfileImage = !string.IsNullOrEmpty(request.AvatarLibraryId)
                        ? CopyLibraryAvatarToProfile(targetUser.Id, request.AvatarLibraryId).Url
                        : SaveProfileImage(targetUser.Id, request.ProfileImage, request.ProfileImageThumb).Url
                });

                Plugin.Instance?.SaveConfiguration();
            }

            LogActivity(_userManager.GetUserById(masterUserId)?.Username + " added the profile " + profileName,
                "ProfileCreated", masterUserId);

            return Ok(new
            {
                ProfileUserId = targetUser.Id,
                ProfileName = profileName
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

            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal)) return pinRefusal!;

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
            if (targetUser != null && mapping.KeepAccountOnDelete)
            {
                // Made from a real account, and the admin chose to keep it (R-1134): the
                // profile leaves the household and the account stays, signing in on its own.
                var keepPassword = string.IsNullOrEmpty(request.Password)
                    ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                    : request.Password;
                var kept = await ReleaseProfileAsAccountAsync(mapping, targetUser, keepPassword, string.Empty).ConfigureAwait(false);
                if (kept.Error != null) return StatusCode(kept.Status, kept.Error);
                LogActivity(CallerName() + " removed " + mapping.ProfileName + " and kept its account",
                    "ProfileMadeAccount", kept.User.Id, "The account stays.");
                return Ok();
            }

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

                // What it had watched today, kept per profile, would stay in viewing_usage.json (R-1023).
                // Outside ConfigLock: the tracker's own lock is never taken inside it.
                Auth.ViewingTracker.Forget(request.ProfileId);

                // The master's last profile may just have gone, and a master with no
                // profiles is no longer one the PIN provider should hold.
                ProfilesBootstrapTask.ReconcileAuthProvidersNow();

                LogActivity(_userManager.GetUserById(masterUserId)?.Username + " deleted the profile " + mapping.ProfileName,
                    "ProfileDeleted", masterUserId);
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

            // A copy: the lists are only read here. The PIN upgrade and the cross-household
            // record find the live row themselves, under the lock.
            var config = ReadConfigSnapshot();
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
            NoteHouseholdSeenLocally(callerMasterUserId, isLocal, config);
            var ip = remoteIp?.ToString() ?? "127.0.0.1";

            // Who may enter what, decided in one place for /switch, /verify-pin and /list.
            //
            // Entering another household — its master, or since this release one of its
            // sub-profiles — hands the caller a real session for it, including a master's
            // admin rights if it has any. Its PIN is what stands between a shared Bonfire code
            // and that session. The exception is consent from the household being entered:
            // its owner can turn on AllowHouseholdLanBypass, which is what two adults sharing
            // one TV actually want (issue #13). It is theirs to grant and nobody else's — the
            // caller's own BypassPinOnLocalNetwork does not carry across a link — and only on
            // the local network, so a leaked code is worth nothing outside the house.
            var rules = EvaluateSwitch(config.Mappings, callerMasterUserId, request.ProfileId, linkedMasterIds, isLocal,
                LinkedAccountsFor(callerMasterUserId, config));
            if (rules.Unauthorized)
            {
                return Unauthorized("Unauthorized profile switch attempt.");
            }

            if (rules.Refusal != null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Blocked Bonfire switch from {Caller} into unprotected account {Target}.",
                    callerMasterUserId, request.ProfileId);
                return BadRequest(rules.Refusal);
            }

            // True when the target is in someone else's household, reached through a Bonfire.
            bool isCrossAccountMasterSwitch = rules.CrossHousehold;

            // Set when the target household's owner has opted into household LAN switching
            // and the request genuinely came from the local network.
            bool householdLanBypass = rules.HouseholdLanBypass;

            if (householdLanBypass)
            {
                // Recorded at Information alongside the audit entry: this is the path where
                // one household is entered without proving anything but network location, so
                // it needs to be visible in the log when an owner reviews access.
                _logger.LogInformation(
                    "ProfilesPlugin: Household LAN bypass — {Caller} entered linked account {Target} " +
                    "from {Ip} without a PIN (the target's owner enabled this).",
                    callerMasterUserId, request.ProfileId, ip);
            }

            // Enforce device restrictions for sub-profiles
            var deviceAccess = EvaluateDeviceRestriction(
                mapping, GetAuthorizationParameter("DeviceId"), config.KnownDevices, config.Mappings);
            if (deviceAccess != DeviceAccess.NotRestricted && deviceAccess != DeviceAccess.Allowed)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: device check refused a switch into {ProfileId} ({Reason}).",
                    request.ProfileId, deviceAccess);
                return BadRequest(DeviceAccessMessage(deviceAccess));
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
                    var limited = BeginPinAttempt(rateLimitKey, request.ProfileId);
                    if (limited != null) return limited;

                    // mapping is non-null here — pinHashToCheck came from it.
                    if (!VerifyPinAndUpgrade(request.Pin, mapping!, config))
                    {
                        return BadRequest("Invalid PIN code.");
                    }
                }
            }

            EndPinAttempt(rateLimitKey, request.ProfileId);
            tAuth = sw.ElapsedMilliseconds;

            var targetUser = _userManager.GetUserById(request.ProfileId);
            if (targetUser == null) return NotFound("Underlying system user missing.");

            // An account turned off in Dashboard -> Users stays off. AuthenticateDirect does
            // not check IsDisabled at all, and the block below used to write
            // IsDisabled = false on every switch into a sub-profile, so disabling a profile's
            // account was undone the next time anyone picked it. That included the account
            // the delete route disables as its fallback "so it cannot log in".
            if (_userManager.GetUserDto(targetUser, string.Empty).Policy?.IsDisabled == true)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a switch into {ProfileId}, whose account is disabled.",
                    request.ProfileId);
                return BadRequest("This profile's account is turned off on the server.");
            }

            // Nor does turning off the main account: a profile's own account is never the one
            // that was turned off, so its profiles stayed enterable (R-1135).
            if (MainAccountDisabled(_userManager, mapping))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a switch into {ProfileId}, whose main account is disabled.",
                    request.ProfileId);
                return BadRequest("This profile's main account is turned off on the server.");
            }

            // Closed for maintenance. An administrator's own household can still switch to
            // the administrator, but not into a profile the gate would refuse anyway: the
            // switch would succeed and every request after it fail.
            var closed = Auth.MaintenanceGate.Active();
            if (closed != null && !Auth.MaintenanceGate.IsAdmin(_userManager, request.ProfileId))
            {
                Response.Headers[ErrorCodes.Header] = Auth.MaintenanceGate.ErrorCode;
                return StatusCode(StatusCodes.Status503ServiceUnavailable, Auth.MaintenanceGate.RefusalBody(closed));
            }

            // Inherit/synchronize streaming policies and configurations from master user dynamically during switch
            var targetMasterUserId = mapping != null ? mapping.MasterUserId : request.ProfileId;
            var masterUser = _userManager.GetUserById(targetMasterUserId);

            // Only a genuine sub-profile inherits. See ShouldInheritMasterPolicy for what
            // this did to a main account when it ran there (issue #27): it cleared the
            // account's library access permanently, demoted an administrator, and hid them.
            // The work itself is shared with every other sign-in; see ProfilePolicySync.
            if (masterUser != null && ShouldInheritMasterPolicy(mapping, targetUser.Id, callerMasterUserId))
            {
                await Auth.ProfilePolicySync.ApplyAsync(_userManager, _libraryManager, _logger, targetUser.Id)
                    .ConfigureAwait(false);
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

            // The profile's earlier sessions on this same device go first. Jellyfin counts
            // them against MaxActiveSessions before it replaces any, so they made the switch
            // fail and the page reload into the picker in a loop (issue #41).
            // Best effort: if this fails, the switch goes ahead as it did before.
            try
            {
                await Auth.SameDeviceSessions.EndAsync(_sessionManager, _deviceManager, ReadConfigSnapshot(),
                    targetUser.Id, authRequest.DeviceId, _logger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: could not end {User}'s earlier sessions on this device.", targetUser.Username);
            }

            // Authenticate directly bypassing password check (securely validated caller + PIN validation)
            // Never let this throw past us: Jellyfin's exception middleware turns these into a 401,
            // and the client reads a 401 as "the caller's session expired" and signs the master out.
            MediaBrowser.Controller.Authentication.AuthenticationResult session;
            try
            {
                session = await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
            }
            catch (MediaBrowser.Controller.Net.SecurityException ex)
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

            // Into another household: remembered, so taking the door away later can sign
            // this device out of it (R-1302). See CrossHouseholdEntry.
            if (isCrossAccountMasterSwitch)
            {
                lock (ConfigLock)
                {
                    var live = Plugin.Instance?.Configuration;
                    var withoutPin = !string.IsNullOrEmpty(pinHashToCheck) && householdLanBypass;
                    if (live != null && CrossHouseholdEntries.Record(live, callerMasterUserId, targetUser.Id, authRequest.DeviceId, withoutPin, DateTime.UtcNow))
                        Plugin.Instance?.SaveConfiguration();
                }
            }

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
            // A copy: the lists are only read here, and a PIN upgrade finds the live row itself.
            var config = ReadConfigSnapshot();
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
            var deviceAccess = EvaluateDeviceRestriction(
                mapping, GetAuthorizationParameter("DeviceId"), config.KnownDevices, config.Mappings);
            if (deviceAccess != DeviceAccess.NotRestricted && deviceAccess != DeviceAccess.Allowed)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: device check refused a PIN verification for {ProfileId} ({Reason}).",
                    request.ProfileId, deviceAccess);
                return BadRequest(DeviceAccessMessage(deviceAccess));
            }

            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            bool isLocal = remoteIp != null && _networkManager.IsInLocalNetwork(remoteIp);
            var ip = remoteIp?.ToString() ?? "127.0.0.1";
            var rateLimitKey = $"{ip}_{request.ProfileId}";

            // Same rules as /switch, through the same function, so this endpoint can never
            // green-light a switch /switch would then refuse.
            var rules = EvaluateSwitch(config.Mappings, callerMasterUserId, request.ProfileId, linkedMasterIds, isLocal,
                LinkedAccountsFor(callerMasterUserId, config));
            if (rules.Unauthorized)
            {
                return Unauthorized("Unauthorized profile PIN verification.");
            }

            if (rules.Refusal != null)
            {
                return BadRequest(rules.Refusal);
            }

            var target = rules.Target;
            if (target != null && !string.IsNullOrEmpty(target.PinHash)
                && !CanSkipPin(target, isLocal, rules.CrossHousehold, rules.HouseholdLanBypass))
            {
                var limited = BeginPinAttempt(rateLimitKey, request.ProfileId);
                if (limited != null) return limited;

                if (!VerifyPinAndUpgrade(request.Pin, target, config))
                {
                    return BadRequest("Invalid PIN.");
                }
            }

            EndPinAttempt(rateLimitKey, request.ProfileId);
            return Ok();
        }

        [HttpGet("admin/mappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetAdminMappings()
        {
            var adminError = RequireAdministrator("view all mappings");
            if (adminError != null) return adminError;

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
                    List<Guid> added;
                    bool inBonfire;
                    lock (ConfigLock)
                    {
                        added = AccountLinks.In(config, user.Id);
                        inBonfire = config.BonfireGroups.Any(g => g.OwnerUserId == user.Id || g.MemberUserIds.Contains(user.Id));
                    }
                    masterUsersList.Add(new
                    {
                        ProfileUserId = user.Id,
                        ProfileName = user.Username,
                        RequiresPin = mapping != null && !string.IsNullOrEmpty(mapping.PinHash),
                        MaxProfiles = GetMaxProfilesForUser(user.Id, config),
                        LimitOverride = limitOverride,
                        ProfileImage = mapping?.ProfileImage,
                        // For "Make it a profile", which refuses both.
                        IsAdministrator = IsAdministratorAccount(user),
                        InBonfire = inBonfire,
                        // Other accounts shown in this household's switcher (issue #39).
                        Added = added
                            .Select(id => new { UserId = id, Name = _userManager.GetUserById(id)?.Username })
                            .Where(a => a.Name != null)
                            .ToList()
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
            var (runningVersion, newestInstalled) = ProfilesBootstrapTask.FindInstalledVersions();

            return new
            {
                // Installed or updated on a running server, so this build's pipeline hook
                // was never registered and cannot be until Jellyfin restarts (issue #25).
                RestartRequired = ProfilesBootstrapTask.RestartRequired,
                // Both versions, so "restart required" is something the reader can check
                // rather than something they have to believe. They differ exactly when a
                // newer copy is on disk waiting for the process to come back.
                RunningVersion = runningVersion,
                NewestInstalledVersion = newestInstalled,
                NewerVersionInstalled = ProfilesBootstrapTask.NewerVersionInstalled(),
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
            var adminError = RequireAdministrator("retry script injection");
            if (adminError != null) return adminError;

            _logger.LogInformation("ProfilesPlugin: Manual injection retry requested by {User}.", GetCurrentUserId());
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
            var adminError = RequireAdministrator("reset PINs");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            string name;
            lock (ConfigLock)
            {
                var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                if (mapping == null) return NotFound("Profile mapping not found.");

                mapping.PinHash = string.Empty;
                name = mapping.ProfileName;
                Plugin.Instance?.SaveConfiguration();
            }

            // The guesses were at the PIN that is gone. The email reset ends them too; this
            // path left them standing (P2-705).
            PinBudget.End(request.ProfileId);

            // Resetting a master's PIN takes it off the PIN provider, or its password would
            // be refused there.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();

            LogActivity(CallerName() + " cleared the PIN on " + name, "PinReset", request.ProfileId,
                severity: LogLevel.Warning);

            return Ok();
        }



        /// <summary>
        /// Lists, and optionally removes, plugin state that points at Jellyfin users who no
        /// longer exist.
        /// <para>
        /// A profile deleted through Jellyfin's own user administration rather than through
        /// Bonfire leaves its mapping behind, and nothing ever collects it. The leftovers are
        /// not inert: a mapping still counts against its master's profile limit, still appears
        /// in the household's device-scoping set, and still names its master — so a sub-profile
        /// whose <em>master</em> was deleted is an orphan that can never be reached or removed
        /// from the interface.
        /// </para>
        /// <para>
        /// <c>apply=false</c> is the default deliberately. This deletes configuration and the
        /// administrator should see the list first; the dashboard shows what would go and asks
        /// before sending the second call.
        /// </para>
        /// </summary>
        [HttpPost("admin/cleanup-orphans")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult CleanupOrphans([FromQuery] bool apply = false)
        {
            var adminError = RequireAdministrator("clean up orphaned profiles");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // Resolved once, outside the lock: GetUserById is a database read per call and
            // the set is small. Doing it inside would hold the lock across all of them.
            bool Exists(Guid id) => id != Guid.Empty && _userManager.GetUserById(id) != null;

            var orphansRemoved = new List<Guid>();
            (int Mappings, int Groups, int Members) removed;
            lock (ConfigLock)
            {
                var orphans = config.Mappings
                    .Where(m => !Exists(m.ProfileUserId) || !Exists(m.MasterUserId))
                    .Select(m => new
                    {
                        profileUserId = m.ProfileUserId,
                        masterUserId = m.MasterUserId,
                        profileName = m.ProfileName,
                        reason = !Exists(m.ProfileUserId)
                            ? "the profile's Jellyfin user was deleted"
                            : "the master account was deleted"
                    })
                    .ToList();

                // Bonfire groups outlive their owner the same way.
                var deadGroups = config.BonfireGroups
                    .Where(g => !Exists(g.OwnerUserId))
                    .Select(g => new { groupId = g.GroupId, code = g.BonfireCode })
                    .ToList();

                // A group whose owner is alive can still list members who are not.
                var deadMembers = config.BonfireGroups
                    .SelectMany(g => (g.MemberUserIds ?? new List<Guid>())
                        .Where(id => !Exists(id))
                        .Select(id => new { groupId = g.GroupId, userId = id }))
                    .ToList();

                if (!apply)
                {
                    return Ok(new { applied = false, orphans, deadGroups, deadMembers });
                }

                var orphanIds = orphans.Select(o => o.profileUserId).ToHashSet();
                orphansRemoved = config.Mappings
                    .Where(m => orphanIds.Contains(m.ProfileUserId) && (!Exists(m.ProfileUserId) || !Exists(m.MasterUserId)))
                    .Select(m => m.ProfileUserId)
                    .ToList();
                config.Mappings.RemoveAll(m => orphanIds.Contains(m.ProfileUserId)
                                               && (!Exists(m.ProfileUserId) || !Exists(m.MasterUserId)));

                var deadGroupIds = deadGroups.Select(g => g.groupId).ToHashSet(StringComparer.Ordinal);
                config.BonfireGroups.RemoveAll(g => deadGroupIds.Contains(g.GroupId));

                foreach (var g in config.BonfireGroups)
                {
                    g.MemberUserIds?.RemoveAll(id => !Exists(id));
                }

                // An account shown in another household's switcher, where either side is gone.
                // Nothing to review: a tile for an account that doesn't exist is never drawn.
                config.LinkedAccounts?.RemoveAll(l => !Exists(l.UserId) || !Exists(l.HouseholdId));

                // Devices are NOT touched here. A device outliving the profile that used it is
                // normal — the hardware is still in the house — and dropping one that some
                // other profile still names would empty that whitelist, which means "any
                // device". Device housekeeping has its own rules; see RemoveStaleDevices.
                Plugin.Instance?.SaveConfiguration();

                removed = (orphans.Count, deadGroups.Count, deadMembers.Count);
            }

            foreach (var gone in orphansRemoved) Auth.ViewingTracker.Forget(gone);   // R-1023

            // A master whose profiles were all orphans is no longer one the PIN provider
            // should hold.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();

            return Ok(new
            {
                applied = true,
                removedMappings = removed.Mappings,
                removedGroups = removed.Groups,
                removedMembers = removed.Members
            });
        }

        /// <summary>
        /// Live Jellyfin sessions belonging to Bonfire profiles, so an administrator can see
        /// who is signed in where.
        /// <para>
        /// Jellyfin's own Devices page lists these too, but it lists them as bare usernames
        /// with no idea that half of them are sub-profiles of one household — and sub-profiles
        /// are hidden users, so the page reads as a list of strangers. Here each session is
        /// named against the account it belongs to.
        /// </para>
        /// </summary>
        [HttpGet("admin/sessions")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult GetProfileSessions()
        {
            var adminError = RequireAdministrator("view profile sessions");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            List<ProfileMapping> mappings;
            lock (ConfigLock)
            {
                mappings = config.Mappings.ToList();
            }

            // Grouped first: a duplicated row — from an import, or two requests racing — made
            // ToDictionary throw and the whole page a 500.
            var byProfile = mappings
                .GroupBy(m => m.ProfileUserId)
                .ToDictionary(g => g.Key, g => g.First());

            var sessions = _sessionManager.Sessions
                .Where(s => s.UserId != Guid.Empty)
                .Select(s =>
                {
                    byProfile.TryGetValue(s.UserId, out var mapping);
                    var isSubProfile = mapping != null && mapping.ProfileUserId != mapping.MasterUserId;
                    var master = mapping == null
                        ? null
                        : _userManager.GetUserById(mapping.MasterUserId);

                    return new
                    {
                        sessionId = s.Id,
                        userId = s.UserId,
                        username = s.UserName,
                        // Null for a plain Jellyfin account that Bonfire knows nothing about.
                        // The dashboard groups on this, so it has to say "not ours" rather
                        // than quietly attributing the session to somebody.
                        masterUsername = isSubProfile ? master?.Username : null,
                        isSubProfile,
                        deviceId = s.DeviceId,
                        deviceName = s.DeviceName,
                        client = s.Client,
                        appVersion = s.ApplicationVersion,
                        lastActivity = s.LastActivityDate,
                        remoteEndPoint = s.RemoteEndPoint,
                        nowPlaying = s.NowPlayingItem?.Name
                    };
                })
                .OrderByDescending(s => s.lastActivity)
                .ToList();

            return Ok(sessions);
        }

        /// <summary>
        /// Signs one device out, revoking its token.
        /// <para>
        /// Routed through <c>IDeviceManager</c> rather than <c>ISessionManager.Logout</c>:
        /// a <c>SessionInfo</c> does not carry its access token, so the device row for that
        /// (user, device) pair is what identifies the credential to revoke. Both are required
        /// — a device id alone would sign every profile on that television out at once, and
        /// the point of this is to end one session.
        /// </para>
        /// </summary>
        [HttpPost("admin/sessions/sign-out")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> SignOutSession([FromBody] SignOutSessionRequest request)
        {
            var adminError = RequireAdministrator("sign a session out");
            if (adminError != null) return adminError;

            if (request == null || string.IsNullOrWhiteSpace(request.DeviceId) || request.UserId == Guid.Empty)
                return BadRequest("Both userId and deviceId are required.");

            var devices = _deviceManager.GetDevices(new DeviceQuery
            {
                DeviceId = request.DeviceId,
                UserId = request.UserId
            }).Items;

            if (devices.Count == 0) return BadRequest("No such session.");

            foreach (var device in devices)
            {
                await _sessionManager.Logout(device).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "ProfilesPlugin: administrator signed out {UserId} on device {DeviceId}.",
                request.UserId, request.DeviceId);

            return Ok(new { signedOut = devices.Count });
        }

        /// <summary>
        /// The whole plugin configuration as JSON, for keeping a copy of.
        /// <para>
        /// Every profile, PIN, Bonfire, device whitelist and avatar record lives in one
        /// <c>PluginConfiguration.xml</c> with nothing behind it. A bad save, a disk problem
        /// or an experiment gone wrong flattens the household's entire setup, and rebuilding
        /// it by hand means knowing every PIN and every library tick that was ever set.
        /// </para>
        /// <para>
        /// PIN hashes are included, because a backup that cannot restore a working PIN is not
        /// a backup. They are PBKDF2-SHA256 with a per-PIN salt, so the file is no more
        /// sensitive than the configuration it came from — but it is exactly as sensitive as
        /// that, and it is a file an administrator can now put somewhere careless. Said plainly
        /// in the dashboard rather than left to be discovered.
        /// </para>
        /// </summary>
        [HttpGet("admin/config/export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult ExportConfiguration()
        {
            var adminError = RequireAdministrator("export the configuration");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            lock (ConfigLock)
            {
                return Ok(new
                {
                    exportedUtc = DateTime.UtcNow,
                    pluginVersion = Plugin.Instance?.Version?.ToString() ?? "unknown",
                    configuration = config
                });
            }
        }

        /// <summary>
        /// Replaces the plugin configuration with a previously exported one.
        /// <para>
        /// Deliberately all-or-nothing. A merge would have to decide what to do about a
        /// profile that exists in both copies with different PINs, different libraries and a
        /// different master, and every answer to that is a guess about what the administrator
        /// meant. Replacing is the thing they asked for and the thing they can predict.
        /// </para>
        /// <para>
        /// What it will not do is leave Jellyfin's own users half-attached: the import drops
        /// any mapping naming a user this server does not have, so restoring a backup onto a
        /// different server produces a smaller, consistent configuration rather than a set of
        /// orphans. Those are reported back, because silently dropping somebody's profile is
        /// worse than refusing.
        /// </para>
        /// </summary>
        [HttpPost("admin/config/import")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult ImportConfiguration([FromBody] JsonElement body)
        {
            var adminError = RequireAdministrator("import a configuration");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // Bound as a JsonElement rather than straight to PluginConfiguration, and this is
            // the most important line in the endpoint.
            //
            // PluginConfiguration initialises every one of its lists to an empty list. So a
            // body that fails to bind — a file whose properties are camelCase when the
            // serialiser wants PascalCase, a truncated download, an object from some other
            // tool — does not arrive as null and does not throw. It arrives as a pristine
            // default configuration, and every field below then writes that emptiness over
            // the real one. The administrator would have seen "Imported 0 profile(s)" and
            // lost the household.
            //
            // Reading the properties explicitly, case-insensitively, means "the file did not
            // contain this" is distinguishable from "the file contained an empty list", which
            // is the distinction the typed binding threw away.
            if (body.ValueKind != JsonValueKind.Object)
                return BadRequest("No configuration in the request body.");

            // The export wrapper is accepted as well as a bare configuration: editing an
            // export and pasting back the inner object is the obvious thing to do with it.
            var root = body;
            if (TryProperty(body, "configuration", out var inner) && inner.ValueKind == JsonValueKind.Object)
                root = inner;

            // At least one list this plugin owns must actually be present. Anything else is
            // not a Bonfire configuration, whatever it is.
            var recognised = new[] { "Mappings", "KnownDevices", "BonfireGroups", "AvatarLibrary" }
                .Count(name => TryProperty(root, name, out _));
            if (recognised == 0)
            {
                return BadRequest(
                    "That file does not look like a Bonfire configuration — it has none of the "
                    + "expected sections. Nothing was changed.");
            }

            var incoming = Read<PluginConfiguration>(root) ?? new PluginConfiguration();

            // Anything absent from the file keeps its current value rather than being reset,
            // so a partial or older export cannot silently clear a setting it predates.
            var hasMappings = TryProperty(root, "Mappings", out _);
            var hasDevices = TryProperty(root, "KnownDevices", out _);
            var hasGroups = TryProperty(root, "BonfireGroups", out _);
            var hasAvatars = TryProperty(root, "AvatarLibrary", out _);
            var hasLimits = TryProperty(root, "UserProfileLimitOverrides", out _);

            bool Exists(Guid id) => id != Guid.Empty && _userManager.GetUserById(id) != null;

            var droppedMappings = new List<string>();

            lock (ConfigLock)
            {
                var mappings = (incoming.Mappings ?? new List<ProfileMapping>())
                    .Where(m =>
                    {
                        var ok = Exists(m.ProfileUserId) && Exists(m.MasterUserId);
                        if (!ok) droppedMappings.Add(string.IsNullOrEmpty(m.ProfileName)
                            ? m.ProfileUserId.ToString()
                            : m.ProfileName);
                        return ok;
                    })
                    .ToList();

                var groups = (incoming.BonfireGroups ?? new List<BonfireGroup>())
                    .Where(g => Exists(g.OwnerUserId))
                    .ToList();
                foreach (var g in groups)
                {
                    g.MemberUserIds?.RemoveAll(id => !Exists(id));
                }

                // Written field by field onto the live object rather than swapping the
                // reference. Jellyfin replaces Plugin.Configuration on its own whenever an
                // administrator saves the settings page, so anything holding the old
                // reference would be writing to an orphan — the same reason every write in
                // this plugin happens inside ConfigLock. This is the shape UpdateAdminSettings
                // uses.
                // Each section is written only if the file actually carried it. A default
                // value from a failed or partial bind must never overwrite a real one.
                if (hasMappings) config.Mappings = mappings;
                Auth.ViewingTracker.Invalidate();
                if (hasGroups) config.BonfireGroups = groups;
                if (TryProperty(root, "LinkedAccounts", out _))
                {
                    config.LinkedAccounts = (incoming.LinkedAccounts ?? new List<LinkedAccount>())
                        .Where(l => l != null && l.UserId != l.HouseholdId && Exists(l.UserId) && Exists(l.HouseholdId))
                        .GroupBy(l => (l.HouseholdId, l.UserId)).Select(g => g.First())
                        .ToList();
                }
                if (hasDevices) config.KnownDevices = incoming.KnownDevices;
                if (hasLimits) config.UserProfileLimitOverrides = incoming.UserProfileLimitOverrides;
                if (hasAvatars) config.AvatarLibrary = incoming.AvatarLibrary;

                if (TryProperty(root, "MaxProfilesPerUser", out _))
                    config.MaxProfilesPerUser = incoming.MaxProfilesPerUser;
                // Only ever stricter: a file can't raise it past the limit (PinBudget.PerDay).
                if (TryProperty(root, "PinGuessesPerDay", out _))
                    config.PinGuessesPerDay = PinBudget.PerDay(incoming);
                if (TryProperty(root, "RequireMainPin", out _))
                    config.RequireMainPin = incoming.RequireMainPin;
                if (TryProperty(root, "DisallowCustomAvatarUploads", out _))
                    config.DisallowCustomAvatarUploads = incoming.DisallowCustomAvatarUploads;
                if (TryProperty(root, "ProfileImageQuality", out _))
                    config.ProfileImageQuality = ImageQualities.Normalize(incoming.ProfileImageQuality);
                if (TryProperty(root, "DefaultAskOnStartup", out _))
                    config.DefaultAskOnStartup = incoming.DefaultAskOnStartup;
                if (TryProperty(root, "DefaultSwitcherLocation", out _))
                    config.DefaultSwitcherLocation = incoming.DefaultSwitcherLocation;
                // Themes before the default, which may name one of them, and each through the
                // same checks as its route. A font whose file is not in this server's data
                // folder is dropped: a backup holds the list, not the files.
                if (TryProperty(root, "CustomFonts", out _))
                {
                    config.CustomFonts = (incoming.CustomFonts ?? new List<CustomFont>())
                        .Where(f => f != null && ThemeCatalog.IsCustomFontFileName(f.File) && Woff2Font.IsSafeFamily(f.Family ?? string.Empty)
                            && ThemeCatalog.CustomFontFallbacks.Contains(f.Fallback)
                            && System.IO.File.Exists(Path.Combine(ThemeCatalog.CustomFontFolder, f.File)))
                        .GroupBy(f => f.File).Select(g => g.First())
                        .Take(ThemeCatalog.MaxCustomFonts)
                        .ToList();
                }
                if (TryProperty(root, "CustomThemes", out _))
                {
                    var keep = new List<CustomTheme>();
                    foreach (var c in incoming.CustomThemes ?? new List<CustomTheme>())
                    {
                        if (c == null || keep.Count >= ThemeCatalog.MaxCustomThemes || keep.Any(k => k.Id == c.Id)) continue;
                        JsonNode? node;
                        try { node = JsonNode.Parse(c.Json ?? string.Empty); }
                        catch (JsonException) { continue; }
                        if (node?["id"] is not JsonValue idv || !idv.TryGetValue<string>(out var nodeId) || nodeId != c.Id) continue;
                        if (ThemeCatalog.ValidateCustom(node, config).Count == 0)
                            keep.Add(new CustomTheme { Id = c.Id, Json = node.ToJsonString() });
                    }
                    config.CustomThemes = keep;
                }
                if (TryProperty(root, "HiddenThemes", out _))
                {
                    config.HiddenThemes = (incoming.HiddenThemes ?? new List<string>())
                        .Where(Themes.IsBuiltIn).Select(id => id.Trim().ToLowerInvariant())
                        .Where(id => id != Themes.Bonfire).Distinct().ToList();
                }
                // The logo, through the same rules as its routes. An image whose file is not on
                // this server is dropped, like a font: a backup holds the name, not the file.
                if (TryProperty(root, "LogoImage", out _))
                {
                    config.LogoImage = ThemeLogo.IsFileName(incoming.LogoImage)
                        && System.IO.File.Exists(Path.Combine(ThemeLogo.Folder, incoming.LogoImage!)) ? incoming.LogoImage : null;
                }
                if (TryProperty(root, "LogoText", out _))
                {
                    try { config.LogoText = ThemeLogo.CleanText(incoming.LogoText, ThemeLogo.MaxText); }
                    catch (ArgumentException) { config.LogoText = null; }
                }
                if (TryProperty(root, "LogoAlt", out _))
                {
                    try { config.LogoAlt = ThemeLogo.CleanText(incoming.LogoAlt, ThemeLogo.MaxAlt); }
                    catch (ArgumentException) { config.LogoAlt = null; }
                }
                // A default the file also hides stays the default, and comes out of hiding.
                var importedDefault = TryProperty(root, "DefaultTheme", out _) ? incoming.DefaultTheme : config.DefaultTheme;
                if (!string.IsNullOrWhiteSpace(importedDefault))
                    config.HiddenThemes.Remove(importedDefault.Trim().ToLowerInvariant());
                if (TryProperty(root, "DefaultTheme", out _))
                    config.DefaultTheme = Themes.Normalize(incoming.DefaultTheme, config);
                if (TryProperty(root, "DefaultTimeZone", out _))
                    config.DefaultTimeZone = HouseholdTime.TryFind(incoming.DefaultTimeZone, out _) ? incoming.DefaultTimeZone.Trim() : string.Empty;
                if (TryProperty(root, "AllowProfileThemes", out _))
                    config.AllowProfileThemes = incoming.AllowProfileThemes;
                if (TryProperty(root, "EnableClientPinLogin", out _))
                    config.EnableClientPinLogin = incoming.EnableClientPinLogin;
                if (TryProperty(root, "EnableClientProfileList", out _))
                    config.EnableClientProfileList = incoming.EnableClientProfileList;
                if (TryProperty(root, "ClientPinLoginFromAnywhere", out _))
                    config.ClientPinLoginFromAnywhere = incoming.ClientPinLoginFromAnywhere;
                if (TryProperty(root, "PanicCodeHash", out _))
                    config.PanicCodeHash = incoming.PanicCodeHash;
                if (TryProperty(root, "ProxyClients", out _))
                    config.ProxyClients = Auth.ProxyClients.Normalize(incoming.ProxyClients);
                // Through the same rules as the route, so a hand-edited file cannot store
                // an announcement the settings page would refuse.
                if (TryProperty(root, "Announcements", out _))
                {
                    config.Announcements = (incoming.Announcements ?? new List<Announcement>())
                        .Where(a => a != null && a.Id != Guid.Empty && Configuration.Announcements.Validate(a) == null)
                        .GroupBy(a => a.Id).Select(g => g.First())
                        .Take(Configuration.Announcements.MaxCount)
                        .ToList();
                }
                // Confirmed addresses, for accounts this server has.
                if (TryProperty(root, "AccountEmails", out _))
                {
                    config.AccountEmails = (incoming.AccountEmails ?? new List<AccountEmail>())
                        .Where(a => a != null && Exists(a.UserId))
                        .GroupBy(a => a.UserId).Select(g => g.First())
                        .ToList();
                }

                // Everything else the file carries, so a setting added to the configuration
                // next month is restored the day it is added. The list above was the whole of
                // import until now, and the SMTP settings, confirmed addresses, the PIN-reset
                // switch, maintenance, first-time setup, starter avatars and the Quick Connect
                // fields were each added after it was written, none came back from a backup,
                // and the answer was still "success" (R-1146). Only what is named in
                // ImportNotRestored stays as the server has it.
                foreach (var property in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.DeclaringType != typeof(PluginConfiguration) || !property.CanRead || !property.CanWrite) continue;
                    if (ImportHandledAbove.Contains(property.Name) || ImportNotRestored.Contains(property.Name)) continue;
                    if (!TryProperty(root, property.Name, out _)) continue;
                    property.SetValue(config, property.GetValue(incoming));
                }

                Plugin.Instance?.SaveConfiguration();
            }

            // Every profile, PIN and the feature switch itself may have changed.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();
            // And which apps get the profile picker, the same as a settings save.
            Auth.ProxySwitcher.ForgetUnproxied();

            return Ok(new
            {
                mappings = (incoming.Mappings ?? new List<ProfileMapping>()).Count - droppedMappings.Count,
                droppedMappings,
                devices = (incoming.KnownDevices ?? new List<KnownDevice>()).Count,
                bonfires = (incoming.BonfireGroups ?? new List<BonfireGroup>()).Count
            });
        }

        /// <summary>The settings ImportConfiguration restores one by one, each through its own rules.</summary>
        private static readonly HashSet<string> ImportHandledAbove = new(StringComparer.Ordinal)
        {
            "MaxProfilesPerUser", "PinGuessesPerDay", "RequireMainPin", "Mappings", "KnownDevices", "BonfireGroups",
            "LinkedAccounts", "UserProfileLimitOverrides", "AvatarLibrary", "DisallowCustomAvatarUploads",
            "ProfileImageQuality", "DefaultAskOnStartup", "DefaultSwitcherLocation", "CustomFonts", "CustomThemes",
            "HiddenThemes", "LogoImage", "LogoText", "LogoAlt", "DefaultTheme", "DefaultTimeZone", "AllowProfileThemes",
            "EnableClientPinLogin", "EnableClientProfileList", "ClientPinLoginFromAnywhere", "PanicCodeHash", "ProxyClients",
            "Announcements", "AccountEmails",
        };

        /// <summary>
        /// The only settings an import leaves as the server has them. Short on purpose: the
        /// default is that a setting is restored.
        /// <para><c>CrossHouseholdEntries</c> records which devices came in from outside a
        /// household, with the tokens issued to them: the state of this server's sign-ins,
        /// not a setting, and a backup of another day's would name devices that are gone.</para>
        /// </summary>
        private static readonly HashSet<string> ImportNotRestored = new(StringComparer.Ordinal)
        {
            "CrossHouseholdEntries",
        };

        /// <summary>
        /// Null for an acceptable theme: null or "" (follow the server default) or a known id.
        /// An unknown one is refused rather than quietly stored as the default, because a
        /// typo that saves as "default" looks exactly like the setting not taking.
        /// </summary>
        internal static string? ValidateTheme(string? theme, PluginConfiguration config) =>
            string.IsNullOrWhiteSpace(theme) || Themes.IsKnown(theme, config)
                ? null
                : "Theme must be one of: " + string.Join(", ", Themes.Offered(config)) + ".";

        /// <summary>What a valid request's theme is stored as: null for "follow the default".</summary>
        internal static string? ThemeToStore(string? theme, PluginConfiguration config) =>
            Themes.IsKnown(theme, config) ? Themes.Normalize(theme, config) : null;

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

            // Setting the main account's own PIN is how a required one gets set at all.
            var settingOwnPin = request.ProfileId == masterUserId && !string.IsNullOrEmpty(request.Pin);
            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal, settingOwnPin)) return pinRefusal!;
            if (request.ProfileId == masterUserId && request.Pin == string.Empty && config.RequireMainPin)
            {
                return BadRequest(MainPinRequiredMessage);
            }

            // Enforce ownership: the profile being edited must belong to the caller's master account.
            if (request.ProfileId != masterUserId)
            {
                var nameError = ValidateProfileName(request.ProfileName);
                if (nameError != null) return BadRequest(nameError);

                var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                if (mapping == null || mapping.MasterUserId != masterUserId)
                {
                    return Unauthorized("Unauthorized profile update attempt.");
                }

                // A new name must not be an account's username (R-1336). One it already has
                // is left alone, so a profile named before this check can still be edited.
                // Not ProfileName?.Trim(): ValidateProfileName above has already refused a
                // missing name, and the "?." told the compiler it might be null from here on,
                // which failed the -warnaserror build at the two later .Trim() calls.
                if (!string.Equals(mapping.ProfileName, request.ProfileName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var takenError = ValidateProfileNameFree(_userManager, request.ProfileName, request.ProfileId);
                    if (takenError != null) return BadRequest(takenError);
                }
            }

            // PIN validation (4-8 digits numeric if provided)
            if (!string.IsNullOrEmpty(request.Pin))
            {
                if (!IsValidPin(request.Pin))
                {
                    return BadRequest("PIN code must be a numeric value between 4 and 8 digits.");
                }
            }

            var themeError = ValidateTheme(request.Theme, config);
            if (themeError != null) return BadRequest(themeError);

            // R-1144: "every library" and "no rating limit" are explicit values now, because
            // leaving a field out keeps it. Two answers at once, or a rating that isn't a
            // score, is refused rather than guessed at: an unreadable rating used to be
            // read as no limit.
            if (request.AllLibraries == true && request.EnabledFolders != null)
                return BadRequest(LibrariesTwiceMessage);
            int? requestedRating = null;
            if (!string.IsNullOrEmpty(request.MaxParentalRating))
            {
                if (!int.TryParse(request.MaxParentalRating, out var rating)) return BadRequest(RatingNotScoreMessage);
                requestedRating = rating;
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

            // A name another account already has is refused before anything is written. It
            // was found after the picture was saved, so a refused rename still changed the
            // picture (ST-11).
            if (request.ProfileId != masterUserId)
            {
                var wantedUsername = $"{masterUser.Username}_{request.ProfileName.Trim().Replace(" ", "")}";
                if (!string.Equals(targetUser.Username, wantedUsername, StringComparison.OrdinalIgnoreCase)
                    && GetAllUsers().Any(u => string.Equals(u.Username, wantedUsername, StringComparison.OrdinalIgnoreCase)))
                {
                    return BadRequest("A profile with this name already exists.");
                }
            }

            // The picture first, before anything else is written. A refused one answers 400
            // with the reason and leaves the profile exactly as it was: the picture it has,
            // its name and everything else in this request. A rejected picture used to be
            // stored as "none", so a 3 MB upload deleted the existing one and answered 200
            // (R-1108).
            string? newImageUrl = null;
            var imageChanged = false;
            if (!string.IsNullOrEmpty(request.AvatarLibraryId))
            {
                var copied = CopyLibraryAvatarToProfile(request.ProfileId, request.AvatarLibraryId);
                if (copied.Error != null) return BadRequest(copied.Error);
                newImageUrl = copied.Url;
                imageChanged = true;
            }
            else if (request.ProfileImage != null)
            {
                var saved = SaveProfileImage(request.ProfileId, request.ProfileImage, request.ProfileImageThumb, existingMapping?.ProfileImage);
                if (saved.Error != null) return BadRequest(saved.Error);
                newImageUrl = saved.Url;
                imageChanged = true;
            }

            // Renaming logic
            if (request.ProfileId != masterUserId)
            {
                string systemUsername = $"{masterUser.Username}_{request.ProfileName.Trim().Replace(" ", "")}";
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

            // Update policy for sub-profiles. The policy sync (every sign-in) reads the profile
            // and writes its account and mapping back; the gate keeps this save from landing
            // between its read and its write and being undone by it (R-1046). Held to the end.
            using var syncGate = request.ProfileId != masterUserId
                ? await Auth.ProfilePolicySync.HoldAsync().ConfigureAwait(false)
                : null;
            List<Guid>? validatedFolders = null;
            // What the editor changed about the rating and what it wrote, for the mapping
            // (R-1045): the profile's own rating when the form set one, and the value written
            // to the account, which the next sign-in compares against. See InheritedRating.
            ProfileRating? ratingChosen = null;
            ProfileRating? ratingWritten = null;
            if (request.ProfileId != masterUserId)
            {
                var targetUserDto = _userManager.GetUserDto(targetUser, string.Empty);
                var targetPolicy = targetUserDto.Policy;

                // Set parental rating, never above the master's.
                //
                // The form has no sub-score, so the one on the account survives as long as the
                // score is the one already set. Dropping it on every save loosened a TV-PG
                // limit set in Dashboard -> Users to "anything scored PG" whenever the profile
                // was saved for any reason, a new avatar included.
                //
                // Left out (null) keeps it; "" is no limit of its own (R-1144). A left-out
                // rating used to remove the limit, so a client that sent only a new name
                // lifted a child's rating. Parsed and checked above, before anything is written.
                if (request.MaxParentalRating != null && requestedRating != targetPolicy.MaxParentalRating)
                {
                    targetPolicy.MaxParentalRating = requestedRating;
                    targetPolicy.MaxParentalSubRating = null;
                    // Only a rating that differs from the account's is a choice: the form shows
                    // the account's value, so one saved unchanged would pin an inherited one.
                    ratingChosen = new ProfileRating { Score = requestedRating };
                }

                (targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating) = ClampRating(
                    targetPolicy.MaxParentalRating, targetPolicy.MaxParentalSubRating,
                    masterPolicy.MaxParentalRating, masterPolicy.MaxParentalSubRating);
                ratingWritten = new ProfileRating { Score = targetPolicy.MaxParentalRating, SubScore = targetPolicy.MaxParentalSubRating };

                // Tag-based filtering (clamped so it can never exceed the master's)
                var (resolvedBlockedTags, resolvedAllowedTags) =
                    ResolveTagPolicy(masterPolicy, profileBlockedTags, profileAllowedTags);
                targetPolicy.BlockedTags = resolvedBlockedTags;
                targetPolicy.AllowedTags = resolvedAllowedTags;

                // Library access propagation. Three answers, each said on purpose (R-1144):
                // allLibraries: true is every library, a list is that list, and neither leaves
                // the libraries as they are. A left-out list used to mean every library, so a
                // client sending only a new name opened every library to a child.
                if (request.AllLibraries != true && request.EnabledFolders != null)
                {
                    // A library another plugin manages is not on the form, so it is never in the list:
                    // what the account already has of one is kept as it is.
                    validatedFolders = ApplyListedFolders(
                        _libraryManager, masterPolicy, ParseFolderIds(request.EnabledFolders) ?? new List<Guid>(), targetPolicy, keepOwned: true);
                }
                else if (request.AllLibraries == true)
                {
                    validatedFolders = ApplyInheritedFolders(_libraryManager, masterPolicy, targetPolicy, keepOwned: true);
                }

                await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);
            }

            var pinChanged = false;
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
                        mappingEntry.ProfileName = request.ProfileName.Trim();
                    }

                    // Left out keeps it, like the other fields. It used to reset to a
                    // fixed blue, the one exception to the API's own rule.
                    if (request.AvatarColor != null)
                        mappingEntry.AvatarColor = SanitizeAvatarColor(request.AvatarColor);

                    // Null leaves it alone, so a caller that predates the field — or one
                    // sending a partial update — cannot silently switch the background off.
                    if (request.TransparentAvatar.HasValue)
                    {
                        mappingEntry.TransparentAvatar = request.TransparentAvatar.Value;
                    }

                    // Null leaves it alone, for the same reason. Ignored while the server
                    // does not let profiles choose: the form hides the picker then, so a
                    // theme arriving anyway is a stale page, not a decision.
                    if (request.Theme != null && config.AllowProfileThemes)
                    {
                        mappingEntry.Theme = ThemeToStore(request.Theme, config);
                    }

                    if (imageChanged)
                    {
                        mappingEntry.ProfileImage = newImageUrl;
                    }

                    // Handle PIN updates
                    if (request.Pin == string.Empty)
                    {
                        mappingEntry.PinHash = string.Empty;
                    }
                    else if (request.Pin != null)
                    {
                        mappingEntry.PinHash = HashPin(request.Pin);
                        pinChanged = true;
                    }

                    // Handle lockout timer update
                    if (request.LockoutMinutes.HasValue)
                    {
                        mappingEntry.LockoutMinutes = ClampLockoutMinutes(request.LockoutMinutes.Value);
                    }

                    // The rating, as Bonfire's own write and the profile's own choice. A profile with
                    // no record is left without one unless the form chose a rating: a record
                    // made here would read "own: none" for a narrower rating it cannot tell apart.
                    if (ratingWritten != null)
                    {
                        if (ratingChosen != null)
                        {
                            mappingEntry.OwnRating = ratingChosen;
                            mappingEntry.LastAppliedRating = ratingWritten;
                        }
                        else if (mappingEntry.LastAppliedRating != null)
                        {
                            mappingEntry.LastAppliedRating = ratingWritten;
                        }
                    }

                    // Update stored library list (plugin's ground truth)
                    if (validatedFolders != null)
                    {
                        mappingEntry.EnabledFolders = validatedFolders;
                        // allLibraries: every library the master has, then and later. Only
                        // reached when the request said which (R-1144).
                        mappingEntry.AllLibraries = request.AllLibraries == true;
                        // What was just written to the account, so the next sign-in does not
                        // take this save for an administrator's change in Dashboard -> Users
                        // (R-1042). Null when the master's own folder policy was copied: the
                        // sign-in records what it applies.
                        mappingEntry.LastAppliedFolders = request.AllLibraries == true ? null : validatedFolders.ToList();
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

                    // Main account only; a sub-profile is always asked for its PIN if it has one.
                    if (request.AskPinToOpen.HasValue && request.ProfileId == masterUserId)
                    {
                        mappingEntry.AskPinToOpen = request.AskPinToOpen.Value;
                    }

                    if (request.AllowedDeviceIds != null)
                    {
                        mappingEntry.AllowedDeviceIds = request.AllowedDeviceIds;
                    }
                }

                Plugin.Instance?.SaveConfiguration();
            }

            // A master setting its PIN becomes enterable by it on other apps at once, rather
            // than after the next restart. A master clearing it must come off the PIN provider
            // just as fast, or its password is refused there.
            if (request.ProfileId == masterUserId && request.Pin != null)
            {
                ProfilesBootstrapTask.ReconcileAuthProvidersNow();
            }

            // The guesses were at the old PIN; a new one, or none, starts clean (P2-705).
            if (request.Pin != null && request.ProfileId != masterUserId) PinBudget.End(request.ProfileId);

            // Another household that opened it with the old PIN is out (R-1302b).
            if (pinChanged) await SignOutAfterPinChangeAsync(request.ProfileId).ConfigureAwait(false);

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
            // The head script's startup cover, raised for a gate this script will never draw.
            "  document.documentElement.removeAttribute('data-jpf-cover');\n" +
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
                        CachedProfilesJs = PublishThemes(PublishStyles(PublishLocales(reader.ReadToEnd())));
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
        // by the client once Jellyfin's Display language for the signed-in user asks for
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

        /// <summary>
        /// Splices <c>Web/styles.css</c> into the served script.
        /// <para>
        /// The stylesheet used to be a 1,400-line template literal inside
        /// <c>injectStyles</c>. In 1.5.2 a code comment in it contained two backticks; they
        /// closed and reopened the literal, the file stayed valid JavaScript so
        /// <c>node --check</c> passed, and at runtime <c>injectStyles</c> threw three calls
        /// into <c>init()</c> and took the nine steps after it down. 1.5.2 and 1.5.3-beta
        /// shipped dead. The stylesheet is a real .css file now, so there is no host string
        /// left for its contents to terminate.
        /// </para>
        /// <para>
        /// Encoded as a JSON string literal rather than interpolated. That is what makes the
        /// guarantee hold in both directions: JSON escaping is total, so a quote, a
        /// backslash, a newline or a backtick in the CSS is data, not syntax. The default
        /// encoder also escapes every non-ASCII character to <c>\uXXXX</c>, which
        /// incidentally removes U+2028 and U+2029 — legal in a JavaScript string only since
        /// ES2019, and a syntax error on the Chromium 68 televisions this has to run on.
        /// </para>
        /// <para>
        /// Same shape as <see cref="PublishLocales"/>: a plain replace on an exact literal.
        /// If the marker is ever edited away this does nothing and the plugin renders
        /// unstyled — loud and immediate, unlike a literal that truncates in silence.
        /// </para>
        /// </summary>
        internal static string PublishStyles(string js)
        {
            const string marker = "let BONFIRE_STYLES = \"\"; // __BONFIRE_STYLES__";

            var css = EmbeddedStyles.Value;
            if (css == null) return js;

            return js.Replace(
                marker,
                "let BONFIRE_STYLES = " + System.Text.Json.JsonSerializer.Serialize(css)
                    + "; // __BONFIRE_STYLES__",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Fills in the themes, the same way as <see cref="PublishStyles"/>: a plain replace
        /// on an exact literal, with JSON that <see cref="Configuration.ThemeCatalog.ClientPayload"/>
        /// has already escaped for a script. If the marker is edited away every screen draws
        /// in the stylesheet's base tokens, which is wrong but readable.
        /// </summary>
        internal static string PublishThemes(string js)
        {
            const string marker = "let BONFIRE_THEMES = { themes: [], patterns: {} }; // __BONFIRE_THEMES__";
            return js.Replace(
                marker,
                "let BONFIRE_THEMES = " + Configuration.ThemeCatalog.ClientPayload() + "; // __BONFIRE_THEMES__",
                StringComparison.Ordinal);
        }

        /// <summary>The stylesheet, read once. Null when the resource is missing.</summary>
        private static readonly Lazy<string?> EmbeddedStyles = new(() =>
        {
            using var stream = typeof(ProfilesController).Assembly
                .GetManifestResourceStream("Jellyfin.Profiles.Web.styles.css");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });

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

        /// <summary>
        /// The embedded translation for <paramref name="code"/>, or null if it could not be
        /// read. Loaded once per app lifetime, the same reasoning as CachedProfilesJs — but
        /// <b>only a successful read is remembered</b>.
        /// <para>
        /// This was <c>GetOrAdd</c> with a factory that returned null on failure, and GetOrAdd
        /// stores whatever the factory returns. One read that came back empty, for any reason,
        /// put a permanent null in the dictionary: every later request for that language took
        /// the cached null and answered without going near the assembly again. The language
        /// was gone until the server restarted, and because <c>t()</c> falls back to English
        /// per key, what a household saw was the interface quietly reverting to English with
        /// nothing logged to say why.
        /// </para>
        /// <para>
        /// A read that throws is not cached either — that is precisely the transient case that
        /// has to be retried rather than remembered.
        /// </para>
        /// </summary>
        internal static string? ReadLocaleJson(string code, ILogger? logger = null)
        {
            // The set ignores case and the resource lookup does not. "pt-br" passed the
            // endpoint's membership check, then asked the assembly for a resource named
            // pt-br.json when the file is pt-BR.json, got null, and was answered 503 — "ask
            // again" — forever. Resolved to the embedded spelling first, which also keys the
            // cache by the file rather than by however the caller happened to type it.
            if (!EmbeddedLocales.Value.TryGetValue(code, out var canonical)) return null;
            code = canonical;

            if (CachedI18nJson.TryGetValue(code, out var cached) && cached != null) return cached;

            string? json = null;
            try
            {
                var assembly = typeof(Plugin).Assembly;
                using var stream = assembly.GetManifestResourceStream($"Jellyfin.Profiles.Web.i18n.{code}.json");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "ProfilesPlugin: Could not read the {Locale} translation.", code);
                return null;
            }

            if (json != null) CachedI18nJson[code] = json;
            return json;
        }

        /// <summary>Cached locale codes. For the harness — nothing in the plugin needs it.</summary>
        internal static IReadOnlyCollection<string> CachedLocaleCodes => CachedI18nJson.Keys.ToArray();

        /// <summary>Keys for the administrator's settings page. Every other key is the household's.</summary>
        internal const string AdminKeyPrefix = "admin.";

        private static readonly ConcurrentDictionary<string, string> CachedI18nParts = new();

        /// <summary>
        /// One half of a translation file: the settings page's <c>admin.*</c> keys, or
        /// everything else.
        /// <para>
        /// Both halves live in one file per language, so adding a language is still one
        /// file. They are served apart because the household half is fetched by every
        /// television on the server, and the settings page's few hundred strings are no
        /// use to any of them.
        /// </para>
        /// <para>
        /// Null when the file could not be read or is not a JSON object; like
        /// <see cref="ReadLocaleJson"/>, only a success is cached.
        /// </para>
        /// </summary>
        internal static string? ReadLocalePart(string code, bool admin, ILogger? logger = null)
        {
            var json = ReadLocaleJson(code, logger);
            if (json == null) return null;

            var key = code + (admin ? "|admin" : "|app");
            if (CachedI18nParts.TryGetValue(key, out var cached)) return cached;

            System.Text.Json.Nodes.JsonObject? all;
            try
            {
                all = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger?.LogError(ex, "ProfilesPlugin: The {Locale} translation is not valid JSON.", code);
                return null;
            }
            if (all == null) return null;

            var part = new System.Text.Json.Nodes.JsonObject();
            foreach (var pair in all)
            {
                if (pair.Key.StartsWith(AdminKeyPrefix, StringComparison.Ordinal) != admin) continue;
                // The mail's words, and the messages sent to apps, are read by the server
                // (Mail.EmailText), never by a page.
                if (Mail.EmailText.IsServerText(pair.Key)) continue;
                part[pair.Key] = pair.Value?.DeepClone();
            }

            // Letters are written as letters. The default encoder turns every non-ASCII
            // character into \uXXXX, which would roughly double a Polish or Turkish file.
            // Not the "unsafe relaxed" encoder, though: this one still escapes HTML-
            // sensitive characters and U+2028/U+2029, so the output stays safe in any
            // context it might one day be put in (see tests/js/bundle.js).
            var text = part.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
                    System.Text.Unicode.UnicodeRanges.All)
            });
            CachedI18nParts[key] = text;
            return text;
        }

        [HttpGet("i18n/{locale}")]
        [Produces("application/json")]
        public ActionResult GetI18n(string locale, [FromQuery] string? part = null)
        {
            // profiles.js requests "fr.json"; strip the extension so the lookup key
            // matches SupportedI18nLocales and the embedded resource name either way.
            var code = locale.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? locale[..^5]
                : locale;

            // Checked before the cache is touched, so an unknown code cannot grow the
            // dictionary — the key would otherwise be whatever the caller sent.
            if (!EmbeddedLocales.Value.TryGetValue(code, out var canonical)) return NotFound();
            code = canonical;

            // Anything but "admin" is the household's half, so an unknown value cannot
            // become a third cache entry or an error.
            var admin = string.Equals(part, "admin", StringComparison.OrdinalIgnoreCase);
            var json = ReadLocalePart(code, admin, _logger);

            if (json == null)
            {
                // The code is in EmbeddedLocales, so the file is in the assembly and this is a
                // read that failed rather than a language nobody added. 503 says "ask again";
                // 404 would tell the client to stop asking.
                _logger.LogWarning(
                    "ProfilesPlugin: The {Locale} translation is embedded but could not be read; "
                    + "the client will fall back to English and retry.", code);
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Same cache contract as profiles.js: the plugin version is the cache-buster,
            // so a stale copy still picks up an updated translation within max-age.
            var etag = "\"" + GetPluginVersion() + "-" + code + (admin ? "-admin" : "") + "\"";
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

            if (!ManagementAllowed(masterUserId, config, null, out var pinRefusal)) return pinRefusal!;

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

            if (!ManagementAllowed(masterUserId, config, null, out var pinRefusal)) return pinRefusal!;

            // Counted before the code is looked up, per address and per account, so neither
            // parallel requests nor a second address buy extra guesses.
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            var accountKey = "account:" + masterId.ToString("N");
            if (!RateLimiter.Bonfire.TryBegin(ip))
                return TooManyAttempts(RateLimiter.Bonfire, ip, "Too many failed attempts.");
            if (!RateLimiter.Bonfire.TryBegin(accountKey))
                return TooManyAttempts(RateLimiter.Bonfire, accountKey, "Too many failed attempts.");

            var code = request.Code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                return BadRequest("Invalid code format.");
            }

            Guid ownerUserId;

            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g =>
                    string.Equals(g.BonfireCode, code, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
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
            }

            // The count is never cleared by a join. It was, and a household can always join a
            // Bonfire whose code it knows - leave it and join again - so two guesses at other
            // households' codes, a rejoin, two more, without end (R-1303). Three a quarter
            // hour costs someone who mistyped nothing they will notice.

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
        public async Task<ActionResult> KickBonfireMember([FromBody] KickBonfireRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var callerMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (callerMapping != null && callerMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can manage Bonfire groups.");

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            string? newCode = null;
            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
                if (group == null) return BadRequest("You do not own a Bonfire group.");

                if (group.MemberUserIds.Contains(request.MemberId))
                {
                    group.MemberUserIds.Remove(request.MemberId);

                    // The code the kicked member joined with still worked, so a kick lasted
                    // exactly as long as it took them to type it again. Everyone who stays
                    // is already a member and does not need it.
                    group.BonfireCode = GenerateSecureCode();
                    Plugin.Instance?.SaveConfiguration();
                    newCode = group.BonfireCode;
                }
            }

            if (newCode != null)
            {
                // And what they had already opened is closed too, both ways (R-1302).
                await SignOutClosedEntriesAsync().ConfigureAwait(false);
                return Ok(new { BonfireCode = newCode });
            }

            return NotFound("Member not found in your Bonfire group.");
        }

        /// <summary>
        /// Replaces the owner's Bonfire code. Members already in stay in; the old code stops
        /// working. For a code that has been shared further than intended.
        /// </summary>
        [HttpPost("bonfire/regenerate-code")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult RegenerateBonfireCode()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var callerMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (callerMapping != null && callerMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can manage Bonfire groups.");

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
                if (group == null) return BadRequest("You do not own a Bonfire group.");

                group.BonfireCode = GenerateSecureCode();
                Plugin.Instance?.SaveConfiguration();

                _logger.LogInformation("ProfilesPlugin: Bonfire code for {Owner} replaced by its owner.", masterId);
                return Ok(new { BonfireCode = group.BonfireCode });
            }
        }

        [HttpPost("bonfire/leave")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> LeaveBonfire()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            bool left = false;
            lock (ConfigLock)
            {
                var joinedGroup = config.BonfireGroups.FirstOrDefault(g => g.MemberUserIds.Contains(masterId));
                if (joinedGroup != null)
                {
                    joinedGroup.MemberUserIds.Remove(masterId);
                    Plugin.Instance?.SaveConfiguration();
                    left = true;
                }
            }

            if (left)
            {
                await SignOutClosedEntriesAsync().ConfigureAwait(false);
                return Ok();
            }

            return BadRequest("You are not in any Bonfire group.");
        }

        [HttpPost("bonfire/delete-group")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> DeleteBonfireGroup()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            bool deleted = false;
            lock (ConfigLock)
            {
                var group = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
                if (group != null)
                {
                    config.BonfireGroups.Remove(group);
                    Plugin.Instance?.SaveConfiguration();
                    deleted = true;
                }
            }

            if (deleted)
            {
                await SignOutClosedEntriesAsync().ConfigureAwait(false);
                return Ok();
            }

            return BadRequest("You do not own a Bonfire group.");
        }

        [HttpPost("bonfire/settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> UpdateBonfireSettings([FromBody] UpdateBonfireSettingsRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            if (currentMapping != null && currentMapping.MasterUserId != masterUserId)
                return Unauthorized("Only the master profile can update Bonfire settings.");

            if (!ManagementAllowed(masterUserId, config, null, out var pinRefusal)) return pinRefusal!;

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

                // Left out keeps them (R-1159). They were plain bool, so a body without them
                // switched both off.
                if (request.HideMySubProfilesFromOthers.HasValue)
                    masterMapping.HideMySubProfilesFromOthers = request.HideMySubProfilesFromOthers.Value;
                if (request.HideOthersSubProfilesFromMe.HasValue)
                    masterMapping.HideOthersSubProfilesFromMe = request.HideOthersSubProfilesFromMe.Value;

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

            // Hiding profiles, or withdrawing the home-network consent, closes doors too.
            await SignOutClosedEntriesAsync().ConfigureAwait(false);
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
                EmergencyCodeConfigured = !string.IsNullOrEmpty(config.PanicCodeHash),
                // Read from the signed-in account's OWN row, not the master's: unlike the
                // switcher settings above, a theme belongs to one profile.
                Theme = Themes.Resolve(currentMapping, config),
                DefaultTheme = Themes.Normalize(config.DefaultTheme, config),
                AllowProfileThemes = config.AllowProfileThemes,
                RichThemeMotion = config.RichThemeMotion,
                // Read on every page load already, so the web client learns here whether to
                // fetch /onboarding at all rather than asking on every load.
                OnboardingPending = OnboardingPendingFor(currentUserId, config)
            });
        }

        // ── First-time setup ────────────────────────────────────────────────────────

        /// <summary>A sub-profile, as opposed to a main account (which may have no row).</summary>
        internal static bool IsSubProfile(Guid userId, PluginConfiguration config)
        {
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            return row != null && row.MasterUserId != Guid.Empty && row.MasterUserId != userId;
        }

        /// <summary>
        /// The steps to offer this main account, in order. A step whose feature is off is
        /// left out, not shown disabled: the theme step needs profiles to be allowed a theme.
        /// <c>Done</c> marks what is already true, so a client can pass over it.
        /// </summary>
        internal static List<OnboardingStepInfo> OnboardingStepsFor(Guid mainId, PluginConfiguration config)
        {
            var o = config.Onboarding ?? new OnboardingSettings();
            return o.OfferedSteps()
                .Where(id => id != OnboardingSteps.Theme || config.AllowProfileThemes)
                .Where(id => id != OnboardingSteps.ResetEmail || Features.Available(Features.Email, config))
                .Select(id => new OnboardingStepInfo
                {
                    Id = id,
                    Done = id == OnboardingSteps.MainPin ? MainHasPin(mainId, config)
                         : id == OnboardingSteps.FirstProfile ? config.Mappings.Any(m => m.MasterUserId == mainId && m.ProfileUserId != mainId)
                         : id == OnboardingSteps.ResetEmail ? config.AccountEmails.Any(a => a.UserId == mainId && !string.IsNullOrEmpty(a.Address))
                         : false
                })
                .ToList();
        }

        internal static bool OnboardingPendingFor(Guid userId, PluginConfiguration config)
        {
            var o = config.Onboarding;
            return o != null && o.Enabled
                && !IsSubProfile(userId, config)
                && !o.HasSeen(userId)
                && OnboardingStepsFor(userId, config).Count > 0;
        }

        /// <summary>
        /// The first-time setup this account should see: whether to show it, the welcome,
        /// and the steps. Main accounts only; a profile is always told it is not pending.
        /// </summary>
        [HttpGet("onboarding")]
        [ProducesResponseType(typeof(OnboardingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<OnboardingResponse> GetOnboarding()
        {
            var userId = GetCurrentUserId();
            // An API key carries an all-zero id: there is no account to walk through anything.
            if (userId == null || userId == Guid.Empty) return Unauthorized();
            var config = ReadConfigSnapshot() ?? new PluginConfiguration();
            var pending = OnboardingPendingFor(userId.Value, config);

            return Ok(new OnboardingResponse
            {
                Pending = pending,
                Generation = config.Onboarding?.Generation ?? 1,
                Steps = pending ? OnboardingStepsFor(userId.Value, config) : new List<OnboardingStepInfo>()
            });
        }

        /// <summary>
        /// Records that this main account has been through first-time setup, finished or
        /// skipped. No PIN: it changes nothing but whether the setup is shown, and each step
        /// that changes something goes through its own route and that route's checks.
        /// </summary>
        [HttpPost("onboarding/done")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult OnboardingDone()
        {
            var userId = GetCurrentUserId();
            if (userId == null || userId == Guid.Empty) return Unauthorized();

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                // A profile is never shown it, so there is nothing to record.
                if (IsSubProfile(userId.Value, config)) return Ok();
                config.Onboarding ??= new OnboardingSettings();
                config.Onboarding.MarkSeen(userId.Value);
                Plugin.Instance?.SaveConfiguration();
            }
            return Ok();
        }

        /// <summary>
        /// "Show again to everyone": every main account sees first-time setup once more,
        /// on its next visit, including accounts that were counted as having seen it.
        /// </summary>
        [HttpPost("admin/onboarding/show-again")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> ShowOnboardingAgain()
        {
            var adminError = RequireAdministrator("show first-time setup again");
            if (adminError != null) return adminError;

            int generation;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                config.Onboarding ??= new OnboardingSettings();
                config.Onboarding.Generation++;
                // Whoever is in the list now was counted as having seen the old setup; they
                // will see this one. The list itself can go: a record is only ever compared
                // against the current generation, and none can reach it yet.
                config.Onboarding.Seen = new List<OnboardingRecord>();
                generation = config.Onboarding.Generation;
                Plugin.Instance?.SaveConfiguration();
            }
            _logger.LogInformation("ProfilesPlugin: First-time setup will be shown again to every main account.");
            return Ok(new { Generation = generation });
        }

        /// <summary>
        /// Sets one profile's theme. The Settings page uses this rather than <c>update</c>,
        /// which rewrites every field of the profile — its rating, libraries and devices —
        /// from whatever the caller sends, and a page that only knows the theme has none of
        /// those to send. Same checks as <c>update</c>: the master only, with its PIN.
        /// </summary>
        [HttpPost("theme")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public ActionResult<object> SetProfileTheme([FromBody] SetProfileThemeRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid currentUserId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;
            if (currentUserId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal)) return pinRefusal!;

            ThemeChange result;
            string resolved;
            // Read before the lock: a user lookup can reach the database, and nothing else
            // waits on ConfigLock for that.
            var ownName = _userManager.GetUserById(masterUserId)?.Username;
            lock (ConfigLock)
            {
                result = Themes.SetFor(config, masterUserId, request.ProfileId, request.Theme, ownName);
                var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                resolved = Themes.Resolve(row, config);
                if (result == ThemeChange.Changed) Plugin.Instance?.SaveConfiguration();
            }

            switch (result)
            {
                case ThemeChange.NotAllowed:
                    return StatusCode(StatusCodes.Status403Forbidden, "This server does not let profiles choose a theme.");
                case ThemeChange.UnknownTheme:
                    return BadRequest(ValidateTheme(request.Theme, config));
                case ThemeChange.NotInHousehold:
                    return Unauthorized("Unauthorized profile update attempt.");
            }

            return Ok(new
            {
                ProfileId = request.ProfileId,
                Theme = resolved,
                Stored = Themes.IsKnown(request.Theme, config) ? Themes.Normalize(request.Theme, config) : null
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

            if (!ManagementAllowed(currentUserId, config, null, out var pinRefusal)) return pinRefusal!;

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

        // ── The main account's own settings ─────────────────────────────────────────

        /// <summary>
        /// Checks the main account's PIN and nothing else. For a client about to manage the
        /// household, so it can ask for the PIN once and send it with what follows.
        /// <para>
        /// /verify-pin is not this. It answers "may I open this profile", so it says yes
        /// without a PIN wherever opening needs none: the home network, or a main account
        /// that doesn't ask. A client that took that yes as proof of the PIN stored whatever
        /// had been typed, and every change after it was refused as a wrong PIN.</para>
        /// </summary>
        [HttpPost("account/verify-pin")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult VerifyAccountPin([FromBody] VerifyAccountPinRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            var mainId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId);
            if (currentMapping != null && currentMapping.MasterUserId != mainId)
                return Unauthorized("Only the master profile can manage profiles.");

            if (!ManagementAllowed(mainId, config, request?.Pin, out var refusal)) return refusal!;
            return Ok();
        }

        /// <summary>
        /// The main account's own settings: its PIN, and whether opening it asks for one.
        /// <para>
        /// The full profile update can set both too, but it also writes the colour and name
        /// it is sent, so a prompt that only wants a PIN would have to send the whole form
        /// back to avoid resetting them.</para>
        /// </summary>
        [HttpPost("account")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<object>> UpdateAccount([FromBody] UpdateAccountRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            var mainId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId);
            if (currentMapping != null && currentMapping.MasterUserId != mainId)
                return Unauthorized("Only the master profile can manage profiles.");

            if (!string.IsNullOrEmpty(request.Pin) && !IsValidPin(request.Pin))
                return BadRequest("PIN code must be a numeric value between 4 and 8 digits.");

            if (request.Pin == string.Empty && config.RequireMainPin)
                return BadRequest(MainPinRequiredMessage);

            if (!string.IsNullOrEmpty(request.TimeZone) && !HouseholdTime.TryFind(request.TimeZone, out _))
                return BadRequest("That time zone isn't known to this server.");

            var settingOwnPin = !string.IsNullOrEmpty(request.Pin);
            if (!ManagementAllowed(mainId, config, request.MasterPin, out var refusal, settingOwnPin)) return refusal!;

            bool hasPin;
            bool askPinToOpen;
            bool bypass;
            int lockout;
            string timeZone;
            bool seenLocally;
            lock (ConfigLock)
            {
                seenLocally = Plugin.Instance?.Configuration?.HouseholdsSeenLocally?.Contains(mainId) ?? false;
                var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId);
                if (row == null)
                {
                    row = new ProfileMapping
                    {
                        ProfileUserId = mainId,
                        MasterUserId = mainId,
                        ProfileName = _userManager.GetUserById(mainId)?.Username ?? "Master",
                        IsHidden = false
                    };
                    config.Mappings.Add(row);
                }

                if (request.Pin == string.Empty) row.PinHash = string.Empty;
                else if (request.Pin != null) row.PinHash = HashPin(request.Pin);

                if (request.AskPinToOpen.HasValue) row.AskPinToOpen = request.AskPinToOpen.Value;
                if (request.BypassPinOnLocalNetwork.HasValue) row.BypassPinOnLocalNetwork = request.BypassPinOnLocalNetwork.Value;
                if (request.LockoutMinutes.HasValue) row.LockoutMinutes = ClampLockoutMinutes(request.LockoutMinutes.Value);
                if (request.TimeZone != null) row.TimeZone = request.TimeZone.Trim();

                hasPin = !string.IsNullOrEmpty(row.PinHash);
                askPinToOpen = row.AskPinToOpen;
                bypass = row.BypassPinOnLocalNetwork;
                lockout = row.LockoutMinutes;
                timeZone = row.TimeZone ?? string.Empty;
                Plugin.Instance?.SaveConfiguration();
            }

            // As for the profile update: a main PIN set or cleared changes which provider the
            // account signs in with on other apps, and that should not wait for a restart.
            if (request.Pin != null) ProfilesBootstrapTask.ReconcileAuthProvidersNow();
            if (request.TimeZone != null) Auth.ViewingTracker.Invalidate();
            // Another household that opened it with the old PIN is out (R-1302b).
            if (!string.IsNullOrEmpty(request.Pin)) await SignOutAfterPinChangeAsync(mainId).ConfigureAwait(false);

            return Ok(new { HasPin = hasPin, AskPinToOpen = askPinToOpen, BypassPinOnLocalNetwork = bypass, SeenOnLocalNetwork = seenLocally, LockoutMinutes = lockout, TimeZone = timeZone });
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

            return ImageFileResult(file, ContentTypeForExtension(extension));
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
            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal)) return pinRefusal!;

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

            return ImageFileResult(found.Value.Path, found.Value.ContentType);
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
            // The lists below are read from a copy; the live configuration is only touched
            // under the lock further down, where this route writes.
            var view = ReadConfigSnapshot() ?? config;
            var currentMapping = view.Mappings.FirstOrDefault(m => m.ProfileUserId == currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            // Every device already whitelisted on one of this account's profiles MUST appear in
            // the picker, whether or not it is currently switched on. The edit form rebuilds
            // AllowedDeviceIds from the checkboxes it rendered, so any whitelisted device that
            // is missing from this response gets silently dropped on the next save — and a
            // whitelist that empties out stops restricting anything at all.
            var whitelistedDeviceIds = view.Mappings
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
            foreach (var m in view.Mappings.Where(m => m.MasterUserId == masterUserId))
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
                var changed = false;
                foreach (var d in config.KnownDevices)
                {
                    if (d.MasterUserId == Guid.Empty
                        && (sessionDeviceIds.Contains(d.DeviceId) || whitelistedDeviceIds.Contains(d.DeviceId)))
                    {
                        d.MasterUserId = masterUserId;
                        changed = true;
                    }

                    // Names recorded before 1.5.10 kept whatever encoding the client sent,
                    // because the header parser returned values verbatim — a phone shows as
                    // "Pixel+8". The parser is fixed, but a stored name only corrects itself
                    // the next time that device contacts the server, and a device on a
                    // whitelist may not do so for months. Repaired in place instead.
                    var decodedName = DecodeLegacyDeviceName(d.DeviceName);
                    if (!string.Equals(decodedName, d.DeviceName, StringComparison.Ordinal))
                    {
                        d.DeviceName = decodedName;
                        changed = true;
                    }

                    var decodedClient = DecodeLegacyDeviceName(d.Client);
                    if (!string.Equals(decodedClient, d.Client, StringComparison.Ordinal))
                    {
                        d.Client = decodedClient;
                        changed = true;
                    }
                }
                if (changed) Plugin.Instance?.SaveConfiguration();
            }

            List<KnownDevice> devices;
            lock (ConfigLock)
            {
                // Copied out under the lock before the names are touched: the scoping step
                // synthesises records for whitelisted devices that are no longer in the list,
                // and DisambiguateDeviceNames writes to what it is handed. Renaming the live
                // configuration objects would persist a display decision as if it were the
                // device's real name, and the next request would then disambiguate the
                // already-disambiguated name.
                devices = ScopeDevicesToHousehold(config.KnownDevices, masterUserId, whitelistedDeviceIds)
                    .Select(d => new KnownDevice
                    {
                        DeviceId = d.DeviceId,
                        DeviceName = d.DeviceName,
                        Client = d.Client,
                        LastSeen = d.LastSeen,
                        MasterUserId = d.MasterUserId,
                        NameIsCustom = d.NameIsCustom
                    })
                    .ToList();
            }

            return Ok(DisambiguateDeviceNames(devices));
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

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            if (string.IsNullOrEmpty(request.DeviceId))
                return BadRequest("DeviceId is required.");

            lock (ConfigLock)
            {
                // This household's claim and this household's lists, nothing more (R-1048).
                // It matched on the id alone across the server-wide list: any main account
                // could forget another household's television, strip it from that household's
                // device lists, and read that household's profile names off the refusal.
                // Rename and merge always checked ownership; this did not.
                var household = config.Mappings.Where(m => m.MasterUserId == masterId).ToList();
                bool Matches(string? id) => string.Equals(id?.Trim(), request.DeviceId.Trim(), StringComparison.OrdinalIgnoreCase);
                var records = config.KnownDevices.Where(d => Matches(d.DeviceId)).ToList();
                var owned = records.Where(d => d.OwnedBy(masterId)).ToList();
                // A device on one of its lists with no record left is still the household's
                // to take off them: the device list shows it as "Previously allowed device".
                var listed = household.Any(m => m.AllowedDeviceIds?.Any(Matches) == true);
                if (owned.Count == 0 && !listed) return BadRequest("Unknown device.");

                // Forgetting a device also drops it from every list that named it, and an
                // empty list means "any device". So forgetting the only device a profile
                // was restricted to would silently make that profile available everywhere —
                // the exact widening RemoveStaleDevices is written to avoid, arrived at by a
                // different route. Refuse and say which profiles are in the way; the
                // household can loosen or re-point them deliberately.
                var wouldUnrestrict = HouseholdProfilesLeftUnrestrictedBy(config, new[] { request.DeviceId }, masterId);
                if (wouldUnrestrict.Count > 0)
                {
                    var names = household
                        .Where(m => wouldUnrestrict.Contains(m.ProfileUserId))
                        .Select(m => m.ProfileName)
                        .ToList();
                    return BadRequest(
                        "This is the only device allowed for "
                        + string.Join(", ", names)
                        + ". Allow another device for those profiles first, or clear their device list.");
                }

                // Another household that uses it keeps it, and sees it shown as theirs.
                foreach (var d in owned)
                {
                    var others = Auth.DeviceRegistry.AddOwners(d.Owners, d.MasterUserId).Where(o => o != masterId).ToList();
                    if (others.Count == 0)
                    {
                        config.KnownDevices.Remove(d);
                        continue;
                    }
                    d.Owners = others;
                    if (d.MasterUserId == masterId) d.MasterUserId = others[others.Count - 1];
                }

                foreach (var mapping in household)
                    mapping.AllowedDeviceIds?.RemoveAll(id => Matches(id));

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        /// <summary>
        /// Folds one device record into another, for the case the device list cannot detect on
        /// its own: one physical machine holding two device ids.
        /// <para>
        /// Jellyfin's web client stores its device id in <c>localStorage</c>, which is scoped
        /// to the origin — so one computer reaching the server by LAN address and by domain
        /// name is two devices as far as any client-supplied id can tell, and so is a new
        /// browser profile or a reinstalled app. Nothing in the request distinguishes that
        /// from two genuinely different machines, and guessing wrong widens a whitelist, so
        /// the plugin will not merge on its own. This is the administrator saying so.
        /// </para>
        /// </summary>
        [HttpPost("devices/merge")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult MergeDevices([FromBody] MergeDevicesRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can merge devices.");

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            if (string.IsNullOrWhiteSpace(request.FromDeviceId) || string.IsNullOrWhiteSpace(request.IntoDeviceId))
                return BadRequest("Both devices are required.");

            lock (ConfigLock)
            {
                // Ownership is checked against the household, not just "is an administrator":
                // KnownDevices is one server-wide list, so without this one account could fold
                // another household's device into its own and inherit its whitelist entries.
                var owned = config.KnownDevices.Where(d =>
                    (string.Equals(d.DeviceId, request.FromDeviceId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(d.DeviceId, request.IntoDeviceId, StringComparison.OrdinalIgnoreCase))
                    && d.OwnedBy(masterId)).ToList();

                if (owned.Count != 2)
                    return BadRequest("Both devices must be ones this account has used.");

                if (!MergeDeviceRecords(config, request.FromDeviceId, request.IntoDeviceId))
                    return BadRequest("Those devices could not be merged.");

                Plugin.Instance?.SaveConfiguration();
            }

            return Ok();
        }

        /// <summary>
        /// Finds device records that are almost certainly one machine, and — only when asked
        /// twice — folds each group into its most recently seen member.
        /// <para>
        /// Merging one pair at a time is fine for a stray duplicate and useless for the case
        /// that actually happens: one browser that has minted a new id every time its site
        /// data was cleared, leaving four or five identical-looking rows to work through.
        /// </para>
        /// <para>
        /// <c>apply=false</c> is the default. This rewrites device whitelists and there is no
        /// undo, so the picker shows what would be merged and asks first.
        /// </para>
        /// </summary>
        [HttpPost("devices/merge-duplicates")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult MergeDuplicateDevices([FromQuery] bool apply = false)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can merge devices.");

            if (apply && !ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            lock (ConfigLock)
            {
                // This household's devices only. The list is server-wide, and grouping across
                // it could fold another household's hardware into this one's whitelist.
                var owned = config.KnownDevices.Where(d => d.OwnedBy(masterId)).ToList();
                var groups = GroupLikelyDuplicates(owned);

                if (!apply)
                {
                    return Ok(new
                    {
                        applied = false,
                        groups = groups.Select(g => new
                        {
                            keep = new { g[0].DeviceId, g[0].DeviceName, g[0].Client, g[0].LastSeen },
                            merge = g.Skip(1).Select(d => new { d.DeviceId, d.DeviceName, d.LastSeen })
                        })
                    });
                }

                var merged = 0;
                foreach (var group in groups)
                {
                    // Into the most recently seen, which is the one still in use.
                    var keep = group[0];
                    foreach (var other in group.Skip(1))
                    {
                        if (MergeDeviceRecords(config, other.DeviceId, keep.DeviceId)) merged++;
                    }
                }

                if (merged > 0) Plugin.Instance?.SaveConfiguration();
                return Ok(new { applied = true, merged, groups = groups.Count });
            }
        }

        /// <summary>
        /// Renames a device in the picker. The name sticks: see <c>KnownDevice.NameIsCustom</c>.
        /// </summary>
        [HttpPost("devices/rename")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult RenameDevice([FromBody] RenameDeviceRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();
            Guid masterId = currentUserIdVal.Value;

            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can rename devices.");

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            var name = request.DeviceName?.Trim();
            if (string.IsNullOrWhiteSpace(request.DeviceId)) return BadRequest("DeviceId is required.");
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("A name is required.");
            if (name.Length > 64) name = name.Substring(0, 64);

            lock (ConfigLock)
            {
                var device = config.KnownDevices.FirstOrDefault(d =>
                    string.Equals(d.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase)
                    && d.OwnedBy(masterId));

                if (device == null) return BadRequest("Unknown device.");

                device.DeviceName = name;
                device.NameIsCustom = true;
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
            return ImageFileResult(found.Value.Path, found.Value.ContentType);
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
            if (!RateLimiter.Panic.TryBegin(ip))
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

            LogActivity("Bonfire was switched off with the emergency code", "EmergencyDisable", Guid.Empty,
                "From " + ip + ". It stays off until Jellyfin restarts.", LogLevel.Error);

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

        // ── Announcements ───────────────────────────────────────────────────────────

        /// <summary>
        /// The announcements showing now, for the profile screen.
        /// <para>
        /// Signed in, not anonymous: a message an administrator writes for the household
        /// ("down for an upgrade at 2") is not something to publish to anyone who finds the
        /// server's address.
        /// </para>
        /// </summary>
        [HttpGet("announcements")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetAnnouncements()
        {
            if (GetCurrentUserId() == null) return Unauthorized();

            // Revalidated every time, never served stale: one that has just ended, or just
            // started, changes the body and so the tag.
            var all = Plugin.Instance?.Configuration?.Announcements;
            return ETagged(Configuration.Announcements.Active(all, DateTime.UtcNow).Select(DescribeAnnouncement).ToList());
        }

        /// <summary>Every announcement, showing or not, for the settings page.</summary>
        [HttpGet("admin/announcements")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetAllAnnouncements()
        {
            var adminError = RequireAdministrator("manage announcements");
            if (adminError != null) return adminError;

            var now = DateTime.UtcNow;
            var all = Plugin.Instance?.Configuration?.Announcements ?? new List<Announcement>();
            return Ok(all
                .OrderByDescending(a => a.CreatedUtc)
                .Select(a => new
                {
                    a.Id,
                    a.Message,
                    a.Severity,
                    a.StartsUtc,
                    a.EndsUtc,
                    a.CreatedUtc,
                    Showing = Configuration.Announcements.IsActive(a, now),
                }));
        }

        /// <summary>Creates an announcement, or replaces the one with the same id.</summary>
        [HttpPost("admin/announcements")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<object> SaveAnnouncement([FromBody] SaveAnnouncementRequest request)
        {
            var adminError = RequireAdministrator("manage announcements");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");

            var now = DateTime.UtcNow;
            var announcement = new Announcement
            {
                Id = request.Id ?? Guid.NewGuid(),
                Message = (request.Message ?? string.Empty).Trim(),
                Severity = string.IsNullOrWhiteSpace(request.Severity)
                    ? Configuration.Announcements.Info
                    : request.Severity.Trim().ToLowerInvariant(),
                StartsUtc = Configuration.Announcements.ToUtc(request.StartsUtc),
                EndsUtc = Configuration.Announcements.ToUtc(request.EndsUtc),
                CreatedUtc = now,
            };

            var invalid = Configuration.Announcements.Validate(announcement);
            if (invalid != null) return BadRequest(invalid);

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                var existing = config.Announcements?.FirstOrDefault(a => a.Id == announcement.Id);
                if (request.Id.HasValue && existing == null) return NotFound("No such announcement.");
                if (existing != null) announcement.CreatedUtc = existing.CreatedUtc;

                var next = Configuration.Announcements.With(config.Announcements, announcement, now);
                if (next.Count > Configuration.Announcements.MaxCount)
                {
                    return BadRequest($"Delete an announcement first. At most {Configuration.Announcements.MaxCount} are kept.");
                }

                config.Announcements = next;
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok(DescribeAnnouncement(announcement));
        }

        /// <summary>Deletes an announcement. Deleting one that is already gone is not an error.</summary>
        [HttpPost("admin/announcements/delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult DeleteAnnouncement([FromBody] DeleteAnnouncementRequest request)
        {
            var adminError = RequireAdministrator("manage announcements");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                if (config.Announcements?.Any(a => a.Id == request.Id) == true)
                {
                    config.Announcements = config.Announcements.Where(a => a.Id != request.Id).ToList();
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            return Ok();
        }

        // ── Standalone accounts ─────────────────────────────────────────────────────

        /// <summary>
        /// What Jellyfin itself accepts as a username (UserManager.ThrowIfInvalidUsername).
        /// Checked here so the refusal is a sentence, not an exception.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex JellyfinUsername =
            new(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Turns a profile into an ordinary Jellyfin account of its own: a teenager moving
        /// out, a lodger who was given a profile to start with.
        /// <para>
        /// <b>It keeps what it can watch.</b> The master's policy is applied once more, so the
        /// account starts with exactly the restrictions it had as a profile, and from then on
        /// nothing is inherited. Loosening them is the administrator's call, in Dashboard →
        /// Users, like any other account.
        /// </para>
        /// <para>
        /// <b>Its own sign-in.</b> A profile is bound to Bonfire's PIN provider or carries a
        /// random password nobody knows. Leaving it like that would be an account nobody can
        /// open, so a password is required, and the account is put on the provider its
        /// master signs in with (never Bonfire's).
        /// </para>
        /// <para>
        /// <b>Signed out everywhere.</b> Its sessions came from a profile switch. They are
        /// ended, so the next sign-in is with the new password.
        /// </para>
        /// <para>
        /// Administrator only: it creates a sign-in for the server, which is not a household's
        /// decision. One way: there is no route back to being a profile.
        /// </para>
        /// </summary>
        [HttpPost("admin/standalone")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<object>> MakeStandalone([FromBody] StandaloneRequest request)
        {
            var adminError = RequireAdministrator("turn a profile into its own account");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");

            if (string.IsNullOrEmpty(request.Password))
            {
                return BadRequest("Set a password for the new account.");
            }

            ProfileMapping? mapping;
            lock (ConfigLock)
            {
                mapping = Plugin.Instance?.Configuration?.Mappings?.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
            }

            if (mapping == null) return NotFound("Profile not found.");
            if (mapping.MasterUserId == mapping.ProfileUserId || mapping.MasterUserId == Guid.Empty)
            {
                return BadRequest("A main account is already its own account.");
            }

            var user = _userManager.GetUserById(request.ProfileId);
            if (user == null) return NotFound("Underlying system user missing.");

            var username = (request.Username ?? string.Empty).Trim();
            if (username.Length > 0 && !string.Equals(username, user.Username, StringComparison.Ordinal))
            {
                if (username.Length > 100 || !JellyfinUsername.IsMatch(username))
                {
                    return BadRequest("A username can use letters, numbers, spaces and - _ . ' @ +.");
                }

                var taken = _userManager.GetUserByName(username);
                if (taken != null && taken.Id != user.Id)
                {
                    return BadRequest("An account with that username already exists.");
                }

                // Before the first write. If this server has no rename we can call, say so now:
                // found at the end, the profile would already be an account with a new password
                // and the old name, and the administrator would get a 500 for a half-done job.
                if (!CanRenameUser())
                {
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        "This server can't rename accounts from here. Leave the name blank and rename it in Dashboard → Users.");
                }
            }

            var released = await ReleaseProfileAsAccountAsync(mapping, user, request.Password, username).ConfigureAwait(false);
            if (released.Error != null) return StatusCode(released.Status, released.Error);

            return Ok(new { UserId = released.User.Id, Username = released.User.Username });
        }

        /// <summary>
        /// Takes a profile out of its household and leaves its account standing on its own, with
        /// a sign-in of its own: the steps of <see cref="MakeStandalone"/>, shared with deleting a
        /// profile that was an account (R-1134). The caller has checked who may do it, and that
        /// the new username, if any, is free. Returns an error and the status to send, or none.
        /// </summary>
        private async Task<(int Status, string? Error, Jellyfin.Database.Implementations.Entities.User User)> ReleaseProfileAsAccountAsync(
            ProfileMapping mapping, Jellyfin.Database.Implementations.Entities.User user, string password, string username)
        {
            // 1. The restrictions it has today, made current. Nothing inherits after this.
            if (ShouldInheritMasterPolicy(mapping, mapping.ProfileUserId, mapping.MasterUserId))
            {
                await Auth.ProfilePolicySync.ApplyAsync(_userManager, _libraryManager, _logger, mapping.ProfileUserId)
                    .ConfigureAwait(false);
            }

            // 2. Out of every session a profile switch gave it.
            try
            {
                await _sessionManager.RevokeUserTokens(user.Id, null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: could not sign {User} out before making it its own account.", user.Username);
            }

            // 3. No longer a profile. Before the provider changes below: while it is mapped,
            //    the provider reconcile would put it straight back on Bonfire's.
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                var row = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == mapping.ProfileUserId);
                if (row != null)
                {
                    config!.Mappings.Remove(row);
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            // 4. Shown on the sign-in screen if its master is. Every profile is hidden, because
            //    it is reached through its master; left that way, the new account is missing
            //    from Jellyfin's user list and nobody is told to type its name. Before the
            //    provider below: Jellyfin's UpdatePolicyAsync also writes the policy's
            //    provider id onto the account, so a policy write after it would undo it.
            user = _userManager.GetUserById(mapping.ProfileUserId) ?? user;
            var masterAccount = _userManager.GetUserById(mapping.MasterUserId);
            var hidden = masterAccount != null && _userManager.GetUserDto(masterAccount, string.Empty).Policy?.IsHidden == true;
            var ownPolicy = _userManager.GetUserDto(user, string.Empty).Policy;
            if (ownPolicy != null && ownPolicy.IsHidden != hidden)
            {
                ownPolicy.IsHidden = hidden;
                await _userManager.UpdatePolicyAsync(user.Id, ownPolicy).ConfigureAwait(false);
                user = _userManager.GetUserById(mapping.ProfileUserId) ?? user;
            }

            // 5. Its own sign-in, on Jellyfin's own provider. Never an empty id: see
            //    ProfilesBootstrapTask.ReconcileAuthProviders for what that did to a server.
            var provider = StandaloneProviderId(mapping.MasterUserId);
            if (!string.Equals(user.AuthenticationProviderId, provider, StringComparison.Ordinal))
            {
                user.AuthenticationProviderId = provider;
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }

            // After the mapping is gone: while it is a profile, Bonfire's provider refuses a
            // password change outright. Past the point of no return, so a failure here says
            // what is left to do rather than pretending nothing happened.
            try
            {
                await ChangePasswordCompat(user, password).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ProfilesPlugin: {User} is its own account now, but its password could not be set.", user.Username);
                ProfilesBootstrapTask.ReconcileAuthProvidersNow();
                return (StatusCodes.Status500InternalServerError,
                    "The account was made, but its password couldn't be set. Set it in Dashboard → Users.", user);
            }

            if (username.Length > 0 && !string.Equals(username, user.Username, StringComparison.Ordinal))
            {
                try
                {
                    await RenameUserCompat(user, username).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "ProfilesPlugin: {User} is its own account now, but could not be renamed.", user.Username);
                    ProfilesBootstrapTask.ReconcileAuthProvidersNow();
                    return (StatusCodes.Status500InternalServerError,
                        "The account was made, but it couldn't be renamed. Rename it in Dashboard → Users.", user);
                }
            }

            // 6. The master may have just lost its last profile.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();

            var masterName = _userManager.GetUserById(mapping.MasterUserId)?.Username ?? mapping.MasterUserId.ToString();
            _logger.LogWarning(
                "ProfilesPlugin: {Admin} made profile {Profile} its own account, {Username}. It no longer belongs to {Master}.",
                CallerName(),
                mapping.ProfileName,
                user.Username,
                masterName);
            LogActivity(CallerName() + " made " + mapping.ProfileName + " its own account, " + user.Username,
                "ProfileMadeAccount", user.Id, "It no longer belongs to " + masterName + ".");

            return (0, null, user);
        }

        /// <summary>
        /// The provider a former profile signs in with: always Jellyfin's own, whose id is
        /// read from the master when the master is on it, so an upstream rename is picked up.
        ///
        /// <para>The first version copied the master's provider whatever it was. For a master
        /// on LDAP or SSO that is wrong: the profile's account was made by Bonfire, not by the
        /// directory, so the directory has never heard of it, and the password set here goes
        /// to a provider that cannot keep it. The account could never sign in. A password set
        /// by an administrator lives in Jellyfin, so the account belongs on Jellyfin's
        /// provider. An administrator who wants it in the directory moves it there.</para>
        /// </summary>
        private string StandaloneProviderId(Guid masterId)
        {
            const string jellyfinDefault = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
            var id = _userManager.GetUserById(masterId)?.AuthenticationProviderId;
            return !string.IsNullOrEmpty(id) && id.EndsWith(".DefaultAuthenticationProvider", StringComparison.Ordinal)
                ? id
                : jellyfinDefault;
        }

        // ── Maintenance ─────────────────────────────────────────────────────────────

        /// <summary>Whether the server is closed for maintenance, and with what message.</summary>
        [HttpGet("admin/maintenance")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<object> GetMaintenance()
        {
            var adminError = RequireAdministrator("manage maintenance mode");
            if (adminError != null) return adminError;

            // A closure whose time has come is open, and is saved as open before it is described.
            Auth.MaintenanceGate.ReopenIfLapsed(_logger, HttpContext?.RequestServices);
            return Ok(DescribeMaintenance(Plugin.Instance?.Configuration?.Maintenance ?? new MaintenanceMode()));
        }

        /// <summary>
        /// Opens or closes the server. Closing tells every open app of a household member,
        /// and stops what is playing if asked to.
        /// <para>
        /// The settings are replaced as a whole object, never edited in place, because the
        /// request gate reads them on every request without the lock.
        /// </para>
        /// </summary>
        [HttpPost("admin/maintenance")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<object>> SetMaintenance([FromBody] MaintenanceRequest request)
        {
            var adminError = RequireAdministrator("manage maintenance mode");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");

            var message = (request.Message ?? string.Empty).Trim();
            if (message.Length > MaintenanceMode.MaxMessageLength)
            {
                return BadRequest($"A maintenance message can be at most {MaintenanceMode.MaxMessageLength} characters.");
            }

            var now = DateTime.UtcNow;
            var until = Configuration.Announcements.ToUtc(request.UntilUtc);
            if (request.Enabled && until.HasValue && until.Value <= now)
            {
                return BadRequest("The time maintenance ends must be in the future.");
            }

            // Nothing to reopen at. Refused rather than ignored: a tick that did nothing would
            // leave a server closed that its administrator believed would reopen.
            if (request.Enabled && request.ReopenAutomatically && !until.HasValue)
            {
                return BadRequest("The time maintenance ends must be set for it to reopen by itself.");
            }

            // So that "was it already closed" below is about the server, not about a saved flag
            // the clock has already cleared.
            Auth.MaintenanceGate.ReopenIfLapsed(_logger, HttpContext?.RequestServices);

            MaintenanceMode next;
            bool opening;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                var was = config.Maintenance ?? new MaintenanceMode();
                opening = request.Enabled && !was.Enabled;
                next = new MaintenanceMode
                {
                    Enabled = request.Enabled,
                    Message = message,
                    UntilUtc = request.Enabled ? until : null,
                    ReopenAutomatically = request.Enabled && request.ReopenAutomatically && until.HasValue,
                    StopPlayback = request.Enabled && request.StopPlayback,
                    StartedUtc = request.Enabled ? (was.Enabled ? was.StartedUtc : now) : null,
                };
                config.Maintenance = next;
                Plugin.Instance?.SaveConfiguration();
            }

            Auth.MaintenanceGate.Forget();

            var caller = CallerName();
            _logger.LogWarning(
                next.Enabled
                    ? "ProfilesPlugin: {Admin} closed the server for maintenance. Only administrators can use it."
                    : "ProfilesPlugin: {Admin} reopened the server after maintenance.",
                caller);
            LogActivity(caller + (next.Enabled ? " closed the server for maintenance" : " reopened the server"),
                next.Enabled ? "MaintenanceOn" : "MaintenanceOff", Guid.Empty,
                severity: next.Enabled ? LogLevel.Warning : LogLevel.Information);

            int told = 0, stopped = 0;
            if (next.Enabled && (opening || request.StopPlayback))
            {
                (told, stopped) = await Auth.MaintenanceGate
                    .NotifyAsync(_sessionManager, _userManager, next, next.StopPlayback, _logger)
                    .ConfigureAwait(false);
            }

            var described = DescribeMaintenance(next);
            return Ok(new
            {
                described.Enabled,
                described.Message,
                described.UntilUtc,
                described.StartedUtc,
                described.StopPlayback,
                described.ReopenAutomatically,
                Told = told,
                Stopped = stopped,
            });
        }

        private static MaintenanceDescription DescribeMaintenance(MaintenanceMode m) => new(
            m.Enabled,
            m.Message ?? string.Empty,
            m.UntilUtc,
            m.StartedUtc,
            m.StopPlayback,
            m.ReopenAutomatically);

        private sealed record MaintenanceDescription(
            bool Enabled, string Message, DateTime? UntilUtc, DateTime? StartedUtc, bool StopPlayback, bool ReopenAutomatically);

        private static object DescribeAnnouncement(Announcement a) => new
        {
            a.Id,
            a.Message,
            a.Severity,
            a.StartsUtc,
            a.EndsUtc,
        };

        /// <summary>
        /// What an app needs to know before it shows any Bonfire screen: that Bonfire is
        /// here, which version, whether it is switched off, and which features are on.
        /// <para>
        /// Anonymous, like <c>panic-state</c>, because an app decides what its sign-in screen
        /// looks like before anyone has signed in. It says nothing about any household: only
        /// settings an administrator has turned on for everyone.
        /// </para>
        /// <para>
        /// <c>ApiVersion</c> goes up only when a released route changes in a way that breaks
        /// a caller. Adding a route, a field or an error code does not change it.
        /// </para>
        /// </summary>
        [HttpGet("info")]
        [ProducesResponseType(typeof(InfoResponse), StatusCodes.Status200OK)]
        public ActionResult<InfoResponse> GetInfo()
        {
            Response.Headers["Cache-Control"] = "no-store";
            var config = ReadConfigSnapshot();

            return Ok(new InfoResponse
            {
                Version = GetPluginVersion(),
                ApiVersion = 1,
                Disabled = Plugin.IsPanicDisabled,
                Features = new InfoFeatures
                {
                    ClientPinLogin = config?.EnableClientPinLogin == true,
                    ClientProfileList = config?.EnableClientProfileList == true,
                    SkipQuickConnectOnKnownDevices = config?.SkipQuickConnectOnKnownDevices == true,
                    ProfileThemes = config?.AllowProfileThemes != false,
                    ProxyClients = config?.ProxyClients ?? new List<string>(),
                    RequireMainPin = config?.RequireMainPin == true,
                    Email = config != null && Features.Available(Features.Email, config),
                    PinResetByEmail = config != null && Features.Available(Features.PinResetByEmail, config),
                },

                // Anonymous on purpose: the people it is for cannot sign in.
                Maintenance = Auth.MaintenanceGate.Active() is MaintenanceMode m
                    ? new InfoMaintenance { Message = m.ShownMessage(), UntilUtc = m.UntilUtc }
                    : null,
            });
        }

        /// <summary>
        /// Who the caller is, in Bonfire's terms: a profile or a main account, and whose.
        /// <para>
        /// A client could work this out from /list, but only by knowing that the main account
        /// is the row with isMaster and not isBonfire, and /list is the heaviest route there
        /// is. This is one lookup.</para>
        /// </summary>
        [HttpGet("me")]
        [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<MeResponse> GetMe()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var user = _userManager.GetUserById(userId.Value);
            if (user == null) return Unauthorized();

            var config = ReadConfigSnapshot() ?? new PluginConfiguration();
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId.Value);
            var isProfile = row != null && row.MasterUserId != Guid.Empty && row.MasterUserId != userId.Value;
            var mainId = isProfile ? row!.MasterUserId : userId.Value;

            return Ok(new MeResponse
            {
                UserId = userId.Value,
                Name = isProfile ? row!.ProfileName : user.Username,
                IsProfile = isProfile,
                MainAccountId = mainId,
                MainAccountName = (isProfile ? _userManager.GetUserById(mainId)?.Username : user.Username) ?? string.Empty,
                IsAdministrator = IsAdministratorAccount(user),
                MainHasPin = MainHasPin(mainId, config),
                ProfileCount = config.Mappings.Count(m => m.MasterUserId == mainId && m.ProfileUserId != mainId),
                MaxProfiles = GetMaxProfilesForUser(mainId, config),
                PinResetByEmail = !isProfile && PinResetPossible(mainId, config),
            });
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
                // What the browser should render a picture at before sending it. Every
                // resize happens on a canvas there, so this is the whole of the quality
                // setting as far as a client is concerned; the server checks MaxBytes.
                Image = DescribeImageSpec(config),
                StarterAvatarsEnabled = StarterAvatars.Enabled(config),
                // The administrator's own first: a household that went to the trouble of
                // uploading pictures wants those seen before the built-in set.
                Avatars = config.AvatarLibrary.Select(a => new
                {
                    a.Id,
                    a.DisplayName,
                    Url = $"/plugins/profiles/avatars/{a.Id}",
                    ThumbUrl = $"/plugins/profiles/avatars/{a.Id}?size=thumb",
                    IsStarter = false
                }).Concat((StarterAvatars.Enabled(config) ? StarterAvatars.All : Enumerable.Empty<(string Slug, string DisplayName)>())
                    .Select(s => new
                    {
                        Id = StarterAvatars.IdFor(s.Slug),
                        s.DisplayName,
                        // 256px already, which is thumbnail size; there is no second rendering.
                        Url = $"/plugins/profiles/avatars/{StarterAvatars.IdFor(s.Slug)}",
                        ThumbUrl = $"/plugins/profiles/avatars/{StarterAvatars.IdFor(s.Slug)}",
                        IsStarter = true
                    })).ToList()
            });
        }

        /// <summary>
        /// Serves a library avatar. Unauthenticated for the same reason as
        /// <see cref="GetProfileImage"/>: it is rendered as an &lt;img src&gt;, and browsers
        /// do not attach Authorization headers to image requests. These are pictures the
        /// administrator published to every user on the server, so there is nothing here
        /// that an authenticated user could not already fetch.
        /// </summary>
        private static object DescribeImageSpec(PluginConfiguration config)
        {
            var spec = ImageQualities.For(config.ProfileImageQuality);
            return new
            {
                Quality = ImageQualities.Normalize(config.ProfileImageQuality),
                spec.MasterSize,
                spec.ThumbSize,
                spec.JpegQuality,
                spec.MaxBytes
            };
        }

        [HttpGet("avatars/{id}")]
        public ActionResult GetLibraryAvatar(string id, [FromQuery] string? size = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return NotFound();

            // A starter avatar is served from the assembly. Gone the moment the set is
            // switched off, like any other avatar the administrator withdraws; profiles that
            // chose one hold their own copy and are served from /image, not from here.
            if (StarterAvatars.IsStarterId(id))
            {
                var starter = StarterAvatars.Find(id);
                if (starter == null || !StarterAvatars.Enabled(config)) return NotFound();
                var stream = StarterAvatars.Open(starter.Value.Slug);
                if (stream == null)
                {
                    _logger.LogWarning("ProfilesPlugin: Starter avatar {Slug} is missing from the plugin assembly.", starter.Value.Slug);
                    return NotFound();
                }

                Response.Headers["Cache-Control"] = "private, max-age=3600";
                // Streamed from the manifest resource, which is already mapped memory, and
                // answers If-None-Match itself: the validator changes with the plugin version.
                return File(stream, StarterAvatars.ContentType, null,
                    new Microsoft.Net.Http.Headers.EntityTagHeaderValue(StarterAvatars.ETagFor(starter.Value.Slug)));
            }

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

            return ImageFileResult(found.Value.Path, found.Value.ContentType);
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
                return BadRequest($"That image could not be read, or it is larger than the {MaxProfileImageBytes / (1024 * 1024)} MB limit.");

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
                if (request.EnableStarterAvatars.HasValue)
                    config.EnableStarterAvatars = request.EnableStarterAvatars.Value;
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

            // A security limit: lower, never higher (R-1002).
            if (request.PinGuessesPerDay.HasValue
                && (request.PinGuessesPerDay.Value < 1 || request.PinGuessesPerDay.Value > PinBudget.DailyLimit))
            {
                return BadRequest($"PIN tries a day must be 1 to {PinBudget.DailyLimit}.");
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

            if (!string.IsNullOrEmpty(request.DefaultTimeZone) && !HouseholdTime.TryFind(request.DefaultTimeZone, out _))
            {
                return BadRequest("That time zone isn't known to this server.");
            }

            if (request.ProfileImageQuality != null && !ImageQualities.IsKnown(request.ProfileImageQuality))
            {
                return BadRequest($"Picture quality must be '{ImageQualities.Standard}', "
                                + $"'{ImageQualities.High}', '{ImageQualities.Maximum}' or '{ImageQualities.Ultra}'.");
            }

            // The dependency rule: nothing that needs email can be turned on until it works.
            // Turned ON, not kept on: the dashboard sends every field on every save, so
            // refusing a switch that is already on would fail every unrelated save from the
            // moment email broke. While it is broken the feature reports unavailable anyway
            // (Features.Available), so keeping the switch costs nothing.
            if (request.EnablePinResetByEmail == true)
            {
                var current = ReadConfigSnapshot();
                if (current == null || (!current.EnablePinResetByEmail && !Features.Available(Features.Email, current)))
                    return BadRequest("Set up email and send a test first.");
            }

            // The first time setup is turned on, every account that already exists is
            // counted as having seen it: it is for accounts made from now on, until "show
            // again to everyone". Read before the lock, because walking users touches the
            // database.
            List<Guid>? existingAccounts = null;
            if (request.OnboardingEnabled == true)
            {
                var snapshot = ReadConfigSnapshot();
                if (snapshot != null && snapshot.Onboarding?.ExistingAccountsExempted != true)
                {
                    // GetAllUsers, never _userManager.Users: that property is GetUsers() from
                    // 10.11.7 on, and calling it directly throws there.
                    existingAccounts = GetAllUsers()
                        .Select(u => u.Id)
                        .Where(id => !IsSubProfile(id, snapshot))
                        .ToList();
                }
            }

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                // Refused rather than normalised, for the same reason as the location above.
                // Checked here, before anything is assigned, because what is offered depends on
                // the custom and hidden themes, which are read under the lock.
                if (request.DefaultTheme != null && !Themes.IsKnown(request.DefaultTheme, config))
                {
                    return BadRequest("Default theme must be one of: " + string.Join(", ", Themes.Offered(config)) + ".");
                }

                if (request.ProfileImageQuality != null)
                    config.ProfileImageQuality = ImageQualities.Normalize(request.ProfileImageQuality);
                if (request.MaxProfilesPerUser.HasValue)
                    config.MaxProfilesPerUser = request.MaxProfilesPerUser.Value;
                if (request.PinGuessesPerDay.HasValue)
                    config.PinGuessesPerDay = request.PinGuessesPerDay.Value;
                if (request.RequireMainPin.HasValue)
                    config.RequireMainPin = request.RequireMainPin.Value;
                if (request.DisallowCustomAvatarUploads.HasValue)
                    config.DisallowCustomAvatarUploads = request.DisallowCustomAvatarUploads.Value;
                if (request.EnableStarterAvatars.HasValue)
                    config.EnableStarterAvatars = request.EnableStarterAvatars.Value;
                if (request.DefaultAskOnStartup.HasValue)
                    config.DefaultAskOnStartup = request.DefaultAskOnStartup.Value;
                if (request.DefaultSwitcherLocation != null)
                    config.DefaultSwitcherLocation = SwitcherLocations.Normalize(request.DefaultSwitcherLocation);
                if (request.DefaultTheme != null)
                    config.DefaultTheme = Themes.Normalize(request.DefaultTheme, config);
                if (request.DefaultTimeZone != null)
                    config.DefaultTimeZone = request.DefaultTimeZone.Trim();
                if (request.DefaultTimeZone != null)
                    Auth.ViewingTracker.Invalidate();
                if (request.AllowProfileThemes.HasValue)
                    config.AllowProfileThemes = request.AllowProfileThemes.Value;
                if (request.RichThemeMotion.HasValue)
                    config.RichThemeMotion = request.RichThemeMotion.Value;

                // Two switches rather than one, because they carry different risks: the
                // first changes how a profile authenticates, the second edits the endpoint
                // every client on this server signs in through.
                if (request.EnableClientPinLogin.HasValue)
                    config.EnableClientPinLogin = request.EnableClientPinLogin.Value;
                if (request.EnableClientProfileList.HasValue)
                    config.EnableClientProfileList = request.EnableClientProfileList.Value;
                if (request.ClientPinLoginFromAnywhere.HasValue)
                    config.ClientPinLoginFromAnywhere = request.ClientPinLoginFromAnywhere.Value;
                if (request.SkipQuickConnectOnKnownDevices.HasValue)
                    config.SkipQuickConnectOnKnownDevices = request.SkipQuickConnectOnKnownDevices.Value;
                if (request.ProxyClients != null)
                    config.ProxyClients = Auth.ProxyClients.Normalize(request.ProxyClients);
                if (request.EnablePinResetByEmail.HasValue)
                    config.EnablePinResetByEmail = request.EnablePinResetByEmail.Value;

                config.Onboarding ??= new OnboardingSettings();
                var onboarding = config.Onboarding;
                if (request.OnboardingAsked.HasValue)
                    onboarding.Asked = request.OnboardingAsked.Value;
                if (request.OnboardingEnabled.HasValue)
                {
                    onboarding.Enabled = request.OnboardingEnabled.Value;
                    if (existingAccounts != null && !onboarding.ExistingAccountsExempted)
                    {
                        existingAccounts.ForEach(onboarding.MarkSeen);
                        onboarding.ExistingAccountsExempted = true;
                    }
                }

                Plugin.Instance?.SaveConfiguration();
            }

            // A token looked at while its app was not ticked is remembered as "not ours" for
            // a few minutes. Ticking it must take effect on the next request, not after that.
            if (request.ProxyClients != null)
            {
                Auth.ProxySwitcher.ForgetUnproxied();
            }

            // Outside the lock: this walks users and writes to the database, and holding
            // ConfigLock across that would block every other request that reads the
            // configuration. Switching client PIN login OFF has to re-point the profiles
            // immediately — left until the next restart they would be pointing at a
            // provider that is no longer enabled, which means no provider at all, which
            // breaks the web switcher's own ability to enter them.
            if (request.EnableClientPinLogin.HasValue)
            {
                ProfilesBootstrapTask.ReconcileAuthProvidersNow();
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
            var adminError = RequireAdministrator("update profile limits");
            if (adminError != null) return adminError;

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
            var adminError = RequireAdministrator("view audit logs");
            if (adminError != null) return adminError;

            lock (AuditLogLock)
            {
                // Served from the in-memory cache so the dashboard reflects entries whose
                // background write may not have landed yet.
                return Ok(GetAuditLogSnapshot().OrderByDescending(l => l.Timestamp).ToList());
            }
        }

    }
}
