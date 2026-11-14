/*
 * ni-C2a: library artwork is asked one at a time, with a backoff after failures.
 *
 *  - loadLibraryArtwork: checkRoute calls it every tick while the rules have not arrived.
 *    With no in-flight guard and no backoff a failing or hanging route was asked twice a
 *    second and the requests stacked.
 *
 * The real methods are cut out of the source and run against stubs.
 *
 *   node tests/js/artworkbackoff.js [path-to-profiles.js]
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
    // ── library artwork ────────────────────────────────────────────────
    let sent = 0, mode = 'hang';
    global.fetch = () => {
        sent++;
        if (mode === 'hang') return new Promise(() => {});
        if (mode === 'fail') return Promise.resolve({ ok: false });
        return Promise.resolve({ ok: true, json: () => Promise.resolve([]) });
    };
    global.ApiClient = { accessToken: () => 't', getCurrentUserId: () => 'u', getUrl: p => p };
    // _fetchTimed with it when the build has one; its timer is never reached by these cases.
    const REQUEST_TIMEOUT_MS = 15000; // eslint-disable-line no-unused-vars
    const timedSrc = src.indexOf('\n        _fetchTimed: function') >= 0 ? ',' + method('_fetchTimed') : '';
    function art() {
        return eval('({ _libraryArtLoaded: false, getAuthHeaders: () => ({}),'
            + 'cacheLibraryArtwork() {}, applyLibraryArtwork() {},'
            + method('loadLibraryArtwork') + timedSrc + '})');
    }

    let o = art();
    for (let i = 0; i < 10; i++) { o.loadLibraryArtwork(); now += 500; }
    await flush();
    check(sent === 1, 'a request that has not answered is not asked again (sent ' + sent + ')');

    // But one that never answers is given up on: the one-at-a-time flag was cleared only by an
    // answer, so a dropped reply stopped artwork being asked for on that page (r3-06-01).
    {
        const timers = [], realSet = global.setTimeout, realClear = global.clearTimeout;
        global.setTimeout = (fn, ms) => { timers.push(fn); return timers.length; };
        global.clearTimeout = () => {};
        const REQUEST_TIMEOUT_MS = 15000; // eslint-disable-line no-unused-vars
        sent = 0; mode = 'hang';
        const timed = src.indexOf('\n        _fetchTimed: function') >= 0 ? ',' + method('_fetchTimed') : '';
        const h = eval('({ _libraryArtLoaded: false, getAuthHeaders: () => ({}),'
            + 'cacheLibraryArtwork() {}, applyLibraryArtwork() {},'
            + method('loadLibraryArtwork') + timed + '})');
        h.loadLibraryArtwork(); await flush();
        timers.splice(0).forEach(fn => fn()); await flush();
        now += 6 * 60 * 1000;
        h.loadLibraryArtwork(); await flush();
        global.setTimeout = realSet; global.clearTimeout = realClear;
        check(sent === 2, 'a request that never answers is given up on and asked again later (sent ' + sent + ')');
    }

    sent = 0; mode = 'fail'; o = art();
    for (let i = 0; i < 20; i++) { o.loadLibraryArtwork(); await flush(); now += 500; }
    check(sent <= 2, 'a failing route is not asked on every tick (sent ' + sent + ' in 10 s)');
    const afterFirst = sent;
    now += 3 * 60 * 1000;
    o.loadLibraryArtwork(); await flush();
    check(sent === afterFirst + 1, 'but it is tried again later (sent ' + sent + ')');
    let gaps = [];
    for (let k = 0; k < 10; k++) {
        const before = sent;
        let waited = 0;
        while (sent === before && waited < 10 * 60 * 1000) { now += 1000; waited += 1000; o.loadLibraryArtwork(); await flush(); }
        gaps.push(waited);
    }
    check(gaps[gaps.length - 1] <= 300000 + 1000, 'the wait is capped at 5 minutes (' + gaps[gaps.length - 1] + ' ms)');
    check(gaps[gaps.length - 1] > gaps[0], 'and grows after repeated failures (' + gaps[0] + ' then ' + gaps[gaps.length - 1] + ')');

    sent = 0; mode = 'ok'; o = art();
    o.loadLibraryArtwork(); await flush();
    check(o._libraryArtLoaded === true && sent === 1, 'a good answer is taken');


    console.log(pass + ' passed, ' + bad + ' failed');
    process.exit(bad ? 1 : 0);
})();
