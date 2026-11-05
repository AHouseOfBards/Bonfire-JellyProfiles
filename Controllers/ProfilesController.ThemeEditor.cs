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
    /// and the fonts they upload. Every route here is administrator-only (Logan, beta 8). What
    /// they make reaches everyone else through <c>GET /themes</c>.
    /// <para>
    /// A theme is checked by <see cref="ThemeCatalog.ValidateCustom"/> before it is stored,
    /// and again whenever it is served, so this is not the only gate. It is the one that tells
    /// the administrator which value is wrong.
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

                // Every custom theme, the broken ones too, each with what is wrong with it: a
                // theme edited by hand into something invalid is left out of every picker, and
                // this is where the administrator finds out why.
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
                });
            }
        }

        /// <summary>
        /// Checks a theme without saving it, for the editor's live warnings. Contrast is
        /// computed here rather than in the page so there is one implementation of the rules.
        /// </summary>
        [HttpPost("admin/themes/validate")]
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

        /// <summary>Saves one custom theme, new or edited. Its id is the one in the address.</summary>
        [HttpPut("admin/themes/custom/{id}")]
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

                var problems = ThemeCatalog.ValidateCustom(node, config).ToList();
                var bodyId = node?["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (problems.Count == 0 && bodyId != id)
                    problems.Add(new ThemeProblem("id", "range", "The id must match the address."));
                if (problems.Count > 0)
                {
                    // A list, not a sentence, so the code is set here: the filter only reads
                    // a message.
                    Response.Headers[ErrorCodes.Header] = "theme_invalid";
                    return BadRequest(new { message = "That theme isn't valid.", problems = ProblemList(problems) });
                }

                var existing = config.CustomThemes.FirstOrDefault(c => c.Id == id);
                if (existing == null && config.CustomThemes.Count >= ThemeCatalog.MaxCustomThemes)
                    return BadRequest("Delete a custom theme first. A server can keep up to " + ThemeCatalog.MaxCustomThemes + ".");

                // Stored as compact JSON of the parsed file, not the text that arrived: what is
                // kept is exactly what was checked, without the whitespace.
                var json = node!.ToJsonString();
                if (existing != null) existing.Json = json;
                else config.CustomThemes.Add(new CustomTheme { Id = id, Json = json });
                Plugin.Instance?.SaveConfiguration();
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
                config.CustomThemes.Remove(existing);
                Plugin.Instance?.SaveConfiguration();
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

                config.HiddenThemes = ids;
                Plugin.Instance?.SaveConfiguration();
                return Ok(new { hidden = ids });
            }
        }

        /// <summary>
        /// Uploads one WOFF2 font. The body is the file itself. <c>rights=true</c> says the
        /// administrator may use it; <c>fallback</c> is <c>sans-serif</c> or <c>serif</c>.
        /// <c>family</c> names it; without one, the name the file gives itself is used.
        /// <para>
        /// A name can be given because the file's own is often not the family's: Google's
        /// subsetter names a variable font's default instance, so Outfit's file calls itself
        /// "Outfit Thin". The name is only what CSS and the editor call it.
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
                Plugin.Instance?.SaveConfiguration();
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

                config.CustomFonts.Remove(font);
                Plugin.Instance?.SaveConfiguration();

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
                .Where(c => ParseTheme(c.Json)?["type"]?["font"]?["family"] is JsonValue v
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
