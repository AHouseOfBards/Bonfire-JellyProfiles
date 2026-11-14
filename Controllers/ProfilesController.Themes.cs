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
    /// <see cref="ProfilesController"/>, so tests/js/apidocs.js reads these routes with the rest.
    /// The web client does not use them; its themes are spliced into profiles.js.
    /// <para>
    /// Both are anonymous on purpose (beta 8): an app draws its switcher before anyone signs in,
    /// and nothing here belongs to a household. Only the server's default theme and whether
    /// profiles may choose are shown.
    /// </para>
    /// </summary>
    public partial class ProfilesController
    {
        /// <summary>Every theme, the fonts and pictures they use, and the server's default.</summary>
        [HttpGet("themes")]
        [Produces("application/json")]
        public ActionResult GetThemes([FromQuery] string? part = null)
        {
            var payload = BuildThemesPayload(customOnly: part == "custom");
            if (payload == null) return NotFound();

            var etag = ThemeCatalog.ETagFor(payload);
            Response.Headers["ETag"] = etag;
            // Revalidated every time: the default theme can change at any moment, and an unchanged
            // answer costs a 304.
            Response.Headers["Cache-Control"] = "public, no-cache";
            if (IfNoneMatchHas(etag))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Content(payload, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// The body <c>/themes</c> sends, or null when there is no configuration (its 404);
        /// shared with <c>/startup</c>.
        /// </summary>
        private string? BuildThemesPayload(bool customOnly)
        {
            // Copied under the lock, built outside it. The payload reads the custom theme and font
            // lists, which a save can change, and a cache miss re-checks every custom theme; an anonymous
            // caller must not make an administrator's save wait for that.
            PluginConfiguration snapshot;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return null;
                snapshot = config.SnapshotForThemes();
            }

            return ThemeCatalog.PublicPayload(snapshot, customOnly);
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

                if (snapshot != null)
                {
                    // Named by contents, so the ETag is the name and a browser that has it is answered before
                    // the file is opened (built-ins are cached; these were read from disk on every request,
                    // 304s included).
                    var tag = ThemeCatalog.CustomFontETag(file);
                    if (IfNoneMatchHas(tag))
                    {
                        Response.Headers["ETag"] = tag;
                        Response.Headers["Cache-Control"] = "public, max-age=86400";
                        Response.Headers["Access-Control-Allow-Origin"] = "*";
                        return StatusCode(StatusCodes.Status304NotModified);
                    }

                    found = ThemeCatalog.CustomFontFile(file, snapshot);
                }
            }

            if (found == null) return NotFound();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            // A different plugin build can carry a different file under the same name, so a day plus
            // revalidation, not forever.
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            // A packaged client (Tizen, whose page is the app) fetches fonts cross-origin, and browsers
            // refuse a font without this.
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
            // Only the current logo, by a name that can only be a hash and an extension, so the join
            // below cannot leave the folder and an old logo is not served.
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
            // Only a name that can be a hash and an extension, so the join below cannot leave the
            // folder, and only a file Bonfire's upload wrote there, not just ones a saved theme names:
            // the editor shows a picture before the theme is saved.
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

        /// <summary>
        /// A pattern picture an administrator uploaded, by the name a theme's pattern gives.
        /// Anonymous like the background image, since the picker draws before sign-in. A route of its
        /// own, not a branch of <see cref="GetThemePattern"/>, which joins no path from its name and
        /// is held to that by tests/cs/themes.
        /// </summary>
        [HttpGet("themes/pattern-files/{file}")]
        public ActionResult GetThemePatternFile(string file)
        {
            // Only a name that can be a hash and an extension, so the join below cannot leave the folder.
            if (!ThemePattern.IsFileName(file)) return NotFound();
            var path = Path.Combine(ThemePattern.Folder, file);
            if (!System.IO.File.Exists(path)) return NotFound();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Named by its contents: a new picture is a new address, so this one never changes.
            Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            var etag = "\"" + file + "\"";
            Response.Headers["ETag"] = etag;
            if (IfNoneMatchHas(etag)) return StatusCode(StatusCodes.Status304NotModified);
            return PhysicalFile(path, file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/webp");
        }

        /// <summary>One pattern picture, by the name a theme's decoration gives.</summary>
        [HttpGet("themes/patterns/{name}")]
        public ActionResult GetThemePattern(string name)
        {
            // Looked up, never joined to a path: the name arrives from the URL.
            if (string.IsNullOrEmpty(name) || !ThemeCatalog.Patterns.TryGetValue(name, out var svg)) return NotFound();

            // Built-in files, but SVG can carry script if opened on its own. Nothing of ours does; this
            // keeps it that way if one is added unnoticed.
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
