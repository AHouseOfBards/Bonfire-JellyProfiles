/*
 * serverInfo() reads /info at most once a minute, but only a read that worked is kept.
 * A failed read (network error, non-OK) was cached as null for the full minute, and
 * withMainPin takes null as "no main PIN required", so one dropped request turned the
 * requirement off for sixty seconds (R-1208). The server still refuses the writes, so it
 * was a confusing screen, not a hole.
 *
 *     node tests/js/serverinfo.js [/path/to/profiles.js]
 */
'use strict';

const L = require('./_lib');
const src = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const fnSrc = L.extractFunction(src.replace(/serverInfo\s*:\s*function/, 'function serverInfo'), 'serverInfo');

function make(serve) {
    const calls = [];
    const fetchStub = (url) => { calls.push(url); return serve(calls.length); };
    const ApiClient = { getUrl: (p) => p };
    const self = { _serverInfo: null };
    const fn = new Function('fetch', 'ApiClient', 'Date', 'return (' + fnSrc.replace(/^function serverInfo/, 'function') + ');')(
        fetchStub, ApiClient, Date);
    return { calls, info: () => fn.call(self), self };
}

const good = () => Promise.resolve({ ok: true, json: () => Promise.resolve({ features: { requireMainPin: true } }) });
const refused = () => Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) });
const offline = () => Promise.reject(new Error('offline'));

(async () => {
    const settle = () => new Promise(r => setTimeout(r, 5));

    let e = make(good);
    await e.info(); await settle(); await e.info();
    ok('a good read is kept: the second call does not ask again', e.calls.length === 1);

    for (const [label, bad] of [['a non-OK answer', refused], ['a network failure', offline]]) {
        e = make(n => (n === 1 ? bad() : good()));
        const first = await e.info();
        await settle();
        ok(label + ' reads as null', first === null);
        const second = await e.info();
        ok(label + ' is not remembered: the next call asks again', e.calls.length === 2);
        ok('and gets the real answer', !!(second && second.features && second.features.requireMainPin));
    }

    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
