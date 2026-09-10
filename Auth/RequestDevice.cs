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
    /// id this household has signed in on", which is an ANDROID_ID or a browser's random
    /// token. A PIN is the only thing here that is a secret.</para>
    /// </summary>
    public static class RequestDevice
    {
        private static readonly AsyncLocal<string?> _current = new();

        /// <summary>
        /// The current request's device id, or null when there is no request or it carried no
        /// <c>DeviceId</c>. Null must always be treated as "unknown device", never as a pass.
        /// </summary>
        public static string? Current => _current.Value;

        /// <summary>
        /// Records the device id for the rest of this request. Called by the middleware, and
        /// by harnesses that need to stand in for one.
        /// </summary>
        public static void Capture(string? deviceId)
        {
            _current.Value = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        }
    }
}
