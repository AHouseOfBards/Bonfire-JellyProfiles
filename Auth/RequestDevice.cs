using System.Threading;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// The device id of the request currently being served, for the one caller that needs
    /// it and cannot be handed it.
    ///
    /// <para><b>Why this exists.</b> <see cref="System.Security.Principal.IIdentity"/> is not
    /// the problem — <c>IAuthenticationProvider.Authenticate</c> is simply given a username,
    /// a password and a user, and nothing about the request they arrived on. Jellyfin has no
    /// reason to pass more, because its own providers check a credential and nothing else.
    /// Bonfire needs one thing more: whether a PIN-less profile is being opened from a
    /// device its household actually uses.</para>
    ///
    /// <para><b>Why an AsyncLocal rather than IHttpContextAccessor.</b> The accessor is only
    /// present if the host registered it, which is not ours to assume across two Jellyfin
    /// major versions. <see cref="ProfilesIndexMiddleware"/> already runs on every request,
    /// so it can record this at the top of the pipeline; a value set there flows into
    /// everything the request goes on to await, which includes authentication.</para>
    ///
    /// <para><b>This is not a credential.</b> A device id is client-supplied and can be
    /// forged by anyone who knows one. It raises entering a PIN-less profile from "know the
    /// username", which is guessable and which Bonfire itself publishes, to "know a device
    /// id this household has signed in on". On Android that is an ANDROID_ID. In a browser it
    /// is NOT random: jellyfin-web (release-10.11.z and master, apphost.js generateDeviceId)
    /// makes it <c>btoa(userAgent + "|" + Date.now())</c>, so anyone who knows the browser
    /// and roughly when it was first used can enumerate candidates. Treat it as unlisted,
    /// not secret. A PIN is the only thing here that is a secret.</para>
    /// </summary>
    public static class RequestDevice
    {
        private static readonly AsyncLocal<string?> _current = new();
        private static readonly AsyncLocal<string?> _client = new();
        private static readonly AsyncLocal<bool?> _local = new();

        /// <summary>
        /// The current request's device id, or null when there is no request or it carried no
        /// <c>DeviceId</c>. Null must always be treated as "unknown device", never as a pass.
        /// </summary>
        public static string? Current => _current.Value;

        /// <summary>
        /// The current request's app name (<c>Client</c>), or null. Client-supplied like the
        /// device id; used only to refuse a profile sign-in on an app that gets the picker
        /// instead (see <see cref="ProxySwitcher"/>), where a forged name gains nothing.
        /// </summary>
        public static string? Client => _client.Value;

        /// <summary>
        /// Records the device id for the rest of this request. Called by the middleware, and
        /// by harnesses that need to stand in for one.
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
        /// Whether Jellyfin places the current request on the local network, after its own
        /// known-proxy rules (<see cref="ClientAddress"/>). False when there is no request
        /// or it could not be decided: unknown is never home (R-1334).
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
        /// A holder, not a value: an AsyncLocal set inside the provider would not flow back
        /// out to the middleware that asked, but a field on an object both can see does. The
        /// session listener sees it too, because Jellyfin raises SessionStarted through
        /// Task.Run (EventHelper.QueueEventIfNotNull, release-10.11.z), which carries this
        /// request's execution context.
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
        /// only for POSTs under <c>/Users</c>. A sign-in that arrives some other way has none:
        /// SSO signs in through its own endpoint (<c>POST /sso/OID/Auth/{provider}</c>, which
        /// calls <c>AuthenticateDirect</c>), and that is a real identity-provider sign-in, as
        /// good as a password for this purpose, so the device is recorded. A sign-in a PIN,
        /// an empty box or a switch let in always starts a note and marks it, so it is the
        /// only kind refused. Written as "not marked by PIN" so that a missing note cannot
        /// read as "refuse".
        /// </para>
        /// </summary>
        public static bool MayMakeDeviceKnown() => SignIn?.ByPin != true;

        /// <summary>
        /// Starts a note for a Quick Connect authorize. Jellyfin makes the session inside the
        /// approver's own request, so the session listener sees it with this note: a code
        /// approved by a profile is the profile vouching for a device the household has never
        /// signed in on, which must not make that device known (R2-01-01).
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
        /// True when a PIN, a PIN-less profile or a profile switch let this sign-in in rather
        /// than a password. Such a sign-in may refresh a device's record, never make one or
        /// make a device known: PINs work only where a password was used.
        /// </summary>
        public bool ByPin
        {
            get => _byPin;
            set => _byPin = value;
        }
    }
}
