using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// A picture an administrator uploads to tile behind a custom theme's switcher, in place of
    /// one of the ten Bonfire ships. docs/themes.md, "Pattern pictures".
    /// <para>
    /// Checked as the background image is (<see cref="ThemeLogo.Sniff"/>), with two things narrower:
    /// PNG or WebP only, never JPEG (a tile with no transparency hides the theme under it), and
    /// never SVG or anything animated. SVG can carry script; the built-in ones are ours and are
    /// served with a policy that stops it, an upload is not. Stored under half its SHA-256, so a
    /// name cannot carry a path and the same picture uploaded twice is one file.
    /// </para>
    /// <para>
    /// Lifetime is the background image's: an upload only stores the file, the theme names it when
    /// saved, and a file no theme names any more is removed by the save that stopped naming it, by
    /// deleting the theme, or by the orphan cleanup.
    /// </para>
    /// </summary>
    public static class ThemePattern
    {
        /// <summary>An upload larger than this is refused unread: the background image's cap.</summary>
        public const int MaxBytes = ThemeBackground.MaxBytes;

        /// <summary>The smallest side accepted, in pixels. The decoration's own size runs from 16.</summary>
        public const int MinSide = 16;

        /// <summary>The largest side accepted, in pixels: the decoration's own largest size.</summary>
        public const int MaxSide = 1024;

        private static readonly System.Text.RegularExpressions.Regex FileName =
            new(@"^[0-9a-f]{32}\.(png|webp)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Where the pictures are kept: the plugin's data folder, <c>patterns/</c>.</summary>
        public static string Folder => Path.Combine(
            Plugin.Instance?.AppPaths.DataPath ?? Path.GetTempPath(), "plugins", "ProfilesManagement", "patterns");

        /// <summary>Where a picture is served, relative to the server. Named by its contents.</summary>
        public static string Url(string file) => "/plugins/profiles/themes/pattern-files/" + Uri.EscapeDataString(file);

        /// <summary>True for a name an upload could have been stored under: a hash and .png or .webp.</summary>
        public static bool IsFileName(string? name) => name != null && FileName.IsMatch(name);

        /// <summary>
        /// The format and size of <paramref name="bytes"/>. Throws
        /// <see cref="InvalidDataException"/> with an English reason for anything else.
        /// </summary>
        public static LogoImageInfo Read(byte[] bytes)
        {
            LogoImageInfo info;
            try
            {
                info = ThemeLogo.Sniff(bytes);
            }
            catch (InvalidDataException e) when (ThemeLogo.IsAnimated(e))
            {
                throw new InvalidDataException("A pattern picture can't be animated.", e);
            }

            if (info.Extension != ".png" && info.Extension != ".webp")
                throw new InvalidDataException("Use a PNG or WebP image.");
            if (info.Width < MinSide || info.Height < MinSide || info.Width > MaxSide || info.Height > MaxSide)
                throw new InvalidDataException("A pattern picture is " + MinSide + " to " + MaxSide + " pixels on each side.");
            return info;
        }

        /// <summary>The uploaded picture a stored theme's text names, or null when it names none.</summary>
        public static string? FileOf(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return FileOf(JsonNode.Parse(json));
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>The uploaded picture a theme file's pattern names, or null for none or a built-in one.</summary>
        public static string? FileOf(JsonNode? theme)
        {
            // Kind checks, as ThemeBackground.FileOf: the indexer throws on an array or a bare
            // value, and a theme body of [1] reached here as a 500 instead of the validator's 400.
            if (theme is not JsonObject t || t["decorations"] is not JsonArray list) return null;
            foreach (var d in list)
            {
                if (d is JsonObject o && o["name"] is JsonValue n && n.TryGetValue<string>(out var name) && name == "pattern"
                    && o["image"] is JsonValue v && v.TryGetValue<string>(out var f) && IsFileName(f)) return f;
            }

            return null;
        }

        /// <summary>
        /// Every uploaded picture a theme file names, from its pattern and its second pattern.
        /// FileOf alone looked at the first, so a picture uploaded for the second counted as unused
        /// and was deleted by the next save or cleanup.
        /// </summary>
        public static List<string> FilesOf(JsonNode? theme)
        {
            var found = new List<string>();
            if (theme is not JsonObject t || t["decorations"] is not JsonArray list) return found;
            foreach (var d in list)
            {
                if (d is JsonObject o && o["name"] is JsonValue n && n.TryGetValue<string>(out var name) && (name == "pattern" || name == "pattern2")
                    && o["image"] is JsonValue v && v.TryGetValue<string>(out var f) && IsFileName(f) && !found.Contains(f)) found.Add(f);
            }

            return found;
        }

        /// <summary>FilesOf for a stored theme's text; none when it does not parse.</summary>
        public static List<string> FilesOf(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new List<string>();
            try
            {
                return FilesOf(JsonNode.Parse(json));
            }
            catch (System.Text.Json.JsonException)
            {
                return new List<string>();
            }
        }

        /// <summary>Every uploaded picture the configuration's custom themes name.</summary>
        public static HashSet<string> Named(PluginConfiguration config) =>
            config.CustomThemes.SelectMany(c => FilesOf(c.Json)).ToHashSet(StringComparer.Ordinal);

        /// <summary>True when <paramref name="file"/> is a stored picture's name and its file is here.</summary>
        public static bool Exists(string? file) =>
            IsFileName(file) && File.Exists(Path.Combine(Folder, file!));

        /// <summary>
        /// The stored pictures no custom theme names and nobody has uploaded within
        /// <see cref="ThemeBackground.UnusedGrace"/>, for the same reason as a background
        /// image's. The caller holds ConfigLock.
        /// </summary>
        public static List<string> Unused(PluginConfiguration config)
        {
            if (!Directory.Exists(Folder)) return new List<string>();
            var named = Named(config);
            var cutoff = DateTime.UtcNow - ThemeBackground.UnusedGrace;
            return Directory.GetFiles(Folder)
                .Where(p => File.GetLastWriteTimeUtc(p) < cutoff)
                .Select(Path.GetFileName)
                .Where(f => IsFileName(f) && !named.Contains(f!)).Select(f => f!).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Deletes <paramref name="file"/> when no custom theme names it. Named by a checked
        /// hash, so the join cannot leave the folder. False when it could not be deleted; a
        /// file left behind costs disk only, since nothing names it.
        /// </summary>
        public static bool DeleteIfUnused(string? file, PluginConfiguration config)
        {
            if (!IsFileName(file) || Named(config).Contains(file!)) return true;
            try
            {
                var path = Path.Combine(Folder, file!);
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The theme file without a pattern whose picture is not here, for a file that arrived
        /// from somewhere the picture did not (a backup restored on another server, a theme
        /// imported). The rest of its decorations stay. True when one was taken out.
        /// </summary>
        public static bool DropMissing(JsonNode? theme)
        {
            if (theme is not JsonObject t || t["decorations"] is not JsonArray list) return false;
            var dropped = false;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not JsonObject o || o["name"] is not JsonValue n || !n.TryGetValue<string>(out var name) || (name != "pattern" && name != "pattern2")) continue;
                if (o["image"] is JsonValue v && v.TryGetValue<string>(out var f) && IsFileName(f) && !Exists(f))
                {
                    list.RemoveAt(i);
                    dropped = true;
                }
            }

            // A second pattern and a twinkle are drawn on the first; without it they are not valid.
            if (dropped && !list.Any(x => x is JsonObject p && p["name"] is JsonValue pn && pn.TryGetValue<string>(out var s) && s == "pattern"))
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is JsonObject o && o["name"] is JsonValue n && n.TryGetValue<string>(out var name) && (name == "pattern2" || name == "twinkle")) list.RemoveAt(i);
                }
            }

            return dropped;
        }
    }
}
