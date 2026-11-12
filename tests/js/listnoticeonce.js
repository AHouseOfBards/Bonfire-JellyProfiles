/*
 * ni-C2b: the profile-list failure note is shown once until a list loads.
 *
 *  - fetchAndRenderProfiles: the "Can't load profiles right now" note came back with every
 *    30 s retry on the home page.
 *
 * The real methods are cut out of the source and run against stubs.
 *
 *   node tests/js/listnoticeonce.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const lib = require('./_lib');
const src = fs.readFileSync(lib.profilesPath(), 'utf8');

function method(name) {
    const at = src.indexOf('\n        ' + name + ': function');
    if (at < 0) throw new Error('no method ' + name);
    const open = src.indexOf('{', src.indexOf(')', at));
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(at + 1, i + 1);
    }
    throw new Error('unterminated ' + name);
}
function between(a, b) {
    const i = src.indexOf(a), j = src.indexOf(b, i);
    if (i < 0 || j < 0) throw new Error('no block ' + a);
    return src.slice(i, j);
}

let pass = 0, bad = 0;
function check(ok, msg) { if (ok) pass++; else { bad++; console.log('FAIL ' + msg); } }
const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); };

let now = 1000000;
Date.now = () => now;

(async function () {
    // ── list notice ────────────────────────────────────────────────────
    let notices = 0, fail = true;
    const console_ = console.error; console.error = () => {};
    global.localStorage = { setItem() {} };
    const list = eval('({ _navTicket: 1, cachedProfiles: [], POLL_RETRY_MS: 30000,'
        + 'invalidateProfileCache() {}, navIsCurrent: () => true,'
        + 'normalizeProfiles: p => p, writeMasterState() {}, _cacheableProfiles: p => p,'
        + 'showProfileOverlay() {}, _showListNotice() { notices++; }, _dropCover() {},'
        + '_requestList: () => fail ? Promise.reject(new Error("down")) : Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([]) }),'
        + method('fetchAndRenderProfiles') + '})');
    for (let i = 0; i < 4; i++) { list.fetchAndRenderProfiles({}, 'm', 't', true); await flush(); }
    check(notices === 1, 'the list-failed note is shown once across repeated failures (' + notices + ')');
    fail = false; list.fetchAndRenderProfiles({}, 'm', 't', true); await flush();
    fail = true; list.fetchAndRenderProfiles({}, 'm', 't', true); await flush();
    check(notices === 2, 'and again after a success has come in between (' + notices + ')');
    console.error = console_;


    console.log(pass + ' passed, ' + bad + ' failed');
    process.exit(bad ? 1 : 0);
})();
