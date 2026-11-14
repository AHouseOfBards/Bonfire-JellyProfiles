using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// The administrator's side of themes (1.7.2): their own themes, the built-ins they hide,
    /// and the fonts they upload. Every route is administrator-only. What they make reaches
    /// everyone else through <c>GET /themes</c>.
    /// <para>
    /// A theme is checked by <see cref="ThemeCatalog.ValidateCustom"/> before it is stored and
    /// again when served. This check is the one that tells the administrator which value is wrong.
    /// </para>
    /// </summary>
    public partial class ProfilesController
    {
        /// <summary>Everything the settings page's Themes section shows.</summary>
        [HttpGet("admin/themes")]
        [Produces("application/json")]
        public ActionResult GetThemeManagement()
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                // Every custom theme, broken ones too, each with what is wrong with it. A theme edited
                // by hand into something invalid is left out of every picker; this is where the
                // administrator finds out why.
                var custom = config.CustomThemes.Select(c =>
                {
                    var node = ParseTheme(c.Json);
                    return new
                    {
                        id = c.Id,
                        theme = node,
                        problems = ProblemList(ThemeCatalog.ValidateCustom(node, config)),
                    };
                }).ToList();

                return Ok(new
                {
                    @default = Themes.Normalize(config.DefaultTheme, config),
                    profilesCanChoose = config.AllowProfileThemes,
                    maxCustomThemes = ThemeCatalog.MaxCustomThemes,
                    maxCustomFonts = ThemeCatalog.MaxCustomFonts,
                    maxFontBytes = ThemeCatalog.MaxFontBytes,
                    builtIn = Themes.All.Select(id => new
                    {
                        id,
                        hidden = id != Themes.Bonfire && config.HiddenThemes.Contains(id),
                    }).ToList(),
                    custom,
                    fonts = config.CustomFonts.Select(f => FontEntry(f, config)).ToList(),
                    builtInFonts = ThemeCatalog.FontFamilies,
                    // The same three fields GET /themes gives every app.
                    logo = JsonNode.Parse(ThemeCatalog.LogoPayload(config).ToJsonString()),
                    maxLogoBytes = ThemeLogo.MaxBytes,
                });
            }
        }

        /// <summary>
        /// Sets the logo's text, alt text and corners. A field the body leaves out stays as it is;
        /// send "" or null to clear it. The image is its own route.
        /// </summary>
        [HttpPut("admin/themes/logo")]
        [Produces("application/json")]
        public ActionResult SetThemeLogo([FromBody] JsonElement body)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            if (body.ValueKind != JsonValueKind.Object) return BadRequest("Send the logo's text and alt text.");

            // A field the body leaves out stays as it is, so a client that sets one does not wipe
            // the other. null or "" clears it. Anything that is not text is refused (R-1090).
            bool Has(string name, out JsonElement v) => TryProperty(body, name, out v);
            string? Read(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            string? text = null, alt = null;
            var hasText = Has("text", out var textEl);
            var hasAlt = Has("alt", out var altEl);
            try
            {
                if (hasText)
                {
                    if (textEl.ValueKind != JsonValueKind.String && textEl.ValueKind != JsonValueKind.Null) throw new ArgumentException("text");
                    text = ThemeLogo.CleanText(Read(textEl), ThemeLogo.MaxText);
                }
            }
            catch (ArgumentException)
            {
                return BadRequest("Logo text is one line of up to " + ThemeLogo.MaxText + " characters.");
            }

            try
            {
                if (hasAlt)
                {
                    if (altEl.ValueKind != JsonValueKind.String && altEl.ValueKind != JsonValueKind.Null) throw new ArgumentException("alt");
                    alt = ThemeLogo.CleanText(Read(altEl), ThemeLogo.MaxAlt);
                }
            }
            catch (ArgumentException)
            {
                return BadRequest("Logo alt text is one line of up to " + ThemeLogo.MaxAlt + " characters.");
            }

            // Only the two values: anything else is refused rather than read as the default.
            string? corners = null;
            var hasCorners = Has("corners", out var cornersEl);
            if (hasCorners)
            {
                corners = cornersEl.ValueKind == JsonValueKind.String ? cornersEl.GetString() : null;
                if (corners != ThemeLogo.Rounded && corners != ThemeLogo.Square)
                    return BadRequest("Logo corners are \"rounded\" or \"square\".");
            }

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                var (oldText, oldAlt, oldCorners) = (config.LogoText, config.LogoAlt, config.LogoCorners);
                if (hasText) config.LogoText = text;
                if (hasAlt) config.LogoAlt = alt;
                if (hasCorners) config.LogoCorners = corners!;
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    // Not saved, so not changed: the live object would otherwise say what disk does not.
                    (config.LogoText, config.LogoAlt, config.LogoCorners) = (oldText, oldAlt, oldCorners);
                    throw;
                }
                return Ok(JsonNode.Parse(ThemeCatalog.LogoPayload(config).ToJsonString()));
            }
        }

        /// <summary>Uploads the logo image. The body is the file: PNG, JPEG or WebP, up to 1 MB.</summary>
        [HttpPost("admin/themes/logo/image")]
        [RequestSizeLimit(ThemeLogo.MaxBytes + 4096)]
        [Produces("application/json")]
        public async Task<ActionResult> UploadThemeLogo()
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            if (Request.ContentLength > ThemeLogo.MaxBytes) return BadRequest("That logo is larger than 1 MB.");
            byte[] bytes;
            using (var copy = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > ThemeLogo.MaxBytes) return BadRequest("That logo is larger than 1 MB.");
                }

                bytes = copy.ToArray();
            }

            if (bytes.Length == 0) return BadRequest("Choose a logo image.");

            LogoImageInfo info;
            try
            {
                info = ThemeLogo.Read(bytes);
            }
            catch (InvalidDataException e)
            {
                Response.Headers[ErrorCodes.Header] = "image_invalid";
                return BadRequest(e.Message);
            }

            var file = ThemeLogo.FileNameFor(bytes, info);
            string? old;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                // Written before it is named, so the configuration never names a missing file.
                Directory.CreateDirectory(ThemeLogo.Folder);
                System.IO.File.WriteAllBytes(Path.Combine(ThemeLogo.Folder, file), bytes);
                old = config.LogoImage;
                config.LogoImage = file;
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    // Not saved, so not named: put the old logo back and take the new file
                    // away, unless it is the file the old name already points at.
                    config.LogoImage = old;
                    if (old != file) DeleteLogoFile(file);
                    throw;
                }

                // Deleted here, not after the lock: two uploads at once could otherwise have the first
                // delete a file the second had just made current, leaving LogoImage naming nothing
                // (R-1083).
                if (old != null && old != file) DeleteLogoFile(old);
            }

            return Ok(new { image = ThemeLogo.Url(file), width = info.Width, height = info.Height });
        }

        /// <summary>Removes the logo image. The text, if any, shows instead.</summary>
        [HttpDelete("admin/themes/logo/image")]
        public ActionResult DeleteThemeLogo()
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            string? old;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                old = config.LogoImage;
                config.LogoImage = null;
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    config.LogoImage = old;
                    throw;
                }
                if (old != null) DeleteLogoFile(old);   // under the lock, as the upload does (R-1083)
            }

            return NoContent();
        }

        /// <summary>Deletes a replaced logo file. Named by a checked hash, so the join cannot leave the folder.</summary>
        private void DeleteLogoFile(string file)
        {
            if (!ThemeLogo.IsFileName(file)) return;
            try
            {
                var path = Path.Combine(ThemeLogo.Folder, file);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (IOException e)
            {
                // No longer named anywhere, so never served: a file left behind costs disk only.
                _logger.LogWarning(e, "ProfilesPlugin: could not delete the old logo {File}.", file);
            }
        }

        /// <summary>
        /// Uploads a background image for a custom theme: PNG, JPEG or WebP, up to 2 MB. Only
        /// stored; the theme names it when saved. A file no theme names goes on the next save that
        /// drops it, or with the orphan cleanup.
        /// </summary>
        [HttpPost("admin/themes/backgrounds")]
        [RequestSizeLimit(ThemeBackground.MaxBytes + 4096)]
        [Produces("application/json")]
        public async Task<ActionResult> UploadThemeBackground()
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            if (Request.ContentLength > ThemeBackground.MaxBytes) return BadRequest("That background image is larger than 2 MB.");
            // Read to a cap rather than trusting Content-Length, which a chunked upload omits.
            byte[] bytes;
            using (var copy = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > ThemeBackground.MaxBytes) return BadRequest("That background image is larger than 2 MB.");
                }

                bytes = copy.ToArray();
            }

            if (bytes.Length == 0) return BadRequest("Choose a background image.");

            LogoImageInfo info;
            try
            {
                info = ThemeBackground.Read(bytes);
            }
            catch (InvalidDataException e)
            {
                Response.Headers[ErrorCodes.Header] = "image_invalid";
                return BadRequest(e.Message);
            }

            var file = ThemeLogo.FileNameFor(bytes, info);
            // Under the lock, as every deletion of an unnamed file is, so a save dropping the
            // same picture cannot delete it between this write and the editor's save.
            lock (ConfigLock)
            {
                Directory.CreateDirectory(ThemeBackground.Folder);
                System.IO.File.WriteAllBytes(Path.Combine(ThemeBackground.Folder, file), bytes);
            }

            return Ok(new { file, url = ThemeBackground.Url(file), width = info.Width, height = info.Height });
        }

        /// <summary>
        /// Uploads a pattern picture for a custom theme: PNG or WebP, up to 2 MB, not animated.
        /// Only stored, like a background image.
        /// </summary>
        [HttpPost("admin/themes/patterns")]
        [RequestSizeLimit(ThemePattern.MaxBytes + 4096)]
        [Produces("application/json")]
        public async Task<ActionResult> UploadThemePattern()
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            if (Request.ContentLength > ThemePattern.MaxBytes) return BadRequest("That pattern picture is larger than 2 MB.");
            // Read to a cap rather than trusting Content-Length, which a chunked upload omits.
            byte[] bytes;
            using (var copy = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > ThemePattern.MaxBytes) return BadRequest("That pattern picture is larger than 2 MB.");
                }

                bytes = copy.ToArray();
            }

            if (bytes.Length == 0) return BadRequest("Choose a pattern picture.");

            LogoImageInfo info;
            try
            {
                info = ThemePattern.Read(bytes);
            }
            catch (InvalidDataException e)
            {
                Response.Headers[ErrorCodes.Header] = "image_invalid";
                return BadRequest(e.Message);
            }

            var file = ThemeLogo.FileNameFor(bytes, info);
            // Under the lock, as every deletion of an unnamed file is, so a save dropping the
            // same picture cannot delete it between this write and the editor's save.
            lock (ConfigLock)
            {
                Directory.CreateDirectory(ThemePattern.Folder);
                System.IO.File.WriteAllBytes(Path.Combine(ThemePattern.Folder, file), bytes);
            }

            return Ok(new { file, url = ThemePattern.Url(file), width = info.Width, height = info.Height });
        }

        /// <summary>
        /// Deletes the uploaded pattern picture <paramref name="before"/> when no custom theme
        /// names it any more. Called under ConfigLock, after the save that stopped naming it.
        /// </summary>
        private void DropUnusedPattern(string? before, PluginConfiguration config)
        {
            if (before != null && !ThemePattern.DeleteIfUnused(before, config))
                _logger.LogWarning("ProfilesPlugin: could not delete the unused pattern picture {File}.", before);
        }

        /// <summary>
        /// Deletes the stored background images no custom theme names, after
        /// <paramref name="before"/> was replaced or removed. Called under ConfigLock, after
        /// the save that stopped naming them.
        /// </summary>
        private void DropUnusedBackground(string? before, PluginConfiguration config)
        {
            if (before != null && !ThemeBackground.DeleteIfUnused(before, config))
                _logger.LogWarning("ProfilesPlugin: could not delete the unused background image {File}.", before);
        }

        /// <summary>
        /// Checks a theme without saving it, for the editor's live warnings. The rules live here
        /// so there is one implementation. A custom theme has no contrast rules.
        /// </summary>
        [HttpPost("admin/themes/validate")]
        [RequestSizeLimit(ThemeValidator.MaxBytes + 4096)]
        [Produces("application/json")]
        public ActionResult ValidateCustomTheme([FromBody] JsonElement body)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            var node = ParseTheme(body.ValueKind == JsonValueKind.Undefined ? null : body.GetRawText());
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                return Ok(new { problems = ProblemList(ThemeCatalog.ValidateCustom(node, config)) });
            }
        }

        /// <summary>The revision in a stored theme's file, 0 when there is none to read.</summary>
        private static int StoredRevision(CustomTheme? theme)
        {
            if (theme == null) return 0;
            try
            {
                return JsonNode.Parse(theme.Json) is JsonObject o && o["revision"] is JsonValue v && v.TryGetValue<double>(out var r) ? (int)r : 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Saves one custom theme, new or edited. Its id is the one in the address. An edit
        /// has to carry a revision above the stored one, or <c>409</c>; one with no revision
        /// takes the next.
        /// </summary>
        [HttpPut("admin/themes/custom/{id}")]
        [RequestSizeLimit(ThemeValidator.MaxBytes + 4096)]
        [Produces("application/json")]
        public ActionResult SaveCustomTheme(string id, [FromBody] JsonElement body)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            var node = ParseTheme(body.ValueKind == JsonValueKind.Undefined ? null : body.GetRawText());
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                // The revision a save has to beat. A file with none takes the next one, so a caller
                // that ignores revisions keeps working (last write wins).
                var stored = config.CustomThemes.FirstOrDefault(c => c.Id == id);
                var storedRevision = StoredRevision(stored);
                if (node is JsonObject file && !file.ContainsKey("revision")) file["revision"] = storedRevision + 1;

                var problems = ThemeCatalog.ValidateCustom(node, config).ToList();
                // Only an object has an "id" to read: an array or a bare value throws on the
                // indexer, and the validator has already said why it is not a theme.
                var bodyId = node is JsonObject asObject && asObject["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (problems.Count == 0 && bodyId != id)
                    problems.Add(new ThemeProblem("id", "range", "The id must match the address."));
                // A name the validator accepts may still be a picture this server does not
                // have: a file brought from another server, or one the cleanup took.
                var image = ThemeBackground.FileOf(node);
                if (problems.Count == 0 && image != null && !ThemeBackground.Exists(image))
                    problems.Add(new ThemeProblem("background.image.file", "image", "Upload the image again."));
                var patternFiles = ThemePattern.FilesOf(node);
                if (problems.Count == 0 && patternFiles.Any(f => !ThemePattern.Exists(f)))
                    problems.Add(new ThemeProblem("decorations", "image", "Upload the pattern picture again."));
                if (problems.Count > 0)
                {
                    // A list, not a sentence, so the code is set here: the filter only reads
                    // a message.
                    Response.Headers[ErrorCodes.Header] = "theme_invalid";
                    return BadRequest(new { message = "That theme isn't valid.", problems = ProblemList(problems) });
                }

                var existing = stored;

                // A save has to carry a revision above the stored one. One that does not was made from
                // a copy another save has since replaced, and would overwrite that save for every
                // profile at once. Refused; the page says to reload (R-1089).
                if (existing != null && node!["revision"] is JsonValue rev && rev.TryGetValue<double>(out var sent) && sent <= storedRevision)
                    return Conflict("That theme was changed somewhere else. Reload it and try again.");

                if (existing == null && config.CustomThemes.Count >= ThemeCatalog.MaxCustomThemes)
                    return BadRequest("Delete a custom theme first. A server can keep up to " + ThemeCatalog.MaxCustomThemes + ".");

                // Stored as compact JSON of the parsed file, not the text that arrived: what is
                // kept is exactly what was checked, without the whitespace.
                var json = node!.ToJsonString();
                var before = ThemeBackground.FileOf(existing?.Json);
                var oldJson = existing?.Json;
                CustomTheme? added = null;
                var patternsBefore = ThemePattern.FilesOf(existing?.Json);
                if (existing != null) existing.Json = json;
                else config.CustomThemes.Add(added = new CustomTheme { Id = id, Json = json });
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    if (added != null) config.CustomThemes.Remove(added);
                    else existing!.Json = oldJson!;
                    throw;
                }
                // After the save, so a save that throws leaves the old picture where the
                // configuration still names it.
                if (before != image) DropUnusedBackground(before, config);
                foreach (var gone in patternsBefore.Where(f => !patternFiles.Contains(f))) DropUnusedPattern(gone, config);
                return Ok(new { id, created = existing == null });
            }
        }

        /// <summary>Deletes one custom theme. Profiles that chose it see the default.</summary>
        [HttpDelete("admin/themes/custom/{id}")]
        public ActionResult DeleteCustomTheme(string id)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                var existing = config.CustomThemes.FirstOrDefault(c => c.Id == id);
                if (existing == null) return NotFound("No such theme.");
                if (Themes.Normalize(config.DefaultTheme, config) == id)
                    return BadRequest("That's the default theme. Choose another default first.");

                // Profiles keep the id they chose. Resolve shows them the default while the
                // theme is gone, and a theme saved again under the same id gives it back.
                var at = config.CustomThemes.IndexOf(existing);
                config.CustomThemes.RemoveAt(at);
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    config.CustomThemes.Insert(at, existing);
                    throw;
                }
                DropUnusedBackground(ThemeBackground.FileOf(existing.Json), config);
                foreach (var gone in ThemePattern.FilesOf(existing.Json)) DropUnusedPattern(gone, config);
            }

            return NoContent();
        }

        /// <summary>
        /// Replaces the list of built-in themes taken out of the pickers. Never Bonfire, and
        /// never the default.
        /// </summary>
        [HttpPut("admin/themes/hidden")]
        [Produces("application/json")]
        public ActionResult SetHiddenThemes([FromBody] JsonElement body)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            if (body.ValueKind != JsonValueKind.Object || !TryProperty(body, "hidden", out var list) || list.ValueKind != JsonValueKind.Array)
                return BadRequest("Send the list of themes to hide.");

            var ids = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                var raw = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!Themes.IsBuiltIn(raw)) return BadRequest("Only built-in themes can be hidden.");
                var id = raw!.Trim().ToLowerInvariant();
                if (id == Themes.Bonfire) return BadRequest("Bonfire can't be hidden.");
                if (!ids.Contains(id)) ids.Add(id);
            }

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                // Read under the lock with the change it guards: a default set between the
                // check and the save would otherwise be hidden from under itself.
                if (ids.Contains(Themes.Normalize(config.DefaultTheme, config)))
                    return BadRequest("That's the default theme. Choose another default first.");

                var oldHidden = config.HiddenThemes;
                config.HiddenThemes = ids;
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    config.HiddenThemes = oldHidden;
                    throw;
                }
                return Ok(new { hidden = ids });
            }
        }

        /// <summary>
        /// Uploads one WOFF2 font. The body is the file. <c>rights=true</c> says the administrator
        /// may use it; <c>fallback</c> is <c>sans-serif</c> or <c>serif</c>. <c>family</c> names it;
        /// without one, the file's own name is used.
        /// <para>
        /// A name can be given because the file's own is often wrong: Google's subsetter names a
        /// variable font's default instance, so Outfit's file calls itself "Outfit Thin".
        /// </para>
        /// </summary>
        [HttpPost("admin/themes/fonts")]
        [RequestSizeLimit(ThemeCatalog.MaxFontBytes + 4096)]
        [Produces("application/json")]
        public async Task<ActionResult> UploadThemeFont(
            [FromQuery] string? fallback = null, [FromQuery] bool rights = false, [FromQuery] string? family = null)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            if (!rights) return BadRequest("Confirm you have the right to use this font.");
            var fb = string.IsNullOrEmpty(fallback) ? "sans-serif" : fallback;
            if (!ThemeCatalog.CustomFontFallbacks.Contains(fb)) return BadRequest("A font falls back to sans-serif or serif.");
            if (Request.ContentLength > ThemeCatalog.MaxFontBytes) return BadRequest("That font is larger than 2 MB.");

            // Read to a cap rather than trusting Content-Length, which a chunked upload omits.
            byte[] bytes;
            using (var copy = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > ThemeCatalog.MaxFontBytes) return BadRequest("That font is larger than 2 MB.");
                }

                bytes = copy.ToArray();
            }

            if (bytes.Length == 0) return BadRequest("Choose a WOFF2 font file.");

            Woff2Info info;
            try
            {
                info = Woff2Font.Read(bytes);
            }
            catch (InvalidDataException e)
            {
                // The reader's reasons are each a sentence for the administrator, and too many
                // to match one by one, so the code is set here.
                Response.Headers[ErrorCodes.Header] = "font_invalid";
                return BadRequest(e.Message);
            }

            if (!string.IsNullOrWhiteSpace(family))
            {
                if (!Woff2Font.IsSafeFamily(family.Trim()))
                    return BadRequest("A font name can use letters, digits, spaces, - _ and . only.");
                info = info with { Family = family.Trim() };
            }

            if (ThemeCatalog.FontFamilies.Contains(info.Family, StringComparer.OrdinalIgnoreCase))
                return BadRequest("Bonfire already has a font called " + info.Family + ".");

            var file = ThemeCatalog.CustomFontFileName(bytes);
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                // The same file again is the font already here, not an error.
                var same = config.CustomFonts.FirstOrDefault(f => f.File == file);
                if (same != null) return Ok(FontEntry(same, config));

                if (config.CustomFonts.Any(f => string.Equals(f.Family, info.Family, StringComparison.OrdinalIgnoreCase)))
                    return BadRequest("Bonfire already has a font called " + info.Family + ". Delete it first.");
                if (config.CustomFonts.Count >= ThemeCatalog.MaxCustomFonts)
                    return BadRequest("Delete a font first. A server can keep up to " + ThemeCatalog.MaxCustomFonts + ".");

                // Written before it is listed, so a font in the list always has its file.
                Directory.CreateDirectory(ThemeCatalog.CustomFontFolder);
                System.IO.File.WriteAllBytes(Path.Combine(ThemeCatalog.CustomFontFolder, file), bytes);

                var font = new CustomFont
                {
                    Family = info.Family,
                    File = file,
                    WeightMin = info.WeightMin,
                    WeightMax = info.WeightMax,
                    Fallback = fb,
                };
                config.CustomFonts.Add(font);
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    // Not saved, so not listed, and the file written for it goes too.
                    config.CustomFonts.Remove(font);
                    try { System.IO.File.Delete(Path.Combine(ThemeCatalog.CustomFontFolder, file)); } catch (IOException) { }
                    throw;
                }
                _logger.LogInformation("ProfilesPlugin: administrator uploaded the font {Family} as {File}.", info.Family, file);
                return Ok(FontEntry(font, config));
            }
        }

        /// <summary>Deletes one uploaded font. Refused while a custom theme uses it.</summary>
        [HttpDelete("admin/themes/fonts/{file}")]
        public ActionResult DeleteThemeFont(string file)
        {
            var adminError = RequireAdministrator("manage themes");
            if (adminError != null) return adminError;

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");

                var font = ThemeCatalog.IsCustomFontFileName(file) ? config.CustomFonts.FirstOrDefault(f => f.File == file) : null;
                if (font == null) return NotFound("No such font.");
                if (ThemesUsing(font.Family, config).Count > 0)
                    return BadRequest("A theme uses that font. Change or delete the theme first.");

                var fontAt = config.CustomFonts.IndexOf(font);
                config.CustomFonts.RemoveAt(fontAt);
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch
                {
                    config.CustomFonts.Insert(fontAt, font);
                    throw;
                }

                // Named by a checked hash, so the join cannot leave the folder.
                var path = Path.Combine(ThemeCatalog.CustomFontFolder, file);
                try
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                catch (IOException e)
                {
                    // Listed nowhere now, so never served: a file left behind costs disk only.
                    _logger.LogWarning(e, "ProfilesPlugin: could not delete the font file {File}.", file);
                }
            }

            return NoContent();
        }

        /// <summary>A theme file from text, or null for anything that is not JSON.</summary>
        private static JsonNode? ParseTheme(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Problems as the camel-case objects every Bonfire route returns.</summary>
        private static List<object> ProblemList(IEnumerable<ThemeProblem> problems) =>
            problems.Select(p => (object)new { path = p.Path, code = p.Code, detail = p.Detail }).ToList();

        /// <summary>The custom themes whose text names <paramref name="family"/> as their font.</summary>
        private static List<string> ThemesUsing(string family, PluginConfiguration config) =>
            config.CustomThemes
                // Kind checks all the way down: a stored text that is not shaped like a theme
                // (edited by hand) must not take the whole Themes page down.
                .Where(c => ParseTheme(c.Json) is JsonObject t && t["type"] is JsonObject ty && ty["font"] is JsonObject fo
                    && fo["family"] is JsonValue v
                    && v.TryGetValue<string>(out var f) && string.Equals(f, family, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Id)
                .ToList();

        private static object FontEntry(CustomFont f, PluginConfiguration config) => new
        {
            family = f.Family,
            file = f.File,
            url = ThemeCatalog.FontUrl(f.File),
            weightMin = f.WeightMin,
            weightMax = f.WeightMax,
            fallback = f.Fallback,
            usedBy = ThemesUsing(f.Family, config),
        };
    }
}
