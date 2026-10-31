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
        /// <para>
        /// The colour it holds is read from <c>jpf-bg</c>, which profiles.js writes from the
        /// page being left. It used to be a hardcoded <c>#101010</c> with
        /// <c>color-scheme:dark</c>, so on any of Jellyfin's light themes a switch flashed a
        /// full-screen black rectangle — the very flash this script exists to prevent, in the
        /// other direction — and forced dark scrollbars and form controls while it did.
        /// </para>
        /// <para>
        /// Two failsafes restore visibility if the reveal never comes. The timeout was four
        /// seconds, which on a television is indistinguishable from a crash; it is 1.5 now.
        /// The second is exact rather than timed: profiles.js sets <c>window.__jpLoaded</c>
        /// as it evaluates, and being a deferred script it has finished before
        /// DOMContentLoaded — so the flag missing at that point is proof the script never
        /// loaded, and there is nothing left to wait for.
        /// </para>
        /// <para>
        /// The second half is the startup cover. With "Ask on startup" on, jellyfin-web
        /// rendered the home screen and the gate arrived on top of it a beat later: the gate
        /// is only raised from the 500 ms route poll, after the profile list has come back
        /// from the server, and then fades in from opacity 0 — so the household's home
        /// screen was on view for anything from a third of a second to a second on every
        /// cold start, on every client that loads this page. The decision the gate will make
        /// is knowable here, from what profiles.js cached on the last visit, so when it says
        /// the gate is coming the page is covered in the gate's own background before
        /// anything paints, and profiles.js takes the cover down once the gate is on top of
        /// it — or as soon as it decides the gate is not coming after all.
        /// </para>
        /// <para>
        /// It fails towards the flash, never towards a blank screen: it is only raised when
        /// every input says yes (signed in, the cached preference is this account's and says
        /// ask, no profile active this session, on the home route, no emergency disable),
        /// it comes down on DOMContentLoaded when profiles.js never loaded, and a three
        /// second timer takes it down regardless. It is a pseudo-element under the gate's
        /// z-index rather than <c>opacity:0</c> on the document, so the gate can draw over
        /// it while it is still up and nothing jellyfin-web paints underneath is held hidden
        /// from focus or layout. Its colour is <c>--jpf-gate-bg</c>, with the gate's current
        /// gradient as the fallback; the same rule is <c>GATE_COVER_CSS</c> in profiles.js,
        /// and tests/js/gatecover.js fails if the two differ.
        /// </para>
        /// </summary>
        internal const string HeadScript =
            "<script id=\"jpf-eh\">" +
            "!function(){" +
                "var h=document.documentElement;" +
                "if(localStorage.getItem('jpf-sw')){" +
                // Handed to profiles.js, which is deferred and so cannot read the key:
                // this script clears it below, synchronously, during head parsing.
                "window.__jpSwitching=1;" +
                "var v=(localStorage.getItem('jpf-bg')||'').split('|');" +
                "var b=v[0]||'';" +
                // Only a colour is ever written into the style property. Same-origin
                // storage is not a threat boundary, but a value that is not a colour would
                // paint nothing and leave the page hidden until a failsafe fires.
                "if(!/^(#[0-9a-fA-F]{3,8}|rgba?\\([\\d\\s.,%]+\\))$/.test(b))b='#101010';" +
                "var s=v[1]==='light'?'light':'dark';" +
                "h.style.opacity='0';" +
                "h.style.background=b;" +
                "h.style.colorScheme=s;" +
                "var r=function(){" +
                    "h.style.opacity='';" +
                    "h.style.background='';" +
                    "h.style.colorScheme='';" +
                    "window.__jpReveal=null;" +
                "};" +
                "window.__jpReveal=setTimeout(r,1.5e3);" +
                "document.addEventListener('DOMContentLoaded',function(){" +
                    "if(!window.__jpLoaded&&window.__jpReveal){clearTimeout(window.__jpReveal);r();}" +
                "});" +
                "localStorage.removeItem('jpf-sw');" +
                // A switch is its own cover; the gate is not raised on the page it lands on.
                "return;" +
                "}" +
                // ── The startup cover ───────────────────────────────────────────────
                // Every early return below leaves the page exactly as jellyfin-web paints
                // it. The order mirrors profiles.js: checkRoute raises the gate only when
                // shouldAskOnStartup() and not isProfileSessionActive(), on the home route.
                "try{" +
                    "var g=function(k){try{return JSON.parse(localStorage.getItem(k)||'null')}catch(x){return null}};" +
                    "var n=function(s){return String(s||'').toLowerCase().replace(/[^a-z0-9]/g,'')};" +
                    // Emergency disable (panicKey): nothing of ours on screen at all.
                    "if(localStorage.getItem('jellyfin_profiles_disabled'))return;" +
                    // switcherModeKey and deviceGateKey. Neither means nobody has asked.
                    "var p=g('jellyfin_profiles_switcher_mode'),d=g('jellyfin_profiles_device_gate');" +
                    "if(!p&&!d)return;" +
                    // Who is signed in. No token anywhere is the sign-in screen.
                    "var c=g('jellyfin_credentials'),sv=c&&c.Servers,ms=g('jellyfin_profiles_master_state'),ids=[],on=0,i;" +
                    "if(ms&&ms.masterUserId)ids.push(n(ms.masterUserId));" +
                    "for(i=0;sv&&i<sv.length;i++)if(sv[i]&&sv[i].AccessToken){on=1;if(sv[i].UserId)ids.push(n(sv[i].UserId));}" +
                    "if(!on)return;" +
                    // getDeviceGatePref first: this device's own answer, filed under the
                    // master, wins over the household's. Then getSwitcherPrefs, which only
                    // believes a cache written for the account signed in now.
                    "var a=null,e=null,pm=p&&n(p.masterUserId);" +
                    "for(i=0;d&&!e&&i<ids.length;i++)if(d[ids[i]]&&typeof d[ids[i]]==='object')e=d[ids[i]];" +
                    "if(e&&e.synced===false&&typeof e.askOnStartup==='boolean')a=e.askOnStartup;" +
                    "else if(pm&&ids.indexOf(pm)!==-1){" +
                        "if(typeof p.askOnStartup==='boolean'&&p.location)a=p.askOnStartup;" +
                        "else if(p.mode)a=p.mode!=='native';" +
                    "}" +
                    "if(a!==true)return;" +
                    // A profile already chosen in this browser session: no gate. The
                    // localStorage mirror is not consulted, as profiles.js only believes it
                    // across a reload of its own, which is the branch above.
                    "try{if(sessionStorage.getItem('jellyfin_profiles_active_token'))return}catch(x){}" +
                    // isHomeRoute, reduced to what is knowable before the router has run.
                    "var ph=location.pathname||'',hs=location.hash||'';" +
                    "if(/login|selectserver|configuration|plugin/.test(ph+hs))return;" +
                    "var m=ph.match(/\\/web\\/(.*)$/),r=m?m[1].replace(/\\/+$/,''):'';" +
                    "if(r&&r!=='index.html'){if(r!=='home'&&r!=='home.html')return;}" +
                    "else if(hs&&hs!=='#/'&&hs.indexOf('home')===-1)return;" +
                    // The theme, so the cover is the gate's own colour from the first frame.
                    // The stylesheet that defines --jpf-gate-bg arrives with profiles.js,
                    // well after this, so each theme's background is repeated here — and
                    // tests/js/themes.js fails if one stops matching styles.css. Applied
                    // only when the cache belongs to the account signed in now, as
                    // applyCachedTheme does; unquoted in the selectors, because a
                    // double quote before a semicolon ends this constant for the tests.
                    "var tb={bonfire:'radial-gradient(circle at 50% 40%, #1e1e2e 0%, #0d0d12 100%)'," +
                        "crimson:'#141414'," +
                        "starlight:'radial-gradient(ellipse at 50% -10%, #17234d 0%, #0d1435 45%, #050817 100%)'," +
                        "harbor:'#0f171e'," +
                        "nightfall:'linear-gradient(180deg, #0b1d5a 0%, #061033 40%, #02040d 100%)'," +
                        "glass:'radial-gradient(circle at 50% 25%, #3a3a3c 0%, #1c1c1e 45%, #000 100%)'},tc='',k;" +
                    "var th=g('jellyfin_profiles_theme'),su=[];" +
                    "for(i=0;sv&&i<sv.length;i++)if(sv[i]&&sv[i].AccessToken&&sv[i].UserId)su.push(n(sv[i].UserId));" +
                    "if(th&&typeof th.theme==='string'&&tb.hasOwnProperty(th.theme)&&su.indexOf(n(th.userId))!==-1)h.setAttribute('data-jpf-theme',th.theme);" +
                    "for(k in tb)if(tb.hasOwnProperty(k))tc+='html[data-jpf-cover][data-jpf-theme='+k+']::before{background:'+tb[k]+'}';" +
                    "var s=document.createElement('style');" +
                    "s.id='jpf-cover-css';" +
                    // content: comes last on purpose. A quote followed by a semicolon is
                    // where tests/js/headscript.js decides this constant ends.
                    "s.textContent='html[data-jpf-cover]::before{position:fixed;top:0;left:0;right:0;bottom:0;z-index:99998;background:#0d0d12;background:var(--jpf-gate-bg,radial-gradient(circle at 50% 40%,#1e1e2e 0%,#0d0d12 100%));content:\"\"}'+tc;" +
                    "(document.head||h).appendChild(s);" +
                    "h.setAttribute('data-jpf-cover','');" +
                    "var u=function(){h.removeAttribute('data-jpf-cover');clearTimeout(window.__jpCoverT);window.__jpCoverT=null;};" +
                    "window.__jpCoverT=setTimeout(u,3e3);" +
                    "document.addEventListener('DOMContentLoaded',function(){if(!window.__jpLoaded)u();});" +
                "}catch(x){try{h.removeAttribute('data-jpf-cover')}catch(y){}}" +
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
            // These patterns take the newline each fragment was inserted with, so a document
            // we patched comes back byte for byte and NewlineOf gets the exact restoration it
            // exists for.
            var cleaned = HeadScriptRemovalRegex.Replace(html, string.Empty);
            cleaned = BodyScriptRemovalRegex.Replace(cleaned, string.Empty);

            if (string.Equals(cleaned, html, StringComparison.Ordinal))
            {
                // Nothing of ours was in there, so there is nothing to restore and no reason
                // to write. This is the important case, not an edge one: middleware-only mode
                // is the default and calls through here on its first served page, and its
                // whole promise is that index.html is never touched. The blank-line tidy that
                // used to run here ran unanchored over the entire document, so it collapsed
                // every blank line in a file it had never patched, reported that as a change,
                // and had the caller write the file back out and log that it had removed
                // script tags that were never there.
                result = html;
                return false;
            }

            result = cleaned;
            return true;
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

        // Removal-only variants. Inject() must keep using the pair above: it replaces a
        // stale fragment in place, and a pattern that swallowed the surrounding newline
        // would glue the replacement to whatever it sits next to.
        private static readonly Regex HeadScriptRemovalRegex = new(
            @"(\r?\n)?<script id=""jpf-eh"">[\s\S]*?</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BodyScriptRemovalRegex = new(
            @"<script[^>]*src=[""'][^""']*/plugins/profiles/profiles\.js[^""']*[""'][^>]*>\s*(</script>)?(\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
