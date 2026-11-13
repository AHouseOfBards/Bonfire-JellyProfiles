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
            if (GetCurrentUserId() == null) return Unauthorized();
            var failed = BuildProfileList(out var list);
            if (failed != null) return failed;

            // Apps ask for this every time the profile screen opens, and on a television
            // that is most of what they ask. An unchanged list costs them a 304.
            return ETagged(list!);
        }

        /// <summary>
        /// What <c>/list</c> answers, or the refusal it would give. Shared with
        /// <c>/startup</c>, so the two cannot disagree about who a caller may see.
        /// </summary>
        private ActionResult? BuildProfileList(out List<object>? result)
        {
            result = null;
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
                            : isLocal && linkedMapping.AllowHouseholdLanBypass,
                        isTrusted: IsTrustedFor(linkedMapping, remoteIp, config));

                profileList.Add(new
                {
                    ProfileUserId = linkedId,
                    ProfileName = linkedUser.Username,
                    AvatarInitial = AvatarInitialOf(linkedUser.Username, "M"),
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
                    //
                    // Own household only (R-1304): another household's tile does not say
                    // whether its owner is an administrator, how many profiles it may have,
                    // or that it skips the PIN at home. LockoutMinutes stays, because the
                    // client auto-locks a profile it entered on that value.
                    IsAdministrator = linkedId == masterUserId && IsAdministratorAccount(linkedUser),
                    LockoutMinutes = linkedMapping?.LockoutMinutes ?? 5,
                    MaxSubProfiles = linkedId == masterUserId ? GetMaxProfilesForUser(linkedId, config) : (int?)null,
                    BypassPinOnLocalNetwork = linkedId == masterUserId && (linkedMapping?.BypassPinOnLocalNetwork ?? false),
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
                                && !CanSkipPin(m, isLocal, x.Rules.CrossHousehold, x.Rules.HouseholdLanBypass,
                                    isTrusted: IsTrustedFor(m, remoteIp, config));
                            return new
                            {
                                m.ProfileUserId,
                                m.ProfileName,
                                AvatarInitial = AvatarInitialOf(m.ProfileName, "?"),
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
                                BypassPinOnLocalNetwork = linkedId == masterUserId && m.BypassPinOnLocalNetwork,
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
                    AvatarInitial = AvatarInitialOf(addedUser.Username, "?"),
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

            result = profileList;
            return null;
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

        /// <summary>
        /// Everything the profile form needs that is the same for every profile: the libraries
        /// the caller can give one, the rating limits, the avatar library and the time zones.
        /// One request where the form used to make four. Each part is built by the method the
        /// four old routes shared, so none of it is filtered twice.
        /// <para>
        /// An administrator's API key may read the time zones, and that is all it may read
        /// here: the other three parts are null for a key, as their routes were 401.</para>
        /// </summary>
        [HttpGet("form-options")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<object> GetFormOptions()
        {
            var callerId = GetCurrentUserId();
            if (callerId == null)
            {
                if (!CallerIsApiKey()) return Unauthorized();
                return Ok(new
                {
                    Libraries = (object?)null,
                    Ratings = (object?)null,
                    Avatars = (object?)null,
                    TimeZones = (object?)TimeZonesFor(null)
                });
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var libraries = LibrariesFor(callerId.Value, out var refusal);
            if (libraries == null) return refusal!;

            return Ok(new
            {
                Libraries = (object?)libraries,
                Ratings = (object?)RatingsAnswer(),
                Avatars = (object?)AvatarLibraryAnswer(config),
                TimeZones = (object?)TimeZonesFor(callerId.Value)
            });
        }

        /// <summary>The libraries the caller can give a profile, or null with the refusal to send.</summary>
        private List<object>? LibrariesFor(Guid currentUserId, out ActionResult? refusal)
        {
            refusal = null;
            var caller = _userManager.GetUserById(currentUserId);
            if (caller == null)
            {
                refusal = NotFound("Calling user not found.");
                return null;
            }

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
            return folders
                .Where(f => Guid.TryParse(f.ItemId, out var parsed) && parsed != Guid.Empty)
                .Select(f => (object)new
                {
                    Id = f.ItemId,
                    Name = f.Name,
                    CollectionType = f.CollectionType
                }).ToList();
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
        private object RatingsAnswer()
        {
            var services = HttpContext?.RequestServices;
            var localization = services?.GetService(typeof(MediaBrowser.Model.Globalization.ILocalizationManager));
            var serverConfig = services?.GetService(typeof(MediaBrowser.Controller.Configuration.IServerConfigurationManager))
                as MediaBrowser.Controller.Configuration.IServerConfigurationManager;
            var country = serverConfig?.Configuration?.MetadataCountryCode;

            var ratings = localization == null
                ? new List<RatingChoice>()
                : RatingSystems.ForCountry(localization.GetType().Assembly, country, _logger);

            return new { Country = country, Ratings = ratings };
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

            var currentMapping = FindMapping(currentUserId);
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
            var existingCount = ProfileCountOf(config, masterUserId);
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
            var ratingError = ValidateRatingScore(request.MaxParentalRating);
            if (ratingError != null) return BadRequest(ratingError);
            var (newDeviceIds, deviceIdsError) = NormalizeAllowedDeviceIds(request.AllowedDeviceIds);
            if (deviceIdsError != null) return BadRequest(deviceIdsError);

            // Reject an allow-list that shares no tags with the master's, which would leave the
            // profile with an empty library. Checked before the user is created so we don't
            // leave a half-built account behind.
            var allowedTagsError = TagListError(request.BlockedTags) ?? TagListError(request.AllowedTags)
                ?? ValidateAllowedTags(
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

            // Everything from here to the saved mapping either completes or takes the account
            // away again. A throw part way used to leave an account no profile owned, and
            // every retry then said the name already existed (R-1136).
            try
            {
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

            // The picture is written before the lock: disk work does not belong inside it
            // (r2-11-05). The rollback below removes it again.
            var profileImageUrl = !string.IsNullOrEmpty(request.AvatarLibraryId)
                ? CopyLibraryAvatarToProfile(targetUser.Id, request.AvatarLibraryId).Url
                : SaveProfileImage(targetUser.Id, request.ProfileImage, request.ProfileImageThumb).Url;

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
                    AllowedDeviceIds = newDeviceIds ?? new List<string>(),
                    ProfileImage = profileImageUrl
                });

                Plugin.Instance?.SaveConfiguration();
            }
            }
            catch
            {
                lock (ConfigLock)
                {
                    config.Mappings.RemoveAll(m => m.ProfileUserId == targetUser.Id);
                }
                // The picture written for it goes too. Best effort, like the account below.
                try { SaveProfileImage(targetUser.Id, null); }
                catch (Exception cleanup)
                {
                    _logger.LogWarning(cleanup, "ProfilesPlugin: could not remove the picture left by a failed profile create for {User}.", systemUsername);
                }
                try { await _userManager.DeleteUserAsync(targetUser.Id).ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    _logger.LogWarning(cleanup, "ProfilesPlugin: could not remove the account {User} left by a failed profile create.", systemUsername);
                }
                throw;
            }

            LogActivity(_userManager.GetUserById(masterUserId)?.Username + " added the profile " + profileName,
                "ProfileCreated", masterUserId);
            BonfireCampground.RaiseCreated(masterUserId, targetUser.Id, profileName);

            return Ok(new
            {
                ProfileUserId = targetUser.Id,
                ProfileName = profileName
            });
        }

        /// <summary>
        /// The letter shown on a profile that has no picture: the first text element, upper-cased
        /// without regard to the server's culture. Substring(0, 1) cut a surrogate pair in half
        /// and ToUpper() followed the culture, so a Turkish server turned "i" into a dotted capital (r2-11-08).
        /// </summary>
        internal static string AvatarInitialOf(string? name, string whenEmpty) =>
            string.IsNullOrEmpty(name)
                ? whenEmpty
                : System.Globalization.StringInfo.GetNextTextElement(name).ToUpperInvariant();

        [HttpPost("delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

            var currentMapping = FindMapping(currentUserId);
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

            // One answer for a profile that does not exist and one that is not this household's,
            // so the route cannot be used to find out which (r2-11-07). As /copy-rules answers.
            var mapping = FindMapping(request.ProfileId);
            if (mapping == null || mapping.MasterUserId != masterUserId)
            {
                return Unauthorized(OtherHouseholdMessage);
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
                        // Revoke the user's tokens so Jellyfin considers every session dead.
                        // This prevents DeleteUserAsync from throwing due to an active session.
                        // One call revokes them all, so it is made once, if there is a session.
                        if (_sessionManager.Sessions.Any(s => s.UserId == request.ProfileId))
                        {
                            try
                            {
                                await _sessionManager.RevokeUserTokens(request.ProfileId, null).ConfigureAwait(false);
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
                // The mapping goes first, so a failure after this leaves a stray picture and not
                // a mapping to an account that is gone (r2-11-05).
                lock (ConfigLock)
                {
                    var mappingToRemove = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.ProfileId);
                    if (mappingToRemove != null)
                    {
                        config.Mappings.Remove(mappingToRemove);
                        Plugin.Instance?.SaveConfiguration();
                    }
                }

                // The static profile image, if any. Disk work, so outside ConfigLock.
                SaveProfileImage(request.ProfileId, null);

                // What it had watched today, kept per profile, would stay in viewing_usage.json (R-1023).
                // Outside ConfigLock: the tracker's own lock is never taken inside it.
                Auth.ViewingTracker.Forget(request.ProfileId);
                Auth.ViewingTracker.RulesChanged();

                // The master's last profile may just have gone, and a master with no
                // profiles is no longer one the PIN provider should hold.
                ProfilesBootstrapTask.ReconcileAuthProvidersNow();

                LogActivity(_userManager.GetUserById(masterUserId)?.Username + " deleted the profile " + mapping.ProfileName,
                    "ProfileDeleted", masterUserId);
                BonfireCampground.RaiseDeleted(masterUserId, request.ProfileId, mapping.ProfileName);
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
                bool bypass = CanSkipPin(mapping, isLocal, isCrossAccountMasterSwitch, householdLanBypass,
                    isTrusted: IsTrustedFor(mapping, remoteIp, config));

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
                            "as local by Jellyfin's network settings, or is a trusted address.",
                            request.ProfileId, ip);
                    }
                }
                else
                {
                    var limited = BeginPinAttempt(rateLimitKey, request.ProfileId);
                    if (limited != null) return limited;

                    // mapping is non-null here — pinHashToCheck came from it.
                    if (!VerifyPinAndUpgrade(request.Pin, mapping!))
                    {
                        return BadRequest("Invalid PIN code.");
                    }

                    // Only a PIN that was actually checked and right starts the allowance again.
                    // A pass with no PIN asked (none set, trusted address, LAN bypass) proves
                    // nothing about it, and resetting there gave a remote guesser 20 more
                    // whenever a household member opened the profile.
                    EndPinAttempt(rateLimitKey, request.ProfileId);
                }
            }

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
            // Before the session exists: the session listener hears of it inside this call, and
            // must leave the "switched" event to this route, which knows who it came from.
            BonfireCampground.ExpectSwitch(targetUser.Id, authRequest.DeviceId);

            // A switch is not a password sign-in: the session it starts may refresh this
            // device's record, never make one. The listener reads this note.
            Auth.RequestDevice.BeginSignIn(byPin: true);
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
                    if (live != null && CrossHouseholdEntries.Record(live, callerMasterUserId, targetUser.Id, authRequest.DeviceId, withoutPin, DateTime.UtcNow, _logger))
                        Plugin.Instance?.SaveConfiguration();
                }
            }

            // Record profile switch audit log
            RecordAuditLog(masterUser?.Username ?? "Unknown", targetUser.Username);

            BonfireCampground.RaiseSwitched(
                mapping?.MasterUserId ?? targetUser.Id,
                targetUser.Id,
                mapping?.ProfileName ?? targetUser.Username,
                authRequest.DeviceId,
                currentUserId);

            // The caller asked for the sign-in it made this request with to end, so it need not
            // call /Sessions/Logout afterwards. This is what that route does: ISessionManager.
            // Logout(token), for this one token. Never a user-wide revoke: the main account's
            // other devices must stay signed in. After the new session exists, so a refused or
            // failed switch ends nothing; and a failure here does not undo the switch.
            // Skipped if Jellyfin handed back the same token (same user, same device).
            if (request.EndPreviousSession)
            {
                var oldToken = Auth.ProxySwitcher.ReadToken(Request);
                if (!string.IsNullOrEmpty(oldToken) && !string.Equals(oldToken, session.AccessToken, StringComparison.Ordinal))
                {
                    try
                    {
                        await _sessionManager.Logout(oldToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ProfilesPlugin: switched to {User}, but could not end the previous session.", targetUser.Username);
                    }
                }
            }

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
                && !CanSkipPin(target, isLocal, rules.CrossHousehold, rules.HouseholdLanBypass,
                    isTrusted: IsTrustedFor(target, remoteIp, config)))
            {
                var limited = BeginPinAttempt(rateLimitKey, request.ProfileId);
                if (limited != null) return limited;

                if (!VerifyPinAndUpgrade(request.Pin, target))
                {
                    return BadRequest("Invalid PIN.");
                }

                // Reset only after a PIN was checked and right, as in /switch.
                EndPinAttempt(rateLimitKey, request.ProfileId);
            }

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

            HashSet<Guid> subProfileIds;
            lock (ConfigLock)
            {
                subProfileIds = config.Mappings
                    .Where(m => m.ProfileUserId != m.MasterUserId)
                    .Select(m => m.ProfileUserId)
                    .ToHashSet();
            }

            var masterUsersList = new List<object>();
            var subProfilesList = new List<object>();

            var allUsers = GetAllUsers().ToList();
            foreach (var user in allUsers)
            {
                if (subProfileIds.Contains(user.Id))
                {
                    var mapping = FindMapping(user.Id);
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
                        ProfileImage = mapping?.ProfileImage,
                        // For the administrator's PIN prompts control.
                        BypassPinOnLocalNetwork = mapping?.BypassPinOnLocalNetwork ?? false
                    });
                }
                else
                {
                    var mapping = FindMapping(user.Id);
                    List<Guid> added;
                    bool inBonfire;
                    int? limitOverride;
                    lock (ConfigLock)
                    {
                        limitOverride = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == user.Id)?.MaxProfiles;
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
                        // For the administrator's PIN prompts control.
                        AskPinToOpen = mapping?.AskPinToOpen ?? true,
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

            // Each id is looked up once, outside the lock: GetUserById is a database read per
            // call, and the old shape asked again for every row and every test inside the lock.
            // The ids are copied under a short lock, resolved with none held, and the rows are
            // then judged against that answer. An id that appears after the copy is simply not
            // in the dead set, so a profile created meanwhile is never taken for an orphan.
            HashSet<Guid> seen;
            lock (ConfigLock)
            {
                seen = new HashSet<Guid>();
                foreach (var m in config.Mappings) { seen.Add(m.ProfileUserId); seen.Add(m.MasterUserId); }
                foreach (var g in config.BonfireGroups)
                {
                    seen.Add(g.OwnerUserId);
                    foreach (var id in g.MemberUserIds ?? new List<Guid>()) seen.Add(id);
                }
                if (config.LinkedAccounts != null) foreach (var l in config.LinkedAccounts) { seen.Add(l.UserId); seen.Add(l.HouseholdId); }
                if (config.Onboarding?.Seen != null) foreach (var s in config.Onboarding.Seen) seen.Add(s.UserId);
                if (config.AccountEmails != null) foreach (var a in config.AccountEmails) seen.Add(a.UserId);
                if (config.UserProfileLimitOverrides != null) foreach (var o in config.UserProfileLimitOverrides) seen.Add(o.UserId);
            }
            var deadIds = seen.Where(id => id == Guid.Empty || _userManager.GetUserById(id) == null).ToHashSet();
            bool Exists(Guid id) => !deadIds.Contains(id);

            var orphansRemoved = new List<Guid>();
            var orphanEvents = new List<(Guid Main, Guid Profile, string Name)>();
            (int Mappings, int Groups, int Members) removed;
            var removedImages = 0;
            lock (ConfigLock)
            {
                var orphans = config.Mappings
                    .Where(m => !Exists(m.ProfileUserId) || !Exists(m.MasterUserId))
                    .Select(m => new
                    {
                        profileUserId = m.ProfileUserId,
                        masterUserId = m.MasterUserId,
                        profileName = m.ProfileName,
                        // The English line is for API callers; the dashboard maps the code to
                        // its own language and never shows this one.
                        reason = !Exists(m.ProfileUserId)
                            ? "the profile's Jellyfin user was deleted"
                            : "the master account was deleted",
                        reasonCode = !Exists(m.ProfileUserId) ? "profile_deleted" : "master_deleted"
                    })
                    .ToList();

                // Bonfire groups outlive their owner the same way.
                var deadGroups = config.BonfireGroups
                    .Where(g => !Exists(g.OwnerUserId))
                    .Select(g => new { groupId = g.GroupId, code = g.BonfireCode })
                    .ToList();

                // A group whose owner is alive can still list members who are not. A group that
                // is going anyway takes its members with it, so they are not counted here too.
                var deadMembers = config.BonfireGroups
                    .Where(g => Exists(g.OwnerUserId))
                    .SelectMany(g => (g.MemberUserIds ?? new List<Guid>())
                        .Where(id => !Exists(id))
                        .Select(id => new { groupId = g.GroupId, userId = id }))
                    .ToList();

                // Theme background images no custom theme names: uploaded in an editor that was
                // closed without saving. Under the lock, so a save cannot name one meanwhile.
                var unusedImages = ThemeBackground.Unused(config);
                // And uploaded pattern pictures, by the same rule; counted with the images.
                var unusedPatterns = ThemePattern.Unused(config);

                if (!apply)
                {
                    return Ok(new { applied = false, orphans, deadGroups, deadMembers, unusedImages = unusedImages.Count + unusedPatterns.Count });
                }

                foreach (var file in unusedPatterns)
                {
                    if (ThemePattern.DeleteIfUnused(file, config)) removedImages++;
                    else _logger.LogWarning("ProfilesPlugin: could not delete the unused pattern picture {File}.", file);
                }

                foreach (var file in unusedImages)
                {
                    if (ThemeBackground.DeleteIfUnused(file, config)) removedImages++;
                    else _logger.LogWarning("ProfilesPlugin: could not delete the unused background image {File}.", file);
                }

                var orphanIds = orphans.Select(o => o.profileUserId).ToHashSet();
                orphansRemoved = config.Mappings
                    .Where(m => orphanIds.Contains(m.ProfileUserId) && (!Exists(m.ProfileUserId) || !Exists(m.MasterUserId)))
                    .Select(m => m.ProfileUserId)
                    .ToList();
                orphanEvents = orphans
                    .Where(o => orphansRemoved.Contains(o.profileUserId))
                    .Select(o => (o.masterUserId, o.profileUserId, o.profileName))
                    .ToList();
                config.Mappings.RemoveAll(m => orphanIds.Contains(m.ProfileUserId)
                                               && (!Exists(m.ProfileUserId) || !Exists(m.MasterUserId)));

                var deadGroupIds = deadGroups.Select(g => g.groupId).ToHashSet(StringComparer.Ordinal);
                config.BonfireGroups.RemoveAll(g => deadGroupIds.Contains(g.GroupId));

                var membersRemoved = 0;
                foreach (var g in config.BonfireGroups)
                {
                    membersRemoved += g.MemberUserIds?.RemoveAll(id => !Exists(id)) ?? 0;
                }

                // An account shown in another household's switcher, where either side is gone.
                // Nothing to review: a tile for an account that doesn't exist is never drawn.
                config.LinkedAccounts?.RemoveAll(l => !Exists(l.UserId) || !Exists(l.HouseholdId));

                // First-time setup's "has seen it" rows for accounts that no longer exist (R-1075).
                config.Onboarding?.Seen?.RemoveAll(s => !Exists(s.UserId));

                // A confirmed address and a profile-limit override are kept per account and are
                // personal data in the export; neither goes when Jellyfin deletes the account (R-1311).
                config.AccountEmails?.RemoveAll(a => !Exists(a.UserId));
                config.UserProfileLimitOverrides?.RemoveAll(o => !Exists(o.UserId));

                // Devices are NOT touched here. A device outliving the profile that used it is
                // normal — the hardware is still in the house — and dropping one that some
                // other profile still names would empty that whitelist, which means "any
                // device". Device housekeeping has its own rules; see RemoveStaleDevices.
                Plugin.Instance?.SaveConfiguration();

                // What was actually taken out, not what the preview listed. The linked-account,
                // first-setup, address and override rows above are housekeeping with nothing
                // for an administrator to review, so they are not counted.
                removed = (orphansRemoved.Count, deadGroups.Count, membersRemoved);
            }

            foreach (var gone in orphansRemoved) Auth.ViewingTracker.Forget(gone);   // R-1023
            if (orphansRemoved.Count > 0) Auth.ViewingTracker.RulesChanged();
            foreach (var (gonesMain, gonesProfile, gonesName) in orphanEvents) BonfireCampground.RaiseDeleted(gonesMain, gonesProfile, gonesName);

            // A master whose profiles were all orphans is no longer one the PIN provider
            // should hold.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();

            return Ok(new
            {
                applied = true,
                removedMappings = removed.Mappings,
                removedGroups = removed.Groups,
                removedMembers = removed.Members,
                removedImages
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
        public ActionResult ExportConfiguration([FromQuery] bool includePins = true)
        {
            var adminError = RequireAdministrator("export the configuration");
            if (adminError != null) return adminError;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // The mail server's password is left out. The file is one an administrator can put
            // somewhere careless, and a password is not a salted hash: it is the credential
            // itself, in plain text. An import without one keeps the server's own (R-1149,
            // R-1312). Exported from a copy whose Password is null, which EmailSettings leaves
            // out of JSON. Not serialised by hand: Jellyfin's own formatter writes the file, as
            // it always has, so nothing else in it changes.
            //
            // So is the emergency code. It is hashed like a PIN, and a short code is recovered
            // from its hash in seconds, so the file would hand the code that switches Bonfire
            // off to whoever reads it. An import without one keeps the server's own and says
            // so, so the administrator knows to set it.
            PluginConfiguration configuration;
            lock (ConfigLock)
            {
                configuration = config.SnapshotForReading();
                configuration.Email.Password = null!;
                configuration.Email.VerifiedFingerprint = string.Empty; // a hash of the password too: it would confirm a guess offline (r2-18-01)
                configuration.PanicCodeHash = null;
                // Ticked off on the dashboard for a file that is going somewhere careless: a
                // short PIN is recovered from its hash. A row with no hash keeps the server's
                // own PIN on import (r2-12-02).
                if (!includePins)
                {
                    foreach (var m in configuration.Mappings) m.PinHash = null;
                }
            }

            return Ok(new
            {
                exportedUtc = DateTime.UtcNow,
                pluginVersion = Plugin.Instance?.Version?.ToString() ?? "unknown",
                configuration
            });
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

            // A value of the wrong type anywhere (a number where a list goes) makes the whole
            // read fail, and substituting a default configuration there would write
            // emptiness over every section. Refuse instead. r2-12-01.
            var incoming = Read<PluginConfiguration>(root);
            if (incoming == null)
                return BadRequest("That file couldn't be read. Nothing was changed.");

            // Anything absent from the file keeps its current value rather than being reset,
            // so a partial or older export cannot silently clear a setting it predates.
            var hasMappings = TryProperty(root, "Mappings", out _);
            var hasDevices = TryProperty(root, "KnownDevices", out _);
            var hasGroups = TryProperty(root, "BonfireGroups", out _);
            var hasAvatars = TryProperty(root, "AvatarLibrary", out _);
            var hasLimits = TryProperty(root, "UserProfileLimitOverrides", out _);

            bool Exists(Guid id) => id != Guid.Empty && _userManager.GetUserById(id) != null;

            var droppedMappings = new List<string>();
            var emergencyCodeSet = false;
            var bonfireOn = true;

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

                // A backup holds the name of a picture, not the picture. One whose file is not in
                // this server's data folder would be a broken image in the picker, so it is
                // cleared, as a font or the logo is dropped. The profile itself stays.
                foreach (var m in mappings)
                {
                    if (!string.IsNullOrEmpty(m.ProfileImage)
                        && FindImageFile(ProfileImageFolder, m.ProfileUserId.ToString(), false) == null)
                    {
                        m.ProfileImage = null;
                    }
                }

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
                if (hasMappings)
                {
                    // A file exported without PINs has none on any row, and must not clear the
                    // PIN a row has here: the row keeps the server's own (r2-12-02).
                    var livePins = (config.Mappings ?? new List<ProfileMapping>())
                        .Where(l => !string.IsNullOrEmpty(l.PinHash))
                        .GroupBy(l => l.ProfileUserId).ToDictionary(g => g.Key, g => g.First().PinHash);
                    foreach (var m in mappings)
                    {
                        if (string.IsNullOrEmpty(m.PinHash) && livePins.TryGetValue(m.ProfileUserId, out var keptPin))
                            m.PinHash = keptPin;
                    }
                    config.Mappings = mappings;
                }
                Auth.ViewingTracker.Invalidate();
                if (hasGroups) config.BonfireGroups = groups;
                if (TryProperty(root, "LinkedAccounts", out _))
                {
                    config.LinkedAccounts = (incoming.LinkedAccounts ?? new List<LinkedAccount>())
                        .Where(l => l != null && l.UserId != l.HouseholdId && Exists(l.UserId) && Exists(l.HouseholdId))
                        .GroupBy(l => (l.HouseholdId, l.UserId)).Select(g => g.First())
                        .ToList();
                }
                if (hasDevices)
                {
                    config.KnownDevices = incoming.KnownDevices;
                    foreach (var d in config.KnownDevices ?? new List<KnownDevice>())
                        d?.Owners?.RemoveAll(id => !Exists(id));
                }
                if (hasLimits)
                    config.UserProfileLimitOverrides = (incoming.UserProfileLimitOverrides ?? new List<UserProfileLimitOverride>()).Where(o => o != null && Exists(o.UserId)).ToList();
                // Only entries whose file is here, and whose id is a plain name: the id is joined
                // onto a folder path.
                if (hasAvatars)
                {
                    config.AvatarLibrary = (incoming.AvatarLibrary ?? new List<AvatarLibraryItem>())
                        .Where(a => a != null && !string.IsNullOrEmpty(a.Id)
                            && a.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
                            && FindImageFile(AvatarLibraryFolder, a.Id, false) != null)
                        .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .ToList();
                }

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
                        // A backup holds a background image's name, not its file, as with the
                        // logo: one that is not on this server is dropped and the theme kept.
                        ThemeBackground.DropMissing(node);
                        ThemePattern.DropMissing(node);
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
                if (TryProperty(root, "LogoCorners", out _))
                    config.LogoCorners = ThemeLogo.CleanCorners(incoming.LogoCorners);
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
                // The export leaves the emergency code out, so a file without one keeps the
                // server's own. A file from before that still carries it, and it is taken.
                if (!string.IsNullOrEmpty(incoming.PanicCodeHash))
                    config.PanicCodeHash = incoming.PanicCodeHash;
                emergencyCodeSet = !string.IsNullOrEmpty(config.PanicCodeHash);
                if (TryProperty(root, "ProxyClients", out _))
                    config.ProxyClients = Auth.ProxyClients.Normalize(incoming.ProxyClients);
                // Only ever within the limits: a file cannot widen what the settings route refuses.
                if (TryProperty(root, "TrustedAddressEntries", out _))
                    config.TrustedAddressEntries = Auth.TrustedAddresses.Normalize(incoming.TrustedAddressEntries);
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
                    // "Email": null is a file that says nothing, not an instruction to remove
                    // the section. Only a property declared nullable may be set to null. r2-12-03.
                    if (property.GetValue(incoming) == null
                        && !PluginConfiguration.AcceptsNull(property)) continue;
                    if (property.Name == nameof(PluginConfiguration.Email)
                        && string.IsNullOrEmpty(incoming.Email?.Password))
                    {
                        // The export leaves the mail password out; a file without one must not
                        // blank the server's own. R-1149.
                        var live = config.Email?.Password;
                        property.SetValue(config, property.GetValue(incoming));
                        if (config.Email != null) config.Email.Password = live ?? string.Empty;
                        continue;
                    }
                    property.SetValue(config, property.GetValue(incoming));
                }

                // The sections the loop above copied whole carry account ids too: drop any this
                // server does not have. r2-12-05.
                config.HouseholdsSeenLocally?.RemoveAll(id => !Exists(id));
                config.Onboarding?.Seen?.RemoveAll(r => r == null || !Exists(r.UserId));

                bonfireOn = config.BonfireOn;
                Plugin.Instance?.SaveConfiguration();
            }

            // Every profile, PIN and the feature switch itself may have changed.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();
            // And which apps get the profile picker, and whether Bonfire is on at all: what a
            // settings save that changes either clears, cleared here whatever the file held.
            Plugin.ForgetOnOffCaches();

            return Ok(new
            {
                // Null when the file had no Mappings section, rather than a 0 that reads as
                // "restored none".
                mappings = hasMappings ? (int?)((incoming.Mappings ?? new List<ProfileMapping>()).Count - droppedMappings.Count) : null,
                droppedMappings,
                devices = (incoming.KnownDevices ?? new List<KnownDevice>()).Count,
                bonfires = (incoming.BonfireGroups ?? new List<BonfireGroup>()).Count,
                // Whether the file carried the emergency code, and whether the server has one
                // now, so the page can say to set it.
                emergencyCodeIncluded = !string.IsNullOrEmpty(incoming.PanicCodeHash),
                emergencyCodeSet,
                // Where the switch stands now. A backup from before 2.0 has no BonfireOn and
                // leaves it as the server has it, which on a new install is off.
                bonfireOn
            });
        }

        /// <summary>The settings ImportConfiguration restores one by one, each through its own rules.</summary>
        private static readonly HashSet<string> ImportHandledAbove = new(StringComparer.Ordinal)
        {
            "MaxProfilesPerUser", "PinGuessesPerDay", "RequireMainPin", "Mappings", "KnownDevices", "BonfireGroups",
            "LinkedAccounts", "UserProfileLimitOverrides", "AvatarLibrary", "DisallowCustomAvatarUploads",
            "ProfileImageQuality", "DefaultAskOnStartup", "DefaultSwitcherLocation", "CustomFonts", "CustomThemes",
            "HiddenThemes", "LogoImage", "LogoText", "LogoAlt", "LogoCorners", "DefaultTheme", "DefaultTimeZone", "AllowProfileThemes",
            "EnableClientPinLogin", "EnableClientProfileList", "ClientPinLoginFromAnywhere", "PanicCodeHash", "ProxyClients", "TrustedAddressEntries",
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
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
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

            var currentMapping = FindMapping(currentUserId);
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

                var mapping = FindMapping(request.ProfileId);
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

            // A theme is refused, not ignored, while the server does not let profiles choose:
            // Settings -> Theme saves through here, and a save that says nothing about why the
            // tick did not move reads as the setting not taking. The profile form leaves the
            // field out when it has no picker, so a form without one is not refused.
            if (request.Theme != null && !config.AllowProfileThemes)
                return StatusCode(StatusCodes.Status403Forbidden, "This server does not let profiles choose a theme.");
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
                var ratingError = ValidateRatingScore(request.MaxParentalRating);
                if (ratingError != null) return BadRequest(ratingError);
                requestedRating = int.Parse(request.MaxParentalRating);
            }
            var (newDeviceIds, deviceIdsError) = NormalizeAllowedDeviceIds(request.AllowedDeviceIds);
            if (deviceIdsError != null) return BadRequest(deviceIdsError);

            var targetUser = _userManager.GetUserById(request.ProfileId);
            if (targetUser == null) return NotFound("Target user not found.");

            var masterUser = _userManager.GetUserById(masterUserId);
            if (masterUser == null) return NotFound("Master user not found.");

            var masterUserDto = _userManager.GetUserDto(masterUser, string.Empty);
            var masterPolicy = masterUserDto.Policy;

            var allowedTagsError = TagListError(request.BlockedTags) ?? TagListError(request.AllowedTags)
                ?? ValidateAllowedTags(masterPolicy, request.AllowedTags);
            if (allowedTagsError != null)
            {
                return BadRequest(allowedTagsError);
            }

            // Tag filters: a null list means "leave unchanged", so fall back to what's stored.
            var existingMapping = FindMapping(request.ProfileId);
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
            // A file was written under this profile's name by this request, so a later
            // refusal or failure has to take it back. A value that only repeats the stored
            // address, or points elsewhere, wrote nothing.
            var imageWritten = false;
            if (!string.IsNullOrEmpty(request.AvatarLibraryId))
            {
                var copied = CopyLibraryAvatarToProfile(request.ProfileId, request.AvatarLibraryId);
                if (copied.Error != null) return BadRequest(copied.Error);
                newImageUrl = copied.Url;
                imageChanged = true;
                imageWritten = true;
            }
            else if (request.ProfileImage != null)
            {
                var saved = SaveProfileImage(request.ProfileId, request.ProfileImage, request.ProfileImageThumb, existingMapping?.ProfileImage);
                if (saved.Error != null) return BadRequest(saved.Error);
                newImageUrl = saved.Url;
                imageChanged = true;
                imageWritten = request.ProfileImage.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && newImageUrl != null;
            }

            // The new account name, decided and checked here and written below, after the
            // policy has been worked out. Written first, a failure in what followed left the
            // account under the new name and the mapping under the old (R-1147).
            string? newUsername = null;
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
                    newUsername = systemUsername;
                }
            }

            // Update policy for sub-profiles. The policy sync (every sign-in) reads the profile
            // and writes its account and mapping back; the gate keeps this save from landing
            // between its read and its write and being undone by it (R-1046). Held to the end.
            using var syncGate = request.ProfileId != masterUserId
                ? await Auth.ProfilePolicySync.HoldAsync().ConfigureAwait(false)
                : null;

            // The profile is looked at again now that the gate is held. The first look was
            // before the picture was saved and before this wait, and the profile could have
            // been deleted, or handed to another household, in between. Without it the account
            // was renamed and its policy written for a profile the mapping no longer had, and
            // the save answered 200 while the lock below found nothing to update (r2-12-04).
            if (request.ProfileId != masterUserId)
            {
                bool stillTheirs;
                lock (ConfigLock)
                {
                    stillTheirs = config.Mappings.Any(m => m.ProfileUserId == request.ProfileId && m.MasterUserId == masterUserId);
                }
                if (!stillTheirs)
                {
                    DiscardNewImage(request.ProfileId, imageWritten);
                    return NotFound("Target user not found.");
                }
            }

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

                // Everything above only worked the policy out. The account is written now: the
                // rename, then the policy, and if the policy write fails the rename is taken
                // back, so the account is not left under a name the mapping does not have.
                var previousUsername = targetUser.Username;
                if (newUsername != null)
                {
                    targetUser.Username = newUsername;
                    try
                    {
                        await _userManager.UpdateUserAsync(targetUser).ConfigureAwait(false);
                    }
                    catch
                    {
                        DiscardNewImage(request.ProfileId, imageWritten);
                        throw;
                    }
                }
                try
                {
                    await _userManager.UpdatePolicyAsync(targetUser.Id, targetPolicy).ConfigureAwait(false);
                }
                catch
                {
                    if (newUsername != null)
                    {
                        try
                        {
                            targetUser.Username = previousUsername;
                            await _userManager.UpdateUserAsync(targetUser).ConfigureAwait(false);
                        }
                        catch (Exception undo)
                        {
                            _logger.LogError(undo, "ProfilesPlugin: Could not undo the rename of {Profile} after its policy write failed.", request.ProfileId);
                        }
                    }
                    DiscardNewImage(request.ProfileId, imageWritten);
                    throw;
                }
            }

            var pinChanged = false;
            string? renamedTo = null;
            string resolvedTheme = string.Empty;
            string? storedTheme = null;
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
                        var newName = request.ProfileName.Trim();
                        if (!string.Equals(mappingEntry.ProfileName, newName, StringComparison.Ordinal)) renamedTo = newName;
                        mappingEntry.ProfileName = newName;
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

                    // Null leaves it alone, for the same reason; "" follows the server's default.
                    // Refused above while the server does not let profiles choose. The rule
                    // itself is Themes.SetFor's, which POST /theme used to call on its own.
                    if (request.Theme != null)
                    {
                        Themes.SetFor(config, masterUserId, request.ProfileId, request.Theme, masterUser.Username);
                    }
                    resolvedTheme = Themes.Resolve(mappingEntry, config);
                    storedTheme = mappingEntry.Theme;

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
                        mappingEntry.AllowedDeviceIds = newDeviceIds!;
                    }
                }

                Plugin.Instance?.SaveConfiguration();
            }

            if (renamedTo != null) BonfireCampground.RaiseRenamed(masterUserId, request.ProfileId, renamedTo);

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

            // The theme this profile now sees, and the one it has stored, for a caller that
            // saved only the theme and has nothing else on the page to refresh.
            return Ok(new { ProfileId = request.ProfileId, Theme = resolvedTheme, Stored = storedTheme });
        }

        /// <summary>
        /// Takes back a picture this request saved, when the rest of the request was refused or
        /// failed. The previous picture was replaced when the new one was placed, so what is
        /// left is no picture rather than a half-applied save with a file nothing points to.
        /// </summary>
        private void DiscardNewImage(Guid profileId, bool written)
        {
            if (!written) return;
            try { DeleteImageFiles(ProfileImageFolder, profileId.ToString()); }
            catch (Exception ex) { _logger.LogWarning(ex, "ProfilesPlugin: Could not remove the picture saved for {Profile} by a save that failed.", profileId); }
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
            //
            // Turned off (the Status tab), it is the same script told to stay off, not the
            // inert one: the settings page's theme preview runs on window.BonfireThemePreview
            // from this script, and the dashboard must keep working while Bonfire is off. While
            // off, index.html carries no tag for it (WebInjection.SyncRegistration), so only the
            // settings page asks for it, loading it itself. The
            // tag carries the state, and every load asks (no-cache, not max-age=300), so a
            // browser holding the other copy gets this one on its next page load in either
            // direction instead of up to five minutes later.
            var off = Plugin.IsOff;
            var etag = "\"" + GetPluginVersion() + (off ? "-off" : "") + "\"";
            Response.Headers["ETag"] = etag;
            Response.Headers["Cache-Control"] = "public, no-cache";

            if (IfNoneMatchHas(etag))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            if (!off) return Content(CachedProfilesJs, "application/javascript");
            var offJs = CachedOffProfilesJs ??= PublishOff(CachedProfilesJs);
            return Content(offJs, "application/javascript");
        }

        /// <summary>The served script with Bonfire turned off, made once from <see cref="ProfilesBaseController.CachedProfilesJs"/>.</summary>
        private static string? CachedOffProfilesJs;

        /// <summary>
        /// Tells the script that Bonfire is turned off: it then takes down anything an earlier
        /// copy left, and draws nothing. An exact literal, like <see cref="PublishLocales"/>;
        /// tests/js/onoff.js fails if the marker is not in profiles.js.
        /// </summary>
        internal static string PublishOff(string js)
            => js.Replace("const BONFIRE_OFF = false; // __BONFIRE_OFF__",
                          "const BONFIRE_OFF = true; // __BONFIRE_OFF__", StringComparison.Ordinal);

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
        /// If the marker is ever edited away, or the stylesheet resource is missing from the
        /// build, this returns the script unchanged without a log line, and that script is
        /// cached until restart: the plugin renders unstyled. Visible on the first page load,
        /// unlike a literal that truncates in silence, but not announced by the server.
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

            if (IfNoneMatchHas(etag))
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

            // Read under the lock, like every other walk of the lists (R-1152), and a group
            // saved with no member list is an empty one, not a crash (R-1153).
            lock (ConfigLock)
            {
            var currentMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterUserId);
            Guid masterId = currentMapping != null ? currentMapping.MasterUserId : masterUserId;
            // The join code and who is in the group are the main account's to see, as the
            // routes that change them already are: a profile gets neither (R-1148).
            bool isProfile = masterId != masterUserId;

            var ownedGroup = config.BonfireGroups.FirstOrDefault(g => g.OwnerUserId == masterId);
            var joinedGroup = config.BonfireGroups.FirstOrDefault(g => g.MemberUserIds != null && g.MemberUserIds.Contains(masterId));
            var masterMapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == masterId);

            return Ok(new
            {
                IsOwner = ownedGroup != null,
                OwnedCode = isProfile ? null : ownedGroup?.BonfireCode,
                OwnedMembers = ownedGroup != null && !isProfile ? GetBonfireGroupMembers(ownedGroup, config) : null,
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

            var currentMapping = FindMapping(masterUserId);
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
            Guid masterId = currentUserIdVal.Value;

            // A profile's own row names a different master; only the main account joins.
            var currentMapping = FindMapping(masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can join Bonfire groups.");

            if (!ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            // Counted before the code is looked up, per address and per account, so neither
            // parallel requests nor a second address buy extra guesses.
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            var accountKey = "account:" + masterId.ToString("N");
            // The account first. A household that has used up its own guesses is refused
            // before it is charged to the address, so it cannot go on spending the budget
            // every other household behind the same router shares (R-1161).
            if (!RateLimiter.Bonfire.TryBegin(accountKey))
                return TooManyAttempts(RateLimiter.Bonfire, accountKey, "Too many failed attempts.");
            if (!RateLimiter.Bonfire.TryBegin(ip))
                return TooManyAttempts(RateLimiter.Bonfire, ip, "Too many failed attempts.");

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

            var callerMapping = FindMapping(masterId);
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

            var callerMapping = FindMapping(masterId);
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

            // Only the main account is ever in a group, so a profile asking is refused the same
            // way the other group routes refuse it, rather than asked for a PIN or told it is
            // in none (R-1162).
            if (IsSubProfile(masterId, config)) return Unauthorized("Only the master profile can manage Bonfire groups.");

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

            if (IsSubProfile(masterId, config)) return Unauthorized("Only the master profile can manage Bonfire groups.");

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

            var currentMapping = FindMapping(masterUserId);
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
            var currentMapping = FindMapping(currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;

            var masterMapping = FindMapping(masterUserId);
            var (askOnStartup, location) = SwitcherLocations.Resolve(masterMapping, config.DefaultAskOnStartup, config.DefaultSwitcherLocation);

            return Ok(new
            {
                AskOnStartup = askOnStartup,
                SwitcherLocation = location,
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
            var row = MappingIn(config, userId);
            return row != null && row.MasterUserId != Guid.Empty && row.MasterUserId != userId;
        }

        /// <summary>
        /// The steps to offer this main account, in order. A step whose feature is off is
        /// left out, not shown disabled: the theme step needs profiles to be allowed a theme,
        /// and a choice of at least two (every built-in hidden but Bonfire, and no custom
        /// theme, is one card with nothing to pick).
        /// <c>Done</c> marks what is already true, so a client can pass over it.
        /// </summary>
        internal static List<OnboardingStepInfo> OnboardingStepsFor(Guid mainId, PluginConfiguration config)
        {
            var o = config.Onboarding ?? new OnboardingSettings();
            var themeChoice = config.AllowProfileThemes && Themes.Offered(config).Count > 1;
            return o.OfferedSteps()
                .Where(id => id != OnboardingSteps.Theme || themeChoice)
                .Where(id => id != OnboardingSteps.ResetEmail || Features.Available(Features.Email, config))
                .Select(id => new OnboardingStepInfo
                {
                    Id = id,
                    Done = id == OnboardingSteps.MainPin ? MainHasPin(mainId, config)
                         : id == OnboardingSteps.FirstProfile ? ProfileCountOf(config, mainId) > 0
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
        private ActionResult? BuildOnboarding(out OnboardingResponse? result)
        {
            result = null;
            var userId = GetCurrentUserId();
            // An API key carries an all-zero id: there is no account to walk through anything.
            if (userId == null || userId == Guid.Empty) return Unauthorized();
            var config = ReadConfigSnapshot() ?? new PluginConfiguration();
            var pending = OnboardingPendingFor(userId.Value, config);

            result = new OnboardingResponse
            {
                Pending = pending,
                Generation = config.Onboarding?.Generation ?? 1,
                Steps = pending ? OnboardingStepsFor(userId.Value, config) : new List<OnboardingStepInfo>()
            };
            return null;
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
            var currentMapping = FindMapping(currentUserId);
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
                SwitcherLocation = location
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

            var currentMapping = FindMapping(mainId);
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

            var currentMapping = FindMapping(mainId);
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
        /// A profile's artwork choices: the caller's own, or with <c>profileId</c> one profile's
        /// for the edit form, which is restricted to that profile's master, the same rule that
        /// governs every other profile setting. Only entries that change something are
        /// returned — an untouched library inherits Jellyfin's artwork and is simply absent.
        /// </summary>
        [HttpGet("library-artwork")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<IEnumerable<object>> GetLibraryArtwork([FromQuery] Guid? profileId = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            var currentUserIdVal = GetCurrentUserId();
            if (currentUserIdVal == null) return Unauthorized();

            if (profileId == null) return Ok(DescribeLibraryArtwork(config, currentUserIdVal.Value));

            var denied = DenyUnlessProfileOwner(config, currentUserIdVal.Value, profileId.Value);
            if (denied != null) return denied;

            return Ok(DescribeLibraryArtwork(config, profileId.Value));
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
            var currentMapping = FindMapping(currentUserId);
            Guid masterUserId = currentMapping != null ? currentMapping.MasterUserId : currentUserId;
            if (!ManagementAllowed(masterUserId, config, request.MasterPin, out var pinRefusal)) return pinRefusal!;

            // The library has to be one Jellyfin actually knows about. Without this the id is
            // just a string that ends up in a filename.
            bool known = _libraryManager.GetVirtualFolders()
                .Any(f => Guid.TryParse(f.ItemId, out var id) && id == libraryId);
            if (!known) return BadRequest("That library no longer exists.");

            string mode = LibraryArtworkModes.Normalize(request.Mode);
            string baseName = LibraryArtName(profileId, libraryId);

            // Looked at before any file is written: the profile has to exist, and a picture
            // can only be set for a library it can open. Both used to be found out after the
            // files were on disk (the second never), so a refusal left them behind and a
            // library the profile cannot see could be given a tile (r2-13-01, r2-13-03).
            // Putting a library back to inherit or none stays open: it hides, never shows.
            var profileUser = _userManager.GetUserById(profileId);
            if (profileUser == null) return NotFound("Profile not found.");
            if (mode == LibraryArtworkModes.Custom)
            {
                var policy = _userManager.GetUserDto(profileUser, string.Empty).Policy;
                var blocked = policy?.BlockedMediaFolders ?? Array.Empty<Guid>();
                var enabled = policy?.EnabledFolders ?? Array.Empty<Guid>();
                var canSee = policy != null && !blocked.Contains(libraryId) && (policy.EnableAllFolders || enabled.Contains(libraryId));
                if (!canSee) return BadRequest("That profile cannot see that library.");
            }

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
                    // creates the master one. A sub-profile with none was deleted since the
                    // ownership check above: it gets no row made for it, and the files
                    // written for it are taken back.
                    if (profileId != masterUserId)
                    {
                        DeleteImageFiles(LibraryArtFolder, baseName);
                        return NotFound("Profile not found.");
                    }

                    mapping = new ProfileMapping
                    {
                        ProfileUserId = profileId,
                        MasterUserId = profileId,
                        ProfileName = profileUser.Username
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
            var mapping = FindMapping(profileId);
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
            var callerMapping = MappingIn(config, callerId);
            Guid masterUserId = callerMapping != null ? callerMapping.MasterUserId : callerId;

            if (callerId != masterUserId)
            {
                return Unauthorized("Only the master profile can manage profiles.");
            }

            if (profileId == masterUserId) return null;

            var mapping = MappingIn(config, profileId);
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
            List<LibraryArtwork> entries;
            lock (ConfigLock)
            {
                entries = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId)?.LibraryArtwork?.ToList()
                    ?? new List<LibraryArtwork>();
            }

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

        /// <summary>
        /// Set once the stored device names have been repaired of legacy URL encoding in this
        /// run. Read and written only inside <c>ConfigLock</c>.
        /// </summary>
        private static bool _legacyNamesRepaired;

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

            // The household's device ids are what a sibling's device whitelist and the known-device
            // sign-in are matched against, so a profile gets them only when it could manage the
            // household anyway: the main account always, a profile only if the main account is open
            // or the PIN came with the request. Everyone else gets an empty list, not an error, so
            // the picker still draws. R-1299.
            if (masterUserId != currentUserId
                && !ManagementAllowed(masterUserId, config, null, out _))
            {
                return Ok(new List<KnownDevice>());
            }

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
                // Names recorded before 1.5.10 kept whatever encoding the client sent,
                // because the header parser returned values verbatim — a phone shows as
                // "Pixel+8". The parser is fixed, but a stored name only corrects itself
                // the next time that device contacts the server, and a device on a
                // whitelist may not do so for months. Repaired in place instead — once
                // per run, for every row, since nothing writes an encoded name any more.
                // It was a pattern match on two strings of every row on every look at
                // this list (r2-13-04).
                var repairNames = !_legacyNamesRepaired;
                foreach (var d in config.KnownDevices)
                {
                    if (d.MasterUserId == Guid.Empty
                        && (sessionDeviceIds.Contains(d.DeviceId) || whitelistedDeviceIds.Contains(d.DeviceId)))
                    {
                        d.MasterUserId = masterUserId;
                        changed = true;
                    }

                    if (!repairNames) continue;

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
                _legacyNamesRepaired = true;

                // One device that got a new id, or one television signed in as several
                // accounts, is one row: see DeviceRegistry.MergeSameDevices. Done here as well
                // as on a sighting, so a household's existing records are tidied the first
                // time anyone looks at them.
                if (Auth.DeviceRegistry.MergeSameDevices(config, masterUserId, DateTime.UtcNow) > 0) changed = true;

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

            var currentMapping = FindMapping(masterId);
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
        /// from two genuinely different machines, and guessing wrong widens a whitelist. The
        /// plugin merges by itself only what DeviceRegistry.MergeSameDevices can show (an id
        /// derived from another, or an old id silent since the new one appeared); two ids in
        /// use side by side need the administrator to say so, and this is how.
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

            var currentMapping = FindMapping(masterId);
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

                // A merge rewrites every household's device lists and owner lists. A device
                // another household has also used would hand it this one's (r2-09-01).
                if (owned.Any(d => !d.OnlyOwnedBy(masterId)))
                    return BadRequest("A device another household has used can't be merged.");

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

            var currentMapping = FindMapping(masterId);
            if (currentMapping != null && currentMapping.MasterUserId != masterId)
                return Unauthorized("Only the master profile can merge devices.");

            if (apply && !ManagementAllowed(masterId, config, null, out var pinRefusal)) return pinRefusal!;

            lock (ConfigLock)
            {
                // This household's devices only. The list is server-wide, and grouping across
                // it could fold another household's hardware into this one's whitelist.
                // Not one another household has also used: see MergeDevices (r2-09-01).
                var owned = config.KnownDevices.Where(d => d.OnlyOwnedBy(masterId)).ToList();
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

            var currentMapping = FindMapping(masterId);
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

            var mapping = FindMapping(profileId);
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
        // and /switch still demands the target profile's PIN. It DOES turn off what Bonfire
        // itself adds on top (through Plugin.IsOff, read by LimitsProvider, MaintenanceGate,
        // ProxySwitcher, ViewingTracker, QuickConnectAuthorize and the PIN sign-in provider):
        // viewing limits stop applying, the maintenance cover lifts, the proxy picker stops
        // being served, and PIN sign-in is declined. Anyone who knows the code can also skip
        // the profile prompt on a device already signed in to the master account.
        //
        // Hence: off by default, hashed like a PIN, validated only here, rate limited hard,
        // and logged loudly.

        /// <summary>A real hash of a random value nobody holds, for <see cref="Panic"/> to verify against when no code is set.</summary>
        private static class DummyPanicHash
        {
            public static readonly string Value = Auth.PinHasher.Hash("unarmed-" + Guid.NewGuid());
        }

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

            // The client's address after Jellyfin's known-proxy rules. Behind a proxy the raw
            // one is the proxy's, so five wrong guesses from anyone would have locked the
            // administrator out for an hour too (R-1166).
            var ip = (Auth.ClientAddress.Of(HttpContext) ?? HttpContext.Connection.RemoteIpAddress)?.ToString() ?? "127.0.0.1";

            // Rate limiting comes first, before the configured/not-configured branch below.
            // Checking it afterwards would make an armed server answer 429 on the sixth
            // attempt while an unarmed one kept answering 400 — the very oracle the matching
            // error messages exist to close.
            // Counted per /64 for IPv6 (one subscriber owns billions of addresses), and under a
            // ceiling for everyone together, so many addresses cannot buy unlimited guesses,
            // CPU, or map entries (R-1307).
            ip = RateLimiter.AddressKey(ip);
            // The address's own limit first, and the shared one only for a request that passed
            // it. The other way round, one address sending 200 requests spent the whole
            // server's allowance and locked the real administrator out for an hour (r2-14-01).
            if (!RateLimiter.Panic.TryBegin(ip) || !RateLimiter.PanicAll.TryBegin("all"))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: Emergency disable code rate limit hit from {Ip}. Someone is guessing.", ip);
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    "Too many attempts. Try again in an hour, or restart Jellyfin.");
            }

            // Nothing to match against. Answer exactly as a wrong code does, so the response
            // cannot be used to discover whether a server has the feature armed.
            // Trimmed, as SetPanicCode trims it before storing: the web client trims what is
            // typed, but an app or a script sends it as it is (R-1165).
            // An unarmed server still does the derivation, against a hash nothing matches, or
            // the armed one answers tens of milliseconds slower and that tells (R-1307).
            var armed = !string.IsNullOrEmpty(config.PanicCodeHash);
            var matched = VerifyPinHash(request?.Code?.Trim(), armed ? config.PanicCodeHash : DummyPanicHash.Value);
            if (!armed || !matched)
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
        /// The announcements showing now, for the profile screen; the <c>announcements</c> part
        /// of <c>/startup</c>, which is signed in, not anonymous: a message an administrator
        /// writes for the household ("down for an upgrade at 2") is not something to publish
        /// to anyone who finds the server's address.
        /// </summary>
        private object BuildAnnouncements()
        {
            var all = Plugin.Instance?.Configuration?.Announcements;
            return Configuration.Announcements.Active(all, DateTime.UtcNow).Select(DescribeAnnouncement).ToList();
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
            var left = false;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                var row = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == mapping.ProfileUserId);
                if (row != null)
                {
                    config!.Mappings.Remove(row);
                    Plugin.Instance?.SaveConfiguration();
                    left = true;
                }
            }

            // Out of the household it was in, so as far as the household goes it was deleted.
            if (left)
            {
                Auth.ViewingTracker.RulesChanged();
                BonfireCampground.RaiseDeleted(mapping.MasterUserId, mapping.ProfileUserId, mapping.ProfileName);
            }

            // 4. Shown on the sign-in screen if its master is. Every profile is hidden, because
            //    it is reached through its master; left that way, the new account is missing
            //    from Jellyfin's user list and nobody is told to type its name. Before the
            //    provider below: Jellyfin's UpdatePolicyAsync also writes the policy's
            //    provider id onto the account, so a policy write after it would undo it.
            //
            //    Both writes are past the point of no return too, the mapping being gone: a
            //    throw would leave an account that is no profile, has no password anyone
            //    knows, and may still be on Bonfire's provider. It is answered like the
            //    password failure below, and the provider reconcile is run (R-1163).
            try
            {
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ProfilesPlugin: {User} is no longer a profile, but its sign-in could not be set up.", user.Username);
                ProfilesBootstrapTask.ReconcileAuthProvidersNow();
                return (StatusCodes.Status500InternalServerError,
                    "The account was made, but its sign-in couldn't be set up. Finish it in Dashboard → Users.", user);
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

            // 5b. Its picture. Bonfire serves a profile's picture only while a mapping exists
            //     (GetProfileImage), so without this the picture is left on disk and the
            //     account shows initials. Best effort: the account is complete without it.
            await CopyPictureToAccountAsync(mapping.ProfileUserId).ConfigureAwait(false);

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
        /// Copies a former profile's stored picture to the Jellyfin user's own avatar, the way
        /// Jellyfin's own upload does: a file under the user configuration directory, named by
        /// the <c>User.ProfileImage</c> row. Never throws; a failure is logged and the account
        /// simply has no picture, as it would have before.
        /// </summary>
        private async Task CopyPictureToAccountAsync(Guid userId)
        {
            try
            {
                var found = FindImageFile(ProfileImageFolder, userId.ToString(), false);
                var user = _userManager.GetUserById(userId);
                if (found == null || user == null) return;

                var usersFolder = (Plugin.Instance?.AppPaths as MediaBrowser.Controller.IServerApplicationPaths)?.UserConfigurationDirectoryPath
                    ?? Path.Combine(Plugin.Instance?.AppPaths.ConfigurationDirectoryPath ?? Path.GetTempPath(), "users");
                // The folder is named after the account, as Jellyfin's is; its name is
                // already valid for a path, but nothing here relies on that.
                var folder = Path.Combine(usersFolder, Path.GetFileName(user.Username));
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, "profile" + Path.GetExtension(found.Value.Path).ToLowerInvariant());
                System.IO.File.Copy(found.Value.Path, target, true);

                user.ProfileImage = new Jellyfin.Database.Implementations.Entities.ImageInfo(target);
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

                // The account has it now, and nothing serves or sweeps this folder for an
                // account with no mapping, so the original would stay for good (r2-14-05).
                // Only after the copy and the account's save both went through.
                DeleteImageFiles(ProfileImageFolder, userId.ToString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: could not copy the picture of {Id} to its new account.", userId);
            }
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
            => ProfilesBootstrapTask.StandaloneProvider(_userManager.GetUserById(masterId)?.AuthenticationProviderId);

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
            return Ok(BuildInfo());
        }

        /// <summary>What <c>/info</c> answers; shared with <c>/startup</c>.</summary>
        private InfoResponse BuildInfo()
        {
            var config = ReadConfigSnapshot();

            return new InfoResponse
            {
                Version = GetPluginVersion(),
                ApiVersion = 2,
                Disabled = Plugin.IsOff,
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
            };
        }

        /// <summary>
        /// Who the caller is, in Bonfire's terms: a profile or a main account, and whose; the
        /// <c>me</c> part of <c>/startup</c>, or the refusal it gives.
        /// </summary>
        private ActionResult? BuildMe(out MeResponse? result)
        {
            result = null;
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var user = _userManager.GetUserById(userId.Value);
            if (user == null) return Unauthorized();

            var config = ReadConfigSnapshot() ?? new PluginConfiguration();
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId.Value);
            var isProfile = row != null && row.MasterUserId != Guid.Empty && row.MasterUserId != userId.Value;
            var mainId = isProfile ? row!.MasterUserId : userId.Value;

            result = new MeResponse
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
            };
            return null;
        }

        /// <summary>
        /// Everything a client needs to draw the profile picker, in one request:
        /// <c>info</c>, <c>profiles</c>, <c>me</c>, <c>themes</c>, <c>announcements</c> and
        /// <c>onboarding</c>.
        /// <para>
        /// Each part is built by the code its own route runs, never a second copy, so the two
        /// cannot drift. If a part would be refused, the whole call is refused the same way:
        /// a partial body would look like a household with no profiles. Not cached, because
        /// <c>/info</c> is not.
        /// </para>
        /// </summary>
        [HttpGet("startup")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult GetStartup()
        {
            if (GetCurrentUserId() == null) return Unauthorized();

            var failed = BuildProfileList(out var profiles);
            if (failed != null) return failed;
            failed = BuildMe(out var me);
            if (failed != null) return failed;
            failed = BuildOnboarding(out var onboarding);
            if (failed != null) return failed;
            var themes = BuildThemesPayload(customOnly: false);
            if (themes == null) return NotFound();

            Response.Headers["Cache-Control"] = "no-store";
            using var themesDoc = System.Text.Json.JsonDocument.Parse(themes);
            var body = new
            {
                info = BuildInfo(),
                profiles = profiles!,
                me = me!,
                themes = themesDoc.RootElement.Clone(),
                announcements = BuildAnnouncements(),
                onboarding = onboarding!,
            };
            return Ok(body);
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

            if (request == null) return BadRequest("No code was sent.");

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
        private object AvatarLibraryAnswer(PluginConfiguration config)
        {
            // A copy, taken under the lock: the list is added to and removed from by the
            // administrator, and walking it live can throw halfway through (R-1167).
            List<AvatarLibraryItem> library;
            lock (ConfigLock) { library = config.AvatarLibrary.ToList(); }

            return new
            {
                AllowCustomUploads = !config.DisallowCustomAvatarUploads,
                // What the browser should render a picture at before sending it. Every
                // resize happens on a canvas there, so this is the whole of the quality
                // setting as far as a client is concerned; the server checks MaxBytes.
                Image = DescribeImageSpec(config),
                StarterAvatarsEnabled = StarterAvatars.Enabled(config),
                // The administrator's own first: a household that went to the trouble of
                // uploading pictures wants those seen before the built-in set.
                Avatars = library.Select(a => new
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
            };
        }

        /// <summary>What a browser should render a picture at before sending it, and the size limit.</summary>
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
            var item = FindLibraryAvatar(id);
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
        internal const int MaxLibraryAvatars = 200;

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

            if (request == null || string.IsNullOrWhiteSpace(request.Image))
                return BadRequest("No image supplied.");

            // A fixed limit, not a setting: no household wants more than this to scroll
            // through, and every one is a file on disk and a row in the configuration (R-1171).
            int libraryCount;
            lock (ConfigLock) { libraryCount = config.AvatarLibrary.Count; }
            if (libraryCount >= MaxLibraryAvatars)
                return BadRequest($"The library is full ({MaxLibraryAvatars} pictures). Remove one first.");

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

            // The cap is looked at again where the row is added: the check above was a
            // separate look, and every add running at once passed it before any had added
            // (r2-14-02). A row that is not kept, for the cap or a save that failed, takes
            // its files with it, so neither is left on disk for nothing.
            var full = false;
            try
            {
                lock (ConfigLock)
                {
                    if (config.AvatarLibrary.Count >= MaxLibraryAvatars)
                    {
                        full = true;
                    }
                    else
                    {
                        config.AvatarLibrary.Add(item);
                        try
                        {
                            Plugin.Instance?.SaveConfiguration();
                        }
                        catch
                        {
                            config.AvatarLibrary.Remove(item);
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DeleteImageFiles(AvatarLibraryFolder, id);
                _logger.LogError(ex, "ProfilesPlugin: Could not save library avatar {Id}.", id);
                throw;
            }

            if (full)
            {
                DeleteImageFiles(AvatarLibraryFolder, id);
                return BadRequest($"The library is full ({MaxLibraryAvatars} pictures). Remove one first.");
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

        /// <summary>
        /// Saves the server-wide settings the plugin's settings page owns.
        /// </summary>
        /// <remarks>
        /// This exists because the settings page used to save through Jellyfin's generic
        /// plugin-configuration API: GET the entire PluginConfiguration, change a few fields on
        /// the copy in the browser, PUT the whole thing back. Everything else in that document
        /// — every profile mapping, every known device, every Bonfire group, the avatar
        /// library and the emergency-disable hash — went along for the ride. A profile created
        /// while the settings page sat open was reverted the moment an administrator pressed
        /// Save, with no error and nothing in the log to connect the two.
        ///
        /// It also happened to be the one call that made Jellyfin replace the configuration
        /// instance, which is what orphaned every lock taken on it. Both problems have the
        /// same cure: send the fields that changed, mutate in place under ConfigLock.
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

            // Fixed limits (Auth/TrustedAddresses.cs), refused rather than trimmed: a list
            // that quietly loses an entry looks saved.
            var trustedError = Auth.TrustedAddresses.Validate(request.TrustedAddressEntries, TrustedAccountProblem);
            if (trustedError != null) return BadRequest(trustedError);

            if (!string.IsNullOrEmpty(request.DefaultTimeZone) && !HouseholdTime.TryFind(request.DefaultTimeZone, out _))
            {
                return BadRequest("That time zone isn't known to this server.");
            }

            if (request.GettingStartedDismiss != null
                && !OnboardingSettings.GettingStartedItems.Contains(request.GettingStartedDismiss, StringComparer.Ordinal))
            {
                return BadRequest("That isn't a Getting started item.");
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
                // Plugin.IsOff reads it on every request, so it takes effect at once, both ways.
                if (request.BonfireOn.HasValue)
                    config.BonfireOn = request.BonfireOn.Value;
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
                if (request.TrustedAddressEntries != null)
                    config.TrustedAddressEntries = Auth.TrustedAddresses.Normalize(request.TrustedAddressEntries);

                config.Onboarding ??= new OnboardingSettings();
                var onboarding = config.Onboarding;
                if (request.OnboardingAsked.HasValue)
                    onboarding.Asked = request.OnboardingAsked.Value;
                onboarding.Dismissed ??= new List<string>();
                if (request.GettingStartedDismiss != null && !onboarding.Dismissed.Contains(request.GettingStartedDismiss))
                    onboarding.Dismissed.Add(request.GettingStartedDismiss);
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

            // Turning Bonfire off or on reaches every reader through Plugin.IsOff at once,
            // except what they have cached.
            if (request.BonfireOn.HasValue) Plugin.ForgetOnOffCaches();

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

            if (request == null) return BadRequest("Missing request body.");

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

            // An override is for an account that exists. Removing one is allowed for any id,
            // so a row left by a deleted account can still be cleared (R-1168).
            if (request.MaxProfiles.HasValue && _userManager.GetUserById(request.UserId) == null)
                return BadRequest("That account does not exist.");

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

        /// <summary>Deletes the whole switch log. Administrators only; there is no undo.</summary>
        [HttpPost("admin/audit-logs/clear")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult ClearAuditLogs()
        {
            var adminError = RequireAdministrator("clear the audit log");
            if (adminError != null) return adminError;

            // 500 when the file could not be written: the log is still there.
            return ClearAuditLog() ? Ok() : StatusCode(StatusCodes.Status500InternalServerError);
        }

    }
}
