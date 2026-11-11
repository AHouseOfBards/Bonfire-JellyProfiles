using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>What a drawn tile shows.</summary>
    public enum TileGlyph
    {
        /// <summary>A person: a profile with no picture.</summary>
        Person,

        /// <summary>Two arrows: the "Switch profile" folder.</summary>
        Switch,

        /// <summary>Four squares: the "Profiles" folder.</summary>
        Profiles,
    }

    /// <summary>
    /// Pictures the server draws in a theme's colours (1.7.2), for apps that show Bonfire's
    /// picker as folders (the Moonfin proxy picker). The theme's background, and a glyph in
    /// its accent. Shapes only, by Logan's choice: drawing letters needs a font rasteriser,
    /// and the one Jellyfin ships (SkiaSharp) is its internal dependency, whose version can
    /// change between 10.11 and 12 under a plugin that linked against it. The app writes the
    /// name under the tile itself.
    /// <para>
    /// A PNG, written here: rows of RGB, zlib-compressed, with a CRC per chunk. No library,
    /// so nothing to version. Glyphs are drawn at 4×4 samples a pixel so their edges are
    /// smooth at the size a television shows them.
    /// </para>
    /// </summary>
    public static class TileImage
    {
        /// <summary>The side of every tile, in pixels: square, as the picker's aspect ratio says.</summary>
        public const int Size = 256;

        /// <summary>
        /// The version of the drawing code, part of every tag. Apps cache a tile for a day by
        /// its tag, so a change to how a glyph or a gradient is drawn must change the tag too,
        /// or a household keeps the old picture until the cache runs out. Raise it with any
        /// change to the drawing; the pixel check in tests/cs/tileimages fails until you do.
        /// </summary>
        public const int DrawingVersion = 1;

        // Lazy, so a television that asks for many tiles at once draws each one once: GetOrAdd
        // alone may run its factory on every thread that misses, and a draw is 65,536 pixels.
        private static readonly ConcurrentDictionary<string, Lazy<byte[]>> Cache = new(StringComparer.Ordinal);

        // What the tests read to show a tile is drawn once and its colours are parsed once
        // per stop, not once per pixel.
        internal static int Draws;
        internal static int ColourParses;

        /// <summary>
        /// A short tag for one theme and glyph that changes whenever the picture would. Apps
        /// cache an image by its tag, so a changed theme is a new picture.
        /// </summary>
        public static string Tag(JsonObject theme, TileGlyph glyph)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                DrawingVersion.ToString(CultureInfo.InvariantCulture) + "|" + ((int)glyph).ToString(CultureInfo.InvariantCulture) + "|" + Key(theme)));
            return "b" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        /// <summary>The PNG for one theme and glyph, drawn once and kept.</summary>
        public static byte[] Png(JsonObject theme, TileGlyph glyph)
        {
            if (Cache.Count > 200) Cache.Clear();
            return Cache.GetOrAdd(Tag(theme, glyph),
                _ => new Lazy<byte[]>(() => Encode(Draw(theme, glyph)), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static string Key(JsonObject theme) =>
            (theme["background"]?.ToJsonString() ?? string.Empty) + "|" + (theme["colors"]?["accent"]?.ToJsonString() ?? string.Empty);

        // ── Drawing ─────────────────────────────────────────────────────────────────────

        private readonly record struct Rgba(double R, double G, double B, double A);

        private static byte[] Draw(JsonObject theme, TileGlyph glyph)
        {
            var bg = theme["background"] as JsonObject;
            var fill = Colour(bg?["fill"]) ?? new Rgba(0x12, 0x15, 0x21, 1);
            var accent = Colour(theme["colors"]?["accent"]) ?? new Rgba(0xff, 0xb3, 0x47, 1);
            System.Threading.Interlocked.Increment(ref Draws);
            // Each layer is read once here, not once for every pixel: its stops parsed, checked
            // and sorted, and its geometry worked out.
            var layers = new List<Layer>();
            if (bg?["layers"] is JsonArray list)
            {
                foreach (var l in list)
                {
                    if (l is JsonObject o) layers.Add(Prepare(o));
                }
            }

            var rgb = new byte[Size * Size * 3];
            const int S = 4;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var u = (x + 0.5) / Size;
                    var v = (y + 0.5) / Size;

                    // The background: the fill, then each layer from the last to the first, so
                    // the first is on top as in CSS.
                    var c = fill;
                    for (var i = layers.Count - 1; i >= 0; i--) c = Over(LayerAt(layers[i], u, v), c);

                    var covered = 0;
                    for (var sy = 0; sy < S; sy++)
                    {
                        for (var sx = 0; sx < S; sx++)
                        {
                            if (Inside(glyph, (x + ((sx + 0.5) / S)) / Size, (y + ((sy + 0.5) / S)) / Size)) covered++;
                        }
                    }

                    c = Over(accent with { A = accent.A * covered / (S * S) }, c);
                    var at = ((y * Size) + x) * 3;
                    rgb[at] = Byte(c.R);
                    rgb[at + 1] = Byte(c.G);
                    rgb[at + 2] = Byte(c.B);
                }
            }

            return rgb;
        }

        /// <summary>True when the point (0–1 across, 0–1 down) is part of the glyph.</summary>
        internal static bool Inside(TileGlyph glyph, double x, double y)
        {
            switch (glyph)
            {
                case TileGlyph.Person:
                {
                    // A head, and shoulders: the top of an ellipse, cut flat.
                    if (Sq(x - 0.5) + Sq(y - 0.38) <= Sq(0.13)) return true;
                    return y <= 0.76 && (Sq((x - 0.5) / 0.25) + Sq((y - 0.78) / 0.22)) <= 1;
                }

                case TileGlyph.Switch:
                {
                    // Two arrows, one each way.
                    return Arrow(x, y, 0.41, 0.26, 0.74, true) || Arrow(x, y, 0.59, 0.26, 0.74, false);
                }

                default:
                {
                    // Four rounded squares.
                    foreach (var cx in new[] { 0.39, 0.61 })
                    {
                        foreach (var cy in new[] { 0.39, 0.61 })
                        {
                            if (RoundedSquare(x, y, cx, cy, 0.09, 0.025)) return true;
                        }
                    }

                    return false;
                }
            }
        }

        private static bool Arrow(double x, double y, double row, double from, double to, bool right)
        {
            const double Bar = 0.035, Head = 0.11, HeadLength = 0.12;
            var tip = right ? to : from;
            var neck = right ? to - HeadLength : from + HeadLength;
            var lo = right ? from : neck;
            var hi = right ? neck : to;
            if (x >= lo && x <= hi && Math.Abs(y - row) <= Bar) return true;
            var along = right ? x - neck : neck - x;
            if (along < 0 || along > HeadLength) return false;
            return Math.Abs(y - row) <= Head * (1 - (along / (tip > neck ? tip - neck : neck - tip)));
        }

        private static bool RoundedSquare(double x, double y, double cx, double cy, double half, double r)
        {
            var dx = Math.Max(Math.Abs(x - cx) - (half - r), 0);
            var dy = Math.Max(Math.Abs(y - cy) - (half - r), 0);
            return Sq(dx) + Sq(dy) <= Sq(r);
        }

        /// <summary>One background layer, read once: see <see cref="Prepare"/>.</summary>
        private sealed record Layer(bool Radial, double Cx, double Cy, double Rx, double Ry, double Dx, double Dy, double Half,
            (double At, Rgba Colour)[] Stops);

        private static Layer Prepare(JsonObject layer)
        {
            var stops = new List<(double At, Rgba Colour)>();
            if (layer["stops"] is JsonArray list)
            {
                foreach (var s in list)
                {
                    var c = Colour(s?["color"]);
                    if (c != null) stops.Add((Number(s?["at"], 0) / 100, c.Value));
                }
            }

            // OrderBy is stable, so two stops at one position keep the order they were written
            // in, as CSS does; List.Sort makes no such promise.
            var sorted = stops.OrderBy(x => x.At).ToArray();

            if ((string?)layer["type"] == "radial")
            {
                // An ellipse sized to the farthest corner, as CSS draws one by default.
                var cx = Number(layer["x"], 50) / 100;
                var cy = Number(layer["y"], 50) / 100;
                var rx = Math.Max(cx, 1 - cx) * Math.Sqrt(2);
                var ry = Math.Max(cy, 1 - cy) * Math.Sqrt(2);
                if ((string?)layer["shape"] == "circle") rx = ry = Math.Sqrt(Sq(Math.Max(cx, 1 - cx)) + Sq(Math.Max(cy, 1 - cy)));
                return new Layer(true, cx, cy, rx, ry, 0, 0, 0, sorted);
            }

            // CSS angles: 0deg points up, 90deg right. The line's length is what reaches
            // the corners, so 0% and 100% land on them.
            var a = Number(layer["angle"], 180) * Math.PI / 180;
            var dx = Math.Sin(a);
            var dy = -Math.Cos(a);
            return new Layer(false, 0, 0, 0, 0, dx, dy, (Math.Abs(dx) + Math.Abs(dy)) / 2, sorted);
        }

        private static Rgba LayerAt(Layer layer, double u, double v)
        {
            var stops = layer.Stops;
            if (stops.Length == 0) return new Rgba(0, 0, 0, 0);

            double t;
            if (layer.Radial) t = Math.Sqrt(Sq((u - layer.Cx) / layer.Rx) + Sq((v - layer.Cy) / layer.Ry));
            else t = (((u - 0.5) * layer.Dx) + ((v - 0.5) * layer.Dy) + layer.Half) / (2 * layer.Half);

            if (t <= stops[0].At) return stops[0].Colour;
            for (var i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].At)
                {
                    var span = stops[i].At - stops[i - 1].At;
                    var f = span <= 0 ? 1 : (t - stops[i - 1].At) / span;
                    return Mix(stops[i - 1].Colour, stops[i].Colour, f);
                }
            }

            return stops[stops.Length - 1].Colour;
        }

        private static Rgba Mix(Rgba a, Rgba b, double f) =>
            new(a.R + ((b.R - a.R) * f), a.G + ((b.G - a.G) * f), a.B + ((b.B - a.B) * f), a.A + ((b.A - a.A) * f));

        private static Rgba Over(Rgba top, Rgba under)
        {
            var a = top.A;
            return new Rgba(
                (top.R * a) + (under.R * (1 - a)),
                (top.G * a) + (under.G * (1 - a)),
                (top.B * a) + (under.B * (1 - a)),
                1);
        }

        private static Rgba? Colour(JsonNode? node)
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            System.Threading.Interlocked.Increment(ref ColourParses);
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?\\z")) return null;
            int H(int i) => int.Parse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Rgba(H(1), H(3), H(5), s.Length == 9 ? H(7) / 255.0 : 1);
        }

        // A value parsed from JSON reads as any number type; one built in code reads only as
        // the type it was built from, so an int written by an import or a test would read
        // as missing through TryGetValue<double> alone, and every stop would sit at 0%.
        private static double Number(JsonNode? node, double fallback)
        {
            if (node is not JsonValue v) return fallback;
            if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : fallback;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<decimal>(out var m)) return (double)m;
            return fallback;
        }

        private static double Sq(double v) => v * v;

        private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

        // ── PNG ─────────────────────────────────────────────────────────────────────────

        private static byte[] Encode(byte[] rgb)
        {
            using var output = new MemoryStream();
            output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var header = new byte[13];
            Put32(header, 0, Size);
            Put32(header, 4, Size);
            header[8] = 8; // bit depth
            header[9] = 2; // RGB
            Chunk(output, "IHDR", header);

            byte[] packed;
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (var y = 0; y < Size; y++)
                    {
                        z.WriteByte(0); // no filter
                        z.Write(rgb, y * Size * 3, Size * 3);
                    }
                }

                packed = raw.ToArray();
            }

            Chunk(output, "IDAT", packed);
            Chunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        private static void Chunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            Put32(length, 0, data.Length);
            output.Write(length);
            var body = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
            data.CopyTo(body, 4);
            output.Write(body);
            var crc = new byte[4];
            Put32(crc, 0, unchecked((int)Crc32(body)));
            output.Write(crc);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }

        internal static uint Crc32(byte[] data)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static void Put32(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
