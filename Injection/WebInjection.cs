using System;
using System.Text.RegularExpressions;
using Campground.Shared.Injection;
using Campground.Shared.Registry;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// The two script fragments the plugin adds to Jellyfin's index.html, and the cleanup that
    /// takes them back out of a file an older release patched on disk.
    ///
    /// <para>
    /// Putting them into the document is no longer done here. Since 2.0 every Campground
    /// plugin hands its fragments to the shared coordinator (<see cref="Registration"/>), and
    /// one middleware adds all of them in one pass, so two plugins cannot each buffer
    /// index.html and each add the other's tags a second time. Until then this class had
    /// its own Inject, IsFullyInjected and a version reader; the shared IndexHtml is the
    /// generalised copy of that code, and writes the same body tag byte for byte.
    /// tests/cs/injection drives the shared code with these fragments, so a change there
    /// that would alter what Bonfire serves fails here.
    /// </para>
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
        /// Where the client script is served, without the Base URL. The coordinator writes the
        /// tag as <c>&lt;script src="{base}{ScriptPath}?v={ScriptVersion}" defer&gt;</c>
        /// before &lt;/body&gt;; the prefix has to be there because Jellyfin's
        /// BaseUrlRedirectionMiddleware answers any path without it by redirecting to
        /// <c>&lt;base&gt;/web/</c>, so a bare path comes back as the HTML page and the
        /// switcher, the gate and the theme never load.
        /// </summary>
        internal const string ScriptPath = "/plugins/profiles/profiles.js";

        /// <summary>
        /// What Bonfire asks the Campground coordinator to put in index.html: the client
        /// script, and <see cref="HeadScript"/> in &lt;head&gt;.
        /// <para>
        /// The coordinator puts a head snippet just before &lt;/head&gt;, where this class
        /// used to put it just after &lt;head&gt;. Nothing in &lt;head&gt; can paint, and
        /// jellyfin-web's own bundles there are deferred (html-webpack-plugin 5, whose
        /// defaults are inject: 'head' and scriptLoading: 'defer'; release-10.11.z), so both
        /// places run before the first frame and before any bundle. tests/js/headscript.js
        /// runs the script itself.
        /// </para>
        /// </summary>
        internal static ScriptRegistration Registration => new(
            PluginIds.Bonfire,
            ScriptPath,
            ScriptVersion,
            Order: 100,
            HeadSnippet: HeadScript,
            HeadId: HeadMarker);

        private static volatile bool _tagsWithdrawn;

        /// <summary>
        /// Keeps Bonfire's tags out of index.html while Bonfire is off (<see cref="Plugin.IsOff"/>)
        /// and puts them back when it is on. A server that installed Bonfire and never turned
        /// it on served every page the head script and the whole client script for nothing.
        /// Called before each index.html is answered, so it follows the setting and the
        /// emergency code at once, and changes the registration only when the answer changes.
        /// The settings page does not depend on the tag: it loads the script itself.
        /// </summary>
        internal static void SyncRegistration()
        {
            // The cheap read first, so an ordinary page load takes no lock.
            if (Plugin.IsOff == _tagsWithdrawn) return;

            // Two index.html requests across an on/off flip could otherwise both pass the
            // check and run Register and Unregister in either order, leaving the tags out of
            // step with the setting until it changed again.
            lock (SyncLock)
            {
                var off = Plugin.IsOff;
                if (off == _tagsWithdrawn) return;

                _tagsWithdrawn = off;
                if (off) InjectionCoordinator.Unregister(PluginIds.Bonfire);
                else InjectionCoordinator.Register(Registration);
            }
        }

        private static readonly object SyncLock = new();

        /// <summary>
        /// The Base URL (Dashboard, Networking) as Jellyfin itself normalises it: a leading
        /// slash, no trailing one, and nothing at all when unset.
        /// </summary>
        internal static string NormalizeBaseUrl(string? baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().Trim('/');
            return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
        }

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
        /// from focus or layout. Its colour is the cached theme's gate background, or with no
        /// theme known jellyfin-web's plain page colour, never a theme the household did not
        /// choose; the same rule is <c>GATE_COVER_CSS</c> in profiles.js, and
        /// tests/js/gatecover.js fails if the two differ.
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
                    // switcherModeKey. Absent means nobody has asked.
                    "var p=g('jellyfin_profiles_switcher_mode');" +
                    "if(!p)return;" +
                    // Who is signed in. No token anywhere is the sign-in screen.
                    "var c=g('jellyfin_credentials'),sv=c&&c.Servers,ms=g('jellyfin_profiles_master_state'),ids=[],on=0,i;" +
                    "if(ms&&ms.masterUserId)ids.push(n(ms.masterUserId));" +
                    "for(i=0;sv&&i<sv.length;i++)if(sv[i]&&sv[i].AccessToken){on=1;if(sv[i].UserId)ids.push(n(sv[i].UserId));}" +
                    "if(!on)return;" +
                    // getSwitcherPrefs, which only believes a cache written for the account
                    // signed in now. The per-device answer (deviceGateKey) was cut in 1.7.2.
                    "var a=null,pm=n(p.masterUserId);" +
                    "if(pm&&ids.indexOf(pm)!==-1){" +
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
                    // well after this, so profiles.js leaves each theme's gate background in
                    // its cache and this reads it back. Until 1.7.2 every built-in's
                    // background was copied out here instead, which no theme an
                    // administrator makes could ever be in.
                    // { last, users: { id: theme }, bg: { theme: background } }; see
                    // _readThemeCache in profiles.js. A background is used only when it is
                    // made of the characters a gradient needs: nothing that can end the
                    // rule it is written into, quote, or fetch (url, image).
                    "var th=g('jellyfin_profiles_theme'),tu=th&&th.users,tb={},tc='',tt=null,k;" +
                    "if(th&&th.bg&&typeof th.bg==='object')for(k in th.bg)" +
                        "if(th.bg.hasOwnProperty(k)&&/^[a-z0-9-]{1,40}$/.test(k)&&typeof th.bg[k]==='string'" +
                        "&&/^[a-z0-9#(),.%\\s-]{1,2000}$/i.test(th.bg[k])&&!/url|image/i.test(th.bg[k]))tb[k]=th.bg[k];" +
                    "for(i=0;tu&&sv&&!tt&&i<sv.length;i++)if(sv[i]&&sv[i].AccessToken&&sv[i].UserId&&typeof tu[n(sv[i].UserId)]==='string')tt=tu[n(sv[i].UserId)];" +
                    "if(tt&&tb.hasOwnProperty(tt))h.setAttribute('data-jpf-theme',tt);" +
                    // With no theme known the cover is plain, in jellyfin-web's own page
                    // colour: the one profiles.js stored at the last switch (light or dark),
                    // else #101010 from the rule below. It was Bonfire's gradient, which put
                    // the wrong theme on screen before the household's own (1.9.0.4-beta).
                    // Only a colour, as for the switch hide above.
                    "var pc=(localStorage.getItem('jpf-bg')||'').split('|')[0]||'';" +
                    "if(/^(#[0-9a-fA-F]{3,8}|rgba?\\([\\d\\s.,%]+\\))$/.test(pc))tc+='html[data-jpf-cover]::before{background:'+pc+'}';" +
                    // Quoted, because an id may start with a digit, which is not an
                    // identifier. Escaped as \" so no double quote meets a semicolon, which
                    // is where tests/js/headscript.js decides this constant ends.
                    "for(k in tb)if(tb.hasOwnProperty(k))tc+='html[data-jpf-cover][data-jpf-theme=\"'+k+'\"]::before{background:'+tb[k]+'}';" +
                    "var s=document.createElement('style');" +
                    "s.id='jpf-cover-css';" +
                    // content: comes before the closing brace on purpose. A quote followed
                    // by a semicolon is where tests/js/headscript.js decides this constant
                    // ends. The theme's own background only once something has put a theme
                    // on the page; the rules in tc come after, and win.
                    "s.textContent='html[data-jpf-cover]::before{position:fixed;top:0;left:0;right:0;bottom:0;z-index:99998;background:#101010;content:\"\"}html[data-jpf-cover][data-jpf-theme]::before{background:var(--jpf-gate-bg,#101010)}'+tc;" +
                    "(document.head||h).appendChild(s);" +
                    "h.setAttribute('data-jpf-cover','');" +
                    "var u=function(){h.removeAttribute('data-jpf-cover');clearTimeout(window.__jpCoverT);window.__jpCoverT=null;};" +
                    "window.__jpCoverT=setTimeout(u,3e3);" +
                    "document.addEventListener('DOMContentLoaded',function(){if(!window.__jpLoaded)u();});" +
                "}catch(x){try{h.removeAttribute('data-jpf-cover')}catch(y){}}" +
            "}();" +
            "</script>";

        /// <summary>The head script's id, which is how the coordinator finds and replaces it.</summary>
        internal const string HeadMarker = "jpf-eh";

        /// <summary>
        /// Takes both fragments back out, restoring the document to how Jellyfin shipped it.
        /// Used only on the file on disk, which releases up to 1.4.x (and the "file" and
        /// "both" modes after them) patched; see ProfilesBootstrapTask.TryUnpatchIndex.
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

        // Each pattern takes the newline its fragment was written with, which is what lets a
        // patched file come back byte for byte. The line ending is the document's own, never
        // Environment.NewLine: a Windows server writing CRLF into the LF file Jellyfin shipped
        // would leave it subtly different from the packaged one. (The shared IndexHtml keeps
        // the same rule for the document it serves.)
        private static readonly Regex HeadScriptRemovalRegex = new(
            @"(\r?\n)?<script id=""jpf-eh"">[\s\S]*?</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BodyScriptRemovalRegex = new(
            @"<script[^>]*src=[""'][^""']*/plugins/profiles/profiles\.js[^""']*[""'][^>]*>\s*(</script>)?(\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
