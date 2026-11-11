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

        /// <summary>Messages the server sends to a profile's apps through Jellyfin (1.7.2).
        /// Written here for the same reason as the mail: no page reads them.</summary>
        internal const string NoticePrefix = "notice.";

        /// <summary>A key only the server reads, which no browser is sent.</summary>
        internal static bool IsServerText(string key) =>
            key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.StartsWith(NoticePrefix, StringComparison.Ordinal);

        // Written one per line as ["key"] = "value", which is what validate.js reads.
        internal static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
        {
            ["email.test.subject"] = "Bonfire test email",
            ["email.test.body"] = "Bonfire email is working. Codes and notices will come from this address.",
            ["email.confirm.subject"] = "Confirm your Bonfire email",
            ["email.confirm.body"] = "Your code is {code}. It expires in 15 minutes.\n\nEnter it in Bonfire to use this address for {account}.\n\nIf you didn't ask for this, ignore this email.",
            ["email.reset.subject"] = "Reset your Bonfire PIN",
            ["email.reset.body"] = "Your code is {code}. It expires in 15 minutes.\n\nEnter it in Bonfire to set a new PIN for {account}.\n\nIf you didn't ask for this, ignore this email. Your PIN hasn't changed.",
            ["email.resetDone.subject"] = "Your Bonfire PIN was changed",
            ["email.resetDone.body"] = "The PIN for {account} was reset with a code sent to this address.\n\nIf this wasn't you, tell your server's administrator.",
            ["email.changed.subject"] = "Your Bonfire email address was changed",
            ["email.changed.body"] = "The email address for {account} was changed to {address}. This address will no longer get codes or notices.\n\nIf this wasn't you, tell your server's administrator.",
            ["email.lockout.subject"] = "Too many wrong PINs on Bonfire",
            ["email.lockout.body"] = "Someone entered {name}'s PIN wrong too many times. They can try again in 15 minutes.\n\nTurn these emails off in Bonfire: Settings, Your account.",
            ["email.newDevice.subject"] = "New device signed in to Bonfire",
            ["email.newDevice.body"] = "Your household signed in on a new device: {device}.\n\nIf you don't recognize it, ask your server's administrator to sign it out.\n\nTurn these emails off in Bonfire: Settings, Your account.",
            ["email.timeAsked.subject"] = "{name} asked for more time",
            ["email.timeAsked.body"] = "{name} asked for more time.\n\nTo answer, open Bonfire, choose Manage profiles, then {name}.\n\nTurn these emails off in Bonfire: Settings, Your account.",
            ["email.limitReached.subject"] = "{name} reached today's limit",
            ["email.limitReached.body"] = "{name} has reached today's limit of {minutes} min.\n\nTo give more time, open Bonfire, choose Manage profiles, then {name}.\n\nTurn these emails off in Bonfire: Settings, Your account.",
            ["notice.warning.header"] = "Time's almost up",
            ["notice.timeUp.header"] = "Time's up",
            ["notice.stopsIn"] = "Playback stops in {minutes} min.",
            ["notice.stopped"] = "Viewing time is over for now.",
            ["notice.againAt"] = "You can watch again at {time}. Ask a parent for more time.",
            ["notice.againOn"] = "You can watch again on {day} at {time}. Ask a parent for more time.",
            ["notice.notNow"] = "You can't watch right now. Ask a parent for more time.",
            ["notice.doneToday"] = "That's all for today. Ask a parent for more time.",
            ["notice.picker.profiles"] = "Profiles",
            ["notice.picker.switch"] = "Switch profile",
            ["notice.picker.delete"] = "Delete",
            ["notice.picker.ok"] = "OK",
            ["notice.picker.wrongPin"] = "Wrong PIN",
            ["notice.picker.tooMany"] = "Too many tries. Try again later.",
            ["notice.picker.cantOpen"] = "Couldn't open this profile.",
            ["notice.picker.maintenance"] = "Closed for maintenance.",
            ["notice.picker.device"] = "Not allowed on this device.",
            ["notice.picker.off"] = "This profile is turned off.",
            ["notice.picker.sessions"] = "Too many screens use this profile.",
            ["notice.asked.header"] = "More time?",
            ["notice.asked"] = "{name} asked for more time. To answer, open Bonfire, choose Manage profiles, then {name}.",
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
                // Only a language that exists is kept. Caching the null for a tag that doesn't
                // let anyone fill the table with made-up ones (R-1066).
                if (!Loaded.TryGetValue(code, out var table))
                {
                    var json = ProfilesController.ReadLocaleJson(code);
                    if (json == null) continue;
                    try { table = JsonNode.Parse(json) as JsonObject; }
                    catch (System.Text.Json.JsonException) { continue; }
                    if (table == null) continue;
                    Loaded[code] = table;
                }
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
