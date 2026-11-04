using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Profiles.Controllers;

namespace Jellyfin.Profiles.Mail
{
    /// <summary>
    /// The words in Bonfire's mail. English lives here; every other language is the
    /// <c>email.*</c> keys in the same <c>Web/i18n/{lang}.json</c> file as the rest of the
    /// interface, so adding a language is still one file. <c>Web/i18n/validate.js</c> reads
    /// this dictionary as the third English source, beside EN_STRINGS and the dashboard.
    ///
    /// <para>The language is the one the person was using: sent with the request that
    /// causes the mail, or stored with the address for notices sent later.</para>
    /// </summary>
    internal static class EmailText
    {
        internal const string KeyPrefix = "email.";

        // Written one per line as ["key"] = "value", which is what validate.js reads.
        internal static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
        {
            ["email.test.subject"] = "Bonfire test email",
            ["email.test.body"] = "Email from Bonfire works. Codes and notices will come from this address.",
            ["email.confirm.subject"] = "Confirm your email for Bonfire",
            ["email.confirm.body"] = "Your code is {code}.\n\nEnter it in Bonfire to use this address for {account}. It works for 15 minutes.\n\nIf you didn't ask for this, ignore this email.",
            ["email.reset.subject"] = "Reset your Bonfire PIN",
            ["email.reset.body"] = "Your code is {code}.\n\nEnter it in Bonfire to set a new PIN for {account}. It works for 15 minutes.\n\nIf you didn't ask for this, ignore this email. Your PIN hasn't changed.",
            ["email.resetDone.subject"] = "Your Bonfire PIN was changed",
            ["email.resetDone.body"] = "The PIN for {account} was reset with a code sent to this address.\n\nIf this wasn't you, tell your server's administrator.",
            ["email.lockout.subject"] = "A Bonfire PIN was locked",
            ["email.lockout.body"] = "{name}'s PIN was locked after too many wrong tries. It unlocks by itself within 15 minutes.\n\nYou can turn these emails off in Bonfire: Settings, Your account.",
            ["email.newDevice.subject"] = "New device signed in to Bonfire",
            ["email.newDevice.body"] = "Your household was signed in on a new device: {device}.\n\nIf you don't know it, ask your server's administrator to sign it out.\n\nYou can turn these emails off in Bonfire: Settings, Your account.",
        };

        private static readonly ConcurrentDictionary<string, JsonObject?> Loaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The text for <paramref name="key"/> in <paramref name="language"/>, with
        /// <c>{name}</c> placeholders filled. Falls back to English per key, as t() does.</summary>
        internal static string Get(string key, string? language, IDictionary<string, string>? values = null)
        {
            var text = Translated(key, language) ?? (En.TryGetValue(key, out var en) ? en : key);
            if (values != null)
            {
                foreach (var pair in values) text = text.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
            }
            return text;
        }

        private static string? Translated(string key, string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return null;
            foreach (var code in Candidates(language.Trim()))
            {
                var table = Loaded.GetOrAdd(code, c =>
                {
                    var json = ProfilesController.ReadLocaleJson(c);
                    if (json == null) return null;
                    try { return JsonNode.Parse(json) as JsonObject; }
                    catch (System.Text.Json.JsonException) { return null; }
                });
                if (table != null && table[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        /// <summary>"pt-BR", then "pt". English is the fallback, not a candidate.</summary>
        private static IEnumerable<string> Candidates(string language)
        {
            yield return language;
            var dash = language.IndexOf('-');
            if (dash > 0) yield return language.Substring(0, dash);
        }

        /// <summary>A language tag worth storing: the same shape the server serves.</summary>
        internal static string CleanLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return string.Empty;
            language = language.Trim();
            return language.Length <= 20 && System.Text.RegularExpressions.Regex.IsMatch(language, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$")
                ? language : string.Empty;
        }
    }
}
