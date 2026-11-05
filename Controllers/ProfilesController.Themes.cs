using System;
using System.Text;
using Jellyfin.Profiles.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Themes for other apps (1.7.2): the theme files and the pictures they name. A partial of
    /// <see cref="ProfilesController"/>, so tests/js/apidocs.js reads these routes with the
    /// rest. The web client does not use them; its themes are spliced into profiles.js.
    /// <para>
    /// Both are anonymous, deliberately (Logan, beta 8): an app draws its switcher before
    /// anyone has signed in, and nothing here belongs to a household. The server's default
    /// theme and whether profiles may choose are the only settings they show.
    /// </para>
    /// </summary>
    public partial class ProfilesController
    {
        /// <summary>Every theme, the fonts and pictures they use, and the server's default.</summary>
        [HttpGet("themes")]
        [Produces("application/json")]
        public ActionResult GetThemes()
        {
            string defaultTheme;
            bool allow;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return NotFound();
                defaultTheme = Themes.Normalize(config.DefaultTheme);
                allow = config.AllowProfileThemes;
            }

            var payload = ThemeCatalog.PublicPayload(defaultTheme, allow);
            var etag = ThemeCatalog.ETagFor(payload);
            Response.Headers["ETag"] = etag;
            // Revalidated every time: the default theme can change at any moment, and an
            // unchanged answer costs a 304.
            Response.Headers["Cache-Control"] = "public, no-cache";
            if (string.Equals(Request.Headers["If-None-Match"].ToString(), etag, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(payload, "application/json", Encoding.UTF8);
        }

        /// <summary>One pattern picture, by the name a theme's decoration gives.</summary>
        [HttpGet("themes/patterns/{name}")]
        public ActionResult GetThemePattern(string name)
        {
            // Looked up, never joined to a path: the name arrives from the URL.
            if (string.IsNullOrEmpty(name) || !ThemeCatalog.Patterns.TryGetValue(name, out var svg)) return NotFound();

            // Built-in files, but SVG is a document that can carry script if it is opened on
            // its own. Nothing of ours does; this keeps it that way should one ever be added
            // without looking.
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            var etag = ThemeCatalog.ETagFor(svg);
            Response.Headers["ETag"] = etag;
            if (string.Equals(Request.Headers["If-None-Match"].ToString(), etag, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(svg, "image/svg+xml", Encoding.UTF8);
        }
    }
}
