// Two startup paths that quietly fall short (sweep R-1186, R-1187):
//
//  - a page that starts panic-disabled and is later told the panic is over must still get
//    the once-per-page checks (maintenance, stale-session revert, lockout timer);
//  - the preferences request must not hang the loader for the life of the page, and a
//    failed one must not fix the guessed "ask on startup" answer with no second try.
//
// Red against 2baaa70, which has neither.
//
//   node tests/js/startupreach.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

function boot() {
    const env = D.loadPlugin(L.readProfiles());
    const timers = [];
    env.sandbox.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; };
    return { env, timers };
}

(async () => {
    // ── R-1186 ──────────────────────────────────────────────────────────────
    console.log('\n-- Panic over after a disabled start ----------------------------');
    {
        const { env, timers } = boot();
        const PP = env.PP;
        const calls = { maintenance: 0, session: 0, lockout: 0, route: 0 };
        PP.checkMaintenance = () => { calls.maintenance++; };
        PP.validateSessionState = () => { calls.session++; };
        PP.initLockoutTimer = () => { calls.lockout++; };
        PP.checkRoute = () => { calls.route++; };
        PP.applyPanicDisable = () => { PP._panicDisabled = true; };
        env.sandbox.localStorage.setItem(PP.config.panicKey, '1');
        env.setRespond(url => (/panic-state$/.test(url) ? { status: 200, body: { disabled: false } } : { status: 200, body: {} }));

        PP.checkPersistedPanic();
        await env.settle();
        timers.forEach(t => t.fn());
        ok('the route is checked again', calls.route === 1, JSON.stringify(calls));
        ok('maintenance is checked', calls.maintenance === 1, JSON.stringify(calls));
        ok('a stale session is reverted', calls.session === 1, JSON.stringify(calls));
        ok('the lockout timer starts', calls.lockout === 1, JSON.stringify(calls));

        PP._startPageChecks && PP._startPageChecks();
        ok('and none of it runs twice', calls.maintenance === 1 && calls.session === 1, JSON.stringify(calls));
    }

    // ── R-1187 ──────────────────────────────────────────────────────────────
    console.log('\n-- The preferences request --------------------------------------');
    {
        const { env, timers } = boot();
        const PP = env.PP;
        PP.checkRoute = () => {};
        let hang = true;
        let asked = 0;
        env.sandbox.fetch = (url) => {
            if (!/preferences$/.test(String(url))) return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({}) });
            asked++;
            if (hang) return new Promise(() => {});
            return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ askOnStartup: false, switcherLocation: 'menu', masterUserId: 'main' }) });
        };

        PP.loadSwitcherPrefs();
        await env.settle();
        ok('while it hangs the loader is busy', PP._switcherPrefsLoading === true);
        timers.slice().forEach(t => t.fn());
        await env.settle();
        ok('a hung request gives up', PP._switcherPrefsLoading === false);
        ok('and the page falls back to asking on startup', PP._switcherPrefs && PP._switcherPrefs.askOnStartup === true);

        hang = false;
        const before = asked;
        timers.slice().forEach(t => t.fn());
        await env.settle();
        ok('it asks again', asked > before, 'asked ' + asked);
        ok('and the real answer replaces the guess', PP._switcherPrefs && PP._switcherPrefs.askOnStartup === false
            && PP._switcherPrefs.location === 'menu', JSON.stringify(PP._switcherPrefs));
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
