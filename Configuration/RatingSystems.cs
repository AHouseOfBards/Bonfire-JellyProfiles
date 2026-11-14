using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles
{
    /// <summary>One age a profile can be limited to, with the country's names for it.</summary>
    public sealed class RatingChoice
    {
        public int Score { get; set; }
        public List<string> Labels { get; set; } = new List<string>();
    }

    /// <summary>
    /// The rating system of the server's Preferred metadata country, read from Jellyfin's own
    /// tables, so the profile form offers that country's ratings and no others.
    /// <para>
    /// <b>Why not GET /Localization/ParentalRatings.</b> It answers with the same country, but
    /// LocalizationManager.GetParentalRatings first pads it with generic entries ("Approved", "10",
    /// "13", "14", "21", "XXX", "Banned") for any age the country lacks. A padded "10" cannot be told
    /// from a real one (France has a rating named "10" at age 10), so filtering that list either
    /// drops a country's own rating or offers ages it does not have. The tables have no padding.
    /// </para>
    /// <para>
    /// They are embedded resources in Jellyfin's Emby.Server.Implementations assembly, named
    /// <c>Emby.Server.Implementations.Localization.Ratings.{cc}.json</c>, with the same prefix and
    /// format in 10.11 and 12.0 (verified in both branches' LocalizationManager.LoadAll). If a future
    /// server renames them, nothing is found and the form falls back to the US list it always had.
    /// The limit itself still works, because it is a score.
    /// </para>
    /// </summary>
    public static class RatingSystems
    {
        internal const string ResourcePrefix = "Emby.Server.Implementations.Localization.Ratings.";

        /// <summary>Per country, upper-cased. The tables never change while a server runs.</summary>
        private static readonly ConcurrentDictionary<string, List<RatingChoice>> Cache = new();

        /// <summary>
        /// One choice per age in a Jellyfin rating table, in order, or null if the text is not one.
        /// Adult and banned (1000 and up) are left out: "No restrictions" covers them. Names that only
        /// add a content descriptor (the US TV-PG-D, TV-PG-L and thirteen more, which carry a
        /// sub-score) are left off the label when the age has a plain name, so 10 reads "PG / TV-PG"
        /// rather than seventeen codes.
        /// </summary>
        internal static List<RatingChoice>? Parse(string json, out string? countryCode)
        {
            countryCode = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (Prop(root, "countryCode") is { ValueKind: JsonValueKind.String } cc) countryCode = cc.GetString();
                if (Prop(root, "ratings") is not { ValueKind: JsonValueKind.Array } ratings) return null;

                var byScore = new SortedDictionary<int, (List<string> Plain, List<string> All)>();
                foreach (var r in ratings.EnumerateArray())
                {
                    if (Prop(r, "ratingScore") is not { ValueKind: JsonValueKind.Object } rs) continue;
                    if (Prop(rs, "score") is not { ValueKind: JsonValueKind.Number } sc || !sc.TryGetInt32(out var score)) continue;
                    if (score < 0 || score >= 1000) continue;
                    bool plain = Prop(rs, "subScore") is not { ValueKind: JsonValueKind.Number } sub
                        || (sub.TryGetInt32(out var s) && s == 0);

                    if (!byScore.TryGetValue(score, out var names))
                        byScore[score] = names = (new List<string>(), new List<string>());
                    if (Prop(r, "ratingStrings") is not { ValueKind: JsonValueKind.Array } strings) continue;
                    foreach (var n in strings.EnumerateArray())
                    {
                        var name = n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!names.All.Contains(name)) names.All.Add(name);
                        if (plain && !names.Plain.Contains(name)) names.Plain.Add(name);
                    }
                }

                return byScore
                    .Where(kv => kv.Value.All.Count > 0)
                    .Select(kv => new RatingChoice
                    {
                        Score = kv.Key,
                        Labels = (kv.Value.Plain.Count > 0 ? kv.Value.Plain : kv.Value.All).ToList()
                    })
                    .ToList();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The choices for <paramref name="country"/> from the tables embedded in
        /// <paramref name="jellyfin"/>, or an empty list when it has none for that country.
        /// Matched on the table's own countryCode, ignoring case: the server setting is
        /// "US" and the table says "us".
        /// </summary>
        internal static List<RatingChoice> ForCountry(Assembly jellyfin, string? country, ILogger? logger = null)
        {
            if (jellyfin == null || string.IsNullOrWhiteSpace(country)) return new List<RatingChoice>();
            var key = country.Trim().ToUpperInvariant();
            // A copy each time: the cache is shared by every request, so a caller that sorts
            // or trims what it got would change the answer for the next one.
            if (Cache.TryGetValue(key, out var cached)) return new List<RatingChoice>(cached);

            var found = new List<RatingChoice>();
            try
            {
                foreach (var name in jellyfin.GetManifestResourceNames())
                {
                    if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                    using var stream = jellyfin.GetManifestResourceStream(name);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    var parsed = Parse(reader.ReadToEnd(), out var code);
                    if (parsed != null && string.Equals(code, key, StringComparison.OrdinalIgnoreCase))
                    {
                        found = parsed;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Not cached: a read that threw is worth trying again.
                logger?.LogWarning(ex, "ProfilesPlugin: could not read Jellyfin's rating tables; the profile form will offer the US ratings.");
                return new List<RatingChoice>();
            }

            if (found.Count == 0)
            {
                logger?.LogInformation(
                    "ProfilesPlugin: no rating table for metadata country {Country}; the profile form will offer the US ratings.",
                    country);
            }

            Cache[key] = found;
            return new List<RatingChoice>(found);
        }

        private static JsonElement? Prop(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            }
            return null;
        }
    }
}
