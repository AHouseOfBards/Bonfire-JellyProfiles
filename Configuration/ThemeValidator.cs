using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>One thing wrong with a theme file: where, what kind, and the detail.</summary>
    /// <param name="Path">The value at fault, as a JSON path: <c>colors.text2</c>.</param>
    /// <param name="Code">
    /// <c>missing</c>, <c>type</c>, <c>range</c>, <c>unknown</c>, <c>contrast</c>, <c>hue</c>,
    /// <c>font</c>, <c>pattern</c>, <c>address</c>, <c>size</c> or <c>image</c> (a background image this server does not have).
    /// </param>
    /// <param name="Detail">English, for the log and the settings page's fallback.</param>
    public sealed record ThemeProblem(string Path, string Code, string Detail);

    /// <summary>
    /// Checks a theme file against docs/themes.md, schema 1. Every built-in passes it (see
    /// tests/cs/themes), and an administrator's theme is checked by it before it is saved or
    /// imported.
    /// <para>
    /// Strict, because an imported file is untrusted: an unknown field is a problem rather
    /// than something kept, every value is checked for its type and range, and no string may
    /// hold an address. The web client sanitises again as it writes CSS (themeCss forces every
    /// value into its type), so a file that got past this could still not inject CSS; this is
    /// what tells the administrator which value is wrong instead of drawing it as nothing.
    /// </para>
    /// <para>
    /// Contrast is checked pair by pair against every colour the background has, the fill and
    /// each stop of each layer, one at a time. An average would let a gradient that is
    /// unreadable at one end pass on the strength of the other.
    /// </para>
    /// <para>
    /// The pairs are the ones where the theme names both the text and what it sits on: the
    /// colours against the background, text1 against the panel, onAccent against both
    /// accents, and the text of button2, its focus, the add tile (resting and focused) and
    /// the input against their fills. Not checked, on purpose: the card, the sheet, the
    /// surfaces and the borders. They carry no text of their own; the text drawn on them is
    /// the colours above, already held against the background they sit on, and a border or
    /// line is decoration, not something read.
    /// </para>
    /// </summary>
    public static class ThemeValidator
    {
        /// <summary>The schema this validator reads.</summary>
        public const int Schema = 1;

        /// <summary>A serialised theme larger than this is refused before anything else.</summary>
        public const int MaxBytes = 64 * 1024;

        /// <summary>The id of the theme that must reach 7:1 wherever the others need 4.5:1.</summary>
        public const string HighContrastId = Themes.HighContrast;

        /// <summary>The decorations schema 1 knows. docs/themes.md, "Decorations".</summary>
        public static readonly string[] Decorations = { "pattern", "rings", "frost", "twinkle" };

        // \z, not $: in .NET `$` also matches before a final newline, so "dark\n" would pass.
        private static readonly Regex IdRegex = new(@"^[a-z0-9-]{1,40}\z", RegexOptions.CultureInvariant);
        private static readonly Regex HexRegex = new(@"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?\z", RegexOptions.CultureInvariant);
        private static readonly Regex KeyRegex = new(@"^theme\.[a-z0-9-]{1,40}\z", RegexOptions.CultureInvariant);

        private static readonly string[] ColorFields =
        {
            "accent", "accentFocus", "onAccent", "text1", "text2", "text3",
            "surface1", "surface2", "surface3", "border1", "border2", "border3",
            "panel", "ring", "name", "nameFocus", "shadow", "danger", "warning",
        };

        /// <summary>
        /// Every problem with <paramref name="node"/>, or none. <paramref name="fonts"/> and
        /// <paramref name="patterns"/> are the font families and pattern pictures Bonfire
        /// holds; a theme may only name those.
        /// </summary>
        public static IReadOnlyList<ThemeProblem> Validate(JsonNode? node, IEnumerable<string> fonts, IEnumerable<string> patterns)
        {
            var v = new Walker(new HashSet<string>(fonts, StringComparer.Ordinal), new HashSet<string>(patterns, StringComparer.Ordinal));
            if (node is not JsonObject theme)
            {
                v.Add("", "type", "A theme is a JSON object.");
                return v.Problems;
            }

            if (Encoding(theme) > MaxBytes)
            {
                v.Add("", "size", "A theme file can be at most " + (MaxBytes / 1024) + " KB.");
                return v.Problems;
            }

            v.Run(theme);
            return v.Problems;
        }

        private static int Encoding(JsonNode node) => System.Text.Encoding.UTF8.GetByteCount(node.ToJsonString());

        // ── Colour arithmetic, WCAG 2 ────────────────────────────────────────────────────

        /// <summary>A colour: 0–255 channels and 0–1 alpha.</summary>
        internal readonly record struct Rgba(double R, double G, double B, double A);

        internal static Rgba? ParseHex(string? value)
        {
            if (value == null || !HexRegex.IsMatch(value)) return null;
            int Byte(int at) => int.Parse(value.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Rgba(Byte(1), Byte(3), Byte(5), value.Length == 9 ? Byte(7) / 255.0 : 1);
        }

        /// <summary><paramref name="top"/> drawn over an opaque <paramref name="under"/>.</summary>
        internal static Rgba Over(Rgba top, Rgba under) => new(
            (top.R * top.A) + (under.R * (1 - top.A)),
            (top.G * top.A) + (under.G * (1 - top.A)),
            (top.B * top.A) + (under.B * (1 - top.A)),
            1);

        private static double Luminance(Rgba c)
        {
            static double Ch(double x)
            {
                x /= 255;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Ch(c.R)) + (0.7152 * Ch(c.G)) + (0.0722 * Ch(c.B));
        }

        /// <summary>The WCAG contrast ratio of two opaque colours.</summary>
        internal static double Contrast(Rgba a, Rgba b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        /// <summary>Hue in degrees and saturation 0–1, HSL.</summary>
        internal static (double Hue, double Saturation) HueOf(Rgba c)
        {
            double r = c.R / 255, g = c.G / 255, b = c.B / 255;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d == 0) return (0, 0);
            double l = (max + min) / 2;
            double s = d / (1 - Math.Abs((2 * l) - 1));
            double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
            return (h < 0 ? h + 360 : h, s);
        }

        private sealed class Walker
        {
            private readonly HashSet<string> _fonts;
            private readonly HashSet<string> _patterns;

            public Walker(HashSet<string> fonts, HashSet<string> patterns)
            {
                _fonts = fonts;
                _patterns = patterns;
            }

            public List<ThemeProblem> Problems { get; } = new();

            public void Add(string path, string code, string detail) => Problems.Add(new ThemeProblem(path, code, detail));

            // ── Shape of the file ─────────────────────────────────────────────────────

            /// <summary>The object at <paramref name="path"/> with exactly these keys allowed.</summary>
            private JsonObject? Obj(JsonNode? node, string path, bool required, params string[] allowed)
            {
                if (node == null)
                {
                    if (required) Add(path, "missing", "Required.");
                    return null;
                }

                if (node is not JsonObject o)
                {
                    Add(path, "type", "Must be an object.");
                    return null;
                }

                foreach (var key in o.Select(p => p.Key))
                {
                    if (Array.IndexOf(allowed, key) == -1) Add(Join(path, key), "unknown", "Not part of schema " + Schema + ".");
                }

                return o;
            }

            private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

            private string? Str(JsonObject o, string path, string key, bool required)
            {
                var n = o[key];
                var at = Join(path, key);
                if (n == null)
                {
                    if (required) Add(at, "missing", "Required.");
                    return null;
                }

                if (n is not JsonValue jv || !jv.TryGetValue<string>(out var s))
                {
                    Add(at, "type", "Must be text.");
                    return null;
                }

                return s;
            }

            private double? Num(JsonObject o, string path, string key, bool required, double min, double max)
            {
                var n = o[key];
                var at = Join(path, key);
                if (n == null)
                {
                    if (required) Add(at, "missing", "Required.");
                    return null;
                }

                // Read through the JSON text: TryGetValue<double> is false for a value made
                // from an int in code rather than parsed, which is how a theme built by the
                // settings page arrives.
                if (n is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number
                    || !double.TryParse(jv.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    Add(at, "type", "Must be a number.");
                    return null;
                }

                if (double.IsNaN(d) || d < min || d > max)
                {
                    Add(at, "range", "Must be from " + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ".");
                    return null;
                }

                return d;
            }

            private string? OneOf(JsonObject o, string path, string key, bool required, params string[] values)
            {
                var s = Str(o, path, key, required);
                if (s == null) return null;
                if (Array.IndexOf(values, s) == -1)
                {
                    Add(Join(path, key), "range", "Must be one of: " + string.Join(", ", values) + ".");
                    return null;
                }

                return s;
            }

            private Rgba? Color(JsonObject o, string path, string key, bool required)
            {
                var s = Str(o, path, key, required);
                if (s == null) return null;
                var c = ParseHex(s);
                if (c == null) Add(Join(path, key), "type", "Must be a colour, #rrggbb or #rrggbbaa.");
                return c;
            }

            private void Shadow(JsonObject parent, string path, string key)
            {
                var n = parent[key];
                if (n == null || n.GetValueKind() == JsonValueKind.Null) return;
                var at = Join(path, key);
                var o = Obj(n, at, true, "x", "y", "blur", "spread", "opacity", "color");
                if (o == null) return;
                Num(o, at, "x", false, -100, 100);
                Num(o, at, "y", false, -100, 100);
                Num(o, at, "blur", false, 0, 100);
                Num(o, at, "spread", false, -50, 50);
                Num(o, at, "opacity", false, 0, 1);
                Color(o, at, "color", false);
            }

            private Rgba? Border(JsonObject parent, string path, string key, bool required)
            {
                var at = Join(path, key);
                var o = Obj(parent[key], at, required, "width", "style", "color");
                if (o == null) return null;
                Num(o, at, "width", true, 0, 8);
                OneOf(o, at, "style", true, "solid", "dashed", "dotted");
                return Color(o, at, "color", true);
            }

            // ── The file ──────────────────────────────────────────────────────────────

            public void Run(JsonObject t)
            {
                Obj(t, "", true, "schema", "id", "revision", "scheme", "name", "aliases", "colors", "background",
                    "shape", "type", "depth", "focus", "parts", "layout", "decorations");
                NoAddresses(t, "");

                var schema = Num(t, "", "schema", true, Schema, Schema);
                if (schema == null) return; // a file for another schema: nothing below means anything

                var id = Str(t, "", "id", true);
                if (id != null && !IdRegex.IsMatch(id)) Add("id", "range", "Lower case letters, digits and - only, at most 40.");
                var revision = Num(t, "", "revision", true, 1, int.MaxValue);
                if (revision != null && revision != Math.Floor(revision.Value)) Add("revision", "type", "Must be a whole number.");
                OneOf(t, "", "scheme", true, "dark", "light");
                Name(t);
                Aliases(t, id);

                var colors = Colors(t);
                var bg = Background(t);
                Shape(t);
                TypeSection(t);
                Depth(t);
                Focus(t);
                var parts = Parts(t);
                var layout = Obj(t["layout"], "layout", true, "households");
                if (layout != null) OneOf(layout, "layout", "households", true, "panels", "open");
                DecorationsSection(t);

                if (colors != null && bg.Count > 0)
                {
                    // A name is drawn at type.name.opacity, so that is the colour to check.
                    var opacity = t["type"]?["name"]?["opacity"] is JsonValue ov && ov.GetValueKind() == JsonValueKind.Number
                        && double.TryParse(ov.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) && o >= 0 && o <= 1 ? o : 1;
                    colors["name"] = colors["name"] with { A = colors["name"].A * opacity };
                    Contrast(colors, bg, parts, id == HighContrastId);
                }
            }

            private void Name(JsonObject t)
            {
                var o = Obj(t["name"], "name", true, "key", "text");
                if (o == null) return;
                var key = Str(o, "name", "key", false);
                var text = Str(o, "name", "text", false);
                if ((key == null) == (text == null))
                {
                    Add("name", "missing", "Give either a key or a text, not both.");
                    return;
                }

                if (key != null && !KeyRegex.IsMatch(key)) Add("name.key", "range", "Must be theme.<id>.");
                if (text != null && (text.Trim().Length == 0 || text.Length > 40 || text.Any(char.IsControl)))
                {
                    Add("name.text", "range", "1 to 40 characters, on one line.");
                }
            }

            private void Aliases(JsonObject t, string? id)
            {
                var n = t["aliases"];
                if (n == null)
                {
                    Add("aliases", "missing", "Required; [] for none.");
                    return;
                }

                if (n is not JsonArray a)
                {
                    Add("aliases", "type", "Must be a list.");
                    return;
                }

                if (a.Count > 10) Add("aliases", "range", "At most 10.");
                for (int i = 0; i < a.Count; i++)
                {
                    var s = a[i] is JsonValue jv && jv.TryGetValue<string>(out var x) ? x : null;
                    if (s == null || !IdRegex.IsMatch(s) || s == id) Add("aliases[" + i + "]", "range", "Must be another theme id.");
                }
            }

            private Dictionary<string, Rgba>? Colors(JsonObject t)
            {
                var o = Obj(t["colors"], "colors", true, ColorFields);
                if (o == null) return null;
                var all = new Dictionary<string, Rgba>(StringComparer.Ordinal);
                foreach (var f in ColorFields)
                {
                    var c = Color(o, "colors", f, true);
                    if (c != null) all[f] = c.Value;
                }

                // Red and amber, so an error never reads as anything else. Tunable within the
                // band, not removable.
                if (all.TryGetValue("danger", out var danger))
                {
                    var (h, s) = HueOf(danger);
                    if (!((h <= 20 || h >= 340) && s >= 0.4)) Add("colors.danger", "hue", "Must read as red.");
                }

                if (all.TryGetValue("warning", out var warning))
                {
                    var (h, s) = HueOf(warning);
                    if (!(h >= 25 && h <= 50 && s >= 0.4)) Add("colors.warning", "hue", "Must read as amber.");
                }

                return all.Count == ColorFields.Length ? all : null;
            }

            /// <summary>Every colour the background has, each made opaque: the fill, then each stop.</summary>
            private List<(string Path, Rgba Color)> Background(JsonObject t)
            {
                var stops = new List<(string, Rgba)>();
                var o = Obj(t["background"], "background", true, "fill", "layers", "image");
                if (o == null) return stops;
                var fill = Color(o, "background", "fill", true);
                if (fill == null) return stops;
                var ground = fill.Value with { A = 1 };
                if (fill.Value.A < 1) Add("background.fill", "range", "Must not be see-through.");
                stops.Add(("background.fill", ground));

                // A picture (ThemeBackground), drawn under the fill at `dim`. What it holds is
                // not known here, so the text is held against the two ends of what it could
                // be: a white picture and a black one, each under that layer. Every colour
                // in between is lighter than the one and darker than the other, so a pair
                // that passes on both passes on any picture.
                var image = Obj(o["image"], "background.image", false, "file", "dim");
                if (image != null)
                {
                    var file = Str(image, "background.image", "file", true);
                    if (file != null && !ThemeLogo.IsFileName(file)) Add("background.image.file", "range", "Not an image Bonfire stored.");
                    var dim = Num(image, "background.image", "dim", true, 0, ThemeBackground.MaxDim);
                    if (dim != null)
                    {
                        var layer = ground with { A = dim.Value };
                        stops.Add(("background.image", Over(layer, new Rgba(255, 255, 255, 1))));
                        stops.Add(("background.image", Over(layer, new Rgba(0, 0, 0, 1))));
                    }
                }

                var n = o["layers"];
                if (n == null) return stops;
                if (n is not JsonArray layers)
                {
                    Add("background.layers", "type", "Must be a list.");
                    return stops;
                }

                if (layers.Count > 6) Add("background.layers", "range", "At most 6.");
                for (int i = 0; i < layers.Count && i < 6; i++)
                {
                    var at = "background.layers[" + i + "]";
                    var layer = Obj(layers[i], at, true, "type", "angle", "shape", "x", "y", "stops");
                    if (layer == null) continue;
                    var kind = OneOf(layer, at, "type", true, "linear", "radial");
                    if (kind == "linear")
                    {
                        Num(layer, at, "angle", false, 0, 360);
                    }
                    else if (kind == "radial")
                    {
                        OneOf(layer, at, "shape", false, "circle", "ellipse");
                        Num(layer, at, "x", false, 0, 100);
                        Num(layer, at, "y", false, 0, 100);
                    }

                    if (layer["stops"] is not JsonArray list || list.Count < 2 || list.Count > 8)
                    {
                        Add(at + ".stops", "range", "2 to 8 stops.");
                        continue;
                    }

                    for (int k = 0; k < list.Count; k++)
                    {
                        var sat = at + ".stops[" + k + "]";
                        var stop = Obj(list[k], sat, true, "color", "at");
                        if (stop == null) continue;
                        Num(stop, sat, "at", true, 0, 100);
                        var c = Color(stop, sat, "color", true);
                        if (c != null) stops.Add((sat, Over(c.Value, ground)));
                    }
                }

                return stops;
            }

            private void Shape(JsonObject t)
            {
                var o = Obj(t["shape"], "shape", true, "radius", "avatar", "ringWidth", "button", "input");
                if (o == null) return;
                var r = Obj(o["radius"], "shape.radius", true, "small", "medium", "large");
                if (r != null)
                {
                    Num(r, "shape.radius", "small", true, 0, 64);
                    Num(r, "shape.radius", "medium", true, 0, 64);
                    Num(r, "shape.radius", "large", true, 0, 64);
                }

                var av = Obj(o["avatar"], "shape.avatar", true, "kind", "radius");
                if (av != null)
                {
                    var kind = OneOf(av, "shape.avatar", "kind", true, "circle", "square", "rounded", "leaf");
                    Num(av, "shape.avatar", "radius", kind is "rounded" or "leaf", 0, kind == "rounded" ? 50 : 200);
                }

                Num(o, "shape", "ringWidth", true, 0, 12);
                var b = Obj(o["button"], "shape.button", true, "kind", "radius");
                if (b != null)
                {
                    var kind = OneOf(b, "shape.button", "kind", true, "pill", "rounded", "square");
                    Num(b, "shape.button", "radius", kind == "rounded", 0, 64);
                }

                var input = Obj(o["input"], "shape.input", true, "radius");
                if (input != null) Num(input, "shape.input", "radius", true, 0, 64);
            }

            private void TypeSection(JsonObject t)
            {
                var o = Obj(t["type"], "type", false, "font", "title", "name", "button");
                if (o == null) return;
                var font = Obj(o["font"], "type.font", false, "family", "fallback");
                if (font != null)
                {
                    var family = Str(font, "type.font", "family", true);
                    if (family != null && !_fonts.Contains(family)) Add("type.font.family", "font", "Must be one of the fonts Bonfire serves.");
                    OneOf(font, "type.font", "fallback", true, "sans-serif", "serif", "rounded");
                }

                var title = Obj(o["title"], "type.title", false, "weight", "spacing", "scale");
                if (title != null) Text(title, "type.title");
                var name = Obj(o["name"], "type.name", false, "weight", "scale", "opacity");
                if (name != null)
                {
                    Weight(name, "type.name");
                    Num(name, "type.name", "scale", false, 0.5, 2);
                    Num(name, "type.name", "opacity", false, 0.5, 1);
                }

                var button = Obj(o["button"], "type.button", false, "case", "spacing", "weight");
                if (button != null)
                {
                    OneOf(button, "type.button", "case", false, "none", "upper");
                    Num(button, "type.button", "spacing", false, -0.2, 0.5);
                    Weight(button, "type.button");
                }
            }

            private void Text(JsonObject o, string path)
            {
                Weight(o, path);
                Num(o, path, "spacing", false, -0.2, 0.5);
                Num(o, path, "scale", false, 0.5, 2);
            }

            private void Weight(JsonObject o, string path)
            {
                var w = Num(o, path, "weight", false, 100, 900);
                if (w != null && w % 100 != 0) Add(Join(path, "weight"), "range", "Must be a multiple of 100.");
            }

            private void Depth(JsonObject t)
            {
                var o = Obj(t["depth"], "depth", false, "avatar", "title", "sheet");
                if (o == null) return;
                Shadow(o, "depth", "avatar");
                Shadow(o, "depth", "title");
                Shadow(o, "depth", "sheet");
            }

            private void Focus(JsonObject t)
            {
                var o = Obj(t["focus"], "focus", false, "scale", "glow");
                if (o == null) return;
                Num(o, "focus", "scale", false, 1, 1.25);
                Shadow(o, "focus", "glow");
            }

            private Dictionary<string, Rgba> Parts(JsonObject t)
            {
                var found = new Dictionary<string, Rgba>(StringComparer.Ordinal);
                var o = Obj(t["parts"], "parts", true, "button2", "addTile", "card", "sheet", "input");
                if (o == null) return found;

                void Keep(string name, Rgba? c)
                {
                    if (c != null) found[name] = c.Value;
                }

                var b2 = Obj(o["button2"], "parts.button2", true, "fill", "text", "border", "focus");
                if (b2 != null)
                {
                    Keep("button2.fill", Color(b2, "parts.button2", "fill", true));
                    Keep("button2.text", Color(b2, "parts.button2", "text", true));
                    Border(b2, "parts.button2", "border", true);
                    var f = Obj(b2["focus"], "parts.button2.focus", true, "fill", "text", "border", "glow");
                    if (f != null)
                    {
                        Keep("button2.focus.fill", Color(f, "parts.button2.focus", "fill", true));
                        Keep("button2.focus.text", Color(f, "parts.button2.focus", "text", true));
                        Border(f, "parts.button2.focus", "border", true);
                        Shadow(f, "parts.button2.focus", "glow");
                    }
                }

                var add = Obj(o["addTile"], "parts.addTile", true, "fill", "border", "text", "focusFill");
                if (add != null)
                {
                    Keep("addTile.fill", Color(add, "parts.addTile", "fill", true));
                    Border(add, "parts.addTile", "border", true);
                    Keep("addTile.text", Color(add, "parts.addTile", "text", true));
                    Keep("addTile.focusFill", Color(add, "parts.addTile", "focusFill", true));
                }

                var card = Obj(o["card"], "parts.card", true, "fill", "line");
                if (card != null)
                {
                    Color(card, "parts.card", "fill", true);
                    Color(card, "parts.card", "line", true);
                }

                var sheet = Obj(o["sheet"], "parts.sheet", true, "fill", "border");
                if (sheet != null)
                {
                    Color(sheet, "parts.sheet", "fill", true);
                    Border(sheet, "parts.sheet", "border", true);
                }

                var input = Obj(o["input"], "parts.input", true, "fill", "line");
                if (input != null)
                {
                    Keep("input.fill", Color(input, "parts.input", "fill", true));
                    Color(input, "parts.input", "line", true);
                }

                return found;
            }

            private void DecorationsSection(JsonObject t)
            {
                var n = t["decorations"];
                if (n == null)
                {
                    Add("decorations", "missing", "Required; [] for none.");
                    return;
                }

                if (n is not JsonArray list)
                {
                    Add("decorations", "type", "Must be a list.");
                    return;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; i++)
                {
                    var at = "decorations[" + i + "]";
                    if (list[i] is not JsonObject d)
                    {
                        Add(at, "type", "Must be an object.");
                        continue;
                    }

                    var name = OneOf(d, at, "name", true, Decorations);
                    if (name == null) continue;
                    if (!seen.Add(name)) Add(at, "range", "Each decoration at most once.");
                    switch (name)
                    {
                        case "pattern":
                            Obj(d, at, true, "name", "image", "size", "drift", "seconds");
                            var image = Str(d, at, "image", true);
                            if (image != null && !_patterns.Contains(image)) Add(at + ".image", "pattern", "Must be one of the pictures Bonfire holds.");
                            Num(d, at, "size", true, 16, 1024);
                            var drift = OneOf(d, at, "drift", true, "none", "down");
                            Num(d, at, "seconds", drift == "down", 4, 600);
                            break;
                        case "rings":
                            Obj(d, at, true, "name", "gradients", "gap");
                            Color(d, at, "gap", true);
                            if (d["gradients"] is not JsonArray g || g.Count < 1 || g.Count > 12)
                            {
                                Add(at + ".gradients", "range", "1 to 12 gradients.");
                                break;
                            }

                            for (int k = 0; k < g.Count; k++)
                            {
                                if (g[k] is not JsonArray pair || pair.Count != 2
                                    || pair.Any(c => ParseHex(c is JsonValue cv && cv.TryGetValue<string>(out var s) ? s : null) == null))
                                {
                                    Add(at + ".gradients[" + k + "]", "type", "Must be two colours.");
                                }
                            }

                            break;
                        case "frost":
                            Obj(d, at, true, "name", "blur");
                            Num(d, at, "blur", true, 0, 60);
                            break;
                        case "twinkle":
                            Obj(d, at, true, "name", "seconds");
                            Num(d, at, "seconds", true, 2, 120);
                            // It fades the pattern layer, so without a pattern there is
                            // nothing to twinkle and the value would be silently ignored.
                            if (!list.Any(x => x is JsonObject p && p["name"] is JsonValue pn && pn.TryGetValue<string>(out var s) && s == "pattern"))
                            {
                                Add(at, "range", "Needs a pattern decoration to twinkle.");
                            }

                            break;
                    }
                }
            }

            /// <summary>
            /// No string anywhere in the file may point somewhere: fonts and pictures are
            /// files Bonfire holds, named by name. Checked over every string, not only the
            /// fields expected to hold one, because a field this validator does not know is
            /// exactly where an address would be put.
            /// </summary>
            private void NoAddresses(JsonNode? n, string path)
            {
                switch (n)
                {
                    case JsonObject o:
                        foreach (var p in o) NoAddresses(p.Value, Join(path, p.Key));
                        break;
                    case JsonArray a:
                        for (int i = 0; i < a.Count; i++) NoAddresses(a[i], path + "[" + i + "]");
                        break;
                    case JsonValue v when v.TryGetValue<string>(out var s):
                        var low = s.ToLowerInvariant();
                        if (low.Contains("://", StringComparison.Ordinal) || low.StartsWith("//", StringComparison.Ordinal)
                            || low.Contains("url(", StringComparison.Ordinal) || low.StartsWith("data:", StringComparison.Ordinal)
                            || low.Contains("javascript:", StringComparison.Ordinal))
                        {
                            Add(path, "address", "Themes cannot point to other files or sites.");
                        }

                        break;
                }
            }

            // ── Contrast ──────────────────────────────────────────────────────────────

            private void Contrast(Dictionary<string, Rgba> c, List<(string Path, Rgba Color)> bg, Dictionary<string, Rgba> parts, bool high)
            {
                double text = high ? 7 : 4.5;

                // Against every colour of the background, one at a time.
                void OnBackground(string field, double min)
                {
                    foreach (var (path, stop) in bg)
                    {
                        var r = ThemeValidator.Contrast(Over(c[field], stop), stop);
                        if (r < min)
                        {
                            Add("colors." + field, "contrast", Ratio(r, min) + " on " + path + ".");
                            return;
                        }
                    }
                }

                void Pair(string path, Rgba fg, Rgba back, double min, string against)
                {
                    var r = ThemeValidator.Contrast(Over(fg, back), back);
                    if (r < min) Add(path, "contrast", Ratio(r, min) + " on " + against + ".");
                }

                // The title and body text at 7:1, as the web client's own checks have held
                // since 1.6.6. A name is a label under a picture, so 4.5:1, the same as any
                // other text.
                OnBackground("text1", 7);
                OnBackground("name", text);
                OnBackground("text2", text);
                OnBackground("text3", 3);
                OnBackground("ring", 3);
                OnBackground("accent", 3);
                OnBackground("danger", 3);

                var panel = c["panel"] with { A = 1 };
                if (c["panel"].A < 1) Add("colors.panel", "range", "Must not be see-through.");
                Pair("colors.text1", c["text1"], panel, 7, "colors.panel");

                var accent = c["accent"] with { A = 1 };
                var accentFocus = c["accentFocus"] with { A = 1 };
                if (c["accent"].A < 1) Add("colors.accent", "range", "Must not be see-through.");
                if (c["accentFocus"].A < 1) Add("colors.accentFocus", "range", "Must not be see-through.");
                Pair("colors.onAccent", c["onAccent"], accent, text, "colors.accent");
                Pair("colors.onAccent", c["onAccent"], accentFocus, text, "colors.accentFocus");

                // The parts sit on the background, so a see-through fill is mixed onto each
                // of its colours first, and the text checked on each result.
                void OnPart(string textKey, string fillKey, string path)
                {
                    if (!parts.TryGetValue(textKey, out var fg) || !parts.TryGetValue(fillKey, out var fill)) return;
                    foreach (var (bgPath, stop) in bg)
                    {
                        var back = Over(fill, stop);
                        var r = ThemeValidator.Contrast(Over(fg, back), back);
                        if (r < text)
                        {
                            Add(path, "contrast", Ratio(r, text) + " on parts." + fillKey + " over " + bgPath + ".");
                            return;
                        }
                    }
                }

                OnPart("button2.text", "button2.fill", "parts.button2.text");
                OnPart("button2.focus.text", "button2.focus.fill", "parts.button2.focus.text");
                // The "add profile" tile's label, resting and focused (the same text on both).
                OnPart("addTile.text", "addTile.fill", "parts.addTile.text");
                OnPart("addTile.text", "addTile.focusFill", "parts.addTile.text");
                if (parts.TryGetValue("input.fill", out _))
                {
                    parts["input.text"] = c["text1"];
                    OnPart("input.text", "input.fill", "colors.text1");
                }
            }

            private static string Ratio(double r, double min) =>
                r.ToString("0.00", CultureInfo.InvariantCulture) + ":1, " + min.ToString("0.#", CultureInfo.InvariantCulture) + ":1 needed,";
        }
    }
}
