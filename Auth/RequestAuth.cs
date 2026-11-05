using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Who a request is from, as Jellyfin will decide it.
    ///
    /// <para><b>Why not read the headers ourselves.</b> Bonfire's gates (viewing hours,
    /// maintenance, the picker) ran their own parser, and it disagreed with Jellyfin's in
    /// four ways, each of which let a profile past a gate with any HTTP client (R-1295):
    /// it ignored the scheme (Jellyfin reads <c>Authorization</c> only for <c>MediaBrowser</c>,
    /// or <c>Emby</c> with legacy authorization on), so <c>Emby Token="CHILD"</c> or a junk
    /// scheme plus <c>?ApiKey=CHILD</c> read as no user; it read <c>X-Emby-Token</c> and
    /// <c>api_key</c> with legacy authorization off, when Jellyfin doesn't; and it took the
    /// first of two <c>Token</c> values where Jellyfin takes the last. A gate that can't name
    /// the user lets the request through, and Jellyfin then served it as the child.</para>
    ///
    /// <para><b>How.</b> Jellyfin's own <see cref="IAuthorizationContext"/>. Its
    /// <c>HttpContext</c> overload keeps the answer in <c>HttpContext.Items</c>, and Jellyfin's
    /// authentication handler asks the same question of the same request later, so a request
    /// that goes on into Jellyfin is still looked up once. Not a port of its parser: a copy
    /// drifts, and Jellyfin's code is GPL where this is MIT.</para>
    /// </summary>
    internal static class RequestAuth
    {
        /// <summary>
        /// Jellyfin's reading of the request, or null when there is no authorization context to
        /// ask (a harness that doesn't provide one).
        /// </summary>
        public static async Task<AuthorizationInfo?> InfoAsync(HttpContext context)
        {
            if (context.RequestServices?.GetService(typeof(IAuthorizationContext)) is not IAuthorizationContext auth) return null;
            return await auth.GetAuthorizationInfo(context).ConfigureAwait(false);
        }

        /// <summary>
        /// The account the request will run as, or empty: no token, a token Jellyfin will
        /// refuse, or an API key, which is no account. Throws only what Jellyfin throws;
        /// every caller already passes a request on when its own check fails.
        /// </summary>
        public static async Task<Guid> UserIdAsync(HttpContext context, Func<Guid> fallback)
        {
            var info = await InfoAsync(context).ConfigureAwait(false);
            if (info == null) return fallback();
            return info.IsAuthenticated && !info.IsApiKey && info.User != null ? info.User.Id : Guid.Empty;
        }
    }
}
