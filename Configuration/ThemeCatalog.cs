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
