using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// The themes Bonfire knows: the built-in files in <c>Web/themes/</c>, embedded in the assembly.
    /// docs/themes.md describes a file.
    /// <para>
    /// The server never turns a theme into CSS. It hands the files to the web client, which does that
    /// in <c>themeCss</c>, and to other apps, which draw their own way. One generator, so the web
    /// client cannot drift from what the files say.
    /// </para>
    /// </summary>
    public static class ThemeCatalog
    {
        private const string ThemePrefix = "Jellyfin.Profiles.Themes.";
        private const string PatternPrefix = "Jellyfin.Profiles.Themes.Patterns.";

        /// <summary>The built-in themes, parsed once, Bonfire first.</summary>
        public static IReadOnlyList<JsonObject> BuiltIn => Loaded.Value.Themes;

        /// <summary>The font list (<c>Web/themes/fonts.json</c>).</summary>
        public static JsonArray Fonts => (JsonArray)Loaded.Value.Fonts.DeepClone();

        /// <summary>Pattern pictures by name: the SVG text.</summary>
        public static IReadOnlyDictionary<string, string> Patterns => Loaded.Value.Patterns;

        /// <summary>The ids of the built-in themes, in order.</summary>
        public static IReadOnlyList<string> BuiltInIds =>
            Loaded.Value.Themes.Select(t => (string)t["id"]!).ToList();

        /// <summary>The font families a theme may name.</summary>
        public static IReadOnlyList<string> FontFamilies =>
            Loaded.Value.Fonts.Select(f => f?["family"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s != null).Select(s => s!).ToList();

        /// <summary>Every problem with a theme file, against the fonts and pictures Bonfire holds.</summary>
        public static IReadOnlyList<ThemeProblem> Validate(JsonNode? theme) =>
            ThemeValidator.Validate(theme, FontFamilies, Patterns.Keys);

        /// <summary>
        /// What profiles.js is given in place of its <c>__BONFIRE_THEMES__</c> marker: the themes, the
        /// fonts, and each pattern as a data: URL so the web client fetches nothing to draw one.
        /// <para>
        /// Serialized with the default encoder, which escapes everything outside ASCII and the characters
        /// HTML gives meaning to. That keeps U+2028 and U+2029 out of the script (a syntax error before
        /// ES2019, which covers every television down to the Chromium 53 floor) and leaves nothing in a
        /// theme's text that can end the script it sits in.
        /// </para>
        /// </summary>
        public static string ClientPayload()
        {
            var patterns = new JsonObject();
            foreach (var p in Patterns)
            {
                patterns[p.Key] = "data:image/svg+xml," + Uri.EscapeDataString(p.Value.Trim());
            }

            var payload = new JsonObject
            {
                ["themes"] = new JsonArray(BuiltIn.Select(t => (JsonNode)t.DeepClone()).ToArray()),
                ["fonts"] = Fonts,
                ["subsets"] = Loaded.Value.Subsets.DeepClone(),
                ["patterns"] = patterns,
            };
            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>Where a pattern picture is served, relative to the server.</summary>
        public static string PatternUrl(string name) => "/plugins/profiles/themes/patterns/" + Uri.EscapeDataString(name);

        /// <summary>
        /// What <c>GET /plugins/profiles/themes</c> returns: docs/themes.md, "Getting themes". It is for
        /// other apps, so unlike <see cref="ClientPayload"/> the pictures are addresses to fetch, and the
        /// web client's own notes on a font (the names it goes by when installed) are left out.
        /// </summary>
        /// <param name="config">The configuration, read under ConfigLock by the caller.</param>
        /// <param name="customOnly">
        /// <c>?part=custom</c>: only what the administrator added, for the web client, which already holds
        /// the built-ins in profiles.js and would otherwise download every one on every television.
        /// </param>
        public static string PublicPayload(PluginConfiguration config, bool customOnly = false)
        {
            var defaultTheme = Themes.Normalize(config.DefaultTheme, config);
            var fonts = new JsonArray();
            foreach (var f in customOnly ? new JsonArray() : Loaded.Value.Fonts)
            {
                if (f is not JsonObject font) continue;

                // One entry per subset file, with the range of characters it holds, so an app that can load
                // only one fetches latin and an app that can load both knows which to use for what. A family
                // with no files is one Bonfire does not serve: the app draws its fallback.
                var files = new JsonArray();
                if (font["files"] is JsonObject byName)
                {
                    foreach (var sub in Loaded.Value.Subsets)
                    {
                        if (byName[sub.Key] is not JsonValue fv || !fv.TryGetValue<string>(out var file)) continue;
                        files.Add(new JsonObject
                        {
                            ["subset"] = sub.Key,
                            ["url"] = FontUrl(file),
                            ["format"] = "woff2",
                            ["style"] = "normal",
                            ["weight"] = font["weight"]?.DeepClone(),
                            ["unicodeRange"] = sub.Value?.DeepClone(),
                        });
                    }
                }

                var entry = new JsonObject
                {
                    ["family"] = font["family"]?.DeepClone(),
                    ["fallback"] = font["fallback"]?.DeepClone(),
                    ["files"] = files,
                };
                if (font["licence"] is JsonValue lv && lv.TryGetValue<string>(out var licence)) entry["licence"] = FontUrl(licence);
                fonts.Add(entry);
            }

            // An uploaded font is one file holding every character it has: one entry with no subset and
            // no range, which an app loads for everything.
            foreach (var font in config.CustomFonts)
            {
                fonts.Add(new JsonObject
                {
                    ["family"] = font.Family,
                    ["fallback"] = font.Fallback,
                    ["custom"] = true,
                    ["files"] = new JsonArray(new JsonObject
                    {
                        ["subset"] = null,
                        ["url"] = FontUrl(font.File),
                        ["format"] = "woff2",
                        ["style"] = "normal",
                        ["weight"] = new JsonArray(font.WeightMin, font.WeightMax),
                        ["unicodeRange"] = null,
                    }),
                });
            }

            var themes = new JsonArray();
            if (!customOnly)
            {
                foreach (var t in BuiltIn)
                {
                    if (Themes.IsKnown((string)t["id"]!, config)) themes.Add(t.DeepClone());
                }
            }

            foreach (var t in ValidCustomThemes(config)) themes.Add(t.DeepClone());

            var patterns = new JsonArray();
            foreach (var name in Patterns.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                patterns.Add(new JsonObject { ["name"] = name, ["url"] = PatternUrl(name) });
            }

            var payload = new JsonObject
            {
                ["schema"] = ThemeValidator.Schema,
                ["default"] = defaultTheme,
                ["profilesCanChoose"] = config.AllowProfileThemes,
                // Built-ins the administrator took out of the pickers. Not in "themes"; a profile that chose
                // one is drawn in the default.
                ["hidden"] = new JsonArray(config.HiddenThemes.Where(Themes.IsBuiltIn).Where(id => id != Themes.Bonfire)
                    .Distinct(StringComparer.Ordinal).Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["themes"] = themes,
                ["fonts"] = fonts,
                ["patterns"] = patterns,
                ["logo"] = LogoPayload(config),
            };
            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// The logo as GET /themes gives it: the image's address or null, the text or null, and the alt
        /// text. A stored value that no longer passes its rule is left out, so a hand-edited configuration
        /// cannot put an address or a second line on screen.
        /// </summary>
        public static JsonObject LogoPayload(PluginConfiguration config)
        {
            string? text = null, alt = null;
            try { text = ThemeLogo.CleanText(config.LogoText, ThemeLogo.MaxText); } catch (ArgumentException) { }
            try { alt = ThemeLogo.CleanText(config.LogoAlt, ThemeLogo.MaxAlt); } catch (ArgumentException) { }
            var image = ThemeLogo.IsFileName(config.LogoImage) ? ThemeLogo.Url(config.LogoImage!) : null;
            return new JsonObject { ["image"] = image, ["text"] = text, ["alt"] = alt, ["corners"] = ThemeLogo.CleanCorners(config.LogoCorners) };
        }

        /// <summary>A strong validator for <paramref name="payload"/>: its SHA-256, quoted.</summary>
        public static string ETagFor(string payload)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
            return "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";
        }

        /// <summary>
        /// One file Bonfire serves from <c>Web/themes/fonts/</c>, by file name: a font, or its license.
        /// Null for a name it does not hold.
        /// </summary>
        public static (byte[] Bytes, string ContentType, string ETag)? FontFile(string name)
        {
            if (string.IsNullOrEmpty(name) || !Loaded.Value.FontFiles.Contains(name)) return null;
            var cached = FontBytes.GetOrAdd(name, n =>
            {
                using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(FontPrefix + n)!;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var bytes = copy.ToArray();
                var hash = System.Security.Cryptography.SHA256.HashData(bytes);
                return (bytes, "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"");
            });
            var type = name.EndsWith(".woff2", StringComparison.Ordinal) ? "font/woff2" : "text/plain; charset=utf-8";
            return (cached.Bytes, type, cached.ETag);
        }

        /// <summary>Where a font file is served, relative to the server.</summary>
        public static string FontUrl(string file) => "/plugins/profiles/themes/fonts/" + Uri.EscapeDataString(file);

        // An administrator's own themes and fonts (1.7.2).

        /// <summary>At most this many custom themes. Each is up to 64 KB, sent to every client.</summary>
        public const int MaxCustomThemes = 40;

        /// <summary>At most this many uploaded fonts.</summary>
        public const int MaxCustomFonts = 20;

        /// <summary>An uploaded font larger than this is refused unread.</summary>
        public const int MaxFontBytes = 2 * 1024 * 1024;

        /// <summary>What an uploaded font may fall back to: what the web client draws (themeCss).</summary>
        public static readonly string[] CustomFontFallbacks = { "sans-serif", "serif" };

        private static readonly System.Text.RegularExpressions.Regex CustomFontName =
            new(@"^[0-9a-f]{32}\.woff2\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Where uploaded fonts are kept: the plugin's data folder, <c>fonts/</c>.</summary>
        public static string CustomFontFolder => Path.Combine(
            Plugin.Instance?.AppPaths.DataPath ?? Path.GetTempPath(), "plugins", "ProfilesManagement", "fonts");

        /// <summary>
        /// The name an uploaded font is stored and served under: the first half of its SHA-256. Named by
        /// contents, so the same file uploaded twice is one file, and a name never carries a path.
        /// </summary>
        public static string CustomFontFileName(byte[] bytes) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 16).ToLowerInvariant() + ".woff2";

        /// <summary>True for a name <see cref="CustomFontFileName"/> could have produced.</summary>
        public static bool IsCustomFontFileName(string? name) => name != null && CustomFontName.IsMatch(name);

        /// <summary>The built-in font families and the uploaded ones.</summary>
        public static IReadOnlyList<string> FontFamiliesFor(PluginConfiguration config) =>
            FontFamilies.Concat(config.CustomFonts.Select(f => f.Family)).ToList();

        /// <summary>Every id a built-in theme answers to: its own and its aliases.</summary>
        public static IReadOnlyCollection<string> BuiltInNames => Loaded.Value.Names;

        /// <summary>
        /// Every problem with an administrator's theme: <see cref="Validate"/> against the built-in and
        /// uploaded fonts, without the contrast rules (owner, 2026-11-12: "There should be no restrictions
        /// on custom made themes"; the built-ins are still held to them), plus the rules only a custom
        /// theme has. Its id cannot be one a built-in answers to, or the two would be drawn as one. It
        /// names itself in text, since a key would need a translation nobody wrote. It has no aliases,
        /// which exist so a renamed built-in keeps the profiles that chose it.
        /// </summary>
        public static IReadOnlyList<ThemeProblem> ValidateCustom(JsonNode? theme, PluginConfiguration config)
        {
            var problems = ThemeValidator.Validate(theme, FontFamiliesFor(config), Patterns.Keys, contrast: false).ToList();
            if (theme is not JsonObject t) return problems;

            if (t["id"] is JsonValue iv && iv.TryGetValue<string>(out var id) && BuiltInNames.Contains(id))
                problems.Add(new ThemeProblem("id", "taken", "A built-in theme already uses this id."));
            if (t["name"] is JsonObject name && name.ContainsKey("key"))
                problems.Add(new ThemeProblem("name.key", "range", "A custom theme gives its name as text."));
            if (t["aliases"] is JsonArray aliases && aliases.Count > 0)
                problems.Add(new ThemeProblem("aliases", "range", "A custom theme has no aliases."));
            return problems;
        }

        /// <summary>
        /// The administrator's themes that still pass <see cref="ValidateCustom"/>, parsed, in the order
        /// added. Checked on the way out as well as in, so a hand-edited configuration, or a font deleted
        /// from under a theme, leaves that theme out rather than in front of anyone. Each answer is kept,
        /// keyed by the text and fonts it was checked against, because GET /themes is anonymous and asked
        /// often.
        /// </summary>
        public static IReadOnlyList<JsonObject> ValidCustomThemes(PluginConfiguration config)
        {
            var fontsKey = string.Join("\u0001", config.CustomFonts.Select(f => f.Family));
            var list = new List<JsonObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var custom in config.CustomThemes)
            {
                if (custom == null || string.IsNullOrEmpty(custom.Json) || seen.Contains(custom.Id ?? string.Empty)) continue;
                if (CustomParsed.Count > 500) CustomParsed.Clear();
                var parsed = CustomParsed.GetOrAdd(fontsKey + "\u0002" + custom.Json, _ =>
                {
                    JsonNode? node;
                    try
                    {
                        node = JsonNode.Parse(custom.Json);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }

                    return node is JsonObject o && ValidateCustom(o, config).Count == 0 ? (JsonObject)Materialise(o)! : null;
                });
                // Added to seen only now: an invalid first copy of an id must not hide a valid second. r2-20-04.
                if (parsed != null && parsed["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id == custom.Id && seen.Add(custom.Id ?? string.Empty))
                    list.Add(parsed);
            }

            return list;
        }

        /// <summary>
        /// The theme file for <paramref name="id"/>: a built-in, or a custom theme that still passes;
        /// Bonfire for anything else. The caller holds ConfigLock.
        /// </summary>
        public static JsonObject ThemeFor(string? id, PluginConfiguration config)
        {
            var wanted = Themes.Normalize(id, config);
            return BuiltIn.FirstOrDefault(t => (string?)t["id"] == wanted)
                ?? ValidCustomThemes(config).FirstOrDefault(t => (string?)t["id"] == wanted)
                ?? BuiltIn.First(t => (string?)t["id"] == Themes.Bonfire);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject?> CustomParsed =
            new(StringComparer.Ordinal);

        /// <summary>
        /// One uploaded font, by its stored name, when the configuration still lists it; null otherwise.
        /// The name is checked against <see cref="IsCustomFontFileName"/> and the list before it is joined
        /// to the folder.
        /// </summary>
        public static (byte[] Bytes, string ContentType, string ETag)? CustomFontFile(string name, PluginConfiguration config)
        {
            if (!IsCustomFontFileName(name) || !config.CustomFonts.Any(f => f.File == name)) return null;
            var path = Path.Combine(CustomFontFolder, name);
            // No Exists check first: a file replaced or locked between that and the read was a 500. A
            // file that cannot be read is a file that is not there.
            try
            {
                // Named by its hash, so the name is the validator.
                return (File.ReadAllBytes(path), "font/woff2", CustomFontETag(name));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The ETag an uploaded font is served with: its hash, so no read is needed.</summary>
        public static string CustomFontETag(string name) => "\"" + name.Substring(0, 32) + "\"";

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Bytes, string ETag)> FontBytes =
            new(StringComparer.Ordinal);

        private const string FontPrefix = "Jellyfin.Profiles.Themes.Fonts.";

        private sealed record Catalog(
            List<JsonObject> Themes, JsonArray Fonts, JsonObject Subsets, Dictionary<string, string> Patterns, HashSet<string> FontFiles,
            HashSet<string> Names);

        // A parsed JsonObject or JsonArray builds its children on first read, and System.Text.Json
        // documents JsonNode as not safe for concurrent use. The built-ins and cached custom themes are
        // shared by every anonymous GET /themes at once, so the lazy build is done here, once, before
        // anything is published. DeepClone does not do it: a clone is lazy as well.
        internal static JsonNode? Materialise(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                foreach (var pair in o) Materialise(pair.Value);
            }
            else if (node is JsonArray a)
            {
                foreach (var item in a) Materialise(item);
            }

            return node;
        }

        private static readonly Lazy<Catalog> Loaded = new(Load);

        private static Catalog Load()
        {
            var assembly = typeof(ThemeCatalog).Assembly;
            var themes = new List<JsonObject>();
            var fonts = new JsonArray();
            var subsets = new JsonObject();
            var patterns = new Dictionary<string, string>(StringComparer.Ordinal);
            var fontFiles = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                if (name.StartsWith(PatternPrefix, StringComparison.Ordinal) && name.EndsWith(".svg", StringComparison.Ordinal))
                {
                    patterns[name.Substring(PatternPrefix.Length, name.Length - PatternPrefix.Length - 4)] = Read(name);
                    continue;
                }

                if (name.StartsWith(FontPrefix, StringComparison.Ordinal))
                {
                    fontFiles.Add(name.Substring(FontPrefix.Length));
                    continue;
                }

                if (!name.StartsWith(ThemePrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
                if (name.StartsWith(PatternPrefix, StringComparison.Ordinal)) continue;

                var node = Materialise(JsonNode.Parse(Read(name)));
                if (name == ThemePrefix + "fonts.json")
                {
                    if (node?["fonts"] is JsonArray list) fonts = (JsonArray)list.DeepClone();
                    if (node?["subsets"] is JsonObject s) subsets = (JsonObject)s.DeepClone();
                    continue;
                }

                if (node is JsonObject theme && theme["id"] is JsonValue) themes.Add(theme);
            }

            // Bonfire first: it is the look before any theme is known, and each later block in the client
            // wins its ties by coming after it. The rest by id, so tests/_lib.js can reproduce the order
            // from the folder alone.
            themes = themes.OrderBy(t => (string)t["id"]! == Themes.Bonfire ? 0 : 1)
                .ThenBy(t => (string)t["id"]!, StringComparer.Ordinal)
                .ToList();
            var names = new HashSet<string>(Themes.All, StringComparer.Ordinal);
            foreach (var t in themes)
            {
                names.Add((string)t["id"]!);
                if (t["aliases"] is not JsonArray aliases) continue;
                foreach (var a in aliases)
                {
                    if (a is JsonValue av && av.TryGetValue<string>(out var alias)) names.Add(alias);
                }
            }

            return new Catalog(themes, fonts, subsets, patterns, fontFiles, names);

            string Read(string resource)
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
    }
}
