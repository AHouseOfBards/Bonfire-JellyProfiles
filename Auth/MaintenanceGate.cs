using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Jellyfin.Data.Queries;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Closed for maintenance: every request from an account that is not an administrator is
    /// answered with 503 before Jellyfin sees it.
    ///
    /// <para><b>Administrators are never turned away</b>, by any path. That is the one rule
    /// that cannot bend: the person who turned it on has to be able to turn it off.</para>
    ///
    /// <para><b>Running streams keep going</b> unless the administrator asked to stop them.
    /// A video or audio request is let through only for the item that account's session is
    /// already playing, and so are its progress and stop reports. Starting something new is
    /// refused: PlaybackInfo, the "playing" report, and a stream of any other item.</para>
    ///
    /// <para>The first version let every <c>/Videos/</c> and <c>/Audio/</c> request through on
    /// the assumption that new playback needs PlaybackInfo. It does not: a direct stream URL
    /// plays without it, and an app that builds one could start anything while closed.</para>
    ///
    /// <para><b>Not a security boundary.</b> An app that sends a progress report for an item
    /// it is not playing can make that item "playing". This keeps a household out during an
    /// upgrade; parental controls are what keep a profile out of what it may not watch.</para>
    ///
    /// <para><b>What still answers:</b> the web client's own files, the public server info,
    /// the sign-in user list, Quick Connect, signing out, the live connection (so the
    /// maintenance message reaches open apps), Bonfire's anonymous routes (so the client can
    /// say why), and the account's own user record (so it knows who it is).</para>
    ///
    /// <para><b>Sign-in</b> is refused by name, before the password is checked: a household
    /// member sees "closed for maintenance", not "wrong password". Quick Connect cannot be
    /// refused that way, since the account is not known until it succeeds; such a session is
    /// then refused on its first request like any other.</para>
    ///
    /// <para><b>Cost when it is off:</b> one field read. When it is on, a token is resolved
    /// to its account once every 30 seconds, not per request.</para>
    ///
    /// <para><b>The emergency disable turns it off</b>, until Jellyfin restarts, the same as
    /// everything else Bonfire does to Jellyfin's requests.</para>
    /// </summary>
    public static class MaintenanceGate
    {
        /// <summary>The <c>X-Bonfire-Error</c> code on every refusal.</summary>
        public const string ErrorCode = "maintenance";

        private static readonly TimeSpan Remember = TimeSpan.FromSeconds(30);
        private const int MaxRemembered = 10_000;
        private const int MaxSignInBody = 64 * 1024;

        private static readonly ConcurrentDictionary<string, (Guid UserId, DateTime Until)> Tokens = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<Guid, (bool Admin, DateTime Until)> Admins = new();

        /// <summary>The maintenance settings while it is on, otherwise null.</summary>
        public static MaintenanceMode? Active()
        {
            if (Plugin.IsPanicDisabled) return null;
            var m = Plugin.Instance?.Configuration?.Maintenance;
            return m != null && m.Enabled ? m : null;
        }

        /// <summary>Drops what is remembered about tokens and accounts. Called when it is turned on or off.</summary>
        public static void Forget()
        {
            Tokens.Clear();
            Admins.Clear();
        }

        // ── Which requests ──────────────────────────────────────────────────────────

        internal enum PathKind
        {
            /// <summary>Never refused.</summary>
            Open,

            /// <summary><c>POST /Users/AuthenticateByName</c>: refused by the name in the body.</summary>
            SignIn,

            /// <summary><c>POST /Users/{id}/Authenticate</c>: refused by the id in the path.</summary>
            SignInById,

            /// <summary>Part of something already playing.</summary>
            Stream,

            /// <summary>Everything else: refused for an account that is not an administrator.</summary>
            Other,
        }

        private static readonly string[] OpenAnywhere =
        {
            "/web/",
            "/System/Info/Public",
            "/System/Ping",
            "/Branding/",
            "/Users/Public",
            "/Users/Me",
            "/Users/AuthenticateWithQuickConnect",
            "/QuickConnect/",
            "/Sessions/Logout",
            "/Startup/",
            "/socket",
            "/embywebsocket",
            "/plugins/profiles/info",
            "/plugins/profiles/panic",
            "/plugins/profiles/profiles.js",
            "/plugins/profiles/i18n/",
            "/plugins/profiles/image/",
            "/plugins/profiles/avatars/",
            "/plugins/profiles/library-art/",
        };

        private static readonly string[] StreamAnywhere =
        {
            "/Videos/",
            "/Audio/",
            "/Sessions/Playing",
        };

        internal static PathKind Classify(string method, string path)
        {
            if (string.IsNullOrEmpty(path)) return PathKind.Open;

            foreach (var open in OpenAnywhere)
            {
                if (path.Contains(open, StringComparison.OrdinalIgnoreCase)) return PathKind.Open;
            }

            if (path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)) return PathKind.Open;

            if (HttpMethods.IsPost(method))
            {
                if (path.EndsWith("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase)) return PathKind.SignIn;
                if (SignInId(path).HasValue) return PathKind.SignInById;
            }

            foreach (var stream in StreamAnywhere)
            {
                if (path.Contains(stream, StringComparison.OrdinalIgnoreCase)) return PathKind.Stream;
            }

            return PathKind.Other;
        }

        /// <summary>The id in <c>/Users/{id}/Authenticate</c>, or null.</summary>
        internal static Guid? SignInId(string path)
        {
            const string tail = "/Authenticate";
            if (!path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return null;
            var head = path[..^tail.Length];
            var slash = head.LastIndexOf('/');
            if (slash < 0) return null;
            if (!head[..slash].EndsWith("/Users", StringComparison.OrdinalIgnoreCase)) return null;
            return Guid.TryParse(head[(slash + 1)..], out var id) ? id : null;
        }

        /// <summary>
        /// True when a request by <paramref name="userId"/>, who is not an administrator, is
        /// turned away. Also used by the profile picker for a profile it is acting as.
        /// </summary>
        internal static bool Refuses(MaintenanceMode m, string method, string path, Guid userId, ISessionManager? sessions)
        {
            switch (Classify(method, path))
            {
                case PathKind.Open:
                    return false;
                case PathKind.Stream:
                    return m.StopPlayback || !ContinuesPlayback(path, userId, sessions);
            }

            // The account's own record, which a client reads to learn who is signed in.
            if (HttpMethods.IsGet(method))
            {
                var slash = path.LastIndexOf('/');
                if (slash > 0
                    && path[..slash].EndsWith("/Users", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(path[(slash + 1)..], out var id)
                    && id == userId)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True for a request that belongs to something the account is already playing: a
        /// progress or stop report, or a stream of an item the gates let it start
        /// (<see cref="PlaybackRecord"/>). The "playing" report that starts something is not.
        /// </summary>
        internal static bool ContinuesPlayback(string path, Guid userId, ISessionManager? sessions)
        {
            var at = path.IndexOf("/Sessions/Playing", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var rest = path[(at + "/Sessions/Playing".Length)..].TrimEnd('/');
                return rest.Equals("/Progress", StringComparison.OrdinalIgnoreCase)
                    || rest.Equals("/Stopped", StringComparison.OrdinalIgnoreCase)
                    || rest.Equals("/Ping", StringComparison.OrdinalIgnoreCase);
            }

            // What the gates let start, not what a session says it is playing. This used to read
            // session.NowPlayingItem, which POST /Sessions/Playing sets with nothing checked:
            // report an item first, then stream it, and the gate called it "already playing"
            // (R-1297). <paramref name="sessions"/> is no longer consulted.
            var item = StreamItemId(path);
            return item != null && PlaybackRecord.Has(userId, item.Value);
        }

        /// <summary>The item id after <c>/Videos/</c> or <c>/Audio/</c>, or null.</summary>
        internal static Guid? StreamItemId(string path)
        {
            foreach (var marker in new[] { "/Videos/", "/Audio/" })
            {
                var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var start = at + marker.Length;
                var end = path.IndexOf('/', start);
                var segment = end < 0 ? path[start..] : path[start..end];
                // "stream.mp3"-style last segments carry no id; only a whole segment counts.
                return Guid.TryParse(segment, out var id) ? id : null;
            }

            return null;
        }

        /// <summary>
        /// True when <paramref name="name"/> is the name a profile is shown by. A sign-in
        /// screen that lists profiles sends that name, not the account's username, so
        /// <c>GetUserByName</c> finds nothing and Bonfire's provider resolves it later. A
        /// profile is never an administrator, so it is refused here.
        /// </summary>
        private static bool IsProfileName(string name)
        {
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();
            return config?.Mappings?.Any(m => m.ProfileUserId != m.MasterUserId
                && string.Equals(m.ProfileName, name, StringComparison.OrdinalIgnoreCase)) == true;
        }

        // ── The middleware's entry point ────────────────────────────────────────────

        /// <summary>
        /// Answers the request with 503 if it is turned away. False means it was not touched.
        /// </summary>
        public static async Task<bool> TryRefuseAsync(
            HttpContext context, IUserManager? users, IDeviceManager? devices, ISessionManager? sessions, ILogger logger)
        {
            var m = Active();
            if (m == null || users == null) return false;

            var request = context.Request;
            var path = request.Path.Value ?? string.Empty;

            try
            {
                switch (Classify(request.Method, path))
                {
                    case PathKind.Open:
                        return false;

                    case PathKind.SignIn:
                    {
                        var name = await ReadUsernameAsync(request).ConfigureAwait(false);
                        var user = string.IsNullOrEmpty(name) ? null : users.GetUserByName(name);
                        if (user == null)
                        {
                            // A profile's shown name, which only Bonfire's provider resolves.
                            if (string.IsNullOrEmpty(name) || !IsProfileName(name)) return false;
                        }
                        else if (IsAdmin(users, user.Id))
                        {
                            return false;
                        }

                        logger.LogInformation("ProfilesPlugin: sign-in by {User} refused: closed for maintenance.", user?.Username ?? name);
                        await WriteRefusalAsync(context, m).ConfigureAwait(false);
                        return true;
                    }

                    case PathKind.SignInById:
                    {
                        var id = SignInId(path);
                        if (id == null || users.GetUserById(id.Value) == null || IsAdmin(users, id.Value)) return false;
                        await WriteRefusalAsync(context, m).ConfigureAwait(false);
                        return true;
                    }
                }

                // Jellyfin's own reading of the request, not ours (R-1295; see RequestAuth).
                // An API key, or a token Jellyfin will refuse anyway: not ours to answer.
                var userId = await RequestAuth.UserIdAsync(context, () =>
                {
                    var token = ProxySwitcher.ReadToken(request);
                    return string.IsNullOrEmpty(token) ? Guid.Empty : UserOf(token, devices);
                }).ConfigureAwait(false);
                if (userId == Guid.Empty || IsAdmin(users, userId)) return false;

                if (!Refuses(m, request.Method, path, userId, sessions)) return false;

                await WriteRefusalAsync(context, m).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
            {
                // Never the reason a request fails. Jellyfin answers it as it would without us.
                logger.LogWarning(ex, "ProfilesPlugin: the maintenance check failed for {Path}; passing it on.", path);
                return false;
            }
        }

        /// <summary>True for an administrator. Remembered for 30 seconds.</summary>
        internal static bool IsAdmin(IUserManager users, Guid userId)
        {
            var now = DateTime.UtcNow;
            if (Admins.TryGetValue(userId, out var known) && known.Until > now) return known.Admin;

            var user = users.GetUserById(userId);
            var admin = user != null && users.GetUserDto(user, string.Empty).Policy?.IsAdministrator == true;
            if (Admins.Count > MaxRemembered) Admins.Clear();
            Admins[userId] = (admin, now + Remember);
            return admin;
        }

        /// <summary>The account a token belongs to, or empty. Remembered for 30 seconds.</summary>
        internal static Guid UserOf(string token, IDeviceManager? devices)
        {
            if (devices == null) return Guid.Empty;

            var now = DateTime.UtcNow;
            if (Tokens.TryGetValue(token, out var known) && known.Until > now) return known.UserId;

            var device = devices.GetDevices(new DeviceQuery { AccessToken = token })?.Items?.FirstOrDefault();
            var userId = device?.UserId ?? Guid.Empty;
            if (Tokens.Count > MaxRemembered) Tokens.Clear();
            Tokens[token] = (userId, now + Remember);
            return userId;
        }

        /// <summary>The name in a sign-in body, leaving the body readable for Jellyfin.</summary>
        private static async Task<string?> ReadUsernameAsync(HttpRequest request)
        {
            if (request.ContentLength is long length && length > MaxSignInBody) return null;

            request.EnableBuffering();
            string text;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                var buffer = new char[MaxSignInBody];
                var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                text = new string(buffer, 0, read);
            }

            request.Body.Position = 0;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, "Username", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String)
                    {
                        return p.Value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON: Jellyfin will refuse it on its own.
            }

            return null;
        }

        /// <summary>503, the message as plain text, the code, and when to try again.</summary>
        internal static async Task WriteRefusalAsync(HttpContext context, MaintenanceMode m)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers[Controllers.ErrorCodes.Header] = ErrorCode;
            response.Headers[HeaderNames.CacheControl] = "no-store";

            var now = DateTime.UtcNow;
            var seconds = m.UntilUtc.HasValue && m.UntilUtc.Value > now
                ? (long)Math.Ceiling((m.UntilUtc.Value - now).TotalSeconds)
                : 300;
            response.Headers[HeaderNames.RetryAfter] = Math.Max(60, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

            await response.WriteAsync(m.ShownMessage(), context.RequestAborted).ConfigureAwait(false);
        }

        // ── Turning it on ───────────────────────────────────────────────────────────

        /// <summary>
        /// Tells every open app of an account that is not an administrator, and stops what it
        /// is playing if <paramref name="stopPlayback"/>. Best effort: an app that cannot be
        /// reached is refused on its next request anyway.
        /// </summary>
        public static async Task<(int Told, int Stopped)> NotifyAsync(
            ISessionManager sessions, IUserManager users, MaintenanceMode m, bool stopPlayback, ILogger logger)
        {
            int told = 0, stopped = 0;
            foreach (var session in sessions.Sessions.ToList())
            {
                if (session.UserId == Guid.Empty || IsAdmin(users, session.UserId)) continue;

                try
                {
                    await sessions.SendMessageCommand(
                        null,
                        session.Id,
                        new MessageCommand
                        {
                            Header = "Closed for maintenance",
                            Text = m.ShownMessage(),
                            TimeoutMs = null,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    told++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "ProfilesPlugin: could not tell session {Session} about maintenance.", session.Id);
                }

                if (!stopPlayback || session.NowPlayingItem == null) continue;

                try
                {
                    await sessions.SendPlaystateCommand(
                        null,
                        session.Id,
                        new PlaystateRequest { Command = PlaystateCommand.Stop },
                        CancellationToken.None).ConfigureAwait(false);
                    stopped++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "ProfilesPlugin: could not stop playback on session {Session}.", session.Id);
                }
            }

            return (told, stopped);
        }
    }
}
