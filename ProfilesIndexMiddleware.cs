using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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
    /// Serves Jellyfin's index.html with the plugin's script tags already in it, without
    /// modifying the file on disk.
    ///
    /// <para>
    /// Replaces the mechanism behind issues #17, #11 and #3. Patching index.html needs write
    /// access to a directory the package manager owns, has to be redone after every Jellyfin
    /// update, and — worst of all — gives no way to tell from the outside when the file being
    /// served is not the file being patched. Answering the request directly has none of those
    /// problems.
    /// </para>
    /// <para>
    /// This short-circuits rather than buffering the downstream response. Buffering would mean
    /// swapping <c>IHttpResponseBodyFeature</c>, because StaticFileMiddleware sends physical
    /// files through <c>SendFileAsync</c> and never touches <c>Response.Body</c>, and then
    /// contending with response compression on the way back out. For one small HTML file it is
    /// not worth it.
    /// </para>
    /// </summary>
    public sealed class ProfilesIndexMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ProfilesIndexMiddleware> _logger;

        /// <summary>Number of requests this middleware has answered since the server started.</summary>
        internal static long ServedCount;

        /// <summary>Thread-safe read of <see cref="ServedCount"/>.</summary>
        internal static long ServedCountValue => System.Threading.Interlocked.Read(ref ServedCount);

        /// <summary>When it last answered one, or null if it never has.</summary>
        internal static DateTime? LastServedUtc { get; private set; }

        /// <summary>
        /// Why the last attempt fell through to Jellyfin instead of serving, or null. Only set
        /// for genuine failures — declining because the file is already patched is not one.
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
        /// Handles a request, serving an injected index.html when this is one and passing
        /// everything else straight through.
        /// </summary>
        /// <param name="context">The request.</param>
        /// <param name="appPaths">Jellyfin's paths, for locating the web client.</param>
        /// <param name="serverConfig">Server configuration, for the base URL and HTTPS policy.</param>
        /// <returns>A task.</returns>
        public async Task Invoke(
            HttpContext context,
            IApplicationPaths appPaths,
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

            // Everything that can fail happens before a single byte is written, so any problem
            // can still fall through to Jellyfin's own static file handling.
            string html;
            try
            {
                var indexPath = Path.Combine(appPaths.WebPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    Fail($"{indexPath} does not exist.");
                    await _next(context).ConfigureAwait(false);
                    return;
                }

                var original = await File.ReadAllTextAsync(indexPath, context.RequestAborted)
                    .ConfigureAwait(false);

                // Already patched on disk — by our own bootstrap task in "both" mode, or by
                // someone running File Transformation as well. Injecting again would give the
                // browser two copies of the script and two gates.
                if (WebInjection.IsFullyInjected(original))
                {
                    await _next(context).ConfigureAwait(false);
                    return;
                }

                if (!WebInjection.Inject(original, out html))
                {
                    // Nothing to add and nothing already there means neither anchor was found,
                    // so this is not a document we understand. Leave it alone.
                    Fail("index.html has no <head> or </body> to inject into.");
                    await _next(context).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away mid-read. Not our problem, and not worth logging.
                return;
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                _logger.LogWarning(
                    ex,
                    "ProfilesPlugin: could not serve an injected index.html; falling back to Jellyfin's own copy.");
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (context.Response.HasStarted)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = bytes.Length;

            // index.html must never be cached: it carries the ?v= cache-buster that tells the
            // browser to re-fetch profiles.js after a plugin update, which is worthless if the
            // document holding it is itself served from cache.
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";

            LastError = null;
            LastServedUtc = DateTime.UtcNow;
            System.Threading.Interlocked.Increment(ref ServedCount);

            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }

        private static void Fail(string reason) => LastError = reason;

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
