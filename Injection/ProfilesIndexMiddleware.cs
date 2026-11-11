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
    /// Jellyfin never offers plugins a pipeline hook, but it does call
    /// <c>_pluginManager.RegisterServices(serviceCollection)</c> from
    /// <c>ApplicationHost.Init</c>, which runs inside <c>ConfigureServices</c> — before the
    /// pipeline is built. Anything a plugin registers as <see cref="IStartupFilter"/> there is
    /// picked up by ASP.NET the normal way. That is the whole trick, and it is ordinary public
    /// API: no reflection, no Harmony, and nothing tied to one Jellyfin patch release.
    /// </para>
    /// <para>
    /// Being at the front means everything else that touches index.html runs <em>inside</em> us,
    /// which is exactly why the middleware transforms the response rather than producing one.
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
    /// Adds the plugin's script tags to Jellyfin's index.html as it is served, without modifying
    /// the file on disk.
    ///
    /// <para>
    /// Replaces the mechanism behind issues #17, #11 and #3. Patching index.html needs write
    /// access to a directory the package manager owns, has to be redone after every Jellyfin
    /// update, and — worst of all — gives no way to tell from the outside when the file being
    /// served is not the file being patched.
    /// </para>
    /// <para>
    /// <strong>This buffers the downstream response; it does not short-circuit.</strong> Until
    /// 1.4.4 it read the file itself and answered the request, which was simpler and wrong: this
    /// middleware sits at the very front of the pipeline, so answering here means nothing further
    /// down ever sees the request. jellyfin-plugin-file-transformation swaps the static file
    /// providers (by Harmony patch) so that the static file layer returns an already-transformed
    /// index.html, and everything built on it — home screen sections, plugin pages — depends on
    /// that. Short-circuiting silently threw all of it away, and reading the file straight off
    /// disk bypassed the swapped provider a second time. Asking the pipeline for the document and
    /// editing what comes back composes with any of them, in either order.
    /// </para>
    /// <para>
    /// Buffering costs two things, both handled below: <c>StaticFileMiddleware</c> sends physical
    /// files through <c>SendFileAsync</c> and never touches <c>Response.Body</c>, so the capture
    /// has to be an <see cref="IHttpResponseBodyFeature"/> rather than a swapped stream; and the
    /// response would arrive compressed, so the request's content negotiation is suspended for
    /// the duration.
    /// </para>
    /// </summary>
    public sealed class ProfilesIndexMiddleware
    {
        /// <summary>
        /// Request headers that would stop the pipeline handing back a plain, complete document:
        /// a compressed body we would have to decompress, a 304 with no body at all, or a partial
        /// one. Removed for the inner call and put back before returning.
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

        // The three below are the Campground coordinator's, which serves index.html for every
        // Campground plugin on the server, so they describe the page Bonfire's script is in
        // even when another plugin's copy of the middleware is the one doing it.

        /// <summary>index.html documents rewritten since the server started.</summary>
        internal static long ServedCountValue => InjectionCoordinator.Health().ServedCount;

        /// <summary>When one last was, or null if none has been.</summary>
        internal static DateTime? LastServedUtc => InjectionCoordinator.Health().LastServedUtc;

        /// <summary>
        /// Why the last index.html was passed through untouched, or null. Only set for genuine
        /// failures — declining because the tags are already present is not one — and reset
        /// on every page handled, so one transient failure does not sit on the dashboard
        /// until a restart.
        /// </summary>
        internal static string? LastError => InjectionCoordinator.Health().LastError;

        /// <summary>
        /// True once a request has arrived that this middleware would have handled. Distinguishes
        /// "the filter never ran" from "it ran and had nothing to do", which look identical from
        /// the dashboard otherwise.
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
        /// Handles a request, adding the script tags to the index.html the rest of the pipeline
        /// produces and passing everything else straight through.
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
            // Recorded before anything branches, because the one place that needs it is
            // authentication and Jellyfin hands a provider nothing about the request.
            // See Auth/RequestDevice.cs.
            //
            // Only on user routes. The first version did this on EVERY request, and the
            // parse is a quote-aware character scan with a UrlDecode and a closure — on a
            // server streaming to forty people that is thousands of pointless scans a
            // minute, on requests for video segments and images that can never reach an
            // authentication provider. Everything that can is a POST under /Users/:
            // AuthenticateByName, the obsolete {userId}/Authenticate that forwards to it,
            // and the password change that verifies the old one. A substring check on a
            // string we already hold replaces the scan for everything else.
            if (IsUserRoute(context))
            {
                // Whether Jellyfin puts the caller at home, decided here because the provider
                // is handed nothing about the request: the address after Jellyfin's known
                // proxies (ClientAddress), judged by its own INetworkManager, as everywhere
                // else in this file. Unknown is not home (R-1334).
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

                // Every POST here may sign somebody in. The note is started here, in this
                // method, so it flows down to the PIN provider, which marks a PIN or an empty
                // box, and to the session listener, which then only refreshes the device's
                // record. Started in a helper it would not flow back out.
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    Auth.RequestDevice.BeginSignIn();
                }
            }

            // Closed for maintenance: anyone but an administrator is answered here. Before the
            // picker, so an app on it is closed too. One field read when it is off.
            if (await Auth.MaintenanceGate.TryRefuseAsync(context, userManager, deviceManager, sessionManager, _logger, serverConfig).ConfigureAwait(false))
            {
                return;
            }

            // Viewing hours and daily limits: new video for a profile whose time is up. One
            // dictionary count while no profile is limited. The picker's profiles are judged
            // in the picker, since what reaches here carries the main account's token.
            if (await Auth.ViewingGate.TryRefuseAsync(context, deviceManager, sessionManager, _logger).ConfigureAwait(false))
            {
                return;
            }

            // An app that gets the profile picker. Checked before everything below, because
            // a request it answers or rewrites must not also be renamed, listed or injected.
            // Costs one list count when no app is ticked. See Auth/ProxySwitcher.cs.
            if (await Auth.ProxySwitcher.TryHandleAsync(context, _next, _logger).ConfigureAwait(false))
            {
                return;
            }

            // A profile authorizing a Quick Connect code: Jellyfin creates that session itself,
            // past every rule the PIN sign-in has (R-1335). One path check otherwise.
            if (Auth.QuickConnectAuthorize.IsAuthorizePath(context.Request.Method, context.Request.Path.Value)
                && await RefuseQuickConnectAuthorizeAsync(context, deviceManager).ConfigureAwait(false))
            {
                return;
            }

            // Quick Connect on a device somebody has already signed in on. Answered here
            // rather than passed along, because the client's only route to its password
            // field is this request failing.
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

                // A snapshot: the gate walks KnownDevices, which a sign-in elsewhere may be
                // appending to at this moment. See ReadConfigSnapshot.
                if (Auth.QuickConnectGate.ShouldDeny(
                        Controllers.ProfilesBaseController.ReadConfigSnapshot(), qcDevice, qcClient))
                {
                    _logger.LogInformation(
                        "ProfilesPlugin: Quick Connect declined for {Device} ({Client}), a device this "
                        + "server has seen someone sign in on, so the app opens on its PIN field instead.",
                        qcDevice,
                        qcClient);

                // The same status and wording Jellyfin itself returns when Quick Connect is
                // switched off server-wide, so the client is taking a path it already has.
                    await Auth.MaintenanceGate.ApplyCorsAsync(context).ConfigureAwait(false);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("\"Quick connect is disabled\"").ConfigureAwait(false);
                    return;
                }

                // Allowed: note which device asked for which code on the way out, because the
                // authorize request names only the code. See Auth/QuickConnectAuthorize.cs.
                await RememberQuickConnectAsync(context).ConfigureAwait(false);
                return;
            }

            // The two responses that tell a client which account it is. Renaming the
            // sign-in list alone lasted only until somebody used a profile — the client
            // stores what these say and paints that afterwards. See ProfileNameRewriter.
            if (IsOwnUserPath(context))
            {
                await RewriteOwnNameAsync(context).ConfigureAwait(false);
                return;
            }

            // A Roku's picker is opening: whoever was using a PIN-protected profile on it has
            // left, so that profile's saved sign-in on this set is revoked and the app asks
            // for the PIN again. Independent of the profile list setting - a Roku lists the
            // accounts it has saved whether or not we add any. See Auth/SavedSignInLock.cs.
            if (IsPublicUsersRequest(context))
            {
                await LockSavedSignInsAsync(context, userManager, deviceManager, sessionManager, networkManager)
                    .ConfigureAwait(false);
            }

            // The login-screen user list, a completely separate job from the index document
            // below and sharing only the capture machinery. Checked first because it is a
            // different path entirely and must not fall through the index logic.
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

            // Reaching here is the proof the bootstrap task cannot have at startup: the
            // hook is in the pipeline and handling requests. Only now is it safe to take
            // the tags out of index.html, which until this moment are what is making the
            // switcher work. One-shot and off-thread; see the method itself.
            //
            // Safe to do from here rather than from the middleware that now adds the tags:
            // PluginServiceRegistrator adds that one in the same call that adds this, so
            // one cannot be in the pipeline without the other.
            //
            // After the page is answered, not before (R-1101): a first request that failed, or
            // that nothing could add the tags to, would otherwise have taken them off the disk
            // copy with nothing supplying them in their place.

            // The document itself is the Campground coordinator's (Campground.Shared,
            // Injection/IndexMiddleware.cs), which sits inside this middleware and adds every
            // Campground plugin's tags in one pass — Bonfire's included, from
            // WebInjection.Registration. This middleware used to buffer index.html and add
            // them itself; with a second plugin doing the same, the page was buffered twice
            // and each plugin had to recognise the other's work. The capture, the
            // negotiation suspend, the pass-through rules and the cache headers all moved
            // there unchanged, and tests/cs/pipeline drives the two together.
            await _next(context).ConfigureAwait(false);

            // Served: a 200 document the coordinator could handle, and no error recorded for
            // this page. A document it passed through (encoded, not HTML) never got the tags.
            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding)
                && (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
                && LastError == null)
            {
                ProfilesBootstrapTask.CleanIndexOnceMiddlewareIsLive();
            }
        }

        /// <summary>
        /// <c>GET /Users/Public</c> - the list a client paints its login screen from.
        /// <para>
        /// Buffers the response the way the index path does, hands the bytes to
        /// <see cref="PublicUserInjector"/>, and writes back whatever comes out. Every
        /// failure path returns the response Jellyfin produced, byte for byte: this endpoint
        /// is how every client on the server signs in, so nothing here may be able to break
        /// it.
        /// </para>
        /// </summary>
        private async Task InjectPublicUsersAsync(
            HttpContext context, IUserManager userManager, IDeviceManager deviceManager)
        {
            // A snapshot, read without the lock afterwards. The household is resolved by
            // walking Mappings and KnownDevices, and a sign-in on another device can be
            // appending a device row at the same moment.
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();

            // An app that gets the picker signs in as the main account only, and a profile
            // signed in directly there is refused. Listing them would offer tiles that fail.
            if (Auth.ProxyClients.IsEnabled(config, Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                    context.Request.Headers[HeaderNames.Authorization],
                    context.Request.Headers["X-Emby-Authorization"],
                    "Client")))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Resolved BEFORE the response is captured, so a device we do not recognise -
            // the overwhelmingly common case on a busy server - never buffers a body. It is
            // not free: the snapshot above copies the configuration (every mapping, device
            // and list) under ConfigLock before the device is looked at, and that copy is
            // made for every request, known device or not. Not slow enough to be worth
            // caching (2026-11-11); if it ever is, cache an immutable copy rather than
            // moving this check.
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
                // The silent one. Jellyfin's answer goes out untouched, and from the couch
                // that is a sign-in screen missing the profile the television never used.
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

            // The body is no longer what Jellyfin measured, and this list must not be cached
            // anyway - it changes the moment somebody signs in on this device.
            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";

            await context.Response.Body.WriteAsync(rewritten, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// True for <c>GET /Users/Public</c>, under a base URL or not. Matched on the exact
        /// path so no other Users route is ever buffered.
        /// </summary>
        private static bool IsPublicUsersPath(HttpContext context)
            => Plugin.Instance?.Configuration?.EnableClientProfileList == true
               && IsPublicUsersRequest(context);

        /// <summary>
        /// <c>GET /Users/Public</c>, whatever the settings say. The emergency disable reaches
        /// this too; see IsOwnUserPath.
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
        /// Hands a sign-in screen's request to <see cref="SavedSignInLock"/>. Cheap when it
        /// has nothing to do: one setting read, one header parse and an in-memory lookup.
        /// </summary>
        private async Task LockSavedSignInsAsync(
            HttpContext context,
            IUserManager userManager,
            IDeviceManager deviceManager,
            ISessionManager sessionManager,
            // Nullable because it is null-checked, here and in InvokeAsync. Declared
            // non-null, the caller's check (R-1334) made the argument "maybe null" to the
            // compiler and failed the -warnaserror build.
            INetworkManager? networkManager)
        {
            var config = Controllers.ProfilesBaseController.ReadConfigSnapshot();
            if (config?.EnableClientPinLogin != true) return;

            var auth = context.Request.Headers[HeaderNames.Authorization];
            var emby = context.Request.Headers["X-Emby-Authorization"];

            // Every way Jellyfin accepts a token. Any of them means somebody is signed in on
            // this client, and their session is not ours to end.
            var carriesToken =
                !string.IsNullOrEmpty(Controllers.ProfilesBaseController.ParseAuthorizationParameter(auth, emby, "Token"))
                || !StringValues.IsNullOrEmpty(context.Request.Headers["X-Emby-Token"])
                || !StringValues.IsNullOrEmpty(context.Request.Headers["X-MediaBrowser-Token"])
                || context.Request.Query.ContainsKey("api_key")
                || context.Request.Query.ContainsKey("ApiKey");

            // Unknown counts as not local: the cost of being wrong is one more PIN.
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
        /// Deliberately wider than the routes that actually authenticate, and deliberately
        /// far narrower than every request. Getting it too narrow silently breaks PIN-less
        /// entry — the provider would see no device and refuse — so this admits the whole
        /// user area rather than trying to enumerate the authenticating paths exactly.
        /// Media, images and web assets, which is nearly all traffic, are excluded.
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
        /// Both, because they are written at different moments and either one left alone
        /// puts the system username back: the authentication response on a PIN login, and
        /// <c>/Users/Me</c> every time a stored session is restored.
        /// </para>
        /// </summary>
        private static bool IsOwnUserPath(HttpContext context)
        {
            // Emergency disable stops the plugin editing Jellyfin's own responses, not just
            // serving an inert client script.
            //
            // One job was enough while everything Bonfire did happened inside that script.
            // It is not enough now: this rewrites the responses that tell a client which
            // account it is, and the sign-in user list and Quick Connect are rewritten
            // beside it. If one of those is what has gone wrong, an inert script recovers
            // nothing — and the switch an administrator reaches for in an emergency would
            // not have turned off the thing that needed turning off.
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
        /// Captures one of those responses and renames the profile inside it, or hands
        /// Jellyfin's own bytes back untouched.
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
                    // This is how every client learns who it is. Anything unexpected and it
                    // gets Jellyfin's own answer.
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
                Auth.QuickConnectAuthorize.Remember(produced);
            }

            await PassThroughAsync(context, produced).ConfigureAwait(false);
        }

        /// <summary>
        /// Answers a Quick Connect authorize with 403 when the profile it is for may not sign
        /// that device in. The account is the <c>userId</c> asked for, or the caller.
        /// </summary>
        private async Task<bool> RefuseQuickConnectAuthorizeAsync(HttpContext context, IDeviceManager? devices)
        {
            try
            {
                var request = context.Request;
                Guid target = Guid.TryParse(request.Query["userId"].ToString(), out var asked) ? asked : Guid.Empty;
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
                    Controllers.ProfilesBaseController.ReadConfigSnapshot(), target, request.Query["code"].ToString());
                if (refusal == null) return false;

                _logger.LogInformation(
                    "ProfilesPlugin: refused a Quick Connect authorize for profile {ProfileId}: {Reason}", target, refusal);
                await Auth.QuickConnectAuthorize.WriteRefusalAsync(context, refusal).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: the Quick Connect authorize check failed; passing it on.");
                return false;
            }
        }

        /// <summary>
        /// True for <c>POST /QuickConnect/Initiate</c>, under a base URL or not. The obsolete
        /// GET form is matched too — <c>InitiateQuickConnectLegacy</c> is still routed and
        /// simply calls the POST one, so leaving it out would be a way around this.
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
        /// The response had already started when it came back to us. Something downstream
        /// sent the headers while the capture was in place, so the body went into our buffer
        /// and not to the client; returning then gave the client a 200 with nothing in it.
        /// Whatever we meant to change cannot be changed any more - the headers, Content-Length
        /// included, are gone - but Jellyfin's own answer can still be delivered, and the log
        /// says why it was not edited. Every capture path ends here when that happens.
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
        /// True when this request is for the web client's index.html and we are allowed to
        /// answer it.
        /// </summary>
        private static bool ShouldHandle(
            HttpContext context,
            IServerConfigurationManager serverConfig,
            IServerApplicationHost? appHost)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                // HEAD would need the same body length without the body, and nothing asks for
                // it. Let Jellyfin answer those from the file as it always has.
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

                // Step aside only when Jellyfin itself will redirect this request. Its
                // redirect is in the pipeline only when RequireHttps is set AND it is
                // listening with HTTPS (Jellyfin.Server Startup.cs: config.RequireHttps &&
                // ListenWithHttps). RequireHttps alone is not enough: we run before
                // forwarded headers are applied, so behind a TLS-terminating proxy IsHttps
                // is false on every request, and stepping aside then meant nothing was
                // ever injected. Answering a request that is about to be redirected would
                // only serve a page the browser is told to leave.
                if (network.RequireHttps && !context.Request.IsHttps && ListensWithHttps(appHost))
                {
                    return false;
                }

                baseUrl = network.BaseUrl;
            }
            catch (Exception)
            {
                // Without the network configuration we cannot honour a base URL, but the
                // unprefixed match below still covers the default installation.
            }

            // We sit ahead of Jellyfin's UsePathBase, so a server published under a base URL
            // still shows it here. Accept both forms rather than depending on that ordering.
            return IsIndexPath(path, baseUrl) || IsIndexPath(path, null);
        }

        /// <summary>
        /// Whether Jellyfin is itself listening with HTTPS, which is half of the condition
        /// for its HTTPS redirect being in the pipeline.
        /// </summary>
        private static bool ListensWithHttps(IServerApplicationHost? appHost)
        {
            try
            {
                return appHost != null && appHost.ListenWithHttps;
            }
            catch (Exception)
            {
                // Unknown is "not redirecting": the cost of being wrong is one answered
                // request that Jellyfin would have redirected, not a server with no switcher.
                return false;
            }
        }

        private static bool IsIndexPath(string path, string? baseUrl)
        {
            var prefix = (baseUrl ?? string.Empty).TrimEnd('/');
            var web = prefix + "/web";

            // "/web" without the trailing slash is deliberately not handled: the browser needs
            // the redirect to "/web/" for relative URLs in the document to resolve, and that
            // redirect is Jellyfin's to issue.
            return path.Equals(web + "/", StringComparison.OrdinalIgnoreCase)
                || path.Equals(web + "/index.html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
