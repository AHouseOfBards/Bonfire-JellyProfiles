using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Other accounts in a household's switcher (issue #39), and making an account a profile
    /// (1.7.2). A partial of <see cref="ProfilesController"/>, so tests/js/apidocs.js reads
    /// these routes with the rest. The switching rule is in
    /// <see cref="ProfilesBaseController.EvaluateSwitch(IReadOnlyList{ProfileMapping}, Guid, Guid, ICollection{Guid}, bool, IReadOnlyDictionary{Guid, bool}?)"/>.
    /// </summary>
    public partial class ProfilesController
    {
        internal const string CredentialsWrongMessage = "That username or password isn't right.";
        internal const string LinkNotFoundMessage = "That account isn't shown there.";
        internal const string MainAccountOnlyMessage = "Only the main account can do that.";

        // ── Other accounts in the switcher ───────────────────────────────────────────

        /// <summary>
        /// The accounts shown in the caller's switcher, and the households the caller is shown
        /// in. The main account only, with its PIN.
        /// </summary>
        [HttpGet("accounts/links")]
        [ProducesResponseType(typeof(AccountLinksResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<AccountLinksResponse> GetAccountLinks()
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized();
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (HouseholdOf(config, caller.Value) != caller.Value) return Unauthorized(MainAccountOnlyMessage);
            if (!ManagementAllowed(caller.Value, config, null, out var refusal)) return refusal!;

            return Ok(LinksView(caller.Value));
        }

        /// <summary>
        /// Shows another account in the caller's switcher. Its password, typed once, is its
        /// consent; Bonfire never stores it. Wrong passwords count against the same limits as
        /// PINs, and against the account's own lockout in Jellyfin.
        /// </summary>
        [HttpPost("accounts/links/add")]
        [ProducesResponseType(typeof(AccountLinksResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<AccountLinksResponse>> AddAccountLink([FromBody] AddAccountLinkRequest request)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized();
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");
            var household = caller.Value;
            if (HouseholdOf(config, household) != household) return Unauthorized(MainAccountOnlyMessage);
            if (!ManagementAllowed(household, config, request.MasterPin, out var refusal)) return refusal!;

            var username = (request.Username ?? string.Empty).Trim();
            if (username.Length == 0 || string.IsNullOrEmpty(request.Password)) return BadRequest(CredentialsWrongMessage);

            // Counted before the password is tried, like a PIN, so guesses sent together can't
            // all get through.
            var address = "link:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var limited = BeginPinAttempt(address, household);
            if (limited != null) return limited;
            var accountKey = "link:" + username.ToUpperInvariant();
            if (!RateLimiter.LinkPerAccount.TryBegin(accountKey))
                return TooManyAttempts(RateLimiter.LinkPerAccount, accountKey, "Too many failed attempts.");

            // The password is checked by Jellyfin's own provider, not UserManager.AuthenticateUser.
            // That one counts a wrong password towards the account's lockout, and an account
            // with a lockout set is turned off after three: anyone in a household could get
            // somebody else's account disabled by typing wrong passwords on purpose. The rate
            // limit above is what stops guessing here.
            var account = _userManager.GetUserByName(username);
            if (account != null && !PasswordChecksHere(account))
            {
                // LDAP, SSO and the like keep the password somewhere Bonfire can't ask
                // without going through the lockout. Said in the same words as a wrong
                // password or an unknown name: a message of its own, needing no password,
                // let anyone in a household learn which usernames exist (R-1069). An
                // administrator can still add such an account from the dashboard.
                return BadRequest(CredentialsWrongMessage);
            }

            if (account == null
                || (_userManager.GetUserDto(account, string.Empty).Policy?.IsDisabled ?? false)
                || !await PasswordMatchesAsync(account, request.Password!).ConfigureAwait(false))
            {
                return BadRequest(CredentialsWrongMessage);
            }

            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live == null) return BadRequest("Plugin configuration missing.");
                var why = AccountLinks.Refusal(live, household, account.Id, IsAdministratorAccount(account));
                if (why != null) return BadRequest(why);
                live.LinkedAccounts.Add(new LinkedAccount { HouseholdId = household, UserId = account.Id, LinkedUtc = DateTime.UtcNow });
                Plugin.Instance?.SaveConfiguration();
            }

            // Only once something was actually added. Cleared straight after the password
            // matched, an account already on the switcher was a free reset between guesses
            // at any other account's password (R-1068). LinkPerAccount is never cleared.
            EndPinAttempt(address, household);

            var householdName = _userManager.GetUserById(household)?.Username ?? household.ToString();
            LogActivity(householdName + " added " + account.Username + " to their switcher", "AccountLinked", account.Id);
            return Ok(LinksView(household));
        }

        /// <summary>
        /// Takes an account off a switcher. Either side may: the household (send
        /// <c>userId</c>) or the account itself (send <c>householdId</c>).
        /// </summary>
        [HttpPost("accounts/links/remove")]
        [ProducesResponseType(typeof(AccountLinksResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<AccountLinksResponse>> RemoveAccountLink([FromBody] RemoveAccountLinkRequest request)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized();
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");
            var me = caller.Value;
            if (HouseholdOf(config, me) != me) return Unauthorized(MainAccountOnlyMessage);
            if (!ManagementAllowed(me, config, request.MasterPin, out var refusal)) return refusal!;

            // One side or the other is always the caller. Anything else is someone else's link.
            Guid household, account;
            if (request.HouseholdId == Guid.Empty || request.HouseholdId == me) { household = me; account = request.UserId; }
            else if (request.UserId == Guid.Empty || request.UserId == me) { household = request.HouseholdId; account = me; }
            else return Unauthorized(LinkNotFoundMessage);

            if (!RemoveLink(household, account)) return BadRequest(LinkNotFoundMessage);
            await SignOutClosedEntriesAsync().ConfigureAwait(false);

            var who =_userManager.GetUserById(me)?.Username ?? me.ToString();
            var other = _userManager.GetUserById(household == me ? account : household)?.Username ?? "an account";
            LogActivity(household == me
                    ? who + " took " + other + " off their switcher"
                    : who + " left " + other + "'s switcher",
                "AccountUnlinked", account);
            return Ok(LinksView(me));
        }

        // ── The administrator's side ─────────────────────────────────────────────────

        /// <summary>Shows an account in a household's switcher, without its password.</summary>
        [HttpPost("admin/links")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult AdminAddAccountLink([FromBody] AdminAccountLinkRequest request)
        {
            var adminError = RequireAdministrator("add an account to a switcher");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");

            var account = _userManager.GetUserById(request.UserId);
            var householdUser = _userManager.GetUserById(request.HouseholdId);
            if (account == null || householdUser == null) return NotFound("User not found.");

            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live == null) return BadRequest("Plugin configuration missing.");
                if (HouseholdOf(live, request.HouseholdId) != request.HouseholdId) return BadRequest(AccountLinks.ProfileMessage);
                var why = AccountLinks.Refusal(live, request.HouseholdId, request.UserId, IsAdministratorAccount(account));
                if (why != null) return BadRequest(why);
                live.LinkedAccounts.Add(new LinkedAccount
                {
                    HouseholdId = request.HouseholdId,
                    UserId = request.UserId,
                    LinkedUtc = DateTime.UtcNow,
                    ByAdministrator = true
                });
                Plugin.Instance?.SaveConfiguration();
            }

            LogActivity(CallerName() + " added " + account.Username + " to " + householdUser.Username + "'s switcher",
                "AccountLinked", account.Id);
            return Ok();
        }

        [HttpPost("admin/links/remove")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> AdminRemoveAccountLink([FromBody] AdminAccountLinkRequest request)
        {
            var adminError = RequireAdministrator("take an account off a switcher");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");
            if (!RemoveLink(request.HouseholdId, request.UserId)) return BadRequest(LinkNotFoundMessage);
            await SignOutClosedEntriesAsync().ConfigureAwait(false);

            LogActivity(CallerName() + " took " + (_userManager.GetUserById(request.UserId)?.Username ?? "an account")
                    + " off " + (_userManager.GetUserById(request.HouseholdId)?.Username ?? "a household") + "'s switcher",
                "AccountUnlinked", request.UserId);
            return Ok();
        }

        private static bool RemoveLink(Guid household, Guid account)
        {
            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live?.LinkedAccounts == null) return false;
                if (live.LinkedAccounts.RemoveAll(l => l.HouseholdId == household && l.UserId == account) == 0) return false;
                Plugin.Instance?.SaveConfiguration();
                return true;
            }
        }

        /// <summary>
        /// Whether the account's password lives in Jellyfin: on Jellyfin's own provider, or
        /// on Bonfire's PIN provider, which hands passwords to Jellyfin's.
        /// </summary>
        private static bool PasswordChecksHere(Jellyfin.Database.Implementations.Entities.User account)
        {
            var id = account.AuthenticationProviderId ?? string.Empty;
            return id.EndsWith(".DefaultAuthenticationProvider", StringComparison.Ordinal)
                || string.Equals(id, typeof(Auth.BonfirePinAuthenticationProvider).FullName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Checks a password with Jellyfin's own provider, which compares the hash and
        /// nothing else: no lockout count, no session, no last-login time.
        /// </summary>
        private async Task<bool> PasswordMatchesAsync(Jellyfin.Database.Implementations.Entities.User account, string password)
        {
            var jellyfins = Auth.BonfirePinAuthenticationProvider.JellyfinsProvider(HttpContext?.RequestServices);
            if (jellyfins == null)
            {
                _logger.LogError("ProfilesPlugin: Jellyfin's own authentication provider wasn't found, so {Name}'s password can't be checked.", account.Username);
                return false;
            }

            try
            {
                if (jellyfins is MediaBrowser.Controller.Authentication.IRequiresResolvedUser resolved)
                    await resolved.Authenticate(account.Username, password, account).ConfigureAwait(false);
                else
                    await jellyfins.Authenticate(account.Username, password).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation("ProfilesPlugin: adding {Name} to a switcher was refused: {Reason}", account.Username, ex.Message);
                return false;
            }
        }

        private AccountLinksResponse LinksView(Guid me)
        {
            List<LinkedAccount> links;
            List<ProfileMapping> rows;
            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                links = live?.LinkedAccounts?.Select(l => l.Copy()).ToList() ?? new List<LinkedAccount>();
                rows = live?.Mappings?.ToList() ?? new List<ProfileMapping>();
            }

            LinkedAccountView View(LinkedAccount l, Guid shown)
            {
                return new LinkedAccountView
                {
                    UserId = shown,
                    Name = _userManager.GetUserById(shown)?.Username ?? string.Empty,
                    HasPin = !string.IsNullOrEmpty(rows.FirstOrDefault(m => m.ProfileUserId == shown)?.PinHash),
                    ByAdministrator = l.ByAdministrator,
                    LinkedUtc = l.LinkedUtc
                };
            }

            return new AccountLinksResponse
            {
                Added = links.Where(l => l.HouseholdId == me && l.UserId != me)
                    .OrderBy(l => l.LinkedUtc).Select(l => View(l, l.UserId))
                    .Where(v => v.Name.Length > 0).ToList(),
                ShownIn = links.Where(l => l.UserId == me && l.HouseholdId != me)
                    .OrderBy(l => l.LinkedUtc).Select(l => View(l, l.HouseholdId))
                    .Where(v => v.Name.Length > 0).ToList()
            };
        }

        // ── An account made a profile ────────────────────────────────────────────────

        /// <summary>
        /// Makes an existing account a profile of a household: the reverse of
        /// <see cref="MakeStandalone"/>. Same user, so its watch history, favourites and
        /// resume points stay.
        ///
        /// <para><b>It keeps what it can watch, made no wider.</b> Its libraries and tags
        /// become the profile's own, and from then on the household's are applied over them,
        /// as for any profile, so it can never see more than the household does.</para>
        ///
        /// <para><b>Its password stops working.</b> It is signed out everywhere and given a
        /// random password, on Jellyfin's own provider: a profile is opened from the switcher
        /// or with its PIN, and a password nobody knows is what every profile has. A Bonfire
        /// PIN it had as a main account becomes the profile's PIN.</para>
        ///
        /// <para>Administrator only, like the reverse: it takes a sign-in away. Refused for an
        /// administrator, for an account with profiles of its own, and for one in a Bonfire,
        /// where the account's own household would be left behind.</para>
        /// </summary>
        [HttpPost("admin/make-profile")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<object>> MakeProfile([FromBody] MakeProfileRequest request)
        {
            var adminError = RequireAdministrator("make an account a profile");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("Missing request body.");

            var user = _userManager.GetUserById(request.UserId);
            var householdUser = _userManager.GetUserById(request.HouseholdId);
            if (user == null || householdUser == null) return NotFound("User not found.");
            if (request.UserId == request.HouseholdId) return BadRequest("Choose another account's household.");
            if (IsAdministratorAccount(user)) return BadRequest("An administrator can't be made a profile.");

            var name = string.IsNullOrWhiteSpace(request.ProfileName) ? user.Username : request.ProfileName.Trim();
            // The account keeps its own username, which is also its default name.
            var nameError = ValidateProfileName(name) ?? ValidateProfileNameFree(_userManager, name, user.Id);
            if (nameError != null) return BadRequest(nameError);

            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");

            // The same gate as creating one: the limit is counted before the change and the
            // row written after it.
            await CreateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ProfileMapping? ownRow;
                lock (ConfigLock)
                {
                    var why = MakeProfileRefusal(config, request.UserId, request.HouseholdId, name);
                    if (why != null) return BadRequest(why);
                    ownRow = config.Mappings.FirstOrDefault(m => m.ProfileUserId == request.UserId)?.CopyForReading();
                }

                var householdPolicy = _userManager.GetUserDto(householdUser, string.Empty).Policy;

                // Sign-out is step 3, after the password. It used to come first, with the hiding
                // below: when the password change then threw, the account was left hidden from
                // the sign-in screen, signed out, and with no Bonfire row to explain why
                // (R-1070). Everything before the password is put back if it fails; a session
                // that has been revoked cannot be.

                // 1. Hidden from the sign-in screen, like every profile, and on Jellyfin's own
                //    provider. Jellyfin's UpdatePolicyAsync writes the policy's provider onto
                //    the account, so both go in the one write.
                var provider = StandaloneProviderId(request.HouseholdId);
                var policy = _userManager.GetUserDto(user, string.Empty).Policy;
                var wasHidden = policy.IsHidden;
                var wasProvider = user.AuthenticationProviderId;
                var enabled = ProfileFoldersFrom(policy, householdPolicy, out var allLibraries);
                var blockedTags = NormalizeTags(policy.BlockedTags);
                var allowedTags = NormalizeTags(policy.AllowedTags);
                policy.IsHidden = true;
                policy.AuthenticationProviderId = provider;
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                user = _userManager.GetUserById(request.UserId) ?? user;
                if (!string.Equals(user.AuthenticationProviderId, provider, StringComparison.Ordinal))
                {
                    user.AuthenticationProviderId = provider;
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }

                // 2. A password nobody knows, set while it is not yet a profile: Bonfire's own
                //    provider refuses a password change for one.
                try
                {
                    await ChangePasswordCompat(user, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "ProfilesPlugin: could not replace {User}'s password before making it a profile.", user.Username);
                    try
                    {
                        // Back to how it was shown and where it signs in; one write, as above.
                        var back = _userManager.GetUserDto(user, string.Empty).Policy;
                        back.IsHidden = wasHidden;
                        back.AuthenticationProviderId = wasProvider;
                        await _userManager.UpdatePolicyAsync(user.Id, back).ConfigureAwait(false);
                        var current = _userManager.GetUserById(request.UserId) ?? user;
                        if (!string.Equals(current.AuthenticationProviderId, wasProvider, StringComparison.Ordinal))
                        {
                            current.AuthenticationProviderId = wasProvider;
                            await _userManager.UpdateUserAsync(current).ConfigureAwait(false);
                        }
                    }
                    catch (Exception undo) when (undo is not OperationCanceledException)
                    {
                        _logger.LogError(undo, "ProfilesPlugin: could not put {User} back after its password change failed. Check it in Dashboard → Users.", user.Username);
                    }

                    return StatusCode(StatusCodes.Status500InternalServerError,
                        "Its password couldn't be changed, so it wasn't made a profile. See the server log.");
                }

                // 3. Out of every session its old password gave it.
                try
                {
                    await _sessionManager.RevokeUserTokens(user.Id, null).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "ProfilesPlugin: could not sign {User} out before making it a profile.", user.Username);
                }

                // 4. A profile. Its main-account row goes, with what only a main account has.
                lock (ConfigLock)
                {
                    config.Mappings.RemoveAll(m => m.ProfileUserId == request.UserId);
                    config.AccountEmails?.RemoveAll(a => a.UserId == request.UserId);
                    config.UserProfileLimitOverrides?.RemoveAll(o => o.UserId == request.UserId);
                    AccountLinks.Forget(config, request.UserId);
                    config.Mappings.Add(new ProfileMapping
                    {
                        ProfileUserId = request.UserId,
                        MasterUserId = request.HouseholdId,
                        ProfileName = name,
                        PinHash = ownRow?.PinHash,
                        AvatarColor = ownRow != null && IsAvatarColor(ownRow.AvatarColor)
                            ? ownRow.AvatarColor
                            : NextAvatarColor(request.HouseholdId, config),
                        TransparentAvatar = ownRow?.TransparentAvatar ?? false,
                        ProfileImage = ownRow?.ProfileImage,
                        Theme = config.AllowProfileThemes ? ownRow?.Theme : null,
                        IsHidden = true,
                        KeepAccountOnDelete = !request.DeleteWithProfile,
                        LockoutMinutes = ownRow?.LockoutMinutes ?? 5,
                        EnabledFolders = enabled,
                        AllLibraries = allLibraries,
                        BlockedTags = blockedTags,
                        AllowedTags = allowedTags,
                    });
                    Plugin.Instance?.SaveConfiguration();
                }
            }
            finally
            {
                CreateGate.Release();
            }

            // 5. The household's restrictions over its own, now it is one of theirs.
            await Auth.ProfilePolicySync.ApplyAsync(_userManager, _libraryManager, _logger, request.UserId).ConfigureAwait(false);

            // 6. Onto Bonfire's provider if the household's apps sign in with PINs.
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();

            LogActivity(CallerName() + " made " + user.Username + " a profile of " + householdUser.Username,
                "AccountMadeProfile", user.Id);
            BonfireCampground.RaiseCreated(request.HouseholdId, request.UserId, name);
            _logger.LogWarning("ProfilesPlugin: {Admin} made {User} a profile of {Household}.", CallerName(), user.Username, householdUser.Username);

            return Ok(new { ProfileUserId = request.UserId, ProfileName = name });
        }

        /// <summary>Why the account can't become a profile of the household, or null. Under the lock.</summary>
        internal static string? MakeProfileRefusal(PluginConfiguration config, Guid userId, Guid householdId, string name)
        {
            var own = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            if (own != null && own.MasterUserId != Guid.Empty && own.MasterUserId != userId) return "That account is already a profile.";
            var home = config.Mappings.FirstOrDefault(m => m.ProfileUserId == householdId);
            if (home != null && home.MasterUserId != Guid.Empty && home.MasterUserId != householdId) return "Choose a main account, not a profile.";
            if (config.Mappings.Any(m => m.MasterUserId == userId && m.ProfileUserId != userId)) return "It has profiles of its own. Move or delete them first.";
            if (config.BonfireGroups.Any(g => g.OwnerUserId == userId || g.MemberUserIds.Contains(userId))) return "It's in a Bonfire. Leave it first.";

            var max = config.UserProfileLimitOverrides?.FirstOrDefault(o => o.UserId == householdId)?.MaxProfiles ?? config.MaxProfilesPerUser;
            if (config.Mappings.Count(m => m.MasterUserId == householdId && m.ProfileUserId != householdId) >= max)
                return $"Maximum profile limit of {max} reached.";
            if (config.Mappings.Any(m => m.MasterUserId == householdId && m.ProfileUserId != householdId
                                         && string.Equals(m.ProfileName, name, StringComparison.OrdinalIgnoreCase)))
                return "A profile with this name already exists.";
            return null;
        }

        /// <summary>
        /// The libraries an account can see now, as a profile's own list. "Every library" with
        /// nothing blocked stays "every library the household has"; anything narrower is kept
        /// as the list it is.
        /// </summary>
        private List<Guid> ProfileFoldersFrom(MediaBrowser.Model.Users.UserPolicy policy, MediaBrowser.Model.Users.UserPolicy householdPolicy, out bool allLibraries)
        {
            var blocked = policy.BlockedMediaFolders ?? Array.Empty<Guid>();
            if (policy.EnableAllFolders && blocked.Length == 0)
            {
                allLibraries = true;
                return GetMasterAccessibleFolders(householdPolicy).ToList();
            }

            allLibraries = false;
            // A library another plugin manages is not copied into the profile's own list: it stays on the
            // account as it is, and Bonfire does not manage it from here on.
            var owned = Auth.LibraryOwnership.ManagedByOwnerIds(_libraryManager);
            IEnumerable<Guid> visible = policy.EnableAllFolders
                ? Auth.LibraryOwnership.ManagedFolderIds(_libraryManager)
                : policy.EnabledFolders ?? Array.Empty<Guid>();
            return visible.Where(id => !blocked.Contains(id) && !owned.Contains(id)).Distinct().ToList();
        }
    }
}
