using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Jellyfin.Profiles.Configuration;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// A profile authorizing a Quick Connect code is signing a device in as itself, and has to
    /// pass the rules a PIN sign-in on that device would (R-1335).
    ///
    /// <para><b>The hole.</b> <c>POST /QuickConnect/Authorize</c> makes Jellyfin's
    /// <c>QuickConnectManager</c> create the session itself, with <c>AuthenticateDirect</c>, for
    /// the device that asked for the code. That never goes through
    /// <see cref="BonfirePinAuthenticationProvider"/>, so a profile's device list and the apps
    /// that only get the picker were both skipped: a profile on an allowed device could
    /// authorize a code from one that was not.</para>
    ///
    /// <para><b>Which device.</b> The one that asked for the code, not the one authorizing
    /// it. The authorize request names only the code, so the code is matched to its device
    /// from the Initiate answer, which passes through the middleware
    /// (<see cref="Remember"/>). Jellyfin keeps its own table private. A code the middleware
    /// never saw, after a restart or from before this plugin loaded, cannot be judged, so a
    /// profile is refused it only where a rule could have refused it (a device list, or an
    /// app ticked for the picker): ten minutes is as long as a code lives anyway.</para>
    ///
    /// <para><b>Not applied.</b> The household-has-signed-in-here rule. It stops a stranger
    /// who guesses a profile's name; here the profile is already signed in and is the one
    /// choosing. And a main account is never judged: it is a real account that owns its own
    /// devices. Android TV asks for a code with a random device id, so a profile with a device
    /// list cannot authorize one, which is the list doing its job.</para>
    /// </summary>
    public static class QuickConnectAuthorize
    {
        public const string DeviceNotAllowed = "device_not_allowed";

        /// <summary>Jellyfin drops a code ten minutes after it was asked for.</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(11);

        private const int MaxRemembered = 1000;

        private static readonly ConcurrentDictionary<string, (string DeviceId, string Client, DateTime Until)> Codes = new(StringComparer.Ordinal);

        /// <summary>Replaced by tests.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        public static bool IsAuthorizePath(string method, string? path)
            => HttpMethods.IsPost(method)
               && !string.IsNullOrEmpty(path)
               && path.TrimEnd('/').EndsWith("/QuickConnect/Authorize", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads Jellyfin's answer to Initiate and remembers which device asked for which code.
        /// Anything unreadable is ignored: that code is then refused to a profile, never allowed.
        /// </summary>
        public static void Remember(byte[] answer)
        {
            try
            {
                using var doc = JsonDocument.Parse(answer);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                string? Text(string name)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        {
                            return p.Value.GetString();
                        }
                    }

                    return null;
                }

                var code = Text("Code");
                if (string.IsNullOrEmpty(code)) return;
                var now = Now();
                Codes[code] = (Text("DeviceId") ?? string.Empty, Text("AppName") ?? string.Empty, now + Lifetime);
                if (Codes.Count > MaxRemembered) Trim(now);
            }
            catch (JsonException)
            {
            }
        }

        private static void Trim(DateTime now)
        {
            foreach (var pair in Codes.ToArray())
            {
                if (pair.Value.Until <= now) Codes.TryRemove(pair.Key, out _);
            }

            if (Codes.Count <= MaxRemembered) return;
            foreach (var pair in Codes.ToArray().OrderBy(p => p.Value.Until).Take(Codes.Count - MaxRemembered + MaxRemembered / 8))
            {
                Codes.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>403, with the code an app can read, and the reason in words.</summary>
        public static async System.Threading.Tasks.Task WriteRefusalAsync(HttpContext context, string message)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status403Forbidden;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers[Controllers.ErrorCodes.Header] = DeviceNotAllowed;
            response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "no-store";
            await response.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// Why <paramref name="userId"/> may not authorize <paramref name="code"/>, or null.
        /// A main account, and anyone who is not a profile, is never refused.
        /// </summary>
        public static string? Refusal(PluginConfiguration? config, Guid userId, string? code)
        {
            if (Plugin.IsOff || config?.Mappings == null) return null;
            var mapping = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId && m.MasterUserId != m.ProfileUserId);
            if (mapping == null) return null;

            if (string.IsNullOrEmpty(code)
                || !Codes.TryGetValue(code, out var asked)
                || asked.Until <= Now())
            {
                // Its device is unknown. Refused only where some rule could have refused it:
                // a profile with a device list, or any app ticked for the picker. Otherwise
                // nothing here applies, and refusing would break an ordinary household's
                // Quick Connect for the ten minutes after a restart.
                var unjudged = Controllers.ProfilesBaseController.EvaluateDeviceRestriction(
                    mapping, null, config.KnownDevices, config.Mappings);
                return unjudged != Controllers.ProfilesBaseController.DeviceAccess.NotRestricted || ProxyClients.AnyEnabled(config)
                    ? "That device can't be checked against this profile."
                    : null;
            }

            // The same two rules the PIN sign-in applies, from the same functions.
            if (ProxyClients.IsEnabled(config, asked.Client))
            {
                return "This profile is not allowed on this device.";
            }

            var access = Controllers.ProfilesBaseController.EvaluateDeviceRestriction(
                mapping, asked.DeviceId, config.KnownDevices, config.Mappings);
            if (access != Controllers.ProfilesBaseController.DeviceAccess.NotRestricted
                && access != Controllers.ProfilesBaseController.DeviceAccess.Allowed)
            {
                return Controllers.ProfilesBaseController.DeviceAccessMessage(access);
            }

            return null;
        }
    }
}
