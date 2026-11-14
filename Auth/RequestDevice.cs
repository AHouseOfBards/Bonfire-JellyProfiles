using System.Threading;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// The device id of the request being served, for the one caller that cannot be handed it.
    ///
    /// <para><b>Why this exists.</b> <c>IAuthenticationProvider.Authenticate</c> gets a
    /// username, a password and a user, nothing about the request. Bonfire also needs to know
    /// whether a PIN-less profile is being opened from a device its household uses.</para>
    ///
    /// <para><b>Why an AsyncLocal, not IHttpContextAccessor.</b> The accessor exists only if
    /// the host registered it, which we cannot assume across two Jellyfin major versions.
    /// <see cref="ProfilesIndexMiddleware"/> runs on every request, so it records this at the
    /// top of the pipeline and the value flows into everything the request awaits.</para>
    ///
    /// <para><b>Not a credential.</b> A device id is client-supplied and can be forged by
    /// anyone who knows one. It raises entering a PIN-less profile from "know the username",
    /// which Bonfire itself publishes, to "know a device id this household has signed in
    /// on". On Android that is an ANDROID_ID. In a browser it is not random: jellyfin-web
    /// (release-10.11.z and master, apphost.js generateDeviceId) uses
    /// <c>btoa(userAgent + "|" + Date.now())</c>, so a known browser and rough first-use time
    /// give enumerable candidates. Unlisted, not secret. Only a PIN is secret.</para>
    /// </summary>
    public static class RequestDevice
    {
        private static readonly AsyncLocal<string?> _current = new();
        private static readonly AsyncLocal<string?> _client = new();
        private static readonly AsyncLocal<bool?> _local = new();

        /// <summary>
        /// The current request's device id, or null when there is no request or no
        /// <c>DeviceId</c>. Null means "unknown device", never a pass.
        /// </summary>
        public static string? Current => _current.Value;

        /// <summary>
        /// The current request's app name (<c>Client</c>), or null. Client-supplied. Used only
        /// to refuse a profile sign-in on an app that gets the picker instead
        /// (<see cref="ProxySwitcher"/>), where a forged name gains nothing.
        /// </summary>
        public static string? Client => _client.Value;

        /// <summary>
        /// Records the device id for the rest of this request. Called by the middleware and
        /// by harnesses.
        /// </summary>
        public static void Capture(string? deviceId)
        {
            _current.Value = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        }

        /// <summary>Records the device id and the app name together.</summary>
        public static void Capture(string? deviceId, string? client)
        {
            Capture(deviceId);
            _client.Value = string.IsNullOrWhiteSpace(client) ? null : client.Trim();
        }

        /// <summary>
        /// Whether Jellyfin places the current request on the local network, after its
        /// known-proxy rules (<see cref="ClientAddress"/>). False when there is no request or
        /// it cannot be decided: unknown is never home (R-1334).
        /// </summary>
        public static bool IsLocal => _local.Value == true;

        /// <summary>Records the device id, the app name and whether the request is from the local network.</summary>
        public static void Capture(string? deviceId, string? client, bool local)
        {
            Capture(deviceId, client);
            _local.Value = local;
        }

        private static readonly AsyncLocal<SignInNote?> _signIn = new();

        /// <summary>
        /// How this request is signing somebody in, or null when it is not a sign-in.
        /// <para>
        /// A holder, not a value: an AsyncLocal set inside the provider does not flow back to
        /// the middleware, but a field on a shared object does. The session listener sees it
        /// too, because Jellyfin raises SessionStarted through Task.Run
        /// (EventHelper.QueueEventIfNotNull, release-10.11.z), which carries this request's
        /// execution context.
        /// </para>
        /// </summary>
        public static SignInNote? SignIn => _signIn.Value;

        /// <summary>Starts a sign-in note for the rest of this request.</summary>
        public static SignInNote BeginSignIn(bool byPin = false)
        {
            var note = new SignInNote { ByPin = byPin };
            _signIn.Value = note;
            return note;
        }

        /// <summary>
        /// Whether this sign-in may make its device known. True when nothing noted otherwise.
        /// <para>
        /// <b>No note means a password sign-in, on purpose.</b> The middleware starts a note
        /// only for POSTs under <c>/Users</c>. SSO signs in through its own endpoint
        /// (<c>POST /sso/OID/Auth/{provider}</c>, which calls <c>AuthenticateDirect</c>) and
        /// has no note; that is a real identity-provider sign-in, as good as a password here,
        /// so the device is recorded. A PIN, empty-box or switch sign-in always has a note
        /// and marks it, so only that kind is refused. Written as "not marked by PIN" so a
        /// missing note cannot read as "refuse".
        /// </para>
        /// </summary>
        public static bool MayMakeDeviceKnown() => SignIn?.ByPin != true;

        /// <summary>
        /// Starts a note for a Quick Connect authorize. Jellyfin makes the session inside the
        /// approver's request, so the listener sees this note. A code approved by a profile
        /// vouches for a device the household has never signed in on, and must not make it
        /// known (R2-01-01).
        /// </summary>
        public static void BeginQuickConnectAuthorize()
        {
            _signIn.Value = new SignInNote { QuickConnect = true };
        }

        /// <summary>True when the current request is a Quick Connect authorize.</summary>
        public static bool IsQuickConnectAuthorize => SignIn?.QuickConnect == true;

        /// <summary>Marks the current sign-in as one a PIN, an empty box or a switch let in.</summary>
        public static void NoteNotByPassword()
        {
            var note = _signIn.Value;
            if (note != null) note.ByPin = true;
        }
    }

    /// <summary>What a sign-in request turned out to be. See <see cref="RequestDevice.SignIn"/>.</summary>
    public sealed class SignInNote
    {
        private volatile bool _byPin;

        /// <summary>True when this note is a Quick Connect authorize, not a sign-in request.</summary>
        public bool QuickConnect { get; set; }

        /// <summary>
        /// True when a PIN, a PIN-less profile or a switch let this sign-in in, not a
        /// password. Such a sign-in may refresh a device's record, never make one or make a
        /// device known: PINs work only where a password was used.
        /// </summary>
        public bool ByPin
        {
            get => _byPin;
            set => _byPin = value;
        }
    }
}
