using System;
using System.Text.RegularExpressions;
using Campground.Shared.Injection;
using Campground.Shared.Registry;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// The two script fragments Bonfire adds to Jellyfin's index.html, and the cleanup that takes them
    /// back out of a file an older release patched on disk.
    ///
    /// <para>
    /// Since 2.0 the fragments are handed to the shared Campground coordinator
    /// (<see cref="Registration"/>), and one middleware adds all plugins' tags in one pass. Two plugins
    /// can no longer each buffer index.html and add the other's tags twice. The shared IndexHtml
    /// writes the same body tag byte for byte as this class used to. tests/cs/injection drives it with
    /// these fragments.
    /// </para>
    /// </summary>
    internal static class WebInjection
    {
        /// <summary>
        /// Cache-buster written into the script URL. Always the assembly version. It used to prefer
        /// Plugin.Instance?.Version, which can be unset when the bootstrap task runs, so the tag and the
        /// later comparison could use different sources ("1.2.8" vs "1.2.8.0"), never match, and leave the
        /// dashboard on "script update pending" forever.
        /// </summary>
        internal static string ScriptVersion =>
            typeof(WebInjection).Assembly.GetName().Version?.ToString() ?? "0";

        /// <summary>
        /// Where the client script is served, without the Base URL. The coordinator writes
        /// <c>&lt;script src="{base}{ScriptPath}?v={ScriptVersion}" defer&gt;</c> before &lt;/body&gt;. The
        /// prefix is required: Jellyfin's BaseUrlRedirectionMiddleware redirects any path without it to
        /// <c>&lt;base&gt;/web/</c>, so a bare path returns the HTML page and nothing loads.
        /// </summary>
        internal const string ScriptPath = "/plugins/profiles/profiles.js";

        /// <summary>
        /// What Bonfire asks the Campground coordinator to put in index.html: the client script, and
        /// <see cref="HeadScript"/> in &lt;head&gt;.
        /// <para>
        /// The coordinator puts the head snippet just before &lt;/head&gt;; this class used to put it just
        /// after &lt;head&gt;. Both run before the first frame and before jellyfin-web's deferred bundles
        /// (html-webpack-plugin 5, release-10.11.z). tests/js/headscript.js runs the script.
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
        /// Keeps Bonfire's tags out of index.html while Bonfire is off (<see cref="Plugin.IsOff"/>) and
        /// puts them back when it is on. Called before each index.html is answered, so it follows the
        /// setting and the emergency code at once, and changes the registration only when the answer
        /// changes. The settings page does not need the tag: it loads the script itself.
        /// </summary>
        internal static void SyncRegistration()
        {
            // Cheap read first, so an ordinary page load takes no lock.
            if (Plugin.IsOff == _tagsWithdrawn) return;

            // Without the lock, two requests across an on/off flip could run Register and Unregister
            // in either order and leave the tags out of step with the setting.
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
        /// The Base URL (Dashboard, Networking) as Jellyfin normalizes it: a leading slash, no trailing
        /// one, and empty when unset.
        /// </summary>
        internal static string NormalizeBaseUrl(string? baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().Trim('/');
            return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
        }

        /// <summary>
        /// Tiny inline script injected into &lt;head&gt;. It runs before any deferred bundle and before
        /// React renders. It reads the switching flag profiles.js sets before each
        /// window.location.reload() and hides the html element, so a profile switch does not flash
        /// the old content.
        /// <para>
        /// The hide color comes from <c>jpf-bg</c>, which profiles.js writes from the page being left. It
        /// was a hardcoded <c>#101010</c> with <c>color-scheme:dark</c>, which flashed a black screen on
        /// Jellyfin's light themes.
        /// </para>
        /// <para>
        /// Two failsafes restore visibility if the reveal never comes. A timeout (1.5 s; four seconds
        /// looked like a crash on a television), and a check on DOMContentLoaded: profiles.js is deferred
        /// and sets <c>window.__jpLoaded</c> as it evaluates, so a missing flag means it never loaded.
        /// </para>
        /// <para>
        /// The second half is the startup cover. With "Ask on startup" on, the home screen showed for
        /// up to a second before the gate, because the gate is raised from the 500 ms route poll after
        /// the profile list loads. Whether the gate is coming is known here from what profiles.js cached
        /// on the last visit, so the page is covered in the gate's own background before anything paints.
        /// profiles.js removes the cover once the gate is on top, or once it decides there is no gate.
        /// </para>
        /// <para>
        /// It fails towards the flash, never a blank screen. It is raised only when every input says yes
        /// (signed in, cached preference is this account's and says ask, no profile active this session,
        /// home route, no emergency disable). It comes down on DOMContentLoaded if profiles.js never
        /// loaded, and a three second timer removes it regardless. It is a pseudo-element under the
        /// gate's z-index, not <c>opacity:0</c> on the document, so the gate draws over it and nothing
        /// underneath is held out of focus or layout. Its color is the cached theme's gate background, or
        /// with no theme known jellyfin-web's plain page color, never a theme the household did not
        /// choose. The same rule is <c>GATE_COVER_CSS</c> in profiles.js, and tests/js/gatecover.js fails
        /// if the two differ.
        /// </para>
        /// </summary>
        internal const string HeadScript =
            "<script id=\"jpf-eh\">" +
            "!function(){" +
                "var h=document.documentElement;" +
                "if(localStorage.getItem('jpf-sw')){" +
                // Handed to profiles.js, which is deferred and cannot read the key; this script clears it
                // below, during head parsing.
                "window.__jpSwitching=1;" +
                "var v=(localStorage.getItem('jpf-bg')||'').split('|');" +
                "var b=v[0]||'';" +
                // Only a color goes into the style property. A non-color would paint nothing and leave
                // the page hidden until a failsafe fires.
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
                // Startup cover. Every early return leaves the page as jellyfin-web paints it. The order
                // mirrors profiles.js: checkRoute raises the gate only when shouldAskOnStartup() and not
                // isProfileSessionActive(), on the home route.
                "try{" +
                    "var g=function(k){try{return JSON.parse(localStorage.getItem(k)||'null')}catch(x){return null}};" +
                    "var n=function(s){return String(s||'').toLowerCase().replace(/[^a-z0-9]/g,'')};" +
                    // Emergency disable (panicKey): nothing of ours on screen.
                    "if(localStorage.getItem('jellyfin_profiles_disabled'))return;" +
                    // switcherModeKey. Absent means nobody has asked.
                    "var p=g('jellyfin_profiles_switcher_mode');" +
                    "if(!p)return;" +
                    // Who is signed in. No token anywhere means the sign-in screen.
                    "var c=g('jellyfin_credentials'),sv=c&&c.Servers,ms=g('jellyfin_profiles_master_state'),ids=[],on=0,i;" +
                    "if(ms&&ms.masterUserId)ids.push(n(ms.masterUserId));" +
                    "for(i=0;sv&&i<sv.length;i++)if(sv[i]&&sv[i].AccessToken){on=1;if(sv[i].UserId)ids.push(n(sv[i].UserId));}" +
                    "if(!on)return;" +
                    // getSwitcherPrefs, which only believes a cache written for the account signed in now.
                    // The per-device answer (deviceGateKey) was cut in 1.7.2.
                    "var a=null,pm=n(p.masterUserId);" +
                    "if(pm&&ids.indexOf(pm)!==-1){" +
                        "if(typeof p.askOnStartup==='boolean'&&p.location)a=p.askOnStartup;" +
                        "else if(p.mode)a=p.mode!=='native';" +
                    "}" +
                    "if(a!==true)return;" +
                    // A profile already chosen in this browser session: no gate. The localStorage mirror is
                    // not consulted; profiles.js only believes it across its own reload, handled above.
                    "try{if(sessionStorage.getItem('jellyfin_profiles_active_token'))return}catch(x){}" +
                    // isHomeRoute, reduced to what is knowable before the router has run.
                    "var ph=location.pathname||'',hs=location.hash||'';" +
                    "if(/login|selectserver|configuration|plugin/.test(ph+hs))return;" +
                    "var m=ph.match(/\\/web\\/(.*)$/),r=m?m[1].replace(/\\/+$/,''):'';" +
                    "if(r&&r!=='index.html'){if(r!=='home'&&r!=='home.html')return;}" +
                    "else if(hs&&hs!=='#/'&&hs.indexOf('home')===-1)return;" +
                    // The theme, so the cover is the gate's own color from the first frame. The stylesheet
                    // defining --jpf-gate-bg arrives with profiles.js, too late, so profiles.js leaves each
                    // theme's gate background in its cache and this reads it back. (Until 1.7.2 the built-in
                    // backgrounds were copied out here, which an admin-made theme could never be in.)
                    // { last, users: { id: theme }, bg: { theme: background } }; see _readThemeCache in
                    // profiles.js. A background is used only if it holds just the characters a gradient needs:
                    // nothing that can end the rule, a quote, or a fetch (url, image).
                    "var th=g('jellyfin_profiles_theme'),tu=th&&th.users,tb={},tc='',tt=null,k;" +
                    "if(th&&th.bg&&typeof th.bg==='object')for(k in th.bg)" +
                        "if(th.bg.hasOwnProperty(k)&&/^[a-z0-9-]{1,40}$/.test(k)&&typeof th.bg[k]==='string'" +
                        "&&/^[a-z0-9#(),.%\\s-]{1,2000}$/i.test(th.bg[k])&&!/url|image/i.test(th.bg[k]))tb[k]=th.bg[k];" +
                    "for(i=0;tu&&sv&&!tt&&i<sv.length;i++)if(sv[i]&&sv[i].AccessToken&&sv[i].UserId&&typeof tu[n(sv[i].UserId)]==='string')tt=tu[n(sv[i].UserId)];" +
                    "if(tt&&tb.hasOwnProperty(tt))h.setAttribute('data-jpf-theme',tt);" +
                    // With no theme known the cover is plain, in jellyfin-web's page color: the one profiles.js
                    // stored at the last switch (light or dark), else #101010 from the rule below. It was
                    // Bonfire's gradient, which showed the wrong theme before the household's own (1.9.0.4-beta).
                    // Only a color, as for the switch hide above.
                    "var pc=(localStorage.getItem('jpf-bg')||'').split('|')[0]||'';" +
                    "if(/^(#[0-9a-fA-F]{3,8}|rgba?\\([\\d\\s.,%]+\\))$/.test(pc))tc+='html[data-jpf-cover]::before{background:'+pc+'}';" +
                    // Quoted, because an id may start with a digit. Escaped as \" so no double quote meets a
                    // semicolon, where tests/js/headscript.js decides this constant ends.
                    "for(k in tb)if(tb.hasOwnProperty(k))tc+='html[data-jpf-cover][data-jpf-theme=\"'+k+'\"]::before{background:'+tb[k]+'}';" +
                    "var s=document.createElement('style');" +
                    "s.id='jpf-cover-css';" +
                    // content: comes before the closing brace on purpose: a quote followed by a semicolon is
                    // where tests/js/headscript.js decides this constant ends. The theme's own background applies
                    // once something has put a theme on the page; the rules in tc come after and win.
                    "s.textContent='html[data-jpf-cover]::before{position:fixed;top:0;left:0;right:0;bottom:0;z-index:99998;background:#101010;content:\"\"}html[data-jpf-cover][data-jpf-theme]::before{background:var(--jpf-gate-bg,#101010)}'+tc;" +
                    "(document.head||h).appendChild(s);" +
                    "h.setAttribute('data-jpf-cover','');" +
                    "var u=function(){h.removeAttribute('data-jpf-cover');clearTimeout(window.__jpCoverT);window.__jpCoverT=null;};" +
                    "window.__jpCoverT=setTimeout(u,3e3);" +
                    "document.addEventListener('DOMContentLoaded',function(){if(!window.__jpLoaded)u();});" +
                "}catch(x){try{h.removeAttribute('data-jpf-cover')}catch(y){}}" +
            "}();" +
            "</script>";

        /// <summary>The head script's id, which the coordinator uses to find and replace it.</summary>
        internal const string HeadMarker = "jpf-eh";

        /// <summary>
        /// Takes both fragments back out, restoring the document to how Jellyfin shipped it. Used only on
        /// the file on disk that releases up to 1.4.x (and the "file" and "both" modes after them) patched;
        /// see ProfilesBootstrapTask.TryUnpatchIndex.
        /// </summary>
        /// <param name="html">The document to clean.</param>
        /// <param name="result">The document afterwards. Equal to the input when nothing changed.</param>
        /// <returns>True when <paramref name="result"/> differs from the input.</returns>
        internal static bool Remove(string html, out string result)
        {
            // These patterns take the newline each fragment was inserted with, so a patched document
            // comes back byte for byte.
            var cleaned = HeadScriptRemovalRegex.Replace(html, string.Empty);
            cleaned = BodyScriptRemovalRegex.Replace(cleaned, string.Empty);

            if (string.Equals(cleaned, html, StringComparison.Ordinal))
            {
                // Nothing of ours in there, so nothing to restore and no reason to write. This is the
                // common case: middleware-only mode is the default and calls through here on its first page,
                // and promises index.html is never touched. The blank-line tidy that used to run here ran over
                // the whole document, collapsed every blank line in a file never patched, and had the caller
                // rewrite it and log removing script tags that were never there.
                result = html;
                return false;
            }

            result = cleaned;
            return true;
        }

        // Each pattern takes the newline its fragment was written with, so a patched file comes back
        // byte for byte. The line ending is the document's own, never Environment.NewLine: a Windows
        // server writing CRLF into Jellyfin's LF file would differ from the packaged one. (The shared
        // IndexHtml follows the same rule.)
        private static readonly Regex HeadScriptRemovalRegex = new(
            @"(\r?\n)?<script id=""jpf-eh"">[\s\S]*?</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BodyScriptRemovalRegex = new(
            @"<script[^>]*src=[""'][^""']*/plugins/profiles/profiles\.js[^""']*[""'][^>]*>\s*(</script>)?(\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
