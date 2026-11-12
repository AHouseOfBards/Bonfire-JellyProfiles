/*
 * The cached profile list is removed when the session ends (R-1310).
 *
 * jellyfin_profiles_cached_list holds names, avatars, library and tag lists and linked
 * households' tiles. It was written on every picker load and never removed, while sign-out
 * cleared every other Bonfire key, so the next person at a shared browser could read it.
 *
 * Two places end a session: the usersignedout event and handleSessionExpired. Each one's
 * body is cut out by brace matching and must remove the key.
 *
 *   node tests/js/cachedlist.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const lib = require('./_lib');
const src = fs.readFileSync(lib.profilesPath(), 'utf8');
const KEY = 'jellyfin_profiles_cached_list';

function bodyAfter(marker) {
    const at = src.indexOf(marker);
    if (at < 0) return null;
    const open = src.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(open, i + 1);
    }
    return null;
}

let bad = 0, pass = 0;
[
    ["document.addEventListener('usersignedout'", 'sign-out'],
    ['handleSessionExpired: function', 'session expiry']
].forEach(function (c) {
    const body = bodyAfter(c[0]);
    if (body === null) { bad++; console.log('FAIL ' + c[1] + ': handler not found'); return; }
    if (body.indexOf("removeItem('" + KEY + "')") < 0) { bad++; console.log('FAIL ' + c[1] + ' does not remove the cached list'); } else { pass++; }
});
console.log(bad ? bad + ' failed' : 'ok: sign-out and session expiry both remove the cached list (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
