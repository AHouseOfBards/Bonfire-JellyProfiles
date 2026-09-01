/*
 * What the 500 ms route poll actually costs, per tick.
 *
 * `bindEvents` installs `setInterval(doCheck, 500)` with the comment that viewshow,
 * popstate and hashchange already cover SPA navigation, so the poll is "only a safety net
 * for rare DOM-mutation scenarios (e.g., video OSD)". That safety net runs twice a second
 * for the entire life of the page, on every device, including during playback on the
 * weakest hardware in the house.
 *
 * This harness counts the DOM work one tick does on a URL that has not changed — the
 * overwhelmingly common case, since a person navigates a few times a minute and this runs
 * 120 times a minute. It instruments document.querySelector / querySelectorAll /
 * getElementById and the storage reads, then reports per selector so the expensive ones
 * are named rather than summed.
 *
 * Aim it at an older client to see the difference:
 *
 *     node tests/js/routetick.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SRC_PATH = L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

// Selectors whose cost is not proportional to the number of matches: the engine cannot
// use an id/class/tag index and has to walk every element in the document and test each
// one. On a Jellyfin home screen that is thousands of nodes, twice a second.
function isFullWalk(sel) {
    return /\[class\s*[*^$~|]?=/.test(sel) || /\[id\s*[*^$~|]?=/.test(sel);
}

function build(opts) {
    opts = opts || {};
    const counts = { querySelector: 0, querySelectorAll: 0, getElementById: 0,
                     storageReads: 0, jsonParse: 0, offsetParent: 0 };
    const selectors = [];
    const storageKeys = [];
    const timers = [];

    function makeClassList(set) {
        set = set || new Set();
        return {
            add: c => set.add(c), remove: c => set.delete(c),
            contains: c => set.has(c),
            toggle: (c, f) => { const on = f === undefined ? !set.has(c) : !!f;
                                if (on) set.add(c); else set.delete(c); return on; },
            _set: set
        };
    }

    function makeEl() {
        // className and classList have to share one set. The plugin assigns
        // `b.className = 'profiles-floating-fallback focusable'` and then asks
        // `classList.contains(...)` later — with them separate, the answer was always
        // false and the measurement was of a state the browser is never in.
        const classes = new Set();
        const el = {
            style: {}, attrs: {}, children: [],
            classList: makeClassList(classes),
            get className() { return [...classes].join(' '); },
            set className(v) {
                classes.clear();
                String(v || '').split(/\s+/).filter(Boolean).forEach(c => classes.add(c));
            },
            addEventListener() {}, removeEventListener() {},
            appendChild() {}, remove() {}, click() {}, focus() {},
            getAttribute: k => (k in el.attrs ? el.attrs[k] : null),
            setAttribute: (k, v) => { el.attrs[k] = v; },
            removeAttribute: k => { delete el.attrs[k]; },
            hasAttribute: k => k in el.attrs,
            contains: () => false,
            cloneNode: () => makeEl(),
            querySelector: () => null,
            querySelectorAll: () => [],
            get offsetParent() { counts.offsetParent++; return null; },
            getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0,
                                            width: 0, height: 0 }),
            dataset: {},
            textContent: '', innerHTML: ''
        };
        return el;
    }

    const store = Object.assign({}, opts.storage || {});

    // Elements the code appends, keyed by id. Without this getElementById returns null
    // forever, so anything the plugin creates once and then reuses looks absent on every
    // tick and is rebuilt — which measures a state no browser is ever in.
    const byId = {};
    function adopt(el) { if (el && el.id) byId[el.id] = el; return el; }

    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: {
            getItem: k => {
                counts.storageReads++;
                storageKeys.push(k);
                return k in store ? store[k] : null;
            },
            setItem: (k, v) => { store[k] = v; },
            removeItem: k => { delete store[k]; }
        },
        sessionStorage: {
            getItem: k => { counts.storageReads++; return null; },
            setItem() {}, removeItem() {}
        },
        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', language: 'en' },
        innerWidth: 1920, innerHeight: 1080,
        requestAnimationFrame: fn => { timers.push({ fn: fn, ms: 16 }); return timers.length; },
        setTimeout: (fn, ms) => { timers.push({ fn: fn, ms: ms }); return timers.length; },
        clearTimeout: id => { if (timers[id - 1]) timers[id - 1].cancelled = true; },
        setInterval: (fn, ms) => { timers.push({ fn: fn, ms: ms, repeating: true });
                                   return timers.length; },
        clearInterval: id => { if (timers[id - 1]) timers[id - 1].cancelled = true; },
        fetch: () => new Promise(() => {}),   // never resolves: no async work in a tick
        getComputedStyle: () => ({ backgroundColor: 'rgb(16, 16, 16)', display: 'block' }),
        history: { pushState() {}, replaceState() {} },
        JSON: {
            parse: function (s) { counts.jsonParse++; return JSON.parse(s); },
            stringify: JSON.stringify
        },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector: sel => { counts.querySelector++; selectors.push(sel); return null; },
            querySelectorAll: sel => { counts.querySelectorAll++; selectors.push(sel); return []; },
            getElementById: id => { counts.getElementById++; return byId[id] || null; },
            // Forces layout in a real browser, so it is counted like offsetParent.
            elementFromPoint: () => { counts.offsetParent++; return null; },
            createElement: () => makeEl(),
            head: { appendChild() {} },
            body: makeEl(),
            documentElement: {
                style: { cssText: '', opacity: '', removeProperty() {}, setProperty() {} },
                classList: makeClassList(),
                appendChild: adopt, contains: () => true
            },
            contains: () => true
        },
        ApiClient: {
            getCurrentUserId: () => '8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a',
            getUrl: u => '/' + u,
            serverAddress: () => 'https://jf.local',
            accessToken: () => 'token',
            setAuthenticationInfo() {}
        },
        Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.addEventListener = function () {};
    sandbox.removeEventListener = function () {};
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    sandbox.__timers = timers;
    sandbox.location = { href: opts.href || 'https://jf.local/web/#/home.html',
                         hash: opts.hash === undefined ? '#/home.html' : opts.hash,
                         pathname: opts.pathname || '/web/' };
    sandbox.window.location = sandbox.location;

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);

    const plugin = sandbox.__PROFILES;

    // Everything the tick would do asynchronously or to the network is out of scope; what
    // is being measured is the synchronous DOM work of one tick.
    plugin.loadSwitcherPrefs = () => {};
    plugin.loadLibraryArtwork = () => {};
    plugin.validateSessionState = () => {};

    function reset() {
        Object.keys(counts).forEach(k => { counts[k] = 0; });
        selectors.length = 0;
        storageKeys.length = 0;
    }

    return { plugin, counts, selectors, storageKeys, reset, sandbox };
}

main();
function main() {

console.log('\n── What one tick costs on a URL that has not changed ───────────');

const env = build({
    // Settled: preferences known, artwork loaded. This is the steady state a session
    // spends essentially all of its time in.
    storage: {}
});

const p = env.plugin;
p._switcherPrefs = { askOnStartup: false, location: 'button' };
p._panicLinkAvailable = false;
p._libraryArtLoaded = true;

// First tick primes whatever caching exists; the measurement is of the ones after it,
// which is what 119 of every 120 ticks in a minute actually are.
p.checkRoute();
env.reset();

const TICKS = 100;
for (let i = 0; i < TICKS; i++) p.checkRoute();

const perTick = {};
Object.keys(env.counts).forEach(k => { perTick[k] = env.counts[k] / TICKS; });

console.log('  per tick, averaged over ' + TICKS + ':');
Object.keys(perTick).forEach(function (k) {
    console.log('    ' + k.padEnd(18) + perTick[k].toFixed(2));
});

// Named individually. A total says the tick is expensive without saying what to delete.
const bySelector = {};
env.selectors.forEach(function (s) { bySelector[s] = (bySelector[s] || 0) + 1; });
const walks = Object.keys(bySelector).filter(isFullWalk);

console.log('\n  distinct selectors run per tick:');
Object.keys(bySelector).sort().forEach(function (s) {
    const n = bySelector[s] / TICKS;
    console.log('    ' + (isFullWalk(s) ? 'WALK  ' : '      ') + n.toFixed(1) + '  ' + s.slice(0, 88));
});

console.log('');

ok('no selector forces a full document walk on every tick'
   + (walks.length ? ' — found ' + walks.length + ': ' + walks.join(' | ').slice(0, 120) : ''),
   walks.length === 0);

// Enumerated, not counted. A budget of "at most N queries" says the tick got heavier
// without saying what was added, and lets a cheap one being removed pay for an expensive
// one being introduced. Each of these is here for a stated reason; anything else on this
// list is a new cost somebody has to justify.
const OSD = '.videoOsdBottom, .osdControls, .upNextContainer, .btnExitVideo';

const ALLOWED = {};
ALLOWED[OSD] = 'the reason the poll exists: playback can start before the URL settles, '
             + 'and the OSD appearing fires no event.';

Object.keys(bySelector).sort().forEach(function (sel) {
    ok('button mode runs "' + sel.slice(0, 60) + '" — '
       + (ALLOWED[sel] || 'NOT on the allowed list'),
       Object.prototype.hasOwnProperty.call(ALLOWED, sel));
});
Object.keys(ALLOWED).forEach(function (sel) {
    ok('still watches "' + sel.slice(0, 44) + '"',
       Object.prototype.hasOwnProperty.call(bySelector, sel));
});

// Exactly once. Twice means two callers asking the same question in one tick, which is
// part of how this reached five.
Object.keys(bySelector).forEach(function (sel) {
    ok('"' + sel.slice(0, 44) + '" runs ' + (bySelector[sel] / TICKS).toFixed(1)
       + ' times a tick, and should run 1.0', bySelector[sel] === TICKS);
});

console.log('\n── With a profile actually active, which is the normal case ────');

// The measurement above has no active profile session, and that turns out to matter a
// great deal: evaluateFloatingBubbleVisibility returns early without one, so the header
// search underneath it never runs. A household using this plugin is signed into a profile
// essentially all the time, so the numbers that count are these.
const live = build({});
live.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
live.plugin._panicLinkAvailable = false;
live.plugin._libraryArtLoaded = true;
live.plugin.isProfileSessionActive = () => true;
live.plugin.checkRoute();
live.reset();
for (let i = 0; i < 20; i++) live.plugin.checkRoute();

const liveSels = {};
live.selectors.forEach(function (s) { liveSels[s] = (liveSels[s] || 0) + 1; });
const liveWalks = Object.keys(liveSels).filter(isFullWalk);
const liveTotal = (live.counts.querySelector + live.counts.querySelectorAll) / 20;

console.log('  per tick with a profile active: ' + liveTotal.toFixed(1) + ' document queries');
Object.keys(liveSels).sort().forEach(function (s) {
    console.log('    ' + (isFullWalk(s) ? 'WALK  ' : '      ')
                + (liveSels[s] / 20).toFixed(1) + '  ' + s.slice(0, 84));
});

ok('no full-document walk while a profile is active'
   + (liveWalks.length ? ' — found: ' + liveWalks.join(' | ').slice(0, 110) : ''),
   liveWalks.length === 0);

// Every selector here is one the browser resolves twice a second for the whole session.
// A selector that cannot match anything is pure cost, and there were five of them in the
// header search alone.
// One: the OSD watch. Everything else the bubble needs — the header container, the
// geometric anchor, the corner pill's position — is settled once and re-derived only
// when the page or the viewport says it has changed. Against 1.5.6 this was ten, two of
// them full-document walks.
ok('a tick with a profile active runs one document query, the OSD watch ('
   + liveTotal.toFixed(1) + ')', liveTotal <= 1);

// The searches must still happen when their answer can have changed, or the button
// quietly stops appearing on any page that rebuilds its header.
live.plugin._headerContainer = null;
live.plugin._headerSearchedAt = 0;
live.reset();
live.plugin.checkRoute();
ok('a detached header is searched for again ('
   + (live.counts.querySelector + live.counts.querySelectorAll) + ' queries)',
   live.counts.querySelector + live.counts.querySelectorAll > 1);

console.log('\n── Menu mode still gets its rows re-asserted ───────────────────');

// The saving above comes from a guard: in button mode there is no row to place, so the
// menus are not queried at all. That guard must not also silence menu mode, where React
// rebuilding a menu is the whole reason the poll touches it — and a menu opens on a click
// that changes no URL, so nothing else would notice.
const menuEnv = build({});
menuEnv.plugin._switcherPrefs = { askOnStartup: false, location: 'menu' };
menuEnv.plugin._panicLinkAvailable = false;
menuEnv.plugin._libraryArtLoaded = true;
menuEnv.plugin.checkRoute();
menuEnv.reset();
for (let i = 0; i < 10; i++) menuEnv.plugin.checkRoute();

const menuSels = {};
menuEnv.selectors.forEach(function (s) { menuSels[s] = (menuSels[s] || 0) + 1; });

ok('menu mode still looks for the user menu every tick',
   menuSels['#app-user-menu'] === 10);
ok('and for the preferences page every tick',
   menuSels['#myPreferencesMenuPage'] === 10);
ok('and still watches for playback', menuSels[OSD] === 10);
ok('button mode does NOT look for the user menu — that is where the saving comes from',
   !bySelector['#app-user-menu']);
ok('nor for the preferences page', !bySelector['#myPreferencesMenuPage']);

// A person who switches from menu mode back to button mode must have the row taken away.
// The guard skips the menus in button mode, so it has to remember that one was placed.
const wasMenu = build({});
wasMenu.plugin._switcherPrefs = { askOnStartup: false, location: 'menu' };
wasMenu.plugin._panicLinkAvailable = false;
wasMenu.plugin._libraryArtLoaded = true;
wasMenu.plugin._userEntryPlaced = true;
wasMenu.plugin._prefsEntryPlaced = true;
wasMenu.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
wasMenu.reset();
wasMenu.plugin.checkRoute();
const cleanupSels = {};
wasMenu.selectors.forEach(function (s) { cleanupSels[s] = (cleanupSels[s] || 0) + 1; });
ok('switching back to button mode still looks, so a placed row can be removed',
   cleanupSels['#app-user-menu'] === 1 && cleanupSels['#myPreferencesMenuPage'] === 1);

ok('an unchanged tick parses no JSON (' + perTick.jsonParse.toFixed(2) + ')',
   perTick.jsonParse === 0);

ok('an unchanged tick forces no layout (' + perTick.offsetParent.toFixed(2)
   + ' offsetParent reads)', perTick.offsetParent === 0);

// Storage is a synchronous main-thread call. Two remain, both from _readSessionMirror's
// fallback for a page still carrying an older head script — see the comment there before
// removing them. Anything beyond that is new.
const keys = [...new Set(env.storageKeys)];
console.log('  storage keys read per tick: ' + (keys.join(', ') || 'none'));
ok('an unchanged tick reads only the switching flag (' + perTick.storageReads.toFixed(2)
   + ' reads of: ' + (keys.join(', ') || 'nothing') + ')',
   keys.length <= 1 && (keys.length === 0 || keys[0] === 'jpf-sw'));

console.log('\n── But it still notices the things it exists to notice ─────────');

// The poll is not decoration. It is the only thing that sees these, because none of them
// fires an event: dropping the work must not drop the watch.
const moved = build({});
moved.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
moved.plugin._panicLinkAvailable = false;
moved.plugin._libraryArtLoaded = true;
moved.plugin.checkRoute();
moved.reset();

// A navigation the events might have missed.
moved.sandbox.location.hash = '#/videoosd.html';
moved.sandbox.location.href = 'https://jf.local/web/#/videoosd.html';
moved.plugin.checkRoute();
ok('a changed URL is still fully re-evaluated ('
   + (moved.counts.querySelector + moved.counts.querySelectorAll) + ' queries)',
   (moved.counts.querySelector + moved.counts.querySelectorAll) > 0);
ok('and the route type is updated (' + moved.plugin._lastRouteType + ')',
   moved.plugin._lastRouteType === 'videoosd');

// Preferences that have not arrived yet must keep being retried, or the gate never
// appears on the first home screen after a fresh sign-in.
const unsettled = build({});
unsettled.plugin._switcherPrefs = null;
unsettled.plugin._panicLinkAvailable = null;
unsettled.plugin._libraryArtLoaded = false;
let retries = 0;
unsettled.plugin.loadSwitcherPrefs = () => { retries++; };
unsettled.plugin.loadLibraryArtwork = () => {};
unsettled.plugin.getSwitcherPrefs = () => null;
unsettled.plugin.checkRoute();
unsettled.plugin.checkRoute();
unsettled.plugin.checkRoute();
ok('preferences that have not arrived are retried on every tick, not cached away ('
   + retries + ' of 3)', retries === 3);

console.log('\n── And it slows down when there is nothing to do ───────────────');

// Playback is the one state where the plugin has nothing to do and the device has least
// to spare. The poll backs off rather than stopping, because the tick is also what
// notices playback ending on a client whose URL does not change.
const paced = build({});
paced.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
paced.plugin._panicLinkAvailable = false;
paced.plugin._libraryArtLoaded = true;
paced.plugin.bindEvents();

const intervals = () => paced.sandbox.__timers.filter(t => t.repeating && !t.cancelled);
ok('a poll is armed', intervals().length >= 1);
ok('at 500 ms on an ordinary page (' + (intervals().slice(-1)[0] || {}).ms + ')',
   (intervals().slice(-1)[0] || {}).ms === 500);

// Move to playback and let one tick run.
paced.sandbox.location.hash = '#/videoosd.html';
paced.sandbox.location.href = 'https://jf.local/web/#/videoosd.html';
const tick = intervals().slice(-1)[0];
tick.fn();

ok('the route is seen as playback (' + paced.plugin._lastRouteType + ')',
   paced.plugin._lastRouteType === 'videoosd');
ok('and the poll backs off to 2 s (' + (intervals().slice(-1)[0] || {}).ms + ')',
   (intervals().slice(-1)[0] || {}).ms === 2000);
ok('the 500 ms one is stood down rather than left running alongside it',
   intervals().length === 1);

// Back out of playback and it speeds up again — otherwise the bubble would take seconds
// to return, and a poll that only ever slows down eventually stops being a safety net.
paced.sandbox.location.hash = '#/home.html';
paced.sandbox.location.href = 'https://jf.local/web/#/home.html';
intervals().slice(-1)[0].fn();
ok('leaving playback restores 500 ms (' + (intervals().slice(-1)[0] || {}).ms + ')',
   (intervals().slice(-1)[0] || {}).ms === 500);
ok('and still exactly one poll is running', intervals().length === 1);

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');

}
