using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// The address a request came from, as Jellyfin will see it.
    ///
    /// <para><b>Why not <c>Connection.RemoteIpAddress</c>.</b> Bonfire's middleware is added by
    /// an <c>IStartupFilter</c>, so it runs in front of Jellyfin's whole pipeline, including
    /// <c>UseForwardedHeaders</c> (Startup.cs, inside <c>app.Map(BaseUrl)</c>). Behind a
    /// reverse proxy, the address it sees is the proxy's: every request from the internet
    /// looked local, so the picker's keypad skipped the PIN for a profile that bypasses it at
    /// home, and the saved-sign-in lock took a phone on mobile data for the living room
    /// (R-1029). Controllers run after the forwarding and were always right.</para>
    ///
    /// <para><b>How.</b> Jellyfin registers its <see cref="ForwardedHeadersOptions"/> from
    /// Networking → Known proxies; with none set it forwards nothing, and neither do we. The
    /// same options are applied by the framework's own middleware to a copy of the request,
    /// so the answer is Jellyfin's to the letter and the real request is not touched — Jellyfin
    /// still forwards it itself, later, exactly as before.</para>
    /// </summary>
    internal static class ClientAddress
    {
        /// <summary>The client's address after Jellyfin's known-proxy rules, or null if unknown.</summary>
        public static IPAddress? Of(HttpContext context)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote == null) return null;

            ForwardedHeadersOptions? options;
            try
            {
                options = (context.RequestServices?.GetService(typeof(IOptions<ForwardedHeadersOptions>))
                    as IOptions<ForwardedHeadersOptions>)?.Value;
            }
            catch (Exception)
            {
                // A disposed scope and the like. Not forwarding is what Jellyfin does with
                // no known proxies.
                return remote;
            }

            return Resolve(remote, context.Request.Headers, options);
        }

        /// <summary>
        /// <paramref name="remote"/> with <paramref name="options"/> applied to the forwarding
        /// headers in <paramref name="headers"/>. Split out so a harness can hand it the
        /// options Jellyfin would build.
        /// </summary>
        internal static IPAddress Resolve(IPAddress remote, IHeaderDictionary headers, ForwardedHeadersOptions? options)
        {
            if (options == null || (options.ForwardedHeaders & ForwardedHeaders.XForwardedFor) == 0) return remote;

            var forwardedFor = headers[options.ForwardedForHeaderName];
            if (forwardedFor.Count == 0) return remote;

            // Every header the options name, so a proto or host that has to agree with the
            // address (header symmetry) is judged as Jellyfin will judge it. Only the probe
            // is changed by it.
            var probe = new DefaultHttpContext();
            probe.Connection.RemoteIpAddress = remote;
            foreach (var name in new[] { options.ForwardedForHeaderName, options.ForwardedProtoHeaderName, options.ForwardedHostHeaderName, options.ForwardedPrefixHeaderName })
            {
                if (!string.IsNullOrEmpty(name) && headers.TryGetValue(name, out var value)) probe.Request.Headers[name] = value;
            }

            // Jellyfin's options object itself, not a copy. A copy would have to name the
            // known-network list, which .NET 10 renamed (KnownNetworks → KnownIPNetworks):
            // this net9 build, loaded on a 12.0 server, would copy the old list while
            // Jellyfin filled the new one, and every proxied request would look local again.
            new ForwardedHeadersMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options))
                .ApplyForwarders(probe);

            return probe.Connection.RemoteIpAddress ?? remote;
        }
    }
}
