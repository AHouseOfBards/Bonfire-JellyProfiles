/*
 * Storage that is full or blocked must not stop the profile screen, nor leave the household's
 * list behind at sign-out (r2-27-02, r2-25-02).
 *
 *  1. Every localStorage.setItem of the cached profile list is inside a try. One of them sits
 *     in the chain that draws the gate; a throw there reached the catch, which said "list
 *     failed", and the screen never appeared on that device. Each site is checked on its own.
 *  2. The usersignedout handler is run with each of its steps throwing in turn; the cached
 *     list must still be removed unless the throwing step is that removal.
 *
 *   node tests/js/storagefail.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const lib = require('./_lib');
const src = fs.readFileSync(lib.profilesPath(), 'utf8');
const KEY = 'jellyfin_profiles_cached_list';
let bad = 0, pass = 0;
function check(ok, msg) { if (!ok) { bad++; console.log('FAIL ' + msg); } else { pass++; } }

// 1. each write of the list
const lines = src.split('\n');
let sites = 0;
lines.forEach(function (l, i) {
    if (l.indexOf("localStorage.setItem('" + KEY + "'") < 0) return;
    sites++;
    check(/\btry\s*\{/.test(l), 'line ' + (i + 1) + ': the cached list is written outside a try');
});
check(sites >= 2, 'expected at least two writes of the cached list, found ' + sites);

// 2. sign-out
const at = src.indexOf("document.addEventListener('usersignedout'");
check(at >= 0, 'usersignedout handler not found');
const open = src.indexOf('{', at);
let depth = 0, end = -1;
for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) { end = i; break; }
}
const body = src.slice(open + 1, end);

const STEPS = ['clearMasterState', 'activeSessionKey', 'switcherModeKey', 'libraryArtKey', 'clearProfileSession'];
STEPS.forEach(function (failing) {
    const removed = [];
    const config = { activeSessionKey: 'a', switcherModeKey: 's', libraryArtKey: 'l', masterStorageKey: 'm' };
    const keyOf = { activeSessionKey: 'a', switcherModeKey: 's', libraryArtKey: 'l' };
    global.localStorage = {
        removeItem: k => { if (keyOf[failing] === k) throw new Error('blocked'); removed.push(k); }
    };
    const self = {
        config: config,
        clearMasterState: () => { if (failing === 'clearMasterState') throw new Error('blocked'); },
        clearProfileSession: () => { if (failing === 'clearProfileSession') throw new Error('blocked'); },
        _switcherPrefs: 'x', _panicLinkAvailable: true
    };
    try { new Function(body).call(self); } catch (e) { check(false, failing + ' failing escaped the handler: ' + e.message); }
    check(removed.indexOf(KEY) >= 0, 'with ' + failing + ' throwing, the cached list was not removed');
    check(self._switcherPrefs === null, 'with ' + failing + ' throwing, the in-memory preferences were kept');
});
console.log(bad ? bad + ' failed' : 'ok: ' + sites + ' list writes guarded; sign-out removes the list whichever step fails (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
