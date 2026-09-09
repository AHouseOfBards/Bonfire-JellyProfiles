using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Authentication;
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

        public BonfirePinAuthenticationProvider(ILogger<BonfirePinAuthenticationProvider> logger)
        {
            _logger = logger;
        }

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
        /// Reached only when Jellyfin could not resolve the username to a user. We cannot
        /// identify a profile from a name alone yet — that arrives with the name-stripping
        /// work — so decline and let another provider answer.
        /// </summary>
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
            => Authenticate(username, password, null);

        public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
        {
            if (resolvedUser == null) throw Decline();

            var mapping = FindSubProfile(resolvedUser.Id);
            if (mapping == null) throw Decline();

            // A profile with no PIN opens with an empty box. Anything typed into it is
            // still wrong — accepting any input would make "no PIN" mean "any PIN", and
            // somebody testing whether the box does something would be let in.
            if (string.IsNullOrEmpty(mapping.PinHash))
            {
                if (!string.IsNullOrEmpty(password)) throw Decline();

                _logger.LogInformation(
                    "ProfilesPlugin: profile {ProfileId} opened on a client with no PIN set.",
                    mapping.ProfileUserId);
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
