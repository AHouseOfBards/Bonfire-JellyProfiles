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
    /// 2. <b>Decline anything that is not a Bonfire sub-profile, immediately.</b> When a
    /// user has no <c>AuthenticationProviderId</c>, Jellyfin tries every enabled provider
    /// in turn and accepts the first success, so this method is reachable for accounts
    /// that have nothing to do with Bonfire. A wrong success here is an authentication
    /// bypass on somebody else's account.
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
        /// Off unless an administrator has turned the feature on. A disabled provider is
        /// filtered out before <c>GetAuthenticationProviders</c> matches on id, so any
        /// profile still bound to it falls back to Jellyfin's own provider and its
        /// unknowable random password — which is exactly the state before this existed.
        /// </summary>
        public bool IsEnabled =>
            Plugin.Instance?.Configuration?.EnableClientPinLogin == true && !Plugin.IsPanicDisabled;

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
            // it is only true of the one account that set the server up. Logan's server has
            // forty masters and thirty-nine of them administer nothing, so the rule was
            // wrong for almost every user of it. Administrator accounts are allowed too, by
            // his decision, with a warning under the PIN field.
            if (mapping == null)
            {
                var master = FindMasterWithPin(resolvedUser.Id);
                if (master == null) throw Decline();

                // The PIN first, then the account's real password. Jellyfin binds a user to
                // exactly one provider — GetAuthenticationProviders filters on
                // AuthenticationProviderId — so a master bound to this one would otherwise
                // LOSE its password everywhere, web included, and be left with four digits
                // in front of an account that may administer the server. Delegating is what
                // makes both work at once, and it is also the safety net: if anything here
                // is wrong, the real password still gets you in.
                if (PinHasher.Verify(password, master.PinHash) == PinHasher.PinResult.Match)
                {
                    _logger.LogInformation(
                        "ProfilesPlugin: master account {UserId} entered by PIN on a client.",
                        master.MasterUserId);
                    return Task.FromResult(new ProviderAuthenticationResult { Username = resolvedUser.Username });
                }

                return DelegateToJellyfin(resolvedUser, password);
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
                // Jellyfin counts the failure and applies its own lockout, because it is the
                // one that sees the AuthenticationException.
                throw Decline();
            }

            _logger.LogInformation(
                "ProfilesPlugin: profile {ProfileId} entered by PIN on a client.",
                mapping.ProfileUserId);

            return Task.FromResult(new ProviderAuthenticationResult { Username = resolvedUser.Username });
        }

        /// <summary>
        /// Jellyfin calls this when an administrator changes the account password. Bonfire
        /// PINs are changed through the plugin, and writing one here would silently create
        /// a second place a PIN lives. Refused rather than ignored, so a caller is told
        /// nothing happened instead of believing it did.
        /// </summary>
        public Task ChangePassword(User user, string newPassword)
            => throw new AuthenticationException(
                "A Bonfire profile's PIN is changed in Bonfire, not as an account password.");

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
            var providers = _services?.GetService(typeof(IEnumerable<IAuthenticationProvider>))
                as IEnumerable<IAuthenticationProvider>;

            var jellyfins = providers?.FirstOrDefault(p =>
                string.Equals(p.GetType().Name, "DefaultAuthenticationProvider", StringComparison.Ordinal));

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
