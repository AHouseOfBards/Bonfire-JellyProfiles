using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

        /// <summary>Number of requests this middleware has answered since the server started.</summary>
        internal static long ServedCount;

        /// <summary>Thread-safe read of <see cref="ServedCount"/>.</summary>
        internal static long ServedCountValue => Interlocked.Read(ref ServedCount);

        /// <summary>When it last answered one, or null if it never has.</summary>
        internal static DateTime? LastServedUtc { get; private set; }

        /// <summary>
        /// Why the last attempt passed the document through untouched, or null. Only set for
        /// genuine failures — declining because the tags are already present is not one.
        /// </summary>
        internal static string? LastError { get; private set; }

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
            IServerConfigurationManager serverConfig)
        {
            if (!ShouldHandle(context, serverConfig))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            HasSeenIndexRequest = true;

            var config = Plugin.Instance?.Configuration;
            if (!IndexInjectionModes.UsesMiddleware(config?.IndexInjectionMode))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Reaching here is the proof the bootstrap task cannot have at startup: the
            // hook is in the pipeline and handling requests. Only now is it safe to take
            // the tags out of index.html, which until this moment are what is making the
            // switcher work. One-shot and off-thread; see the method itself.
            if (!IndexInjectionModes.PatchesFile(config?.IndexInjectionMode))
            {
                ProfilesBootstrapTask.CleanIndexOnceMiddlewareIsLive();
            }

            var originalBody = context.Features.Get<IHttpResponseBodyFeature>();
            if (originalBody == null)
            {
                // Nothing to capture through. Let Jellyfin answer as it would without us.
                Fail("the response has no body feature to capture.");
                await _next(context).ConfigureAwait(false);
                return;
            }

            byte[] produced;
            using (var captured = new MemoryStream())
            {
                // Constructed without a prior feature deliberately: given one, CompleteAsync
                // would complete the real response, and the whole point is that we still get
                // to write to it afterwards.
                var capture = new StreamResponseBodyFeature(captured);
                var negotiation = SuspendContentNegotiation(context.Request);
                context.Features.Set<IHttpResponseBodyFeature>(capture);

                try
                {
                    await _next(context).ConfigureAwait(false);

                    // Downstream may have written through the PipeWriter rather than the
                    // stream; completing it is what pushes those bytes into the buffer.
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
                // Something wrote past the capture. Nothing safe left to do.
                return;
            }

            if (!IsTransformable(context.Response))
            {
                await PassThroughAsync(context, produced).ConfigureAwait(false);
                return;
            }

            string html;
            try
            {
                var document = Encoding.UTF8.GetString(produced);

                // Already there — the file is patched on disk in "both" mode, or something
                // else in the pipeline added the same tags. A second copy would give the
                // browser two scripts and two gates.
                if (WebInjection.IsFullyInjected(document))
                {
                    await PassThroughAsync(context, produced).ConfigureAwait(false);
                    return;
                }

                if (!WebInjection.Inject(document, out html))
                {
                    // Neither anchor found and nothing already present, so this is not a
                    // document we understand. Hand back whatever the pipeline made.
                    Fail("index.html has no <head> or </body> to inject into.");
                    await PassThroughAsync(context, produced).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away. Not our problem, and not worth logging.
                return;
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                _logger.LogWarning(
                    ex,
                    "ProfilesPlugin: could not add the client script to index.html; serving it unchanged.");
                await PassThroughAsync(context, produced).ConfigureAwait(false);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = bytes.Length;

            // The body is no longer the file the static file handler measured, so the
            // validators it attached describe something else. Removed rather than
            // recomputed: this document must not be cached anyway, because it carries the
            // ?v= that tells the browser to re-fetch profiles.js after a plugin update.
            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
            context.Response.Headers[HeaderNames.Pragma] = "no-cache";
            context.Response.Headers[HeaderNames.Expires] = "0";

            LastError = null;
            LastServedUtc = DateTime.UtcNow;
            Interlocked.Increment(ref ServedCount);

            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }

        private static void Fail(string reason) => LastError = reason;

        /// <summary>
        /// True when the captured response is a plain, complete HTML document we can edit.
        /// Anything else — a 404, a redirect, a body some other layer has already encoded — is
        /// handed back exactly as it arrived.
        /// </summary>
        private static bool IsTransformable(HttpResponse response)
        {
            if (response.StatusCode != StatusCodes.Status200OK)
            {
                return false;
            }

            // Suspending Accept-Encoding should have prevented this. If something compressed
            // the body regardless, decoding it is not worth the machinery.
            if (response.Headers.ContainsKey(HeaderNames.ContentEncoding))
            {
                return false;
            }

            var contentType = response.ContentType;
            return !string.IsNullOrEmpty(contentType)
                   && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
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
        private static bool ShouldHandle(HttpContext context, IServerConfigurationManager serverConfig)
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

                // If HTTPS is mandatory, a plain HTTP request is supposed to be redirected.
                // We run ahead of that redirect, so step aside rather than answering it.
                if (network.RequireHttps && !context.Request.IsHttps)
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
