/*
 * Bonfire loader for a bundled Jellyfin web client — the Samsung Tizen app (.wgt).
 *
 * WHY THIS EXISTS. The Tizen app carries its own copy of jellyfin-web, so the server
 * never gets to add Bonfire to its index.html. Until now the only way in was to copy
 * profiles.js itself into the package — and a copy is frozen: every Bonfire fix meant
 * rebuilding and reinstalling the .wgt, and a copy older than the plugin it talks to
 * is a combination nobody tests. Issue #16 ran for weeks partly because of it.
 *
 * This file goes into the package instead, once. It waits for jellyfin-web to know
 * which server it is talking to, then loads Bonfire from that server — the same file a
 * browser gets, at the version the server is running. Updating the plugin updates the
 * television. Rebuilding the package is only ever needed again if this loader changes.
 *
 * HOW TO ADD IT. Copy this file into the package's www/ folder and add one line to
 * www/index.html, just before </body>:
 *
 *     <script src="bonfire-loader.js"></script>
 *
 * Full steps are in TROUBLESHOOTING.md under "Samsung Tizen".
 *
 * WHY ES5. Tizen 4 runs Chromium 56. One token newer than the engine and the whole file
 * fails to parse, silently. tests/js/tizenloader.js runs tests/js/jsbaseline.js on it.
 */
(function () {
    'use strict';

    var SCRIPT_PATH = '/plugins/profiles/profiles.js';
    var SCRIPT_ID = 'bonfire-profiles-script';
    var POLL_MS = 500;
    // Ten minutes. Long enough for somebody to pick a server and sign in at the pace a
    // remote allows; after that the app has been left on a screen with no server, and
    // polling for ever would only cost battery.
    var MAX_POLLS = 1200;

    var polls = 0;
    var failures = 0;

    /// The server jellyfin-web is connected to, without a trailing slash, or null while
    /// it has not chosen one. Read defensively: on the server-selection screen ApiClient
    /// is absent, and its shape has moved between jellyfin-web releases.
    function serverAddress() {
        try {
            var api = window.ApiClient;
            if (!api) return null;
            var address = typeof api.serverAddress === 'function' ? api.serverAddress() : api._serverAddress;
            if (!address) return null;
            address = String(address).replace(/\/+$/, '');
            return /^https?:\/\//i.test(address) ? address : null;
        } catch (e) {
            return null;
        }
    }

    /// A copy of profiles.js put into the package the old way, by its own script tag.
    /// Bonfire has no guard against running twice — two copies are two gates and two route
    /// polls — so if one is already there the loader stands aside and says so, rather than
    /// fight it. Removing that tag is the fix, and the docs say to.
    function bundledCopy() {
        var scripts = document.getElementsByTagName('script');
        for (var i = 0; i < scripts.length; i++) {
            var src = scripts[i].getAttribute('src') || '';
            if (scripts[i].id !== SCRIPT_ID && /(^|\/)profiles\.js(\?|#|$)/.test(src)) return src;
        }
        return null;
    }

    function load(address) {
        if (document.getElementById(SCRIPT_ID)) return true;

        var stale = bundledCopy();
        if (stale) {
            if (window.console && console.warn) {
                console.warn('Bonfire loader: this package already loads its own copy (' + stale
                    + '), which will not update. Remove that script tag from index.html; '
                    + 'the loader then serves the version on your server.');
            }
            return true;
        }

        var script = document.createElement('script');
        script.id = SCRIPT_ID;
        // No cache-buster. The server answers with the plugin version as its ETag and a
        // five-minute max-age, so a new version arrives on its own and an unchanged one
        // costs a 304.
        script.src = address + SCRIPT_PATH;
        script.onerror = function () {
            // The server could not be reached or has no Bonfire. Remove the tag so the
            // next poll can try again — a television often starts before the network.
            if (script.parentNode) script.parentNode.removeChild(script);
            failures++;
            if (failures < 5) setTimeout(tick, POLL_MS * 4);
        };
        (document.head || document.documentElement).appendChild(script);
        return true;
    }

    function tick() {
        var address = serverAddress();
        if (address) {
            load(address);
            return;
        }

        polls++;
        if (polls < MAX_POLLS) setTimeout(tick, POLL_MS);
    }

    tick();
})();
