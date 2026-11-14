using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Campground.Shared.Injection;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Jellyfin.Profiles.Auth;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// Puts <see cref="ProfilesIndexMiddleware"/> at the front of Jellyfin's request pipeline.
    ///
    /// <para>
    /// Jellyfin offers plugins no pipeline hook, but it calls
    /// <c>_pluginManager.RegisterServices(serviceCollection)</c> from <c>ApplicationHost.Init</c>,
    /// inside <c>ConfigureServices</c> and before the pipeline is built. An
    /// <see cref="IStartupFilter"/> registered there is picked up by ASP.NET normally. It is public
    /// API: no reflection, no Harmony, nothing tied to one Jellyfin patch release.
    /// </para>
    /// <para>
    /// At the front, everything else that touches index.html runs inside this middleware, so it
    /// transforms the response rather than producing one.
    /// </para>
    /// </summary>
    public sealed class ProfilesStartupFilter : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.UseMiddleware<ProfilesIndexMiddleware>();
                next(app);
            };
    }

    /// <summary>
    /// Bonfire's place in Jellyfin's request pipeline: the sign-in notes, the maintenance and viewing
    /// gates, the profile picker for apps with no switcher, Quick Connect, profile-name rewriting and
    /// the login-screen user list.
    ///
    /// <para>
    /// It does not add Bonfire's script to index.html. It marks that the page was requested and
    /// passes the request down. The Campground coordinator (Campground.Shared,
    /// Injection/IndexMiddleware.cs), inside this middleware, buffers the document and adds every
    /// Campground plugin's tags in one pass.
    /// </para>
    /// <para>
    /// This replaced patching index.html on disk (issues #17, #11 and #3), which needs write access to
    /// a directory the package manager owns, has to be redone after every Jellyfin update, and cannot
    /// tell when the file being served is not the file patched. The page is not answered from here:
    /// this middleware is at the very front, so answering would hide the request from everything
    /// below it, file-transformation's swapped static file providers included.
    /// </para>
    /// <para>
    /// The user-list and own-name responses are still buffered here, with content negotiation
    /// suspended; see <see cref="NegotiationHeaders"/>.
    /// </para>
    /// </summary>
    public sealed class ProfilesIndexMiddleware
    {
        /// <summary>
        /// Request headers that would stop the pipeline returning a plain, complete body for the
        /// responses this middleware rewrites (user list, own name): a compressed body, a 304 with no
        /// body, or a partial one. Removed for the inner call and restored before returning. index.html
        /// buffering is the coordinator's.
        /// </summary>
        private static readonly string[] NegotiationHeaders =
        {
            HeaderNames.AcceptEncoding,
            HeaderNames.IfNoneMatch,
            HeaderNames.IfModifiedSince,
            HeaderNames.Range,
            HeaderNames.IfRange,
        };

        private readonly RequestDelegate _next;
        private readonly ILogger<ProfilesIndexMiddleware> _logger;

        // The three below are the Campground coordinator's. It serves index.html for every Campground
        // plugin, so they describe the page even when another plugin's middleware did the work.

        /// <summary>index.html documents rewritten since the server started.</summary>
        internal static long ServedCountValue => InjectionCoordinator.Health().ServedCount;

        /// <summary>When one last was, or null if none has been.</summary>
        internal static DateTime? LastServedUtc => InjectionCoordinator.Health().LastServedUtc;

        /// <summary>
        /// Why the last index.html was passed through untouched, or null. Set only for real failures
        /// (tags already present is not one) and reset on every page, so a transient failure does not sit
        /// on the dashboard until a restart.
        /// </summary>
        internal static string? LastError => InjectionCoordinator.Health().LastError;

        /// <summary>
        /// True once a request has arrived that this middleware would have handled. Tells "the filter
        /// never ran" from "it ran and had nothing to do".
        /// </summary>
        internal static bool HasSeenIndexRequest { get; private set; }

        /// <summary>Set at registration, so the dashboard can say whether the hook took at all.</summary>
        internal static bool IsRegistered { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProfilesIndexMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next middleware in the pipeline.</param>
        /// <param name="logger">The logger.</param>
        public ProfilesIndexMiddleware(RequestDelegate next, ILogger<ProfilesIndexMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// Handles a request: marks index.html requests and passes everything else straight through.
        /// </summary>
        /// <param name="context">The request.</param>
        /// <param name="serverConfig">Server configuration, for the base URL and HTTPS policy.</param>
        /// <returns>A task.</returns>
        public async Task Invoke(
            HttpContext context,
            IServerConfigurationManager serverConfig,
            IUserManager userManager,
            IDeviceManager deviceManager,
            ISessionManager sessionManager,
            INetworkManager networkManager,
            IServerApplicationHost appHost)
        {
            // Recorded before anything branches: authentication is where this is needed, and Jellyfin
            // hands a provider nothing about the request. See Auth/RequestDevice.cs.
            //
            // Only on a POST under /Users/. The first version did this on every request (a quote-aware
            // scan, a UrlDecode and a closure), thousands of pointless scans a minute on a busy server.
            // The second covered user routes of any method, which still cost every /Users GET an address
            // check, two header parses and three AsyncLocal sets. The readers are the PIN provider and the
            // session listener, and only a POST reaches them: AuthenticateByName, the obsolete
            // {userId}/Authenticate that forwards to it, and the password change that verifies the old
            // one. Other routes that start a session (switch, picker) record their own note.
            if (HttpMethods.IsPost(context.Request.Method) && IsUserRoute(context))
            {
                // Whether Jellyfin puts the caller at home, decided here because the provider is handed
                // nothing about the request: the address after Jellyfin's known proxies (ClientAddress),
                // judged by its INetworkManager as elsewhere in this file. Unknown is not home (R-1334).
                bool atHome;
                try
                {
                    var from = Auth.ClientAddress.Of(context);
                    atHome = from != null && networkManager != null && networkManager.IsInLocalNetwork(from);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ProfilesPlugin: could not tell whether a sign-in is on the local network.");
                    atHome = false;
                }

                Auth.RequestDevice.Capture(
                    Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                        context.Request.Headers["Authorization"],
                        context.Request.Headers["X-Emby-Authorization"],
                        "DeviceId"),
                    Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                        context.Request.Headers["Authorization"],
                        context.Request.Headers["X-Emby-Authorization"],
                        "Client"),
                    atHome);

                // Every POST here may sign somebody in. The note is started in this method so it flows down
                // to the PIN provider (which marks a PIN or an empty box) and the session listener (which
                // then only refreshes the device record). Started in a helper it would not flow back out.
                Auth.RequestDevice.BeginSignIn();
            }
            else if (Auth.QuickConnectAuthorize.IsAuthorizePath(context.Request.Method, context.Request.Path.Value))
            {
                // Jellyfin creates the approved device's session inside this request, so the session
                // listener reads this note. See RequestDevice.BeginQuickConnectAuthorize.
                Auth.RequestDevice.BeginQuickConnectAuthorize();
            }

            // Closed for maintenance: anyone but an administrator is answered here. Before the picker,
            // so an app on it is closed too. One field read when off.
            if (await Auth.MaintenanceGate.TryRefuseAsync(context, userManager, deviceManager, sessionManager, _logger, serverConfig).ConfigureAwait(false))
            {
                return;
            }

            // Viewing hours and daily limits: new video for a profile whose time is up. One dictionary
            // count while no profile is limited. The picker's profiles are judged in the picker, since
            // what reaches here carries the main account's token.
            if (await Auth.ViewingGate.TryRefuseAsync(context, deviceManager, sessionManager, _logger).ConfigureAwait(false))
            {
                return;
            }

            // An app that gets the profile picker. Checked first: a request it answers or rewrites must
            // not also be renamed, listed or injected. One list count when no app is ticked. See
            // Auth/ProxySwitcher.cs.
            if (await Auth.ProxySwitcher.TryHandleAsync(context, _next, _logger).ConfigureAwait(false))
            {
                return;
            }

            // A profile authorizing a Quick Connect code: Jellyfin creates that session itself, past
            // every rule the PIN sign-in has (R-1335). One path check otherwise.
            if (Auth.QuickConnectAuthorize.IsAuthorizePath(context.Request.Method, context.Request.Path.Value)
                && await RefuseQuickConnectAuthorizeAsync(context, deviceManager).ConfigureAwait(false))
            {
                return;
            }

            // Quick Connect on a device somebody has already signed in on. Answered here, not passed
            // along, because the client's only route to its password field is this request failing.
            if (IsQuickConnectInitiatePath(context))
            {
                var qcDevice = Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                    context.Request.Headers["Authorization"],
                    context.Request.Headers["X-Emby-Authorization"],
                    "Device");

                var qcClient = Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                    context.Request.Headers["Authorization"],
                    context.Request.Headers["X-Emby-Authorization"],
                    "Client");

                // A snapshot: the gate walks KnownDevices, which a sign-in elsewhere may be appending to.
                // See ReadConfigSnapshot.
                if (Auth.QuickConnectGate.ShouldDenyFor(
                        Controllers.ProfilesBaseController.ReadConfigSnapshot(), qcDevice, qcClient,
                        id => Auth.QuickConnectGate.HasNoPasswordPath(userManager, id)))
                {
                    _logger.LogInformation(
                        "ProfilesPlugin: Quick Connect declined for {Device} ({Client}), a device this "
                        + "server has seen someone sign in on, so the app opens on its PIN field instead.",
                        qcDevice,
                        qcClient);

                    // The status and wording Jellyfin returns when Quick Connect is off server-wide, so the
                    // client takes a path it already has.
                    await Auth.MaintenanceGate.ApplyCorsAsync(context).ConfigureAwait(false);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("\"Quick connect is disabled\"").ConfigureAwait(false);
                    return;
                }

                // Allowed: note which device asked for which code on the way out, because the authorize
                // request names only the code. See Auth/QuickConnectAuthorize.cs.
                await RememberQuickConnectAsync(context).ConfigureAwait(false);
                return;
            }

            // The two responses that tell a client which account it is. Renaming only the sign-in list
            // lasted until somebody used a profile: the client stores what these say and paints that
            // afterwards. See ProfileNameRewriter.
            if (IsOwnUserPath(context))
            {
                await RewriteOwnNameAsync(context).ConfigureAwait(false);
                return;
            }

            // A Roku's picker is opening: whoever was using a PIN-protected profile on it has left, so
            // that profile's saved sign-in on this set is revoked and the app asks for the PIN again.
            // Independent of the profile list setting: a Roku lists the accounts it has saved whether or
            // not we add any. See Auth/SavedSignInLock.cs.
            if (IsPublicUsersRequest(context))
            {
                await LockSavedSignInsAsync(context, userManager, deviceManager, sessionManager, networkManager)
                    .ConfigureAwait(false);
            }

            // The login-screen user list: a separate job from the index document below, sharing only the
            // capture machinery. Checked first so it does not fall through the index logic.
            if (IsPublicUsersPath(context))
            {
                await InjectPublicUsersAsync(context, userManager, deviceManager).ConfigureAwait(false);
                return;
            }

            if (!ShouldHandle(context, serverConfig, appHost))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            HasSeenIndexRequest = true;

            // Reaching here proves what the bootstrap task cannot know at startup: the hook is in the
            // pipeline and handling requests. Only now is it safe to take the tags out of index.html,
            // which until now are what makes the switcher work. One-shot and off-thread; see the method.
            //
            // Safe from here rather than from the middleware that adds the tags: PluginServiceRegistrator
            // adds that one in the same call as this, so neither can be in the pipeline alone.
            //
            // After the page is answered, not before (R-1101): a first request that failed, or that
            // nothing could add the tags to, would have taken them off the disk copy with nothing
            // supplying them.

            WebInjection.SyncRegistration();

            // The document itself is the Campground coordinator's (Campground.Shared,
            // Injection/IndexMiddleware.cs), inside this middleware, which adds every Campground plugin's
            // tags in one pass, Bonfire's from WebInjection.Registration. This middleware used to buffer
            // index.html and add them itself; with a second plugin doing the same, the page was buffered
            // twice and each plugin had to recognize the other's work. The capture, negotiation suspend,
            // pass-through rules and cache headers moved there unchanged. tests/cs/pipeline drives the
            // two together.
            await _next(context).ConfigureAwait(false);

            // Served: a 200 document the coordinator could handle, with no error recorded for this
            // page. A document it passed through (encoded, not HTML) never got the tags.
            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding)
                && (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
                && LastError == null)
            {
                ProfilesBootstrapTask.CleanIndexOnceMiddlewareIsLive();
            }
        }

        /// <summary>
        /// <c>GET /Users/Public</c>, the list a client paints its login screen from.
        /// <para>
        /// Buffers the response like the index path, hands the bytes to <see cref="PublicUserInjector"/>
        /// and writes back the result. Every failure path returns Jellyfin's response byte for byte: every
        /// client on the server signs in through this endpoint, so nothing here may break it.
        /// </para>
        /// </summary>
        private async Task InjectPublicUsersAsync(
            HttpContext context, IUserManager userManager, IDeviceManager deviceManager)
        {
            // A snapshot, read without the lock afterwards. The household is resolved by walking
            // Mappings and KnownDevices, and a sign-in on another device may be appending a row.
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();

            // An app that gets the picker signs in as the main account only; a profile signed in
            // directly there is refused, so listing them would offer tiles that fail.
            if (Auth.ProxyClients.IsEnabled(config, Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                    context.Request.Headers[HeaderNames.Authorization],
                    context.Request.Headers["X-Emby-Authorization"],
                    "Client")))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Resolved before the response is captured, so an unrecognized device (the common case on a
            // busy server) never buffers a body. Not free: the snapshot above copies the configuration
            // (every mapping, device and list) under ConfigLock for every request. Not slow enough to
            // cache (2026-11-11); if it ever is, cache an immutable copy rather than moving this check.
            IReadOnlyList<Guid> household;
            try
            {
                household = PublicUserInjector.ResolveHousehold(
                    context.Request.Headers[HeaderNames.Authorization],
                    context.Request.Headers["X-Emby-Authorization"],
                    deviceManager,
                    config,
                    _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: could not resolve a household for the user list.");
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (household.Count == 0)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var originalBody = context.Features.Get<IHttpResponseBodyFeature>();
            if (originalBody == null)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            byte[] produced;
            using (var captured = new MemoryStream())
            {
                var capture = new StreamResponseBodyFeature(captured);
                var negotiation = SuspendContentNegotiation(context.Request);
                context.Features.Set<IHttpResponseBodyFeature>(capture);

                try
                {
                    await _next(context).ConfigureAwait(false);
                    await capture.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    context.Features.Set(originalBody);
                    RestoreContentNegotiation(context.Request, negotiation);
                }

                produced = captured.ToArray();
            }

            if (context.Response.HasStarted)
            {
                // Returning here left the television with a 200 and an empty list. See
                // DeliverStartedAsync.
                await DeliverStartedAsync(context, produced, "user list", "no profiles could be added to it")
                    .ConfigureAwait(false);
                return;
            }

            byte[]? rewritten = null;
            if (context.Response.StatusCode != StatusCodes.Status200OK
                || context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding))
            {
                // The silent one. Jellyfin's answer goes out untouched, and from the couch that is a
                // sign-in screen missing the profile the television never used.
                _logger.LogWarning(
                    "ProfilesPlugin: the user list was left as Jellyfin answered it (status {Status}, "
                    + "Content-Encoding {Encoding}), so no profiles were added.",
                    context.Response.StatusCode,
                    context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding)
                        ? context.Response.Headers[HeaderNames.ContentEncoding].ToString()
                        : "none");
            }
            else
            {
                try
                {
                    rewritten = PublicUserInjector.Inject(
                        produced,
                        context.Response.ContentType,
                        household,
                        userManager,
                        Auth.ClientAddress.Of(context)?.ToString(),
                        _logger,
                        config);
                }
                catch (Exception ex)
                {
                    // Anything at all here and the client gets Jellyfin's own answer.
                    _logger.LogWarning(ex, "ProfilesPlugin: could not add profiles to the user list.");
                    rewritten = null;
                }
            }

            if (rewritten == null)
            {
                await PassThroughAsync(context, produced).ConfigureAwait(false);
                return;
            }

            context.Response.ContentLength = rewritten.Length;

            // The body is no longer what Jellyfin measured, and this list must not be cached: it
            // changes when somebody signs in on this device.
            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";

            await context.Response.Body.WriteAsync(rewritten, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// True for <c>GET /Users/Public</c>, under a base URL or not. Exact path match, so no other
        /// Users route is buffered.
        /// </summary>
        private static bool IsPublicUsersPath(HttpContext context)
            => Plugin.Instance?.Configuration?.EnableClientProfileList == true
               && IsPublicUsersRequest(context);

        /// <summary>
        /// <c>GET /Users/Public</c> whatever the settings say. The emergency disable reaches this too;
        /// see IsOwnUserPath.
        /// </summary>
        private static bool IsPublicUsersRequest(HttpContext context)
        {
            if (Plugin.IsOff) return false;
            if (!HttpMethods.IsGet(context.Request.Method)) return false;

            var path = context.Request.Path.Value;
            return !string.IsNullOrEmpty(path)
                && path.EndsWith("/Users/Public", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Hands a sign-in screen's request to <see cref="SavedSignInLock"/>. Cheap when idle: one
        /// setting read, one header parse and an in-memory lookup.
        /// </summary>
        private async Task LockSavedSignInsAsync(
            HttpContext context,
            IUserManager userManager,
            IDeviceManager deviceManager,
            ISessionManager sessionManager,
            // Nullable because it is null-checked here and in InvokeAsync. Declared non-null, the
            // caller's check (R-1334) made the argument "maybe null" and failed the -warnaserror build.
            INetworkManager? networkManager)
        {
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();
            if (config?.EnableClientPinLogin != true) return;

            var auth = context.Request.Headers[HeaderNames.Authorization];
            var emby = context.Request.Headers["X-Emby-Authorization"];

            // Every way Jellyfin accepts a token. Any of them means somebody is signed in on this
            // client, and their session is not ours to end.
            var carriesToken =
                !string.IsNullOrEmpty(Controllers.ProfilesBaseController.ParseAuthorizationParameter(auth, emby, "Token"))
                || !StringValues.IsNullOrEmpty(context.Request.Headers["X-Emby-Token"])
                || !StringValues.IsNullOrEmpty(context.Request.Headers["X-MediaBrowser-Token"])
                || context.Request.Query.ContainsKey("api_key")
                || context.Request.Query.ContainsKey("ApiKey");

            // Unknown counts as not local: being wrong costs one more PIN.
            bool isLocal;
            System.Net.IPAddress? clientAddress = null;
            try
            {
                // Through Jellyfin's known proxies: this runs before its forwarding (R-1029).
                var remoteIp = Auth.ClientAddress.Of(context);
                clientAddress = remoteIp;
                isLocal = remoteIp != null && networkManager != null && networkManager.IsInLocalNetwork(remoteIp);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: could not tell whether a sign-in screen is on the local network.");
                isLocal = false;
            }

            await SavedSignInLock.LockAsync(
                Controllers.ProfilesBaseController.ParseAuthorizationParameter(auth, emby, "DeviceId"),
                carriesToken,
                isLocal,
                clientAddress,
                config,
                deviceManager,
                sessionManager,
                userManager,
                _logger).ConfigureAwait(false);
        }

        /// <summary>
        /// True for anything under Jellyfin's <c>/Users</c> routes.
        /// <para>
        /// Wider than the routes that authenticate, far narrower than every request. Too narrow silently
        /// breaks PIN-less entry (the provider sees no device and refuses), so this admits the whole user
        /// area instead of listing the authenticating paths. Media, images and web assets, nearly all
        /// traffic, are excluded.
        /// </para>
        /// </summary>
        private static bool IsUserRoute(HttpContext context)
        {
            var path = context.Request.Path.Value;
            return !string.IsNullOrEmpty(path)
                && path.Contains("/Users", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for the two responses that name the account a client is signed in as:
        /// <c>GET /Users/Me</c> and <c>POST /Users/AuthenticateByName</c>.
        /// <para>
        /// Both, because either one left alone puts the system username back: the authentication
        /// response on a PIN login, and <c>/Users/Me</c> every time a stored session is restored.
        /// </para>
        /// </summary>
        private static bool IsOwnUserPath(HttpContext context)
        {
            // Emergency disable stops the plugin editing Jellyfin's own responses, not just serving an
            // inert client script. An inert script was enough while everything happened inside it. Now
            // this rewrites the responses that tell a client which account it is, beside the user list and
            // Quick Connect. If one of those has gone wrong, an inert script recovers nothing.
            if (Plugin.IsOff) return false;

            if (Plugin.Instance?.Configuration?.EnableClientProfileList != true) return false;

            var path = context.Request.Path.Value;
            if (string.IsNullOrEmpty(path)) return false;

            if (HttpMethods.IsGet(context.Request.Method)
                && path.EndsWith("/Users/Me", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HttpMethods.IsPost(context.Request.Method)
                && path.EndsWith("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Captures one of those responses and renames the profile inside it, or returns Jellyfin's own
        /// bytes untouched.
        /// </summary>
        private async Task RewriteOwnNameAsync(HttpContext context)
        {
            var originalBody = context.Features.Get<IHttpResponseBodyFeature>();
            if (originalBody == null)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            byte[] produced;
            using (var captured = new MemoryStream())
            {
                var capture = new StreamResponseBodyFeature(captured);
                var negotiation = SuspendContentNegotiation(context.Request);
                context.Features.Set<IHttpResponseBodyFeature>(capture);

                try
                {
                    await _next(context).ConfigureAwait(false);
                    await capture.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    context.Features.Set(originalBody);
                    RestoreContentNegotiation(context.Request, negotiation);
                }

                produced = captured.ToArray();
            }

            if (context.Response.HasStarted)
            {
                await DeliverStartedAsync(context, produced, "own user record", "the profile could not be renamed in it")
                    .ConfigureAwait(false);
                return;
            }

            byte[]? rewritten = null;
            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding))
            {
                try
                {
                    rewritten = Auth.ProfileNameRewriter.Rewrite(
                        produced,
                        context.Response.ContentType,
                        Controllers.ProfilesBaseController.ReadConfigSnapshot(),
                        _logger);
                }
                catch (Exception ex)
                {
                    // Every client learns who it is from this. Anything unexpected and it gets Jellyfin's own
                    // answer.
                    _logger.LogWarning(ex, "ProfilesPlugin: could not rename a profile in its own user record.");
                    rewritten = null;
                }
            }

            if (rewritten == null)
            {
                await PassThroughAsync(context, produced).ConfigureAwait(false);
                return;
            }

            context.Response.ContentLength = rewritten.Length;
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
            await context.Response.Body.WriteAsync(rewritten, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>Passes Initiate through, remembering the code and the device that asked.</summary>
        private async Task RememberQuickConnectAsync(HttpContext context)
        {
            var originalBody = context.Features.Get<IHttpResponseBodyFeature>();
            if (originalBody == null)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            byte[] produced;
            using (var captured = new MemoryStream())
            {
                var capture = new StreamResponseBodyFeature(captured);
                var negotiation = SuspendContentNegotiation(context.Request);
                context.Features.Set<IHttpResponseBodyFeature>(capture);

                try
                {
                    await _next(context).ConfigureAwait(false);
                    await capture.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    context.Features.Set(originalBody);
                    RestoreContentNegotiation(context.Request, negotiation);
                }

                produced = captured.ToArray();
            }

            if (context.Response.HasStarted)
            {
                await DeliverStartedAsync(context, produced, "Quick Connect", "the code could not be remembered")
                    .ConfigureAwait(false);
                return;
            }

            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding))
            {
                Auth.QuickConnectAuthorize.Remember(produced, Auth.ClientAddress.Of(context)?.ToString());
            }

            await PassThroughAsync(context, produced).ConfigureAwait(false);
        }

        /// <summary>
        /// Answers a Quick Connect authorize with 403 when the profile it is for may not sign that
        /// device in. The account is the <c>userId</c> asked for, or the caller.
        /// </summary>
        private async Task<bool> RefuseQuickConnectAuthorizeAsync(HttpContext context, IDeviceManager? devices)
        {
            try
            {
                var request = context.Request;
                // The first of a repeated value, which is the one Jellyfin binds. ToString() joins them with
                // a comma, which fails to parse and left the caller judged instead.
                Guid target = Guid.TryParse(request.Query["userId"].FirstOrDefault(), out var asked) ? asked : Guid.Empty;
                if (target == Guid.Empty)
                {
                    target = await Auth.RequestAuth.UserIdAsync(context, () =>
                    {
                        var token = Auth.ProxySwitcher.ReadToken(request);
                        return string.IsNullOrEmpty(token) ? Guid.Empty : Auth.MaintenanceGate.UserOf(token, devices);
                    }).ConfigureAwait(false);
                }

                if (target == Guid.Empty) return false;

                var refusal = Auth.QuickConnectAuthorize.Refusal(
                    Controllers.ProfilesBaseController.ReadConfigSnapshot(), target, request.Query["code"].FirstOrDefault());
                if (refusal == null) return false;

                _logger.LogInformation(
                    "ProfilesPlugin: refused a Quick Connect authorize for profile {ProfileId}: {Reason}", target, refusal);
                await Auth.QuickConnectAuthorize.WriteRefusalAsync(context, refusal).ConfigureAwait(false);
                return true;
            }
            catch (MediaBrowser.Controller.Net.SecurityException)
            {
                // Jellyfin refusing the token itself: it answers 401 when the request goes on.
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
            {
                // Fails closed (r2-17-02): a broken check must not let a profile sign in a device its
                // household did not allow. MaintenanceGate and ProxySwitcher fail open.
                _logger.LogError(ex, "ProfilesPlugin: the Quick Connect authorize check failed; refusing it.");
                await Auth.QuickConnectAuthorize.WriteRefusalAsync(context, "This profile is not allowed on this device.").ConfigureAwait(false);
                return true;
            }
        }

        /// <summary>
        /// True for <c>POST /QuickConnect/Initiate</c>, under a base URL or not. The obsolete GET form is
        /// matched too: <c>InitiateQuickConnectLegacy</c> is still routed and calls the POST one, so
        /// omitting it would be a way around this.
        /// </summary>
        private static bool IsQuickConnectInitiatePath(HttpContext context)
        {
            // Emergency disable reaches this too. See IsOwnUserPath.
            if (Plugin.IsOff) return false;

            if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method))
            {
                return false;
            }

            var path = context.Request.Path.Value;
            if (string.IsNullOrEmpty(path)) return false;

            // ASP.NET routes "/QuickConnect/Initiate/" to the same action, so it is matched too.
            return path.TrimEnd('/').EndsWith("/QuickConnect/Initiate", StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>
        /// The response had already started when it came back to us. Something downstream sent the
        /// headers while the capture was in place, so the body went into our buffer and not to the
        /// client; returning then gave a 200 with nothing in it. The edit cannot be made (headers,
        /// Content-Length included, are gone), but Jellyfin's own answer can still be delivered, and the
        /// log says why it was not edited. Every capture path ends here when that happens.
        /// </summary>
        private async Task DeliverStartedAsync(HttpContext context, byte[] produced, string what, string consequence)
        {
            _logger.LogWarning(
                "ProfilesPlugin: the {What} response had already started when it came back to us, "
                + "so {Consequence} (status {Status}, {Bytes} byte(s) passed on).",
                what, consequence, context.Response.StatusCode, produced.Length);

            if (produced.Length == 0)
            {
                return;
            }

            try
            {
                await context.Response.Body.WriteAsync(produced, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: could not pass the started {What} response on.", what);
            }
        }

        /// <summary>Writes back exactly what the pipeline produced, untouched.</summary>
        private static async Task PassThroughAsync(HttpContext context, byte[] produced)
        {
            if (context.Response.HasStarted || produced.Length == 0)
            {
                return;
            }

            await context.Response.Body.WriteAsync(produced, context.RequestAborted).ConfigureAwait(false);
        }

        private static List<KeyValuePair<string, StringValues>> SuspendContentNegotiation(HttpRequest request)
        {
            var saved = new List<KeyValuePair<string, StringValues>>(NegotiationHeaders.Length);
            foreach (var name in NegotiationHeaders)
            {
                if (request.Headers.TryGetValue(name, out var value))
                {
                    saved.Add(new KeyValuePair<string, StringValues>(name, value));
                    request.Headers.Remove(name);
                }
            }

            return saved;
        }

        private static void RestoreContentNegotiation(
            HttpRequest request,
            List<KeyValuePair<string, StringValues>> saved)
        {
            foreach (var header in saved)
            {
                request.Headers[header.Key] = header.Value;
            }
        }

        /// <summary>
        /// True when this request is for the web client's index.html and Jellyfin will serve it as is,
        /// not redirect it to HTTPS. Nothing is answered here: a true result only marks that the page
        /// was requested, and the request goes on to the coordinator.
        /// </summary>
        private static bool ShouldHandle(
            HttpContext context,
            IServerConfigurationManager serverConfig,
            IServerApplicationHost? appHost)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                // HEAD would need the same body length without the body, and nothing asks for it. Jellyfin
                // answers those from the file as it always has.
                return false;
            }

            var path = context.Request.Path.Value;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string? baseUrl = null;
            try
            {
                var network = serverConfig.GetNetworkConfiguration();

                // Step aside only when Jellyfin itself will redirect. Its redirect is in the pipeline only
                // when RequireHttps is set and it is listening with HTTPS (Jellyfin.Server Startup.cs:
                // config.RequireHttps && ListenWithHttps). RequireHttps alone is not enough: we run before
                // forwarded headers are applied, so behind a TLS-terminating proxy IsHttps is false on every
                // request, and stepping aside then meant nothing was ever injected. Answering a request about
                // to be redirected would serve a page the browser is told to leave.
                if (network.RequireHttps && !context.Request.IsHttps && ListensWithHttps(appHost))
                {
                    return false;
                }

                baseUrl = network.BaseUrl;
            }
            catch (Exception)
            {
                // Without the network configuration we cannot honor a base URL, but the unprefixed match
                // below still covers the default installation.
            }

            // We sit ahead of Jellyfin's UsePathBase, so a server under a base URL still shows it here.
            // Accept both forms rather than depend on that ordering.
            return IsIndexPath(path, baseUrl) || IsIndexPath(path, null);
        }

        /// <summary>
        /// Whether Jellyfin is itself listening with HTTPS, half the condition for its HTTPS redirect
        /// being in the pipeline.
        /// </summary>
        private static bool ListensWithHttps(IServerApplicationHost? appHost)
        {
            try
            {
                return appHost != null && appHost.ListenWithHttps;
            }
            catch (Exception)
            {
                // Unknown is "not redirecting": being wrong costs one answered request Jellyfin would have
                // redirected, not a server with no switcher.
                return false;
            }
        }

        private static bool IsIndexPath(string path, string? baseUrl)
        {
            var prefix = (baseUrl ?? string.Empty).TrimEnd('/');
            var web = prefix + "/web";

            // "/web" without the trailing slash is deliberately not handled: the browser needs the
            // redirect to "/web/" for relative URLs in the document to resolve, and that redirect is
            // Jellyfin's to issue.
            return path.Equals(web + "/", StringComparison.OrdinalIgnoreCase)
                || path.Equals(web + "/index.html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
