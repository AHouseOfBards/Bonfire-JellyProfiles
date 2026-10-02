using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// The pictures every server offers until its administrator turns them off: thirty Fluent
    /// Emoji by Microsoft, MIT-licensed, embedded in the plugin (Web/starter-avatars, with
    /// the licence beside them).
    /// <para>
    /// Asked for in GitHub issue #32. The avatar library starts empty, and an empty library
    /// is the whole picker on a television, where there is no file browser to upload from.
    /// </para>
    /// <para>
    /// Kept out of <see cref="PluginConfiguration.AvatarLibrary"/> and off disk on purpose.
    /// Copying them into the library on first run would make them the administrator's
    /// files: a Remove would be undone by the next install, a restore of an older
    /// configuration would resurrect or orphan them, and turning the set off would mean
    /// deleting things the administrator might have chosen to keep. As a fixed list served
    /// from the assembly, they are one switch, <see cref="PluginConfiguration.EnableStarterAvatars"/>,
    /// and nothing else to keep in step.
    /// </para>
    /// <para>
    /// Ids are <c>starter-&lt;slug&gt;</c>. The administrator's own ids are twelve hex
    /// characters, so the two can never collide. A slug is only ever looked up in
    /// <see cref="All"/>, never joined to a path or a resource name unchecked.
    /// </para>
    /// </summary>
    public static class StarterAvatars
    {
        public const string IdPrefix = "starter-";
        public const string ContentType = "image/webp";
        public const string Extension = ".webp";

        /// <summary>Embedded under this prefix by an explicit LogicalName in the csproj.</summary>
        public const string ResourcePrefix = "Jellyfin.Profiles.StarterAvatars.";
        public const string LicenseResource = ResourcePrefix + "LICENSE.txt";

        /// <summary>In picker order. The slug is the file name, without its extension.</summary>
        public static readonly IReadOnlyList<(string Slug, string DisplayName)> All = new[]
        {
            ("dog", "Dog"), ("cat", "Cat"), ("fox", "Fox"), ("panda", "Panda"),
            ("lion", "Lion"), ("tiger", "Tiger"), ("koala", "Koala"), ("frog", "Frog"),
            ("unicorn", "Unicorn"), ("penguin", "Penguin"), ("owl", "Owl"), ("monkey", "Monkey"),
            ("rabbit", "Rabbit"), ("bear", "Bear"), ("hamster", "Hamster"), ("pig", "Pig"),
            ("cow", "Cow"), ("dragon", "Dragon"), ("octopus", "Octopus"), ("turtle", "Turtle"),
            ("polar-bear", "Polar bear"), ("wolf", "Wolf"), ("sloth", "Sloth"), ("chick", "Chick"),
            ("robot", "Robot"), ("alien", "Alien"), ("ghost", "Ghost"), ("cool", "Cool"),
            ("cowboy", "Cowboy"), ("nerd", "Nerd")
        };

        public static bool IsStarterId(string? id)
            => id != null && id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

        public static string IdFor(string slug) => IdPrefix + slug;

        /// <summary>
        /// The catalogue entry for an id, or null when it is not one of ours. The returned
        /// slug is the catalogue's own string, never the caller's.
        /// </summary>
        public static (string Slug, string DisplayName)? Find(string? id)
        {
            if (!IsStarterId(id)) return null;
            var slug = id!.Substring(IdPrefix.Length);
            foreach (var entry in All)
            {
                if (string.Equals(entry.Slug, slug, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            return null;
        }

        /// <summary>
        /// Opens the embedded picture for a catalogue slug. Null when the resource is missing,
        /// which would be a packaging fault — tests/cs/starteravatars fails on it.
        /// </summary>
        public static Stream? Open(string slug)
            => typeof(StarterAvatars).Assembly.GetManifestResourceStream(ResourcePrefix + slug + Extension);

        /// <summary>A cache validator that changes when the plugin does, and only then.</summary>
        public static string ETagFor(string slug)
            => "\"starter-" + slug + "-" + (typeof(StarterAvatars).Assembly.GetName().Version?.ToString() ?? "0") + "\"";

        /// <summary>Whether the administrator is offering them.</summary>
        public static bool Enabled(PluginConfiguration? config) => config?.EnableStarterAvatars ?? true;

        public static IEnumerable<string> Slugs => All.Select(a => a.Slug);
    }
}
