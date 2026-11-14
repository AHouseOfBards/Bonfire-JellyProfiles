using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Who a request is from, as Jellyfin will decide it.
    ///
    /// <para><b>Why not read the headers ourselves.</b> The gates (viewing hours, maintenance,
    /// the picker) ran their own parser, which differed from Jellyfin's in ways that let a
    /// profile past a gate with any HTTP client (R-1295). It ignored the scheme (Jellyfin reads
    /// <c>Authorization</c> only for <c>MediaBrowser</c>, or <c>Emby</c> with legacy
    /// authorization on), so <c>Emby Token="CHILD"</c> or a junk scheme plus
    /// <c>?ApiKey=CHILD</c> read as no user. It read <c>X-Emby-Token</c> and <c>api_key</c> with
    /// legacy authorization off, which Jellyfin does not. And it took the first of two
    /// <c>Token</c> values where Jellyfin takes the last. A gate that cannot name the user lets
    /// the request through, and Jellyfin then served it as the child.</para>
    ///
    /// <para><b>How.</b> Jellyfin's own <see cref="IAuthorizationContext"/>. Its
    /// <c>HttpContext</c> overload caches the answer in <c>HttpContext.Items</c>, and Jellyfin's
    /// authentication handler asks the same question later, so the lookup happens once. Not a
    /// port of its parser: a copy drifts, and Jellyfin's code is GPL where this is MIT.</para>
    /// </summary>
    internal static class RequestAuth
    {
        /// <summary>
        /// Jellyfin's reading of the request, or null when there is no authorization context
        /// (a harness that provides none).
        /// </summary>
        public static async Task<AuthorizationInfo?> InfoAsync(HttpContext context)
        {
            if (context.RequestServices?.GetService(typeof(IAuthorizationContext)) is not IAuthorizationContext auth) return null;
            return await auth.GetAuthorizationInfo(context).ConfigureAwait(false);
        }

        /// <summary>
        /// The account the request will run as, or empty for no token, a token Jellyfin will
        /// refuse, or an API key (no account). Throws only what Jellyfin throws; every caller
        /// already passes a request on when its own check fails.
        /// </summary>
        public static async Task<Guid> UserIdAsync(HttpContext context, Func<Guid> fallback)
        {
            var info = await InfoAsync(context).ConfigureAwait(false);
            if (info == null) return fallback();
            return info.IsAuthenticated && !info.IsApiKey && info.User != null ? info.User.Id : Guid.Empty;
        }
    }
}
