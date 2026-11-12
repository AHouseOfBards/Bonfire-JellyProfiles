using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// A custom theme's background image (2.0): one picture drawn behind the switcher's
    /// screens, under a layer of the theme's own fill whose strength is the theme's
    /// <c>background.image.dim</c>. docs/themes.md, "Background image".
    /// <para>
    /// Checked as the logo is (<see cref="ThemeLogo.Sniff"/>): PNG, JPEG or WebP by its bytes,
    /// never SVG or anything animated. Stored under half its SHA-256, so a name can never
    /// carry a path and the same picture uploaded twice is one file.
    /// </para>
    /// <para>
    /// An upload only stores the file. A theme names it when it is saved, so the editor can
    /// show a picture before anything is committed; saving a theme without it, saving it with
    /// another, or deleting the theme removes a file no theme names any more. An upload that
    /// was never saved is left for the orphan cleanup.
    /// </para>
    /// </summary>
    public static class ThemeBackground
    {
        /// <summary>
        /// An upload larger than this is refused unread. Twice the logo's: a photograph fills
        /// the screen, and a 1080p JPEG is well under it. A television downloads it before the
        /// picker shows it, so not more.
        /// </summary>
        public const int MaxBytes = 2 * 1024 * 1024;

        /// <summary>The smallest side accepted, in pixels. Drawn to cover the screen, so a tiny one is a blur.</summary>
        public const int MinSide = 320;

        /// <summary>The largest side accepted, in pixels: the logo's.</summary>
        public const int MaxSide = ThemeLogo.MaxSide;

        /// <summary>The strongest dim a theme may give. At 1 the picture would not show at all.</summary>
        public const double MaxDim = 0.95;

        /// <summary>Where the images are kept: the plugin's data folder, <c>backgrounds/</c>.</summary>
        public static string Folder => Path.Combine(
            Plugin.Instance?.AppPaths.DataPath ?? Path.GetTempPath(), "plugins", "ProfilesManagement", "backgrounds");

        /// <summary>Where an image is served, relative to the server. Named by its contents.</summary>
        public static string Url(string file) => "/plugins/profiles/themes/backgrounds/" + Uri.EscapeDataString(file);

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
            catch (InvalidDataException e) when (e.Message.Contains("animated", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A background image can't be animated.", e);
            }

            if (info.Width < MinSide || info.Height < MinSide || info.Width > MaxSide || info.Height > MaxSide)
                throw new InvalidDataException("A background image is " + MinSide + " to " + MaxSide + " pixels on each side.");
            return info;
        }

        /// <summary>The image a stored theme's text names, or null when it names none (or a bad one).</summary>
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

        /// <summary>The image a theme file names, or null when it names none (or a bad one).</summary>
        public static string? FileOf(JsonNode? theme) =>
            // Kind checks: an indexer throws on an array or a bare value, and this reads both
            // an uploaded body and a stored text edited by hand.
            theme is JsonObject t && t["background"] is JsonObject b && b["image"] is JsonObject i
                && i["file"] is JsonValue v && v.TryGetValue<string>(out var f) && ThemeLogo.IsFileName(f) ? f : null;

        /// <summary>Every image the configuration's custom themes name.</summary>
        public static HashSet<string> Named(PluginConfiguration config) =>
            config.CustomThemes.Select(c => FileOf(c.Json)).Where(f => f != null).Select(f => f!).ToHashSet(StringComparer.Ordinal);

        /// <summary>True when <paramref name="file"/> is a stored image's name and its file is here.</summary>
        public static bool Exists(string? file) =>
            ThemeLogo.IsFileName(file) && File.Exists(Path.Combine(Folder, file!));

        /// <summary>
        /// How long an upload no theme names is left alone by the orphan cleanup. An upload is
        /// stored when the picture is chosen and named only when the theme is saved, so an
        /// editor that is still open holds a file nothing names yet; the cleanup deleting it
        /// took the picture out from under the editor, and its Save then named a missing file.
        /// An hour covers a long editing session with room to spare, and waiting costs only
        /// disk (a file is at most <see cref="MaxBytes"/>): the cleanup is run by hand and
        /// finds the file next time. Every upload rewrites its file, so choosing the same
        /// picture again restarts the hour.
        /// </summary>
        public static readonly TimeSpan UnusedGrace = TimeSpan.FromHours(1);

        /// <summary>
        /// The stored images no custom theme names and nobody has uploaded within
        /// <see cref="UnusedGrace"/>. The caller holds ConfigLock, so a save cannot name one
        /// between this list and its deletion.
        /// </summary>
        public static List<string> Unused(PluginConfiguration config)
        {
            if (!Directory.Exists(Folder)) return new List<string>();
            var named = Named(config);
            var cutoff = DateTime.UtcNow - UnusedGrace;
            return Directory.GetFiles(Folder)
                .Where(p => File.GetLastWriteTimeUtc(p) < cutoff)
                .Select(Path.GetFileName)
                .Where(f => ThemeLogo.IsFileName(f) && !named.Contains(f!)).Select(f => f!).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Deletes <paramref name="file"/> when no custom theme names it. Named by a checked
        /// hash, so the join cannot leave the folder. False when it could not be deleted; a
        /// file left behind costs disk only, since nothing names it.
        /// </summary>
        public static bool DeleteIfUnused(string? file, PluginConfiguration config)
        {
            if (!ThemeLogo.IsFileName(file) || Named(config).Contains(file!)) return true;
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
        /// The theme file without its image, for a file that arrived from somewhere the image
        /// did not (a backup restored on another server, a theme file imported). True when one
        /// was taken out.
        /// </summary>
        public static bool DropMissing(JsonNode? theme)
        {
            if (theme?["background"] is not JsonObject bg || !bg.ContainsKey("image")) return false;
            if (Exists(FileOf(theme))) return false;
            bg.Remove("image");
            return true;
        }
    }
}
