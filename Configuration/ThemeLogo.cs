using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>What <see cref="ThemeLogo.Read"/> found in an image.</summary>
    /// <param name="Extension">.png, .jpg or .webp.</param>
    /// <param name="ContentType">The type it is served as.</param>
    /// <param name="Width">Its width in pixels.</param>
    /// <param name="Height">Its height in pixels.</param>
    public sealed record LogoImageInfo(string Extension, string ContentType, int Width, int Height);

    /// <summary>
    /// The server's logo (1.7.2): an image, a line of text, or nothing, shown at the top right
    /// of every switcher screen. docs/themes.md, "Getting themes".
    /// <para>
    /// The image is identified by its bytes, never by a name or type the upload claims: PNG, JPEG
    /// or WebP, with a size read from its header. Not SVG, which can carry script and would be
    /// served from the server's own origin; not GIF, whose animation nobody asked for.
    /// </para>
    /// </summary>
    public static class ThemeLogo
    {
        /// <summary>An upload larger than this is refused unread. A logo is drawn small.</summary>
        public const int MaxBytes = 1024 * 1024;

        /// <summary>The smallest and largest side accepted, in pixels.</summary>
        public const int MinSide = 16;

        /// <summary>See <see cref="MinSide"/>.</summary>
        public const int MaxSide = 4096;

        /// <summary>Longest logo text and alt text, in characters.</summary>
        public const int MaxText = 40;

        /// <summary>See <see cref="MaxText"/>.</summary>
        public const int MaxAlt = 100;

        public const string Rounded = "rounded";
        public const string Square = "square";

        /// <summary>The corners a stored value means: "square" or, for anything else, "rounded".</summary>
        public static string CleanCorners(string? value) => value == Square ? Square : Rounded;

        private static readonly System.Text.RegularExpressions.Regex FileName =
            new(@"^[0-9a-f]{32}\.(png|jpg|webp)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Where the logo is kept: the plugin's data folder, <c>logo/</c>.</summary>
        public static string Folder => Path.Combine(
            Plugin.Instance?.AppPaths.DataPath ?? Path.GetTempPath(), "plugins", "ProfilesManagement", "logo");

        /// <summary>The name a logo is stored and served under: half its SHA-256 and its extension.</summary>
        public static string FileNameFor(byte[] bytes, LogoImageInfo info) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 16).ToLowerInvariant() + info.Extension;

        /// <summary>True for a name <see cref="FileNameFor"/> could have produced.</summary>
        public static bool IsFileName(string? name) => name != null && FileName.IsMatch(name);

        /// <summary>Where the logo is served, relative to the server. Named by its contents, so a new one is a new address.</summary>
        public static string Url(string file) => "/plugins/profiles/themes/logo/" + Uri.EscapeDataString(file);

        /// <summary>
        /// Text that can be a logo or its alt text: trimmed, one line, no control characters,
        /// at most <paramref name="max"/> characters. Null for empty; throws
        /// <see cref="ArgumentException"/> for anything else.
        /// </summary>
        public static string? CleanText(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var t = value.Trim();
            if (t.Length > max || t.Any(char.IsControl)) throw new ArgumentException("too long or not one line");
            return t;
        }

        /// <summary>
        /// The format and size of <paramref name="bytes"/>. Throws
        /// <see cref="InvalidDataException"/> with an English reason for anything else.
        /// </summary>
        public static LogoImageInfo Read(byte[] bytes)
        {
            var info = Sniff(bytes);
            if (info.Width < MinSide || info.Height < MinSide || info.Width > MaxSide || info.Height > MaxSide)
                throw new InvalidDataException("A logo is " + MinSide + " to " + MaxSide + " pixels on each side.");
            return info;
        }

        /// <summary>
        /// The format and size of <paramref name="bytes"/>, with no limit on the size: the
        /// check the logo and a theme's background image (<see cref="ThemeBackground"/>) share.
        /// Throws <see cref="InvalidDataException"/> with an English reason for anything else.
        /// </summary>
        public static LogoImageInfo Sniff(byte[] bytes)
        {
            // The size is read from the file's own header and not checked against its data (R-1080).
            // A file that claims one size and holds another is the administrator's own upload, and the
            // browser draws what the data holds. Nothing here allocates by the claimed size.
            LogoImageInfo? info;
            try
            {
                info = Png(bytes) ?? Jpeg(bytes) ?? WebP(bytes);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException)
            {
                throw new InvalidDataException("That image couldn't be read.", e);
            }

            if (info == null) throw new InvalidDataException("Use a PNG, JPEG or WebP image.");
            return info;
        }

        private static LogoImageInfo? Png(byte[] b)
        {
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (b.Length < 24 || !b.AsSpan(0, 8).SequenceEqual(sig)) return null;
            if (b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R') return null;
            // An APNG is a PNG with an acTL chunk ahead of its first IDAT, and a browser plays it. The
            // signature and IHDR look the same, so the chunk list says (r2-20-01). Walked until the
            // first IDAT; a file that runs out first is left to the browser, which draws nothing.
            for (var at = 8; at + 8 <= b.Length;)
            {
                var length = U32(b, at);
                var type = Ascii(b, at + 4);
                if (type == "acTL") throw AnimatedImageException("A logo can't be animated.");
                if (type == "IDAT") break;
                var next = (long)at + 12 + length;
                if (next > b.Length) break;
                at = (int)next;
            }

            return new LogoImageInfo(".png", "image/png", (int)Math.Min(U32(b, 16), int.MaxValue), (int)Math.Min(U32(b, 20), int.MaxValue));
        }

        private static LogoImageInfo? Jpeg(byte[] b)
        {
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
            var at = 2;
            while (at + 4 <= b.Length)
            {
                if (b[at] != 0xFF) return null;
                var marker = b[at + 1];
                if (marker == 0xFF) { at++; continue; }
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { at += 2; continue; }
                var length = (b[at + 2] << 8) | b[at + 3];
                if (length < 2) return null;
                // Start of frame: every SOFn but DHT (C4), JPG (C8) and DAC (CC).
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    if (at + 9 > b.Length) return null;
                    var h = (b[at + 5] << 8) | b[at + 6];
                    var w = (b[at + 7] << 8) | b[at + 8];
                    return new LogoImageInfo(".jpg", "image/jpeg", w, h);
                }

                if (marker == 0xDA || marker == 0xD9) return null;
                at += 2 + length;
            }

            return null;
        }

        private static LogoImageInfo? WebP(byte[] b)
        {
            if (b.Length < 30 || Ascii(b, 0) != "RIFF" || Ascii(b, 8) != "WEBP") return null;
            var chunk = Ascii(b, 12);
            if (chunk == "VP8X")
            {
                // Bit 1 of the feature flags: an animation. A GIF is refused for being one, so
                // an animated WebP is too; a logo does not move.
                if ((b[20] & 0x02) != 0) throw AnimatedImageException("A logo can't be animated.");
                var w = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                var h = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                return new LogoImageInfo(".webp", "image/webp", w, h);
            }

            if (chunk == "VP8L")
            {
                if (b[20] != 0x2F) return null;
                var bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                return new LogoImageInfo(".webp", "image/webp", 1 + (bits & 0x3FFF), 1 + ((bits >> 14) & 0x3FFF));
            }

            if (chunk == "VP8 ")
            {
                if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return null;
                var w = (b[26] | (b[27] << 8)) & 0x3FFF;
                var h = (b[28] | (b[29] << 8)) & 0x3FFF;
                return new LogoImageInfo(".webp", "image/webp", w, h);
            }

            return null;
        }

        private static string Ascii(byte[] b, int at) =>
            at + 4 > b.Length ? throw new IndexOutOfRangeException() : System.Text.Encoding.ASCII.GetString(b, at, 4);

        private static uint U32(byte[] b, int at) =>
            at + 4 > b.Length ? throw new IndexOutOfRangeException()
                : ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

        /// <summary>
        /// The refusal for an animated image. InvalidDataException is sealed, so it carries a
        /// flag in <see cref="Exception.Data"/> that <see cref="IsAnimated"/> reads: a caller
        /// can tell it from a bad image without matching the English message. r2-20-06.
        /// </summary>
        private static InvalidDataException AnimatedImageException(string message)
        {
            var e = new InvalidDataException(message);
            e.Data[AnimatedKey] = true;
            return e;
        }

        private const string AnimatedKey = "Bonfire.Animated";

        internal static bool IsAnimated(InvalidDataException e) => e.Data.Contains(AnimatedKey);
    }
}
