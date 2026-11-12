using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>What <see cref="Woff2Font.Read"/> found in a font file.</summary>
    /// <param name="Family">The family name the file gives itself.</param>
    /// <param name="WeightMin">The lightest weight it draws.</param>
    /// <param name="WeightMax">The heaviest weight it draws; the same as the lightest for a static font.</param>
    public sealed record Woff2Info(string Family, int WeightMin, int WeightMax);

    /// <summary>
    /// Reads an uploaded font far enough to know it is a WOFF2 font that can draw a name, and
    /// what it is called. A C# copy of tests/js/_woff2.js, which reads the built-in fonts.
    /// <para>
    /// WOFF2 is a 48-byte header, a table directory, and every table run through one Brotli
    /// stream. cmap, name, OS/2 and fvar are never transformed (only glyf, loca and hmtx are),
    /// so after decompressing they sit in the stream as plain OpenType tables, one after
    /// another in directory order. https://www.w3.org/TR/WOFF2/
    /// </para>
    /// <para>
    /// This is not the browser's check and does not try to be: Chromium runs every web font
    /// through its own sanitiser and draws the fallback for one it refuses. What this stops
    /// is the administrator uploading something that is not a font at all, a font that cannot
    /// draw Latin letters, or a file whose name would have to go into CSS unescaped — and it
    /// says which, at upload, instead of a theme that quietly draws in the fallback.
    /// </para>
    /// <para>
    /// Every read is bounds-checked, and the decompressed stream is capped, so a file built to
    /// expand without end stops at the cap. Anything not understood is refused with
    /// <see cref="InvalidDataException"/>, never guessed at.
    /// </para>
    /// </summary>
    public static class Woff2Font
    {
        /// <summary>Decompressed tables larger than this are refused: no font a theme needs comes near it.</summary>
        public const int MaxDecompressedBytes = 32 * 1024 * 1024;

        /// <summary>The characters a font must draw: a name has to come out in it.</summary>
        public const string RequiredCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        private static readonly string?[] KnownTags =
        {
            "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca",
            "prep", "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE",
            "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar",
            "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar", "mort", "morx",
            "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
        };

        /// <summary>
        /// The family and weights of <paramref name="file"/>. Throws
        /// <see cref="InvalidDataException"/> with an English reason for anything else.
        /// </summary>
        public static Woff2Info Read(byte[] file)
        {
            try
            {
                var tables = Tables(file);
                if (!tables.TryGetValue("cmap", out var cmap) || !tables.TryGetValue("name", out var name) || !tables.ContainsKey("head"))
                    throw new InvalidDataException("That file has no character map or name.");

                foreach (var c in RequiredCharacters)
                {
                    if (!Maps(cmap, c)) throw new InvalidDataException("That font can't draw Latin letters and digits.");
                }

                var family = Family(name);
                if (family == null) throw new InvalidDataException("That font doesn't give its name.");
                family = family.Trim();
                if (!IsSafeFamily(family)) throw new InvalidDataException("That font's name can't be used. Rename it and try again.");

                var min = 400;
                var max = 400;
                if (tables.TryGetValue("fvar", out var fvar) && WeightAxis(fvar) is { } axis)
                {
                    min = axis.Min;
                    max = axis.Max;
                }
                else if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 6)
                {
                    min = max = Math.Clamp((int)U16(os2, 4), 1, 1000);
                }

                return new Woff2Info(family, min, max);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException
                || e is InvalidOperationException)
            {
                throw new InvalidDataException("That file isn't a WOFF2 font Bonfire can read.", e);
            }
        }

        /// <summary>
        /// True for a family name that can go into CSS and the settings page as it is:
        /// letters, digits, spaces, '-', '_' and '.', 1 to 64 of them. A quote, a backslash,
        /// a brace or a control character would need escaping in every place a name is
        /// written, and one missed place is an injection; refusing is the one rule that holds
        /// everywhere.
        /// </summary>
        public static bool IsSafeFamily(string family)
        {
            if (family.Length == 0 || family.Length > 64 || family.Trim().Length != family.Length) return false;
            foreach (var ch in family)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_' || ch == '.')) return false;
            }

            return true;
        }

        /// <summary>Every table, glyf, loca and hmtx in their transformed form.</summary>
        private static Dictionary<string, byte[]> Tables(byte[] file)
        {
            if (file.Length < 48 || Encoding.ASCII.GetString(file, 0, 4) != "wOF2")
                throw new InvalidDataException("That file isn't a WOFF2 font.");
            if (U32(file, 4) == 0x74746366) throw new InvalidDataException("Font collections aren't supported. Upload one font.");
            if (U32(file, 8) != file.Length) throw new InvalidDataException("That font file is incomplete.");

            var numTables = U16(file, 12);
            if (numTables == 0 || numTables > 100) throw new InvalidDataException("That file isn't a WOFF2 font Bonfire can read.");
            var compressed = U32(file, 20);

            var at = 48;
            var dir = new List<(string Tag, uint Length)>();
            for (var n = 0; n < numTables; n++)
            {
                var flags = file[at++];
                string? tag;
                if ((flags & 0x3f) == 63)
                {
                    if (at + 4 > file.Length) throw new IndexOutOfRangeException();
                    tag = Encoding.Latin1.GetString(file, at, 4);
                    at += 4;
                }
                else
                {
                    var index = flags & 0x3f;
                    tag = index < KnownTags.Length ? KnownTags[index] : null;
                }

                if (tag == null) throw new InvalidDataException("That file isn't a WOFF2 font Bonfire can read.");
                var version = flags >> 6;
                // The format defines glyf and loca at 0 (transformed) or 3 (not), hmtx at 0 or
                // 1, and every other table at 0 only. Anything else is a file no font tool
                // writes, and reading its length by a guess would misplace every table after.
                var knownVersion = tag == "glyf" || tag == "loca" ? version == 0 || version == 3
                    : tag == "hmtx" ? version <= 1 : version == 0;
                if (!knownVersion) throw new InvalidDataException("That file isn't a WOFF2 font Bonfire can read.");
                if (dir.Any(d => d.Tag == tag)) throw new InvalidDataException("That font file is damaged.");
                var orig = Base128(file, ref at);
                var transformed = tag == "glyf" || tag == "loca" ? version != 3 : version != 0;
                var length = transformed ? Base128(file, ref at) : orig;
                dir.Add((tag, length));
            }

            if (compressed == 0 || at + (long)compressed > file.Length) throw new InvalidDataException("That font file is incomplete.");

            byte[] stream;
            using (var input = new MemoryStream(file, at, (int)compressed, writable: false))
            using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > MaxDecompressedBytes) throw new InvalidDataException("That font is too large.");
                }

                stream = output.ToArray();
            }

            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long offset = 0;
            foreach (var (tag, length) in dir)
            {
                if (offset + length > stream.Length) throw new InvalidDataException("That font file is damaged.");
                tables[tag] = stream.AsSpan((int)offset, (int)length).ToArray();
                offset += length;
            }

            if (offset != stream.Length) throw new InvalidDataException("That font file is damaged.");
            return tables;
        }

        private static uint Base128(byte[] buf, ref int at)
        {
            ulong value = 0;
            for (var i = 0; i < 5; i++)
            {
                var b = buf[at++];
                if (i == 0 && b == 0x80) throw new InvalidDataException("That font file is damaged.");
                value = (value << 7) | (uint)(b & 0x7f);
                if (value > uint.MaxValue) throw new InvalidDataException("That font file is damaged.");
                if ((b & 0x80) == 0) return (uint)value;
            }

            throw new InvalidDataException("That font file is damaged.");
        }

        /// <summary>True when the cmap maps <paramref name="c"/> to a glyph other than .notdef.</summary>
        private static bool Maps(byte[] cmap, int c)
        {
            var count = U16(cmap, 2);
            int full = -1, bmp = -1;
            for (var i = 0; i < count; i++)
            {
                var p = U16(cmap, 4 + (i * 8));
                var e = U16(cmap, 6 + (i * 8));
                var off = checked((int)U32(cmap, 8 + (i * 8)));
                var format = U16(cmap, off);
                if (format == 12 && full < 0 && ((p == 3 && e == 10) || p == 0)) full = off;
                if (format == 4 && bmp < 0 && ((p == 3 && e == 1) || p == 0)) bmp = off;
            }

            // The full repertoire first (format 12), then the BMP (format 4): the same order
            // a browser prefers them in.
            if (full >= 0)
            {
                var groups = U32(cmap, full + 12);
                if (groups > (uint)(cmap.Length / 12)) throw new InvalidDataException("That font file is damaged.");
                for (var g = 0; g < groups; g++)
                {
                    var at = full + 16 + (g * 12);
                    uint start = U32(cmap, at), end = U32(cmap, at + 4), glyph = U32(cmap, at + 8);
                    if (c >= start && c <= end) return glyph + (uint)(c - start) != 0;
                }

                // A format 12 table with a gap here is not an answer when the font also
                // carries a BMP table: some fonts list only part of the range in 12. r2-22-04.
                if (bmp < 0) return false;
            }

            if (bmp < 0) throw new InvalidDataException("That font has no Unicode character map.");
            var o = bmp;
            var segs = U16(cmap, o + 6) / 2;
            int ends = o + 14, starts = ends + (segs * 2) + 2, deltas = starts + (segs * 2), ranges = deltas + (segs * 2);
            for (var s = 0; s < segs; s++)
            {
                int end = U16(cmap, ends + (s * 2)), start = U16(cmap, starts + (s * 2));
                if (c < start || c > end) continue;
                var delta = (short)U16(cmap, deltas + (s * 2));
                int rangeOffset = U16(cmap, ranges + (s * 2));
                if (rangeOffset == 0) return ((c + delta) & 0xffff) != 0;
                var g = U16(cmap, ranges + (s * 2) + rangeOffset + ((c - start) * 2));
                return g != 0 && ((g + delta) & 0xffff) != 0;
            }

            return false;
        }

        /// <summary>The typographic family (name 16), else the family (name 1), from the Windows or Unicode records.</summary>
        private static string? Family(byte[] name)
        {
            var count = U16(name, 2);
            var strings = U16(name, 4);
            var found = new Dictionary<int, string>();
            for (var i = 0; i < count; i++)
            {
                var r = 6 + (i * 12);
                var platform = U16(name, r);
                var id = U16(name, r + 6);
                if ((platform != 3 && platform != 0) || (id != 1 && id != 16)) continue;
                var len = U16(name, r + 8);
                var off = U16(name, r + 10);
                if (strings + off + len > name.Length) throw new InvalidDataException("That font file is damaged.");
                var s = Encoding.BigEndianUnicode.GetString(name, strings + off, len - (len % 2));
                if (!found.ContainsKey(id) || platform == 3) found[id] = s;
            }

            return found.TryGetValue(16, out var typo) && typo.Trim().Length > 0 ? typo
                : found.TryGetValue(1, out var fam) && fam.Trim().Length > 0 ? fam
                : null;
        }

        /// <summary>The wght axis rounded to whole weights, or null for a font not variable in weight.</summary>
        private static (int Min, int Max)? WeightAxis(byte[] fvar)
        {
            var axesOffset = U16(fvar, 4);
            var axisCount = U16(fvar, 8);
            var axisSize = U16(fvar, 10);
            for (var i = 0; i < axisCount; i++)
            {
                var a = axesOffset + (i * axisSize);
                if (Encoding.Latin1.GetString(fvar, a, 4) != "wght") continue;
                var min = (int)Math.Round(I32(fvar, a + 4) / 65536.0);
                var max = (int)Math.Round(I32(fvar, a + 12) / 65536.0);
                min = Math.Clamp(min, 1, 1000);
                max = Math.Clamp(max, 1, 1000);
                if (max < min) throw new InvalidDataException("That font file is damaged.");
                return (min, max);
            }

            return null;
        }

        private static ushort U16(byte[] b, int at) =>
            at < 0 || at + 2 > b.Length ? throw new IndexOutOfRangeException() : (ushort)((b[at] << 8) | b[at + 1]);

        private static uint U32(byte[] b, int at) =>
            at < 0 || at + 4 > b.Length ? throw new IndexOutOfRangeException()
                : ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

        private static int I32(byte[] b, int at) => unchecked((int)U32(b, at));
    }
}
