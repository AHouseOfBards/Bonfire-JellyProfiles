using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Profiles.Configuration;
using ProfilesBaseController = Jellyfin.Profiles.Controllers.ProfilesBaseController;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Lets a Bonfire sub-profile be entered with its PIN from any Jellyfin client.
    ///
    /// <para>
    /// Native clients (Android TV, Roku, Swiftfin) never load the web client, so none of our script
    /// runs there. What they have is a login screen with one password box. This turns it into the PIN
    /// prompt: pick a profile, type the PIN, and Jellyfin's own authentication does the rest.
    /// </para>
    ///
    /// <para>
    /// The PIN is never stored as a Jellyfin password. It stays a Bonfire secret in our own PBKDF2
    /// hash, checked here, so there is no account credential to steal.
    /// </para>
    ///
    /// <para><b>Three rules this file must keep.</b></para>
    ///
    /// <para>
    /// 1. <b>Implement every member implicitly, never explicitly.</b> Jellyfin 12.0 removed
    /// <c>HasPassword</c> from <see cref="IAuthenticationProvider"/>; 10.11 still declares it. We ship
    /// one net9 assembly compiled against 10.11, so on 12.0 <c>HasPassword</c> is just an extra public
    /// method. As an explicit implementation (<c>bool IAuthenticationProvider.HasPassword</c>) the
    /// runtime would look for an interface member that does not exist on 12.0 and the type would fail
    /// to load, taking authentication down for every user.
    /// </para>
    ///
    /// <para>
    /// 2. <b>Never succeed for an account that is not a Bonfire profile.</b> When a user has no
    /// <c>AuthenticationProviderId</c>, Jellyfin tries every enabled provider and accepts the first
    /// success, so this method is reachable for unrelated accounts. A wrong success is an
    /// authentication bypass. The only thing done for such an account is to hand its password to
    /// Jellyfin's own provider, and only when it is bound here (<see cref="IsBoundHere"/>).
    /// </para>
    ///
    /// <para>
    /// 3. <b>Nobody bound to this provider may be left without a way in.</b> Jellyfin offers a bound
    /// account this provider and nothing else, and maps it to <c>InvalidAuthProvider</c> when this one
    /// reports itself disabled. So every refusal that is not about a PIN falls through to the
    /// account's real password.
    /// </para>
    /// </summary>
    public class BonfirePinAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
    {
        private readonly ILogger<BonfirePinAuthenticationProvider> _logger;

        /// <param name="services">
        /// Resolved from, not injected as, <c>IUserManager</c>. Jellyfin's <c>UserManager</c> constructor
        /// takes <c>IEnumerable&lt;IAuthenticationProvider&gt;</c>, so asking for the user manager here
        /// would be a dependency cycle and every login on the server would fail. The service provider is
        /// only asked for it during a request, after both exist.
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
        /// A disabled provider does not fall back to Jellyfin's own. <c>GetAuthenticationProviders</c>
        /// filters disabled providers out, and when nothing matches the user's bound id the user gets
        /// <c>InvalidAuthProvider</c>, which refuses everything. So turning the feature off re-points every
        /// bound account first (<see cref="ProfilesBootstrapTask.ReconcileAuthProvidersNow"/>).
        /// </para>
        /// <para>
        /// The emergency disable does not touch this. It used to, and a master bound here for its PIN then
        /// could not sign in with its password at all, during the one event that exists because the web
        /// interface has become hard to use. While it is tripped,
        /// <see cref="Authenticate(string, string, User)"/> accepts no PIN and passes every bound master's
        /// password to Jellyfin.
        /// </para>
        /// </summary>
        public bool IsEnabled =>
            Plugin.Instance?.Configuration?.EnableClientPinLogin == true;

        /// <summary>
        /// Whether the client should show a password box at all.
        /// <para>
        /// Jellyfin 10.11 only. 12.0 removed this from the interface and hardcoded
        /// <c>UserDto.HasPassword</c> to true, so on 12.0 a profile with no PIN is still prompted and the
        /// viewer submits an empty box. Kept implicit so 10.11 still gets the better behavior (rule 1 in
        /// the class summary).
        /// </para>
        /// </summary>
        public bool HasPassword(User user)
        {
            var mapping = FindSubProfile(Controllers.ProfilesBaseController.ReadConfigSnapshot(), user?.Id);

            // Not ours: say it has a password. With a blank AuthenticationProviderId this can be the
            // method Jellyfin happens to ask, and answering false for a real account would hide its
            // password box.
            if (mapping == null) return true;

            return !string.IsNullOrEmpty(mapping.PinHash);
        }

        /// <summary>
        /// Translates the name a household gave a profile back into the account, when Jellyfin could not
        /// resolve the username itself. That is the normal case on a sign-in screen, which shows the
        /// household's name for a profile, not the system username.
        ///
        /// <para><b>Jellyfin never calls this overload.</b> This provider implements
        /// <see cref="IRequiresResolvedUser"/>, and <c>UserManager.AuthenticateWithProvider</c> (10.11.5
        /// line 852, unchanged on master) calls the three-argument form on any provider that does, with a
        /// null user when the name did not resolve. That form refused a null user outright, so from
        /// 1.6.1.11 to 1.6.3.3 every sub-profile chosen from a sign-in screen was refused. The harness
        /// called this overload directly and passed. It now goes through Jellyfin's own dispatch, copied
        /// from that file (tests/cs/clientpin, "the way Jellyfin calls it").</para>
        ///
        /// <para>A profile is created as <c>&lt;master&gt;_&lt;name&gt;</c> so two households can both have
        /// a "kids". That system username is what the web switcher, the audit log and Jellyfin's dashboard
        /// show. Only the sign-in screen shows the short name, so only this path translates it back.</para>
        ///
        /// <para>The device decides which "kids", never the name. The household is resolved from the
        /// device the request came in on and only its profiles are considered, so a collision never
        /// arises. A device we do not recognize has no household and is declined.</para>
        ///
        /// <para>Jellyfin then trusts the username we return and looks it up itself
        /// (<c>UserManager.AuthenticateUser</c>, the <c>user is null</c> branch). That branch excludes
        /// <c>DefaultAuthenticationProvider</c>, which is why this works for us and not for Jellyfin's
        /// own.</para>
        /// </summary>
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();
            if (config?.Mappings == null || !config.EnableClientPinLogin) throw Decline();

            // Only reaches a sub-profile, and the emergency disable turns PIN entry off.
            if (Plugin.IsOff) throw Decline();

            // The id this sign-in came from, resolved to the television's recorded one when the app
            // derived it from the name typed (DeviceRegistry.ResolveSignInDevice).
            var sent = RequestDevice.Current;
            var deviceId = DeviceRegistry.ResolveSignInDevice(config, sent, username);
            var master = DeviceRegistry.FindMaster(config, deviceId);
            if (master == Guid.Empty)
            {
                _logger.LogInformation(
                    "ProfilesPlugin: sign-in as {Name} from device {DeviceId}, which no household has "
                    + "signed in on, so there is no way to tell which profile was meant.",
                    username,
                    string.IsNullOrEmpty(sent) ? "(none sent)" : sent);
                throw Decline();
            }

            // Only this household, and never the master: it keeps its real username and must not become
            // PIN-openable as a side effect of a name lookup.
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

            // The device as resolved from the name the app hashed ("Family", not the system username,
            // which the app never saw).
            return AuthenticateResolved(password, user, config, deviceId);
        }

        public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
        {
            // No user means Jellyfin could not resolve the name ("kids" off a sign-in screen). This is
            // the only overload Jellyfin calls, so the translation is reached from here. It resolves within
            // the household of the asking device.
            if (resolvedUser == null) return Authenticate(username, password);

            // One snapshot for the whole decision (ReadConfigSnapshot).
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();

            // A name typed in full ("Bard_Family") is hashed into the device id the same way.
            var deviceId = DeviceRegistry.ResolveSignInDevice(config, RequestDevice.Current, username);
            return AuthenticateResolved(password, resolvedUser, config, deviceId);
        }

        /// <summary>
        /// The decision for a resolved account, against one configuration snapshot and the device the
        /// sign-in came from (already resolved by the caller).
        /// </summary>
        private Task<ProviderAuthenticationResult> AuthenticateResolved(
            string password, User resolvedUser, PluginConfiguration? config, string? deviceId)
        {
            var mapping = FindSubProfile(config, resolvedUser.Id);

            // A master with a PIN of its own keeps a real Jellyfin password and must go on being able to
            // use it. This used to refuse masters because "masters administer the server", which is true
            // only of the account that set the server up. Administrator accounts are allowed, with a warning
            // under the PIN field.
            if (mapping == null)
            {
                var master = FindMasterWithPin(config, resolvedUser.Id);

                // The PIN first, then the account's real password. Jellyfin binds a user to exactly one
                // provider (GetAuthenticationProviders filters on AuthenticationProviderId), so a master bound
                // here would otherwise lose its password everywhere, web included, and be left with four digits
                // in front of an account that may administer the server. Delegating makes both work, and is the
                // safety net if anything here is wrong.
                //
                // Only something PIN-shaped is tried as a PIN and counted against the throttle. A master's
                // ordinary password sign-in comes through here too and must never use up the PIN allowance.
                //
                // And only on a device this household has signed in on with a password (R-1344). This branch had
                // no device check, so an administrator's four digits opened the server from any address.
                // Elsewhere the PIN is skipped, not refused: the password below still works.
                if (master != null && !Plugin.IsOff && LooksLikePin(password)
                    && !KnownToHousehold(config, deviceId, resolvedUser.Id))
                {
                    _logger.LogInformation(
                        "ProfilesPlugin: a PIN for master account {UserId} from device {DeviceId}, which it has "
                        + "not signed in on with its password; only the password is being checked.",
                        resolvedUser.Id,
                        string.IsNullOrEmpty(deviceId) ? "(none sent)" : deviceId);
                }
                else if (master != null && !Plugin.IsOff && LooksLikePin(password))
                {
                    if (!BeginPinAttempt(resolvedUser.Id))
                    {
                        _logger.LogWarning(
                            "ProfilesPlugin: too many wrong PINs for master account {UserId}; PIN sign-in is "
                            + "paused for it, and only its password is being checked.",
                            resolvedUser.Id);
                        PinLockout.Raise(resolvedUser.Id, resolvedUser.Id.ToString("N"), _services, null, _logger);
                    }
                    else if (PinHasher.Verify(password, master.PinHash) == PinHasher.PinResult.Match)
                    {
                        EndPinAttempt(resolvedUser.Id);
                        UpgradeLegacyHash(master, password);
                        _logger.LogInformation(
                            "ProfilesPlugin: master account {UserId} entered by PIN on a client.",
                            master.MasterUserId);
                        return NotByPassword(resolvedUser);
                    }
                }

                // A master with no PIN (never set, cleared, or its last profile deleted) that is still bound
                // here. This used to refuse, and since Jellyfin offers a bound account nothing but this provider,
                // clearing your PIN locked your password out everywhere until the binding was repaired by hand.
                // Rule 3: the password always gets through.
                if (master == null && !IsBoundHere(resolvedUser)) throw Decline();

                return DelegateToJellyfin(resolvedUser, password);
            }

            // Every PIN below belongs to a sub-profile, which has no password to fall back to. The
            // emergency disable turns PIN entry off.
            if (Plugin.IsOff) throw Decline();

            // A main account turned off in Dashboard -> Users shuts its profiles too (R-1135). Jellyfin
            // checks the account being signed into, which is the profile's, so a PIN went on opening them. A
            // master that can't be found is not "disabled": this only ever removes access.
            if (_services?.GetService(typeof(IUserManager)) is IUserManager accounts
                && accounts.GetUserById(mapping.MasterUserId) is User mainAccount
                && Jellyfin.Data.UserEntityExtensions.HasPermission(mainAccount, Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a sign-in to profile {ProfileId}, whose main account is turned off.",
                    mapping.ProfileUserId);
                throw Decline();
            }

            // An app that gets the picker signs in as the main account and picks there. A profile signed
            // in directly would skip the picker and its keypad and leave a profile token the app could keep,
            // which is the Roku problem (#40) again.
            if (ProxyClients.IsEnabled(config, RequestDevice.Client))
            {
                _logger.LogInformation(
                    "ProfilesPlugin: refused a direct sign-in to profile {ProfileId} on {Client}, which "
                    + "gets the profile picker instead.",
                    mapping.ProfileUserId,
                    RequestDevice.Client);
                throw Decline();
            }

            // A profile limited to particular devices is limited here too. The switcher and verify-pin
            // enforced this and the sign-in-screen path did not, so a profile restricted to the living room
            // television could be opened with its PIN from any phone on the internet.
            //
            // The same evaluator as the other two call sites, not a copy: a copy is where the two halves of
            // a rule drift apart.
            var access = Controllers.ProfilesBaseController.EvaluateDeviceRestriction(
                mapping,
                deviceId,
                config?.KnownDevices,
                config?.Mappings);

            if (access != Controllers.ProfilesBaseController.DeviceAccess.NotRestricted
                && access != Controllers.ProfilesBaseController.DeviceAccess.Allowed)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a client sign-in to profile {ProfileId} from device "
                    + "{DeviceId} ({Reason}).",
                    mapping.ProfileUserId,
                    string.IsNullOrEmpty(deviceId) ? "(none sent)" : deviceId,
                    access);

                // Declined with the message every other refusal carries, so a response cannot say which
                // profiles are device-restricted.
                throw Decline();
            }

            // From outside the home network, a profile with no PIN or with a device list does not open
            // here (R-1334). Both rest on a device id the client supplies, and the device-list and
            // known-device checks below pass for anybody who has seen a household's id: "PIN login on other
            // apps" on was enough to open a PIN-less profile from the internet. A PIN stays the credential,
            // so a profile with one and no list is untouched. The administrator may allow it from anywhere
            // (ClientPinLoginFromAnywhere); the default is home. Declined with the usual message.
            if ((string.IsNullOrEmpty(mapping.PinHash)
                    || access != Controllers.ProfilesBaseController.DeviceAccess.NotRestricted)
                && config?.ClientPinLoginFromAnywhere != true
                && !RequestDevice.IsLocal)
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a client sign-in to profile {ProfileId}, which has {Why} and was "
                    + "asked for from outside the home network.",
                    mapping.ProfileUserId,
                    string.IsNullOrEmpty(mapping.PinHash) ? "no PIN" : "a device list");
                throw Decline();
            }

            // A profile with no PIN opens with an empty box, but only on a device the household has
            // signed in on.
            //
            // The empty box alone was a hole, found by trying it on a live server. This provider is reached
            // by anything that can POST /Users/AuthenticateByName and sees no device and no network, and
            // sub-profile usernames are predictable (`<master>_<profile>`) and published by our
            // /Users/Public injection. On an internet-facing server a PIN-less sub-profile was an account a
            // stranger could enter by guessing a username. Before client PIN login existed that was
            // impossible, because the Jellyfin password is a random 64 characters that is discarded.
            //
            // Requiring a known device keeps the wanted behavior (walk up to the family television, choose a
            // profile, type nothing) while the same request from elsewhere fails. It is not a boundary: it
            // raises entry from "know the username" to "know a device id this household has signed in on".
            // A PIN remains the only real credential.
            if (string.IsNullOrEmpty(mapping.PinHash))
            {
                // "No PIN" must not quietly become "any PIN": somebody testing whether the box does anything
                // would be let in.
                if (!string.IsNullOrEmpty(password)) throw Decline();

                if (!KnownToHousehold(config, deviceId, mapping.MasterUserId))
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
                return NotByPassword(resolvedUser);
            }

            // Nothing that is not PIN-shaped can match, so refuse it without spending a key derivation
            // or an attempt from the allowance.
            if (!LooksLikePin(password)) throw Decline();

            // A PIN only works on a device the household has signed in on with a password (R-1344).
            // It used to stand alone, so a profile typed by its full system name ("Bard_Family") was four
            // digits from anywhere. Checked before the allowance, so a stranger's guesses cannot pause the
            // PIN for the household's own television.
            if (!KnownToHousehold(config, deviceId, mapping.MasterUserId))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: refused a PIN for profile {ProfileId} from device {DeviceId}, which this "
                    + "household has not signed in on.",
                    mapping.ProfileUserId,
                    string.IsNullOrEmpty(deviceId) ? "(none sent)" : deviceId);
                throw Decline();
            }

            // Jellyfin's own lockout does not cover this: LoginAttemptsBeforeLockout is null on every
            // account Bonfire creates, and null means "never lock". A four-digit PIN behind no limit is ten
            // thousand requests from anywhere. Counted per account, not per address, so more addresses buy
            // nothing. The price is that hammering a profile's name can pause PIN sign-in for it on other
            // apps for a while; the web switcher is limited separately.
            if (!BeginPinAttempt(mapping.ProfileUserId))
            {
                _logger.LogWarning(
                    "ProfilesPlugin: too many wrong PINs for profile {ProfileId}; PIN sign-in on other "
                    + "apps is paused for it.",
                    mapping.ProfileUserId);
                PinLockout.Raise(mapping.ProfileUserId, mapping.ProfileUserId.ToString("N"), _services, null, _logger);
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
            UpgradeLegacyHash(mapping, password);

            _logger.LogInformation(
                "ProfilesPlugin: profile {ProfileId} entered by PIN on a client.",
                mapping.ProfileUserId);

            return NotByPassword(resolvedUser);
        }

        /// <summary>
        /// Jellyfin calls this when a password is changed or reset, on the provider the account is bound
        /// to.
        /// <para>
        /// For a sub-profile it is refused, not ignored: Bonfire PINs are changed through the plugin, and
        /// writing one here would create a second place a PIN lives while the caller believes it worked.
        /// </para>
        /// <para>
        /// For a master it is Jellyfin's to do. A master bound here for its PIN still owns a real
        /// password, and refusing this left it unable to change that password, or have an administrator
        /// reset it, for as long as the binding lasted.
        /// </para>
        /// </summary>
        public Task ChangePassword(User user, string newPassword)
        {
            if (user != null && FindSubProfile(Controllers.ProfilesBaseController.ReadConfigSnapshot(), user.Id) == null)
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

        /// <summary>
        /// True if <paramref name="householdId"/> has signed in on this device with its password: the
        /// record <see cref="BonfireSessionListener"/> writes. A device id is client-supplied, so this
        /// raises the bar from "anywhere" to "an id this household uses"; the PIN is still the credential.
        /// </summary>
        private static bool KnownToHousehold(PluginConfiguration? config, string? deviceId, Guid householdId)
            => DeviceRegistry.IsOwnedBy(config, deviceId, householdId);

        /// <summary>
        /// Success for a sign-in a PIN or an empty box let in, noted on the request so the session it
        /// starts never makes its device known (see BonfireSessionListener).
        /// </summary>
        private static Task<ProviderAuthenticationResult> NotByPassword(User user)
        {
            RequestDevice.NoteNotByPassword();
            return Task.FromResult(new ProviderAuthenticationResult { Username = user.Username });
        }

        /// <summary>A PIN as Bonfire accepts one: four to eight digits.</summary>
        private static bool LooksLikePin(string? value)
            => !string.IsNullOrEmpty(value)
               && value.Length >= 4 && value.Length <= 8
               && value.All(char.IsAsciiDigit);

        /// <summary>
        /// Replaces an old unsalted hash with a salted one after its PIN has just matched, as the web path
        /// does. <paramref name="snapshot"/> is a copy, so the live row is found again under the lock and
        /// changed only if it still holds the hash that matched. Hashed outside the lock; a failure here
        /// never refuses the sign-in.
        /// </summary>
        private void UpgradeLegacyHash(ProfileMapping snapshot, string pin)
        {
            try
            {
                var old = snapshot.PinHash;
                if (!PinHasher.IsLegacy(old)) return;
                var upgraded = PinHasher.Hash(pin);
                lock (ProfilesBaseController.ConfigLock)
                {
                    var live = Plugin.Instance?.Configuration?.Mappings
                        .FirstOrDefault(m => m.ProfileUserId == snapshot.ProfileUserId);
                    if (live == null || live.PinHash != old) return;
                    live.PinHash = upgraded;
                    Plugin.Instance?.SaveConfiguration();
                }
                _logger.LogInformation(
                    "ProfilesPlugin: Upgraded legacy PIN hash for profile {Id} to PBKDF2.", snapshot.ProfileUserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: Could not upgrade the PIN hash for profile {Id}.", snapshot.ProfileUserId);
            }
        }

        /// <summary>
        /// Counts one PIN attempt against this account and says whether it may be made: the account's one
        /// allowance, shared with the web (<see cref="Controllers.PinBudget"/>). Counted before the PIN is
        /// checked, so parallel guesses cannot all pass.
        /// </summary>
        internal static bool BeginPinAttempt(Guid userId) => Controllers.PinBudget.TryBegin(userId);

        /// <summary>A PIN was right: the account's allowance starts again.</summary>
        internal static void EndPinAttempt(Guid userId) => Controllers.PinBudget.End(userId);

        /// <summary>
        /// Whether Jellyfin has this account bound to this provider. Jellyfin then offers it no other, so
        /// a refusal here is a refusal everywhere.
        /// </summary>
        private bool IsBoundHere(User user)
            => string.Equals(user.AuthenticationProviderId, GetType().FullName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The mapping row of a master that has set a PIN, or null.
        /// <para>
        /// A master's own row has <c>MasterUserId == ProfileUserId</c> and is written lazily, the first
        /// time switcher preferences are saved. Most households have none, and a master with no PIN is not
        /// a candidate here.
        /// </para>
        /// </summary>
        private static ProfileMapping? FindMasterWithPin(PluginConfiguration? config, Guid userId)
        {
            if (config?.Mappings == null || userId == Guid.Empty) return null;

            var row = config.Mappings.FirstOrDefault(m =>
                m.MasterUserId == userId
                && m.ProfileUserId == userId
                && !string.IsNullOrEmpty(m.PinHash));

            // Only an account other profiles actually point at: a stray self-row on an account with no
            // profiles must not become a PIN-openable login.
            if (row == null) return null;
            if (!config.Mappings.Any(m => m.MasterUserId == userId && m.ProfileUserId != userId)) return null;

            return row;
        }

        /// <summary>
        /// Hands the credential to Jellyfin's own provider, so an account bound to this one keeps working
        /// with its real password.
        /// <para>
        /// <c>DefaultAuthenticationProvider</c> is registered as an <c>IAuthenticationProvider</c> in
        /// Jellyfin's container (<c>CoreAppHost.RegisterServices</c>), which is what makes this possible.
        /// Matched on the type's short name so a harness can stand in for it, and never as "whichever
        /// provider is not ours": the other one registered there is <c>InvalidAuthProvider</c>, which
        /// refuses everything.
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
        private IAuthenticationProvider? FindJellyfinsProvider() => JellyfinsProvider(_services);

        /// <summary>
        /// Jellyfin's own password provider, from a service container. Also used to check a password
        /// without <c>UserManager.AuthenticateUser</c>, which counts a wrong one towards the account's
        /// lockout (see <c>AddAccountLink</c>).
        /// </summary>
        internal static IAuthenticationProvider? JellyfinsProvider(IServiceProvider? services)
        {
            var providers = services?.GetService(typeof(IEnumerable<IAuthenticationProvider>))
                as IEnumerable<IAuthenticationProvider>;

            return providers?.FirstOrDefault(p =>
                string.Equals(p.GetType().Name, "DefaultAuthenticationProvider", StringComparison.Ordinal));
        }

        /// <summary>
        /// The mapping for a Bonfire sub-profile, or null for anything else.
        /// <para>
        /// A master maps to itself, and returning it here would let a master account be opened with its
        /// profile PIN. That is a decision for the master's own settings, not a side effect of this lookup.
        /// </para>
        /// </summary>
        private static ProfileMapping? FindSubProfile(PluginConfiguration? config, Guid? userId)
        {
            if (userId == null || userId == Guid.Empty) return null;

            // The caller's snapshot, so a decision is made against one consistent copy.
            if (config?.Mappings == null) return null;

            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId.Value);
            if (mapping == null) return null;
            if (mapping.MasterUserId == mapping.ProfileUserId) return null;   // a master, not a sub-profile

            return mapping;
        }

        /// <summary>
        /// The only failure this provider produces. One message for every case: "wrong PIN", "no such
        /// profile" and "not ours" must be indistinguishable from outside, or the response enumerates
        /// which accounts are Bonfire profiles.
        /// </summary>
        private static AuthenticationException Decline()
            => new AuthenticationException("Invalid username or password entered.");
    }
}
