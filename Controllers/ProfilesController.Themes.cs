using System;
using System.IO;
using System.Linq;
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
        public ActionResult GetThemes([FromQuery] string? part = null)
        {
            // Copied under the lock, built outside it. The payload reads the custom theme and
            // font lists, which a save can change underneath it, but on a cache miss it
            // re-checks every custom theme, and an anonymous caller must not make an
            // administrator's save wait for that.
            PluginConfiguration snapshot;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return NotFound();
                snapshot = config.SnapshotForThemes();
            }

            var payload = ThemeCatalog.PublicPayload(snapshot, customOnly: part == "custom");

            var etag = ThemeCatalog.ETagFor(payload);
            Response.Headers["ETag"] = etag;
            // Revalidated every time: the default theme can change at any moment, and an
            // unchanged answer costs a 304.
            Response.Headers["Cache-Control"] = "public, no-cache";
            if (IfNoneMatchHas(etag))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(payload, "application/json", Encoding.UTF8);
        }

        /// <summary>One font file Bonfire serves, or the licence that comes with it.</summary>
        [HttpGet("themes/fonts/{file}")]
        public ActionResult GetThemeFont(string file)
        {
            // Looked up among the embedded files, never joined to a path. An uploaded font is
            // looked up in the configuration's list first, by a name that can only be a hash.
            var found = ThemeCatalog.FontFile(file);
            if (found == null && ThemeCatalog.IsCustomFontFileName(file))
            {
                PluginConfiguration? snapshot = null;
                lock (ConfigLock)
                {
                    var config = Plugin.Instance?.Configuration;
                    if (config != null && config.CustomFonts.Any(f => f.File == file))
                        snapshot = new PluginConfiguration { CustomFonts = config.CustomFonts.Where(f => f.File == file).ToList() };
                }

                if (snapshot != null) found = ThemeCatalog.CustomFontFile(file, snapshot);
            }

            if (found == null) return NotFound();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            // A different plugin build can carry a different file under the same name, so a
            // day and a revalidation rather than forever.
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            // A font is fetched cross-origin by a packaged client (Tizen, whose page is the
            // app), and browsers refuse a font without this.
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["ETag"] = found.Value.ETag;
            if (IfNoneMatchHas(found.Value.ETag))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return File(found.Value.Bytes, found.Value.ContentType);
        }

        /// <summary>The server's logo image, by the name GET /themes gives it.</summary>
        [HttpGet("themes/logo/{file}")]
        public ActionResult GetThemeLogo(string file)
        {
            // Only the current logo, by a name that can only be a hash and an extension, so
            // the join below cannot leave the folder and an old logo is not served on.
            bool current;
            lock (ConfigLock)
            {
                current = ThemeLogo.IsFileName(file) && Plugin.Instance?.Configuration?.LogoImage == file;
            }

            var path = Path.Combine(ThemeLogo.Folder, file);
            if (!current || !System.IO.File.Exists(path)) return NotFound();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Named by its contents: a new logo is a new address, so this one never changes.
            Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            var type = file.EndsWith(".png", StringComparison.Ordinal) ? "image/png"
                : file.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp" : "image/jpeg";
            return PhysicalFile(path, type);
        }

        /// <summary>
        /// A custom theme's background image, by the name its file gives. Anonymous for the
        /// same reason as the logo: the picker draws before anyone signs in.
        /// </summary>
        [HttpGet("themes/backgrounds/{file}")]
        public ActionResult GetThemeBackground(string file)
        {
            // Only a name that can be a hash and an extension, so the join below cannot leave
            // the folder, and only a file Bonfire's upload wrote there. Not only the ones a
            // saved theme names: the editor shows a picture before the theme is saved.
            if (!ThemeLogo.IsFileName(file)) return NotFound();
            var path = Path.Combine(ThemeBackground.Folder, file);
            if (!System.IO.File.Exists(path)) return NotFound();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Named by its contents: a new picture is a new address, so this one never changes.
            Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            var type = file.EndsWith(".png", StringComparison.Ordinal) ? "image/png"
                : file.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp" : "image/jpeg";
            return PhysicalFile(path, type);
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
            if (IfNoneMatchHas(etag))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(svg, "image/svg+xml", Encoding.UTF8);
        }
    }
}
