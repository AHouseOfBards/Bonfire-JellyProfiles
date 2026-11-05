using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// The themes Bonfire knows: the built-in files in <c>Web/themes/</c>, embedded in the
    /// assembly. docs/themes.md describes a file.
    /// <para>
    /// The server never turns a theme into CSS. It hands the files to the web client, which
    /// does that in <c>themeCss</c>, and to other apps, which draw their own way. One
    /// generator, so the web client cannot drift from what the files say.
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
        /// What profiles.js is given in place of its <c>__BONFIRE_THEMES__</c> marker: the
        /// themes, the fonts, and each pattern as a data: URL so the web client fetches
        /// nothing to draw one.
        /// <para>
        /// Serialized with the default encoder, which escapes everything outside ASCII and
        /// the characters HTML gives meaning to. That keeps U+2028 and U+2029 out of the
        /// script (a syntax error before ES2019, which is every Chromium 68 television), and
        /// leaves nothing in a theme's text that can end the script it sits in.
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
                ["patterns"] = patterns,
            };
            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>Where a pattern picture is served, relative to the server.</summary>
        public static string PatternUrl(string name) => "/plugins/profiles/themes/patterns/" + Uri.EscapeDataString(name);

        /// <summary>
        /// What <c>GET /plugins/profiles/themes</c> returns: docs/themes.md, "Getting themes".
        /// For other apps, so unlike <see cref="ClientPayload"/> the pictures are addresses
        /// to fetch, and the web client's own notes on a font (the names it goes by when
        /// installed) stay out of it.
        /// </summary>
        public static string PublicPayload(string defaultTheme, bool profilesCanChoose)
        {
            var fonts = new JsonArray();
            foreach (var f in Loaded.Value.Fonts)
            {
                if (f is not JsonObject font) continue;
                fonts.Add(new JsonObject
                {
                    ["family"] = font["family"]?.DeepClone(),
                    ["fallback"] = font["fallback"]?.DeepClone(),
                    // Bonfire serves no font files yet; an app uses the fallback until it does.
                    ["files"] = new JsonArray(),
                });
            }

            var patterns = new JsonArray();
            foreach (var name in Patterns.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                patterns.Add(new JsonObject { ["name"] = name, ["url"] = PatternUrl(name) });
            }

            var payload = new JsonObject
            {
                ["schema"] = ThemeValidator.Schema,
                ["default"] = defaultTheme,
                ["profilesCanChoose"] = profilesCanChoose,
                ["themes"] = new JsonArray(BuiltIn.Select(t => (JsonNode)t.DeepClone()).ToArray()),
                ["fonts"] = fonts,
                ["patterns"] = patterns,
                // No logo yet; an app draws none.
                ["logo"] = new JsonObject { ["image"] = null, ["text"] = null },
            };
            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>A strong validator for <paramref name="payload"/>: its SHA-256, quoted.</summary>
        public static string ETagFor(string payload)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
            return "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";
        }

        private sealed record Catalog(List<JsonObject> Themes, JsonArray Fonts, Dictionary<string, string> Patterns);

        private static readonly Lazy<Catalog> Loaded = new(Load);

        private static Catalog Load()
        {
            var assembly = typeof(ThemeCatalog).Assembly;
            var themes = new List<JsonObject>();
            var fonts = new JsonArray();
            var patterns = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var name in assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                if (name.StartsWith(PatternPrefix, StringComparison.Ordinal) && name.EndsWith(".svg", StringComparison.Ordinal))
                {
                    patterns[name.Substring(PatternPrefix.Length, name.Length - PatternPrefix.Length - 4)] = Read(name);
                    continue;
                }

                if (!name.StartsWith(ThemePrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
                if (name.StartsWith(PatternPrefix, StringComparison.Ordinal)) continue;

                var node = JsonNode.Parse(Read(name));
                if (name == ThemePrefix + "fonts.json")
                {
                    if (node?["fonts"] is JsonArray list) fonts = (JsonArray)list.DeepClone();
                    continue;
                }

                if (node is JsonObject theme && theme["id"] is JsonValue) themes.Add(theme);
            }

            // Bonfire first: it is the look before any theme is known, and each later block
            // in the client wins its ties by coming after it. The rest by id, so the order
            // is one tests/_lib.js can reproduce from the folder alone.
            themes = themes.OrderBy(t => (string)t["id"]! == Themes.Bonfire ? 0 : 1)
                .ThenBy(t => (string)t["id"]!, StringComparer.Ordinal)
                .ToList();
            return new Catalog(themes, fonts, patterns);

            string Read(string resource)
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
    }
}
