using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// The two script fragments the plugin adds to Jellyfin's index.html, and the logic that
    /// puts them there.
    ///
    /// This used to live inside <see cref="ProfilesBootstrapTask"/>, which patched the file on
    /// disk. From 1.4.1 the same fragments are also injected on the fly by
    /// <see cref="ProfilesIndexMiddleware"/>, which serves index.html straight from the request
    /// pipeline and never touches the file. Both paths share this class so the tag they produce
    /// cannot drift apart — a mismatch would make the on-disk patcher and the middleware
    /// perpetually "fix" each other's work.
    /// </summary>
    internal static class WebInjection
    {
        /// <summary>
        /// Cache-buster written into the script URL.
        ///
        /// Deliberately the assembly version and nothing else. This used to prefer
        /// Plugin.Instance?.Version with the assembly version as a fallback, which made the
        /// value depend on WHEN it was read: the bootstrap task can run before the Plugin
        /// constructor has assigned Plugin.Instance, so the tag could be written using one
        /// source and later compared against the other. If those two render differently
        /// (e.g. "1.2.8" vs "1.2.8.0") the comparison never matches again and the dashboard
        /// shows "script update pending" forever, no matter how many times the file is
        /// rewritten or what permissions are granted.
        /// </summary>
        internal static string ScriptVersion =>
            typeof(WebInjection).Assembly.GetName().Version?.ToString() ?? "0";

        /// <summary>
        /// The exact script tag to inject before &lt;/body&gt;. The URL
        /// /plugins/profiles/profiles.js is the path Jellyfin uses to serve embedded resources
        /// from plugin assemblies.
        /// </summary>
        internal static string BodyScriptTag =>
            $"<script src=\"/plugins/profiles/profiles.js?v={ScriptVersion}\" defer></script>";

        /// <summary>Unique substring to detect whether the body tag is already present.</summary>
        internal const string BodyMarker = "/plugins/profiles/profiles.js";

        /// <summary>
        /// Tiny inline script injected into &lt;head&gt; — runs before any deferred bundle,
        /// before React renders. Reads the switching flag set by profiles.js before each
        /// window.location.reload() and hides the html element instantly to prevent the
        /// flash-of-content during profile switches.
        /// A 4-second failsafe restores visibility if profiles.js fails to load.
        /// </summary>
        internal const string HeadScript =
            "<script id=\"jpf-eh\">" +
            "!function(){" +
                "if(localStorage.getItem('jpf-sw')){" +
                    "var h=document.documentElement;" +
                    "h.style.opacity='0';" +
                    "h.style.background='#101010';" +
                    "h.style.colorScheme='dark';" +
                    "window.__jpReveal=setTimeout(function(){" +
                        "h.style.opacity='';" +
                        "h.style.background='';" +
                        "h.style.colorScheme='';" +
                    "},4e3);" +
                    "localStorage.removeItem('jpf-sw');" +
                "}" +
            "}();" +
            "</script>";

        /// <summary>Unique substring to detect whether the head script is already present.</summary>
        internal const string HeadMarker = "jpf-eh";

        // Pulls the ?v= value out of whatever plugin script tag is currently in the HTML.
        // Comparing the extracted version beats comparing the whole tag string: a hand-edited
        // file with different attribute order, quoting or spacing is still recognised as
        // current instead of being reported as stale forever.
        private static readonly Regex InjectedVersionRegex = new(
            @"/plugins/profiles/profiles\.js\?v=([^""'&\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HeadScriptRegex = new(
            @"<script id=""jpf-eh"">[\s\S]*?</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BodyScriptRegex = new(
            @"<script[^>]*src=[""'][^""']*/plugins/profiles/profiles\.js[^""']*[""'][^>]*>\s*(</script>)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Version recorded in the HTML's script tag, or null if absent/unparseable.</summary>
        internal static string? GetInjectedScriptVersion(string html)
        {
            var m = InjectedVersionRegex.Match(html);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>True when the HTML's script tag already points at this build.</summary>
        internal static bool IsScriptVersionCurrent(string html)
            => string.Equals(GetInjectedScriptVersion(html), ScriptVersion, StringComparison.Ordinal);

        /// <summary>
        /// True when both fragments are present and the body tag names this exact build, so
        /// there is nothing to do.
        /// </summary>
        internal static bool IsFullyInjected(string html)
            => html.Contains(BodyMarker, StringComparison.Ordinal)
               && html.Contains(HeadMarker, StringComparison.Ordinal)
               && IsScriptVersionCurrent(html);

        /// <summary>
        /// Adds both fragments to <paramref name="html"/>, updating either one if it is present
        /// but out of date.
        /// </summary>
        /// <param name="html">The document to inject into.</param>
        /// <param name="result">The document afterwards. Equal to the input when nothing changed.</param>
        /// <returns>True when <paramref name="result"/> differs from the input.</returns>
        internal static bool Inject(string html, out string result)
        {
            bool changed = false;

            // ── 1. Update or inject head early-hide script ───────────────────
            if (HeadScriptRegex.IsMatch(html))
            {
                if (!html.Contains(HeadScript, StringComparison.Ordinal))
                {
                    // MatchEvaluator, not a replacement string: "$" is a substitution token
                    // in the string overload, so a "$" anywhere in the script would corrupt
                    // the document silently.
                    html = HeadScriptRegex.Replace(html, _ => HeadScript);
                    changed = true;
                }
            }
            else
            {
                int headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                if (headIdx != -1)
                {
                    html = html.Insert(headIdx + "<head>".Length, NewlineOf(html) + HeadScript);
                    changed = true;
                }
            }

            // ── 2. Update or inject body script ──────────────────────────────
            if (BodyScriptRegex.IsMatch(html))
            {
                if (!IsScriptVersionCurrent(html))
                {
                    // MatchEvaluator — see the note on the head script above.
                    var tag = BodyScriptTag;
                    html = BodyScriptRegex.Replace(html, _ => tag);
                    changed = true;
                }
            }
            else
            {
                int bodyIdx = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyIdx != -1)
                {
                    html = html.Insert(bodyIdx, BodyScriptTag + NewlineOf(html));
                    changed = true;
                }
            }

            result = html;
            return changed;
        }

        /// <summary>
        /// Takes both fragments back out, restoring the document to how Jellyfin shipped it.
        /// </summary>
        /// <param name="html">The document to clean.</param>
        /// <param name="result">The document afterwards. Equal to the input when nothing changed.</param>
        /// <returns>True when <paramref name="result"/> differs from the input.</returns>
        internal static bool Remove(string html, out string result)
        {
            var cleaned = HeadScriptRegex.Replace(html, string.Empty);
            cleaned = BodyScriptRegex.Replace(cleaned, string.Empty);

            // Both fragments were inserted on a line of their own, so removing them leaves a
            // blank line behind. Tidy it up rather than letting index.html accumulate one per
            // install/uninstall cycle.
            cleaned = BlankLineRegex.Replace(cleaned, NewlineOf(html));

            result = cleaned;
            return !string.Equals(cleaned, html, StringComparison.Ordinal);
        }

        /// <summary>
        /// The line ending the document already uses.
        ///
        /// <para>
        /// Not <see cref="Environment.NewLine"/>, which is whatever the *server* runs on. A
        /// Windows Jellyfin writing CRLF into the LF index.html it shipped with is harmless to
        /// the browser but leaves the file subtly different from the packaged one, which then
        /// makes it impossible to restore exactly when injection is turned off again.
        /// </para>
        /// </summary>
        private static string NewlineOf(string html)
            => html.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        private static readonly Regex BlankLineRegex = new(
            @"(\r?\n)[ \t]*(\r?\n)",
            RegexOptions.Compiled);
    }
}
