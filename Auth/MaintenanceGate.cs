using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
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

        /// <summary>
        /// The maintenance settings while it is on, otherwise null. A closure that was set to
        /// reopen by itself is not on once its time has come, whether or not anything has yet
        /// written that down (<see cref="ReopenIfLapsed"/>): every reader gets the same answer
        /// at the same moment.
        /// </summary>
        public static MaintenanceMode? Active()
        {
            if (Plugin.IsPanicDisabled) return null;
            var m = Plugin.Instance?.Configuration?.Maintenance;
            return m != null && m.Enabled && !m.HasLapsed(DateTime.UtcNow) ? m : null;
        }

        /// <summary>
        /// Writes down a reopening that has already happened: when a closure's automatic time
        /// has come, saves it as open and says so in the log and the activity feed. Called from
        /// the request gate and the maintenance routes, so the saved state catches up on the
        /// first request after the time, from nothing but the clock. Does nothing, and costs a
        /// field read, the rest of the time. True when it reopened.
        /// </summary>
        public static bool ReopenIfLapsed(ILogger logger, IServiceProvider? services = null)
        {
            var seen = Plugin.Instance?.Configuration?.Maintenance;
            if (seen == null || !seen.HasLapsed(DateTime.UtcNow)) return false;

            lock (ProfilesBaseController.ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                var m = config?.Maintenance;
                if (config == null || m == null || !m.HasLapsed(DateTime.UtcNow)) return false;

                // Replaced, never edited in place: the gate reads it without the lock.
                config.Maintenance = new MaintenanceMode { Enabled = false, Message = m.Message };
                Plugin.Instance?.SaveConfiguration();
            }

            Forget();
            logger.LogWarning("ProfilesPlugin: the server reopened after maintenance, at the time the administrator set.");
            try
            {
                var activity = services?.GetService(typeof(MediaBrowser.Model.Activity.IActivityManager))
                    as MediaBrowser.Model.Activity.IActivityManager;
                if (activity != null)
                {
                    var entry = new Jellyfin.Database.Implementations.Entities.ActivityLog(
                        "The server reopened after maintenance", "BonfireMaintenanceOff", Guid.Empty)
                    {
                        LogSeverity = LogLevel.Information,
                    };
                    _ = activity.CreateAsync(entry).ContinueWith(
                        t => logger.LogDebug(t.Exception, "ProfilesPlugin: Could not write to the activity log."),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "ProfilesPlugin: Could not write to the activity log.");
            }

            return true;
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

        private static readonly string[] OpenPaths =
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
            "/PlayingItems/",
        };

        /// <summary>
        /// The path as Jellyfin routes it: what follows the base URL it is published under. We
        /// sit ahead of its UsePathBase, so the request still carries it. With no base URL
        /// configured, or one the path does not start with, the path is returned as it is.
        /// </summary>
        internal static string AfterBaseUrl(string path, string? baseUrl)
        {
            var prefix = (baseUrl ?? string.Empty).TrimEnd('/');
            if (prefix.Length == 0 || !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return path;
            return path[prefix.Length..];
        }

        /// <summary>The published Base URL, or empty. Read only once the gate is on.</summary>
        internal static string BaseUrlOf(IServerConfigurationManager? serverConfig)
        {
            try
            {
                return serverConfig == null
                    ? string.Empty
                    : WebInjection.NormalizeBaseUrl(serverConfig.GetNetworkConfiguration().BaseUrl);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static PathKind Classify(string method, string path, string? baseUrl = null)
        {
            if (string.IsNullOrEmpty(path)) return PathKind.Open;

            // Open paths are matched at the start of the path, never anywhere in it: the
            // stream URL /Videos/{id}/hls1/{playlistId}/{n}.ts carries a free-text segment, and
            // one that read "web" was an open path that skipped the stream check.
            path = AfterBaseUrl(path, baseUrl);

            foreach (var open in OpenPaths)
            {
                if (path.StartsWith(open, StringComparison.OrdinalIgnoreCase)) return PathKind.Open;
            }

            if (path.Equals("/web", StringComparison.OrdinalIgnoreCase)) return PathKind.Open;

            if (HttpMethods.IsPost(method))
            {
                if (path.TrimEnd('/').EndsWith("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase)) return PathKind.SignIn;
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
            path = path.TrimEnd('/');
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
        internal static bool Refuses(MaintenanceMode m, string method, string path, Guid userId, ISessionManager? sessions, string? baseUrl = null)
        {
            switch (Classify(method, path, baseUrl))
            {
                case PathKind.Open:
                    return false;
                case PathKind.Stream:
                    return m.StopPlayback || !ContinuesPlayback(path, userId, sessions, method);
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
        internal static bool ContinuesPlayback(string path, Guid userId, ISessionManager? sessions, string? method = null)
        {
            // The older routes, /PlayingItems/{id} and /Users/{id}/PlayingItems/{id}: a POST to the
            // item is the report that starts something, a POST to its /Progress goes on, and a
            // DELETE is the stop. Only the last two continue.
            var playing = path.IndexOf("/PlayingItems/", StringComparison.OrdinalIgnoreCase);
            if (playing >= 0)
            {
                var tail = path[(playing + "/PlayingItems/".Length)..].TrimEnd('/');
                var slash = tail.IndexOf('/');
                var itemSegment = slash < 0 ? tail : tail[..slash];
                if (!Guid.TryParse(itemSegment, out _) || method == null) return false;
                return slash < 0
                    ? HttpMethods.IsDelete(method)
                    : HttpMethods.IsPost(method) && tail[slash..].Equals("/Progress", StringComparison.OrdinalIgnoreCase);
            }

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

        // ── The middleware's entry point ────────────────────────────────────────────

        /// <summary>
        /// Answers the request with 503 if it is turned away. False means it was not touched.
        /// </summary>
        public static async Task<bool> TryRefuseAsync(
            HttpContext context, IUserManager? users, IDeviceManager? devices, ISessionManager? sessions, ILogger logger,
            IServerConfigurationManager? serverConfig = null)
        {
            var m = Active();
            if (m == null)
            {
                // Off, or just reopened by the clock: either way nothing is turned away. This
                // is where the saved state catches up, since it is the one place every request
                // passes through.
                ReopenIfLapsed(logger, context.RequestServices);
                return false;
            }

            if (users == null) return false;

            var request = context.Request;
            var path = request.Path.Value ?? string.Empty;

            try
            {
                var baseUrl = BaseUrlOf(serverConfig);
                switch (Classify(request.Method, path, baseUrl))
                {
                    case PathKind.Open:
                        return false;

                    case PathKind.SignIn:
                    {
                        var (name, tooLarge) = await ReadUsernameAsync(request).ConfigureAwait(false);
                        if (tooLarge)
                        {
                            // No sign-in is this big. Passing it on unread would let the name hide in it.
                            logger.LogInformation("ProfilesPlugin: a sign-in body too large to read was refused: closed for maintenance.");
                            await WriteRefusalAsync(context, m).ConfigureAwait(false);
                            return true;
                        }

                        // Only an administrator's name goes on to Jellyfin. Every other name, an
                        // account's, a profile's shown name or one nobody has, gets the same
                        // closed answer: telling them apart (503 for one that exists, a passed
                        // request for one that does not) let a stranger list the household's
                        // usernames and profile names (R-1306). The lookup is the same for all
                        // of them, so the answer does not take longer for a name that exists.
                        var user = string.IsNullOrEmpty(name) ? null : users.GetUserByName(name);
                        if (user != null && IsAdmin(users, user.Id)) return false;

                        logger.LogInformation("ProfilesPlugin: a sign-in was refused: closed for maintenance.");
                        await WriteRefusalAsync(context, m).ConfigureAwait(false);
                        return true;
                    }

                    case PathKind.SignInById:
                    {
                        var id = SignInId(path);
                        // As for a name: only an administrator's id goes on, an unknown one is
                        // refused like any other (R-1306).
                        if (id != null && users.GetUserById(id.Value) != null && IsAdmin(users, id.Value)) return false;
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

                if (!Refuses(m, request.Method, path, userId, sessions, baseUrl)) return false;

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

        /// <summary>
        /// The name in a sign-in body, leaving the body readable for Jellyfin. The last
        /// <c>Username</c> wins, as it does when Jellyfin binds the body. TooLarge is true when
        /// the body is more than a sign-in can be and was not read.
        /// </summary>
        private static async Task<(string? Name, bool TooLarge)> ReadUsernameAsync(HttpRequest request)
        {
            if (request.ContentLength is long length && length > MaxSignInBody) return (null, true);

            request.EnableBuffering();
            string text;
            int read;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                var buffer = new char[MaxSignInBody];
                read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                text = new string(buffer, 0, read);
            }

            request.Body.Position = 0;
            if (read >= MaxSignInBody) return (null, true);

            string? name = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, false);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, "Username", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String)
                    {
                        name = p.Value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON: Jellyfin will refuse it on its own.
            }

            return (name, false);
        }

        /// <summary>The message with "Try again later." after it, however the message ends.</summary>
        internal static string RefusalBody(MaintenanceMode m)
        {
            var text = m.ShownMessage();
            var last = text[text.Length - 1];
            return text + (last == '.' || last == '!' || last == '?' ? " " : ". ") + "Try again later.";
        }

        /// <summary>
        /// Puts the server's own CORS headers on a response this plugin writes itself. The
        /// plugin runs in front of Jellyfin's CORS middleware, so a refusal written here would
        /// otherwise reach a web client on another origin with no CORS headers, and the browser
        /// would show an opaque network error instead of the message. Never the reason a
        /// request fails.
        /// </summary>
        internal static async Task ApplyCorsAsync(HttpContext context)
        {
            try
            {
                if (!context.Request.Headers.ContainsKey(HeaderNames.Origin)) return;
                var provider = context.RequestServices.GetService(typeof(Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider))
                    as Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider;
                var cors = context.RequestServices.GetService(typeof(Microsoft.AspNetCore.Cors.Infrastructure.ICorsService))
                    as Microsoft.AspNetCore.Cors.Infrastructure.ICorsService;
                if (provider == null || cors == null) return;
                var policy = await provider.GetPolicyAsync(context, null).ConfigureAwait(false);
                if (policy == null) return;
                cors.ApplyResult(cors.EvaluatePolicy(context, policy), context.Response);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>503, the message as plain text, the code, and when to try again.</summary>
        internal static async Task WriteRefusalAsync(HttpContext context, MaintenanceMode m)
        {
            await ApplyCorsAsync(context).ConfigureAwait(false);
            var response = context.Response;
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers[Controllers.ErrorCodes.Header] = ErrorCode;
            response.Headers[HeaderNames.CacheControl] = "no-store";

            var now = DateTime.UtcNow;
            var seconds = m.UntilUtc.HasValue && m.UntilUtc.Value > now
                ? (long)Math.Ceiling((m.UntilUtc.Value - now).TotalSeconds)
                : 300;
            // An end time months away is a reason to check back now and then, not a promise
            // that nothing changes for months: cap it at an hour.
            response.Headers[HeaderNames.RetryAfter] = Math.Min(3600, Math.Max(60, seconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // The administrator's words, then what to do: an app with no notice shows only this.
            await response.WriteAsync(RefusalBody(m), context.RequestAborted).ConfigureAwait(false);
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
