// The inactivity lock (R-1192, R-1193, R-1202).
//
//  - R-1202: every pointermove re-armed the timer, about 60 clearTimeout/setTimeout
//    pairs a second on a magic remote. Activity must cost no timer work.
//  - R-1192: the timer counts timer time, so a suspended TV or a throttled tab locked
//    hours late. The lock has to be judged against the clock when the page comes back.
//  - R-1193: the lock was armed only from the one /list fetch at load; if it failed
//    the page never locked. It arms from the cached list instead.
//
//   node tests/js/lockout.js [path/to/profiles.js]

'use strict';
const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

function load() {
    const src = fs.readFileSync(L.profilesPath(), 'utf8').replace('ProfilesPlugin.init();', 'globalThis.__PP = ProfilesPlugin;');
    let clock = 1000000;
    const h = { timers: [], sets: 0, listeners: {}, fire: () => { h.timers.slice().forEach(t => { if (t.live && t.at <= clock) { t.live = false; t.fn(); } }); }, fetchOk: true, list: [], now: () => clock, advance: ms => { clock += ms; } };
    const noop = { style: {}, classList: { add() {}, remove() {}, contains: () => false }, appendChild() {}, addEventListener() {} };
    class FakeDate extends Date { static now() { return clock; } }
    const sandbox = {
        console, Date: FakeDate,
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        setTimeout: (fn, ms) => { h.sets++; h.timers.push({ fn, ms, at: clock + ms, live: true }); return h.timers.length; },
        clearTimeout: id => { if (id && h.timers[id - 1]) h.timers[id - 1].live = false; }, setInterval: () => 0, requestAnimationFrame: () => 0,
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        fetch: () => (h.fetchOk
            ? Promise.resolve({ ok: true, json: () => Promise.resolve(h.list) })
            : Promise.reject(new Error('Failed to fetch'))),
        document: {
            head: noop, body: noop, documentElement: noop, visibilityState: 'visible',
            createElement: () => Object.assign({}, noop, { querySelector: () => null, querySelectorAll: () => [] }),
            querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
            addEventListener: (ev, fn) => { (h.listeners[ev] = h.listeners[ev] || []).push(fn); },
            removeEventListener: (ev, fn) => { h.listeners[ev] = (h.listeners[ev] || []).filter(f => f !== fn); }
        },
        window: { location: { hash: '', pathname: '/web/' }, addEventListener: () => {} },
        history: { pushState() {}, replaceState() {} },
        ApiClient: { getUrl: p => p, getCurrentUserId: () => 'abc', accessToken: () => 'tok' }
    };
    sandbox.globalThis = sandbox;
    sandbox.window.localStorage = sandbox.localStorage;
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox);
    const PP = sandbox.__PP;
    h.PP = PP; h.sandbox = sandbox;
    PP.isProfileSessionActive = () => true;
    PP.readMasterState = () => ({ token: 'm' });
    PP.getAuthHeaders = () => ({});
    h.locked = 0;
    PP.lockActiveProfile = function () { h.locked++; this.stopInactivityTimer(); };
    return h;
}
const settle = () => new Promise(r => setImmediate(r));
const profile = { profileUserId: 'abc', requiresPin: true, lockoutMinutes: 5 };

(async function () {
    // R-1202: activity burst costs no timer work.
    let h = load();
    h.PP.startInactivityTimer(5);
    const before = h.sets;
    for (let i = 0; i < 100; i++) (h.listeners.pointermove || []).forEach(f => f());
    ok('100 pointer moves re-arm no timers', h.sets - before === 0, (h.sets - before) + ' timers set');

    // Activity still postpones the lock: the timer fires after 4 minutes of activity-free time
    // from a move made at minute 3, so it must not lock at minute 5 from arming.
    h = load();
    h.PP.startInactivityTimer(5);
    h.advance(3 * 60000);
    (h.listeners.pointermove || []).forEach(f => f());
    h.advance(2 * 60000);
    h.fire();
    ok('a move at minute 3 keeps the lock off at minute 5', h.locked === 0);
    h.advance(3 * 60000 + 1000);
    h.fire();
    ok('and it locks 5 minutes after the last move', h.locked >= 1, h.locked + ' locks');

    // R-1192: timers frozen by suspend; the page comes back.
    h = load();
    h.PP.startInactivityTimer(5);
    h.advance(3 * 3600000);
    h.sandbox.document.visibilityState = 'visible';
    (h.listeners.visibilitychange || []).forEach(f => f());
    ok('coming back after a suspend locks without waiting for the timer', h.locked === 1, h.locked + ' locks');
    h = load();
    h.PP.startInactivityTimer(5);
    h.advance(60000);
    (h.listeners.visibilitychange || []).forEach(f => f());
    ok('coming back inside the window does not lock', h.locked === 0);

    // R-1193: the /list fetch fails; the cached list arms it.
    h = load();
    h.fetchOk = false;
    h.PP.cachedProfiles = [profile];
    let armed = null;
    h.PP.startInactivityTimer = function (m) { armed = m; };
    h.PP.initLockoutTimer();
    await settle(); await settle();
    ok('a failed /list arms the lock from the cached list', armed === 5, 'armed with ' + armed);
    h = load();
    h.fetchOk = false;
    armed = null;
    h.PP.startInactivityTimer = function (m) { armed = m; };
    h.PP.initLockoutTimer();
    await settle(); await settle();
    ok('with no cache it retries later instead of giving up', h.timers.length > 0, h.timers.length + ' timers');

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
