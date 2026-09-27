using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Lets a Bonfire sub-profile be entered with its PIN, from any Jellyfin client.
    ///
    /// <para>
    /// Native clients — Android TV, Roku, Swiftfin — never load the web client, so no
    /// script of ours runs on them and there is no switcher to draw. What they do have is
    /// a login screen with one password box. This turns that box into the PIN prompt: the
    /// household picks a profile, types the PIN on the remote, and Jellyfin's own
    /// authentication does the rest.
    /// </para>
    ///
    /// <para>
    /// The PIN is never stored as a Jellyfin password. It stays a Bonfire secret in
    /// Bonfire's own PBKDF2 hash, checked here, so there is no account credential to
    /// steal and the rules stay ours to enforce.
    /// </para>
    ///
    /// <para><b>Two rules this file must keep.</b></para>
    ///
    /// <para>
    /// 1. <b>Every member is implemented implicitly, never explicitly.</b> Jellyfin 12.0
    /// removed <c>HasPassword</c> from <see cref="IAuthenticationProvider"/>; 10.11 still
    /// declares it. We ship one net9 assembly for both, compiled against 10.11, so on 12.0
    /// <c>HasPassword</c> is simply an extra public method nobody calls. Written as an
    /// explicit implementation (<c>bool IAuthenticationProvider.HasPassword</c>) the
    /// runtime would look for an interface member that does not exist on 12.0 and the type
    /// would fail to load — taking authentication with it, for every user on the server.
    /// </para>
    ///
    /// <para>
    /// 2. <b>Never succeed for an account that is not a Bonfire profile.</b> When a user
    /// has no <c>AuthenticationProviderId</c>, Jellyfin tries every enabled provider in
    /// turn and accepts the first success, so this method is reachable for accounts that
    /// have nothing to do with Bonfire. A wrong success here is an authentication bypass on
    /// somebody else's account. The one thing done for an account that is not a profile is
    /// to hand its password to Jellyfin's own provider, and only when the account is bound
    /// to this one — see <see cref="IsBoundHere"/>.
    /// </para>
    ///
    /// <para>
    /// 3. <b>Nobody bound to this provider may be left without a way in.</b> Jellyfin
    /// offers a bound account this provider and nothing else, and maps it to
    /// <c>InvalidAuthProvider</c> when this one reports itself disabled. So every refusal
    /// below that is not about a PIN falls through to the account's real password.
    /// </para>
    /// </summary>
    public class BonfirePinAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
    {
        private readonly ILogger<BonfirePinAuthenticationProvider> _logger;

        /// <param name="services">
        /// Resolved from, rather than injected as, <c>IUserManager</c>. Jellyfin's
        /// <c>UserManager</c> constructor takes <c>IEnumerable&lt;IAuthenticationProvider&gt;</c>,
        /// so asking for the user manager here would be a dependency cycle and the container
        /// would fail to build it — taking every login on the server with it. The service
        /// provider is safe to hold and is only asked for the user manager during a request,
        /// long after both exist.
        /// </param>
        public BonfirePinAuthenticationProvider(
            ILogger<BonfirePinAuthenticationProvider> logger,
            IServiceProvider? services = null)
        {
            _logger = logger;
            _services = services;
        }

        private readonly IServiceProvider? _services;

        /// <summary>Shown in Dashboard → Users as the account's authentication provider.</summary>
        public string Name => "Bonfire PIN";

        /// <summary>
        /// Off unless an administrator has turned the feature on.
        /// <para>
        /// A disabled provider does NOT fall back to Jellyfin's own. <c>GetAuthenticationProviders</c>
        /// filters disabled providers out and then matches the user's bound id against what
        /// is left; when nothing matches, the user is handed <c>InvalidAuthProvider</c>, which
        /// refuses everything. That is why turning the feature off re-points every bound
        /// account first (<see cref="ProfilesBootstrapTask.ReconcileAuthProvidersNow"/>).
        /// </para>
        /// <para>
        /// The emergency disable deliberately does not touch this. It used to, and a master
        /// bound here for its PIN then could not sign in with its password at all — during
        /// the one event that exists because the web interface has become hard to use.
        /// While it is tripped, <see cref="Authenticate(string, string, User)"/> accepts no
        /// PIN and passes every bound master's password to Jellyfin instead.
        /// </para>
        /// </summary>
        public bool IsEnabled =>
            Plugin.Instance?.Configuration?.EnableClientPinLogin == true;

        /// <summary>
        /// Whether the client should show a password box at all.
        /// <para>
        /// <b>Jellyfin 10.11 only.</b> 12.0 removed this from the interface and hardcoded
        /// <c>UserDto.HasPassword</c> to true, marking it "This information is no longer
        /// provided" — so on 12.0 a profile with no PIN is still prompted and the viewer
        /// submits an empty box. Kept implicit rather than deleted so 10.11 still gets the
        /// better behaviour; see rule 1 in the class summary for why it must not be
        /// explicit.
        /// </para>
        /// </summary>
        public bool HasPassword(User user)
        {
            var mapping = FindSubProfile(user?.Id);

            // Not ours: say the account has a password rather than claiming otherwise about
            // a user we know nothing about. With a blank AuthenticationProviderId this
            // method can be the one Jellyfin happens to ask, and answering false for a real
            // account would hide its password box entirely.
            if (mapping == null) return true;

            return !string.IsNullOrEmpty(mapping.PinHash);
        }

        /// <summary>
        /// Reached when Jellyfin could not resolve the username to a user — which is the
        /// normal case now, because the sign-in screen offers a household's own name for a
        /// profile rather than the system username underneath it.
        ///
        /// <para>A profile is created as <c>&lt;master&gt;_&lt;name&gt;</c> so that two
        /// households on one server can both have a "kids", and that system username is what
        /// the web switcher, the audit log and Jellyfin's own dashboard all show. Only the
        /// sign-in screen shows the short name, so only this path has to translate it
        /// back.</para>
        ///
        /// <para><b>Which "kids" is decided by the device, never by the name.</b> The
        /// household is resolved from the device the request came in on, and only that
        /// household's profiles are considered — so the collision case is not ambiguous, it
        /// simply never arises. A device we do not recognise has no household, and a name
        /// alone cannot be resolved, so it is declined.</para>
        ///
        /// <para>Jellyfin then trusts the username we return and looks it up itself:
        /// <c>UserManager.AuthenticateUser</c>, the <c>user is null</c> branch. That branch
        /// excludes <c>DefaultAuthenticationProvider</c>, which is why this works for us and
        /// would not for Jellyfin's own.</para>
        /// </summary>
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.Mappings == null || !config.EnableClientPinLogin) throw Decline();

            // Only ever reaches a sub-profile, and the emergency disable turns PIN entry off.
            if (Plugin.IsPanicDisabled) throw Decline();

            var deviceId = RequestDevice.Current;
            var master = DeviceRegistry.FindMaster(config, deviceId);
            if (master == Guid.Empty)
            {
                _logger.LogInformation(
                    "ProfilesPlugin: sign-in as {Name} from device {DeviceId}, which no household has "
                    + "signed in on, so there is no way to tell which profile was meant.",
                    username,
                    string.IsNullOrEmpty(deviceId) ? "(none sent)" : deviceId);
                throw Decline();
            }

            // Only this household, and never the master: it keeps its real username, is
            // reachable by it, and must not become PIN-openable as a side effect of a
            // name lookup.
            var mapping = config.Mappings.FirstOrDefault(m =>
                m.MasterUserId == master
                && m.ProfileUserId != master
                && string.Equals(m.ProfileName, username, StringComparison.OrdinalIgnoreCase));

            if (mapping == null) throw Decline();

            var user = _services?.GetService(typeof(IUserManager)) is IUserManager users
                ? users.GetUserById(mapping.ProfileUserId)
                : null;

            if (user == null)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: {Name} resolved to profile {ProfileId}, which no longer exists.",
                    username, mapping.ProfileUserId);
                throw Decline();
            }

            return Authenticate(user.Username, password, user);
        }

        public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
        {
            if (resolvedUser == null) throw Decline();

            var mapping = FindSubProfile(resolvedUser.Id);

            // A master with a PIN of its own, which is a different case entirely: it keeps a
            // real Jellyfin password and must go on being able to use it.
            //
            // "Masters administer the server" was the reason this used to refuse them, and
            // it is only true of the one account that set the server up — on a server with
            // many households, most masters administer nothing. Administrator accounts are
            // allowed too, deliberately, with a warning under the PIN field.
            if (mapping == null)
            {
                var master = FindMasterWithPin(resolvedUser.Id);

                // The PIN first, then the account's real password. Jellyfin binds a user to
                // exactly one provider — GetAuthenticationProviders filters on
                // AuthenticationProviderId — so a master bound to this one would otherwise
                // LOSE its password everywhere, web included, and be left with four digits
                // in front of an account that may administer the server. Delegating is what
                // makes both work at once, and it is also the safety net: if anything here
                // is wrong, the real password still gets you in.
                //
                // Only something PIN-shaped is tried as a PIN, and only that is counted
                // against the throttle. A master's ordinary password sign-in, on the web or
                // anywhere else, comes through here too and must never use up the PIN
                // allowance or be slowed by it.
                if (master != null && !Plugin.IsPanicDisabled && LooksLikePin(password))
                {
                    if (!BeginPinAttempt(resolvedUser.Id))
                    {
                        _logger.LogWarning(
                            "ProfilesPlugin: too many wrong PINs for master account {UserId}; PIN sign-in is "
                            + "paused for it, and only its password is being checked.",
                            resolvedUser.Id);
                    }
                    else if (PinHasher.Verify(password, master.PinHash) == PinHasher.PinResult.Match)
                    {
                        EndPinAttempt(resolvedUser.Id);
                        _logger.LogInformation(
                            "ProfilesPlugin: master account {UserId} entered by PIN on a client.",
                            master.MasterUserId);
                        return Task.FromResult(new ProviderAuthenticationResult { Username = resolvedUser.Username });
                    }
                }

                // A master with no PIN — never set, since cleared, or its last profile
                // deleted — that is still bound here. This used to refuse outright, and
                // because Jellyfin offers a bound account nothing but this provider, clearing
                // your PIN locked your real password out everywhere until the binding was
                // repaired by hand. Rule 3: the password always gets through.
                if (master == null && !IsBoundHere(resolvedUser)) throw Decline();

                return DelegateToJellyfin(resolvedUser, password);
            }

            // Every PIN below belongs to a sub-profile, and the emergency disable turns PIN
            // entry off. A sub-profile has no password of its own to fall back to.
            if (Plugin.IsPanicDisabled) throw Decline();

            // A profile limited to particular devices is limited here too.
            //
            // The switcher and verify-pin both enforce this, and the sign-in-screen path
            // enforced it nowhere — so a profile restricted to the living room television
            // could be opened with its PIN from any phone on the internet. Restricting it
            // is the whole reason somebody sets the list.
            //
            // The same evaluator as the other two call sites, not a copy: a copy is where
            // the two halves of a rule drift apart.
            var access = Controllers.ProfilesBaseController.EvaluateDeviceRestriction(
                mapping,
                RequestDevice.Current,
                Plugin.Instance?.Configuration?.KnownDevices);

            if (access != Controllers.ProfilesBaseController.DeviceAccess.NotRestricted
                && access != Controllers.ProfilesBaseController.DeviceAccess.Allowed)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a client sign-in to profile {ProfileId} from device "
                    + "{DeviceId} ({Reason}).",
                    mapping.ProfileUserId,
                    string.IsNullOrEmpty(RequestDevice.Current) ? "(none sent)" : RequestDevice.Current,
                    access);

                // Declined with the message every other refusal carries, so a response
                // cannot say which profiles are device-restricted.
                throw Decline();
            }

            // A profile with no PIN opens with an empty box — but only on a device the
            // household has actually signed in on.
            //
            // The empty box on its own was a hole, and Logan found it by trying: this
            // provider is reached by anything that can POST /Users/AuthenticateByName, it
            // sees no device and no network, and sub-profile usernames are both predictable
            // (`<master>_<profile>`) and published by our own /Users/Public injection. On a
            // server reachable from the internet that made a PIN-less sub-profile into an
            // account a stranger could enter by guessing one username. Before client PIN
            // login existed it was impossible, because the account's Jellyfin password is a
            // random 64 characters that is generated, set and discarded.
            //
            // Requiring a known device keeps exactly the behaviour that is wanted — walk up
            // to the family television, choose a profile, type nothing — while the same
            // request from anywhere else fails. It is not a secret and not a boundary: it
            // raises entry from "know the username" to "know a device id this household has
            // signed in on". A PIN remains the only actual credential.
            if (string.IsNullOrEmpty(mapping.PinHash))
            {
                // "No PIN" must not quietly become "any PIN" — somebody testing whether the
                // box does anything would otherwise be let in.
                if (!string.IsNullOrEmpty(password)) throw Decline();

                var deviceId = RequestDevice.Current;
                var seenFor = DeviceRegistry.FindMaster(Plugin.Instance?.Configuration, deviceId);

                if (seenFor == Guid.Empty || seenFor != mapping.MasterUserId)
                {
                    _logger.LogWarning(
                        "ProfilesPlugin: refused to open PIN-less profile {ProfileId} from device {DeviceId}, "
                        + "which this household has not signed in on. Set a PIN on the profile to allow "
                        + "entry from anywhere.",
                        mapping.ProfileUserId,
                        string.IsNullOrEmpty(deviceId) ? "(none sent)" : deviceId);
                    throw Decline();
                }

                _logger.LogInformation(
                    "ProfilesPlugin: profile {ProfileId} opened with no PIN on {DeviceId}, a device this "
                    + "household uses.",
                    mapping.ProfileUserId, deviceId);
                return Task.FromResult(new ProviderAuthenticationResult { Username = resolvedUser.Username });
            }

            // Nothing that is not PIN-shaped can match, so it is refused without spending a
            // key derivation on it or an attempt from the allowance.
            if (!LooksLikePin(password)) throw Decline();

            // Jellyfin's own lockout does not cover this: LoginAttemptsBeforeLockout is null
            // on every account Bonfire creates, and Jellyfin reads null as "never lock". A
            // four-digit PIN behind no limit at all is ten thousand requests from anywhere.
            // Counted per account rather than per address, so more addresses buy nothing.
            // The price is that somebody hammering a profile's name can pause PIN sign-in
            // for it on other apps for a while; the web switcher is limited separately and
            // is unaffected.
            if (!BeginPinAttempt(mapping.ProfileUserId))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: too many wrong PINs for profile {ProfileId}; PIN sign-in on other "
                    + "apps is paused for it.",
                    mapping.ProfileUserId);
                throw Decline();
            }

            var result = PinHasher.Verify(password, mapping.PinHash);
            if (result == PinHasher.PinResult.MalformedHash)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: stored PIN hash for profile {ProfileId} is malformed; refusing to verify.",
                    mapping.ProfileUserId);
                throw Decline();
            }

            if (result != PinHasher.PinResult.Match)
            {
                throw Decline();
            }

            EndPinAttempt(mapping.ProfileUserId);

            _logger.LogInformation(
                "ProfilesPlugin: profile {ProfileId} entered by PIN on a client.",
                mapping.ProfileUserId);

            return Task.FromResult(new ProviderAuthenticationResult { Username = resolvedUser.Username });
        }

        /// <summary>
        /// Jellyfin calls this when a password is changed or reset, and it calls the
        /// provider the account is bound to.
        /// <para>
        /// For a sub-profile it is refused rather than ignored: Bonfire PINs are changed
        /// through the plugin, writing one here would silently create a second place a PIN
        /// lives, and a caller should be told nothing happened instead of believing it did.
        /// </para>
        /// <para>
        /// For a master it is Jellyfin's to do. A master bound here for its PIN still owns a
        /// real password, and refusing this left it unable to change that password, or have
        /// an administrator reset it, for as long as the binding lasted.
        /// </para>
        /// </summary>
        public Task ChangePassword(User user, string newPassword)
        {
            if (user != null && FindSubProfile(user.Id) == null)
            {
                var jellyfins = FindJellyfinsProvider();
                if (jellyfins != null) return jellyfins.ChangePassword(user, newPassword);

                _logger.LogError(
                    "ProfilesPlugin: could not find Jellyfin's own authentication provider, so the "
                    + "password for {Username} cannot be changed.",
                    user.Username);
            }

            throw new AuthenticationException(
                "A Bonfire profile's PIN is changed in Bonfire, not as an account password.");
        }

        /// <summary>A PIN as Bonfire accepts one: four to eight digits.</summary>
        private static bool LooksLikePin(string? value)
            => !string.IsNullOrEmpty(value)
               && value.Length >= 4 && value.Length <= 8
               && value.All(char.IsAsciiDigit);

        /// <summary>
        /// Counts one PIN attempt against this account and says whether it may be made.
        /// Counted before the PIN is checked, so parallel guesses cannot all pass the check
        /// together. Both windows are consulted every time.
        /// </summary>
        private static bool BeginPinAttempt(Guid userId)
        {
            var key = userId.ToString("N");
            var shortWindow = Controllers.RateLimiter.ClientPin.TryBegin(key);
            var day = Controllers.RateLimiter.ClientPinDaily.TryBegin(key);
            return shortWindow && day;
        }

        /// <summary>A PIN was right: the account's allowance starts again.</summary>
        private static void EndPinAttempt(Guid userId)
        {
            var key = userId.ToString("N");
            Controllers.RateLimiter.ClientPin.Reset(key);
            Controllers.RateLimiter.ClientPinDaily.Reset(key);
        }

        /// <summary>
        /// Whether Jellyfin has this account bound to this provider — the one case where
        /// Jellyfin offers it no other, so a refusal here is a refusal everywhere.
        /// </summary>
        private bool IsBoundHere(User user)
            => string.Equals(user.AuthenticationProviderId, GetType().FullName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The mapping row of a master that has set a PIN, or null.
        /// <para>
        /// A master's own row has <c>MasterUserId == ProfileUserId</c> and is written once,
        /// lazily, the first time switcher preferences are saved — so most households have
        /// none, and a master with no PIN is simply not a candidate here.
        /// </para>
        /// </summary>
        private static ProfileMapping? FindMasterWithPin(Guid userId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.Mappings == null || userId == Guid.Empty) return null;

            var row = config.Mappings.FirstOrDefault(m =>
                m.MasterUserId == userId
                && m.ProfileUserId == userId
                && !string.IsNullOrEmpty(m.PinHash));

            // Only an account other profiles actually point at. A stray self-row on an
            // account with no profiles must not become a PIN-openable login.
            if (row == null) return null;
            if (!config.Mappings.Any(m => m.MasterUserId == userId && m.ProfileUserId != userId)) return null;

            return row;
        }

        /// <summary>
        /// Hands the credential to Jellyfin's own provider, so an account bound to this one
        /// keeps working with its real password.
        /// <para>
        /// <c>DefaultAuthenticationProvider</c> is registered as an
        /// <c>IAuthenticationProvider</c> in Jellyfin's container
        /// (<c>CoreAppHost.RegisterServices</c>), which is the only reason this is possible.
        /// Matched on the type's short name so a harness can stand in for it, and never on
        /// "whichever provider is not ours" — the other one registered there is
        /// <c>InvalidAuthProvider</c>, whose entire job is to refuse.
        /// </para>
        /// </summary>
        private Task<ProviderAuthenticationResult> DelegateToJellyfin(User user, string password)
        {
            var jellyfins = FindJellyfinsProvider();

            if (jellyfins == null)
            {
                _logger.LogError(
                    "ProfilesPlugin: could not find Jellyfin's own authentication provider, so the "
                    + "password for {Username} cannot be checked. Turn off PIN login on other apps to "
                    + "restore password sign-in for this account.",
                    user.Username);
                throw Decline();
            }

            return jellyfins is IRequiresResolvedUser resolved
                ? resolved.Authenticate(user.Username, password, user)
                : jellyfins.Authenticate(user.Username, password);
        }

        /// <summary>Jellyfin's own <c>DefaultAuthenticationProvider</c>; see <see cref="DelegateToJellyfin"/>.</summary>
        private IAuthenticationProvider? FindJellyfinsProvider()
        {
            var providers = _services?.GetService(typeof(IEnumerable<IAuthenticationProvider>))
                as IEnumerable<IAuthenticationProvider>;

            return providers?.FirstOrDefault(p =>
                string.Equals(p.GetType().Name, "DefaultAuthenticationProvider", StringComparison.Ordinal));
        }

        /// <summary>
        /// The mapping for a Bonfire <b>sub-profile</b>, or null for anything else.
        ///
        /// <para>
        /// A master maps to itself, and returning it here would let a master account be
        /// opened with its profile PIN — which is a decision for the settings that govern
        /// the master, not a side effect of this lookup. So the mapping is only returned
        /// when the user is genuinely somebody's sub-profile.
        /// </para>
        /// </summary>
        private static ProfileMapping? FindSubProfile(Guid? userId)
        {
            if (userId == null || userId == Guid.Empty) return null;

            // Read once. Jellyfin replaces the whole configuration object when an
            // administrator saves plugin settings, so a second read could land on a
            // different instance than the first.
            var config = Plugin.Instance?.Configuration;
            if (config?.Mappings == null) return null;

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId.Value);
            if (mapping == null) return null;
            if (mapping.MasterUserId == mapping.ProfileUserId) return null;   // a master, not a sub-profile

            return mapping;
        }

        /// <summary>
        /// The only failure this provider produces. Deliberately one message for every
        /// case: "wrong PIN", "no such profile" and "not ours" must be indistinguishable
        /// from outside, or the response enumerates which accounts are Bonfire profiles.
        /// </summary>
        private static AuthenticationException Decline()
            => new AuthenticationException("Invalid username or password entered.");
    }
}
