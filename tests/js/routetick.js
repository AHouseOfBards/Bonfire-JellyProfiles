/*
 * What the 500 ms route poll actually costs, per tick.
 *
 * `bindEvents` installs `setInterval(doCheck, 500)` with a comment saying viewshow,
 * popstate and hashchange already cover SPA navigation, so the poll is "only a safety net
 * for rare DOM-mutation scenarios (e.g., video OSD)". It runs twice a second for the life
 * of the page on every device, during playback on the weakest hardware in the house.
 *
 * This harness counts the DOM work one tick does on a URL that has not changed, the
 * common case: a person navigates a few times a minute and this runs 120 times a minute.
 * It instruments document.querySelector / querySelectorAll / getElementById and the
 * storage reads, and reports per selector so the expensive ones are named, not summed.
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

// Selectors whose cost is not proportional to the matches: the engine cannot use an
// id/class/tag index and walks every element, thousands on a Jellyfin home screen, twice
// a second.
function isFullWalk(sel) {
    return /\[class\s*[*^$~|]?=/.test(sel) || /\[id\s*[*^$~|]?=/.test(sel);
}

function build(opts) {
    opts = opts || {};
    // R-1279: counted from the element as well as the document. A query on an element the
    // tick already holds costs the same as one on the document, as do layout reads
    // (getBoundingClientRect, getClientRects, getComputedStyle) and requestAnimationFrame
    // callbacks. None were seen, so the harness could not go red on them.
    const counts = { querySelector: 0, querySelectorAll: 0, getElementById: 0,
                     storageReads: 0, jsonParse: 0, offsetParent: 0,
                     elementQueries: 0, getElementsBy: 0, layoutReads: 0, rafCalls: 0 };
    const selectors = [];
    const idLookups = [];
    const elSelectors = [];
    // Elements the page already has, by selector. Without them every query answers null
    // and a path that runs only when a header or button exists is never measured.
    const present = opts.present || {};
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
        // className and classList must share one set. The plugin assigns
        // `b.className = 'profiles-floating-fallback focusable'` and later asks
        // `classList.contains(...)`; kept separate, the answer was always false and the
        // measurement was of a state no browser is in.
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
            appendChild: c => adopt(c), remove() {}, click() {}, focus() {},
            getAttribute: k => (k in el.attrs ? el.attrs[k] : null),
            setAttribute: (k, v) => { el.attrs[k] = v; },
            removeAttribute: k => { delete el.attrs[k]; },
            hasAttribute: k => k in el.attrs,
            contains: () => false,
            cloneNode: () => makeEl(),
            querySelector: sel => { counts.elementQueries++; elSelectors.push(sel); return null; },
            querySelectorAll: sel => { counts.elementQueries++; elSelectors.push(sel); return []; },
            getElementsByClassName: () => { counts.getElementsBy++; return []; },
            getElementsByTagName: () => { counts.getElementsBy++; return []; },
            laidOut: false,
            getClientRects: () => { counts.layoutReads++; return el.laidOut ? [{}] : []; },
            get offsetParent() { counts.offsetParent++; return null; },
            getBoundingClientRect: () => { counts.layoutReads++;
                                           return { top: 0, left: 0, right: 0, bottom: 0,
                                                    width: 0, height: 0 }; },
            dataset: {},
            textContent: '', innerHTML: ''
        };
        return el;
    }

    const store = Object.assign({}, opts.storage || {});

    // Elements the code appends, keyed by id. Without this getElementById returns null
    // forever, so anything the plugin creates once and reuses looks absent every tick and
    // is rebuilt, a state no browser is in.
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
        requestAnimationFrame: fn => { counts.rafCalls++; timers.push({ fn: fn, ms: 16 }); return timers.length; },
        setTimeout: (fn, ms) => { timers.push({ fn: fn, ms: ms }); return timers.length; },
        clearTimeout: id => { if (timers[id - 1]) timers[id - 1].cancelled = true; },
        setInterval: (fn, ms) => { timers.push({ fn: fn, ms: ms, repeating: true });
                                   return timers.length; },
        clearInterval: id => { if (timers[id - 1]) timers[id - 1].cancelled = true; },
        fetch: () => new Promise(() => {}),   // never resolves: no async work in a tick
        getComputedStyle: () => { counts.layoutReads++;
                                  return { backgroundColor: 'rgb(16, 16, 16)', display: 'block' }; },
        history: { pushState() {}, replaceState() {} },
        JSON: {
            parse: function (s) { counts.jsonParse++; return JSON.parse(s); },
            stringify: JSON.stringify
        },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector: sel => { counts.querySelector++; selectors.push(sel);
                                    return present[sel] || null; },
            querySelectorAll: sel => { counts.querySelectorAll++; selectors.push(sel);
                                       return present[sel] ? [present[sel]] : []; },
            getElementsByClassName: () => { counts.getElementsBy++; return []; },
            getElementsByTagName: () => { counts.getElementsBy++; return []; },
            getElementById: id => { counts.getElementById++; idLookups.push(id); return byId[id] || null; },
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

    // Anything async or on the network is out of scope; this measures the synchronous DOM
    // work of one tick.
    plugin.loadSwitcherPrefs = () => {};
    plugin.loadLibraryArtwork = () => {};
    plugin.validateSessionState = () => {};

    function reset() {
        Object.keys(counts).forEach(k => { counts[k] = 0; });
        selectors.length = 0;
        idLookups.length = 0;
        elSelectors.length = 0;
        storageKeys.length = 0;
    }

    return { plugin, counts, selectors, idLookups, elSelectors, storageKeys, reset, sandbox, makeEl, present, byId };
}

main();
function main() {

console.log('\n── What one tick costs on a URL that has not changed ───────────');

const env = build({
    // Settled: preferences known, artwork loaded. A session spends nearly all its time here.
    storage: {}
});

const p = env.plugin;
p._switcherPrefs = { askOnStartup: false, location: 'button' };
p._panicLinkAvailable = false;
p._libraryArtLoaded = true;

// The first tick primes whatever caching exists; the measurement is of the ones after it,
// 119 of every 120 ticks in a minute.
p.checkRoute();
// Gate off and signed in: since 1.6.4.1 the button stands here with no profile session
// (before, nothing was shown and there was no way to switch), so this fixture goes through
// the header search and starts inside its startup grace. Retire the grace and pay the
// one-off searches, as the "profile active" section below explains; the steady state is
// measured, and the grace is bounded separately further down.
p._headerAnchorWaitFrom = 0;
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

// Named individually: a total says the tick is expensive without saying what to delete.
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
// without saying what was added, and lets a removed cheap query pay for a new expensive
// one. Each of these is here for a stated reason; anything else is a new cost somebody
// has to justify.
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

// Exactly once. Twice means two callers asking the same question in one tick, part of how
// this reached five.
Object.keys(bySelector).forEach(function (sel) {
    ok('"' + sel.slice(0, 44) + '" runs ' + (bySelector[sel] / TICKS).toFixed(1)
       + ' times a tick, and should run 1.0', bySelector[sel] === TICKS);
});

// getElementById was counted all along and never asserted, so a lookup added to the tick
// went unseen. It is indexed and cheap, so it is enumerated rather than banned: each id
// here is one the tick has a stated reason to ask for, once. A new one is a new per-tick
// cost somebody has to justify.
const idsPerTick = {};
env.idLookups.forEach(function (id) { idsPerTick[id] = (idsPerTick[id] || 0) + 1; });
const ALLOWED_IDS = {};
ALLOWED_IDS['profiles-floating-bubble'] = 'the button the tick keeps in place or removes.';
console.log('\n  element ids looked up per tick: '
    + (Object.keys(idsPerTick).map(function (k) { return k + ' x' + (idsPerTick[k] / TICKS).toFixed(1); }).join(', ') || 'none'));
Object.keys(idsPerTick).sort().forEach(function (id) {
    ok('an unchanged tick looks up #' + id + ' - '
       + (ALLOWED_IDS[id] || 'NOT on the allowed list'),
       Object.prototype.hasOwnProperty.call(ALLOWED_IDS, id));
    ok('#' + id + ' is looked up at most once a tick (' + (idsPerTick[id] / TICKS).toFixed(1) + ')',
       idsPerTick[id] <= TICKS);
});

console.log('\n── With a profile actually active, which is the normal case ────');

// The measurement above has no active profile session. Until 1.6.4.1 that mattered:
// evaluateFloatingBubbleVisibility returned early without one, so the header search under
// it never ran and the numbers above were of a page with no button. With the gate off the
// button now stands regardless; with the gate on it needs the session the gate provides,
// and a household using this plugin is signed into a profile nearly all the time, so
// these numbers are the ones that count.
const live = build({});
live.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
live.plugin._panicLinkAvailable = false;
live.plugin._libraryArtLoaded = true;
live.plugin.isProfileSessionActive = () => true;
live.plugin.checkRoute();

// -- The two phases differ, and this file used to measure only one ---------------
//
// For the first few seconds of a page the header search runs every tick, because the
// modern toolbar's account button is not in the DOM yet and a named anchor is worth
// waiting for (HEADER_ANCHOR_GRACE_MS). Before that, geometry guessed immediately and the
// button visibly jumped a second later.
//
// That window is bounded. This section is about the other phase: the selectors resolved
// twice a second for the WHOLE SESSION. So the grace is retired first, rather than left to
// expire on a wall clock that 20 synchronous ticks never advance.
//
// The grace itself is measured further down, with its bound asserted there; otherwise a
// grace that never ended would read exactly like this.
live.plugin._headerAnchorWaitFrom = 0;

// Then warm up AGAIN, because retiring the grace is what lets the one-off searches run.
// During the grace the geometric branch returns without searching, so its single document
// query was still owed; measuring right after would count a startup cost as a per-tick
// one. Steady state means every one-off has been paid.
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

// Every selector here is resolved twice a second for the whole session. One that cannot
// match anything is pure cost, and there were five in the header search alone.
// One: the OSD watch. Everything else the bubble needs (header container, geometric
// anchor, corner pill position) is settled once and re-derived only when the page or
// viewport changes. Against 1.5.6 this was ten, two of them full-document walks.
ok('a tick with a profile active runs one document query, the OSD watch ('
   + liveTotal.toFixed(1) + ')', liveTotal <= 1);

console.log('\n── The same, with a header and a button actually on the page ───');

// R-1279. Every fixture above answers null to every query, so paths that run only when a
// header exists (the cached container's re-check, the bubble's placement) were never
// measured. Here the page has Jellyfin's right-hand header container, laid out, and the
// plugin puts its button in it.
function populated(location) {
    const e = build({});
    e.plugin._switcherPrefs = { askOnStartup: false, location: location };
    e.plugin._panicLinkAvailable = false;
    e.plugin._libraryArtLoaded = true;
    e.plugin.isProfileSessionActive = () => true;
    const header = e.makeEl();
    header.laidOut = true;
    // A header that remembers what was put in it. If contains() is always false the plugin
    // thinks its button drifted out and re-inserts it every tick.
    const kids = [];
    header.appendChild = c => { kids.push(c); if (c && c.id) e.byId[c.id] = c; return c; };
    header.contains = c => kids.indexOf(c) >= 0;
    e.present['.headerRight'] = header;
    e.plugin.checkRoute();
    e.plugin._headerAnchorWaitFrom = 0;
    e.plugin.checkRoute();
    e.reset();
    for (let i = 0; i < TICKS; i++) e.plugin.checkRoute();
    return e;
}
const pop = populated('button');
const popHeader = pop.present['.headerRight'];
ok('the populated fixture really found the header (otherwise it measures nothing)',
   pop.plugin._headerContainer === popHeader);
const popDoc = (pop.counts.querySelector + pop.counts.querySelectorAll) / TICKS;
console.log('  per tick, populated: ' + popDoc.toFixed(2) + ' document queries, '
            + (pop.counts.elementQueries / TICKS).toFixed(2) + ' element queries, '
            + (pop.counts.layoutReads / TICKS).toFixed(2) + ' layout reads, '
            + (pop.counts.getElementsBy / TICKS).toFixed(2) + ' getElementsBy*, '
            + (pop.counts.rafCalls / TICKS).toFixed(2) + ' requestAnimationFrame');
ok('populated: a tick runs one document query, the OSD watch (' + popDoc.toFixed(2) + ')',
   popDoc <= 1);
ok('populated: a tick queries no element (' + (pop.counts.elementQueries / TICKS).toFixed(2)
   + (pop.elSelectors.length ? ': ' + pop.elSelectors.slice(0, 3).join(' | ') : '') + ')',
   pop.counts.elementQueries === 0);
ok('populated: a tick calls no getElementsBy*', pop.counts.getElementsBy === 0);
// The cached container is re-checked for layout once every three seconds, and 100 ticks of
// a synchronous loop take far less, so a layout read per tick is a regression.
ok('populated: a tick reads no layout (' + (pop.counts.layoutReads / TICKS).toFixed(2) + ')',
   pop.counts.layoutReads === 0);
ok('populated: a tick schedules no animation frame (' + (pop.counts.rafCalls / TICKS).toFixed(2)
   + ')', pop.counts.rafCalls === 0);
const popMenu = populated('menu');
ok('populated, menu mode: a tick queries no element ('
   + (popMenu.counts.elementQueries / TICKS).toFixed(2) + ')',
   popMenu.counts.elementQueries === 0);
ok('populated, menu mode: a tick reads no layout ('
   + (popMenu.counts.layoutReads / TICKS).toFixed(2) + ')', popMenu.counts.layoutReads === 0);
ok('populated, menu mode: no animation frame, no getElementsBy*',
   popMenu.counts.rafCalls === 0 && popMenu.counts.getElementsBy === 0);

// A page that stays hidden (server dashboard, playback). Once the 160 ms fade has finished
// the button is display:none; the next tick must leave it alone, not restart the fade and
// its timer twice a second for as long as the page is open (R-1248).
const hid = populated('button');
hid.sandbox.location.hash = '#/dashboard';
hid.sandbox.location.href = 'https://jf.local/web/#/dashboard';
hid.plugin.checkRoute();
const hidBubble = hid.byId['profiles-floating-bubble'];
ok('hidden page: the button exists to be hidden', !!hidBubble);
hid.sandbox.__timers.filter(function (t) { return t.ms === 160 && !t.cancelled; })
    .forEach(function (t) { t.cancelled = true; t.fn(); });
ok('hidden page: the fade finished and the button is display:none',
   !!hidBubble && hidBubble.style.display === 'none');
const before = hid.sandbox.__timers.length;
for (let i = 0; i < 20; i++) hid.plugin.checkRoute();
ok('hidden page: 20 more ticks start no new fade or timer ('
   + (hid.sandbox.__timers.length - before) + ')', hid.sandbox.__timers.length === before);

// Element-level counters for the earlier fixtures too: an unpopulated page is the other
// way a tick can be cheap by accident.
ok('unpopulated: no element query, layout read, getElementsBy* or animation frame per tick',
   env.counts.elementQueries === 0 && env.counts.layoutReads === 0
   && env.counts.getElementsBy === 0 && env.counts.rafCalls === 0);

console.log('\n── The startup grace, which is the other phase ─────────────────');

// While waiting for a named anchor the search runs every tick. That may cost more than
// steady state, but only indexed lookups, never a walk. Strategy C ends in
// [class*="skinHeader"], [class*="topBar"], two attribute-substring selectors that can
// never use an index; running them twice a second was the first thing this change got
// wrong. They are skipped until the grace is over.
const grace = build({});
grace.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
grace.plugin._panicLinkAvailable = false;
grace.plugin._libraryArtLoaded = true;
grace.plugin.isProfileSessionActive = () => true;
grace.plugin.checkRoute();
grace.plugin._headerAnchorWaitFrom = Date.now();   // squarely inside the window
grace.plugin._headerContainer = null;
grace.reset();
for (let i = 0; i < 20; i++) grace.plugin.checkRoute();

const graceSels = {};
grace.selectors.forEach(function (s) { graceSels[s] = (graceSels[s] || 0) + 1; });
const graceWalks = Object.keys(graceSels).filter(isFullWalk);
const gracePerTick = (grace.counts.querySelector + grace.counts.querySelectorAll) / 20;
console.log('  per tick during the grace: ' + gracePerTick.toFixed(1) + ' document queries');

ok('no full-document walk during the grace either'
   + (graceWalks.length ? ' — found: ' + graceWalks.join(' | ').slice(0, 110) : ''),
   graceWalks.length === 0);

// A handful of indexed lookups is the point of the trade. Ten would mean the expensive
// strategies had crept back.
ok('and it stays in single figures (' + gracePerTick.toFixed(1) + ')', gracePerTick < 10);

// The bound makes all of the above a startup cost, not a permanent one. Without this
// assertion, deleting the expiry would leave every check in this file green.
const graceMs = grace.plugin.HEADER_ANCHOR_GRACE_MS;
ok('the grace is a finite number of milliseconds (' + graceMs + ')',
   typeof graceMs === 'number' && isFinite(graceMs) && graceMs > 0);
ok('and short enough to be a page-load cost, not a session one', graceMs <= 5000);

// It must end. Wound back past the bound, and given one tick to pay the one-off searches
// the grace deferred, the cost returns to steady state.
grace.plugin._headerAnchorWaitFrom = 0;
grace.plugin.checkRoute();
grace.reset();
for (let i = 0; i < 20; i++) grace.plugin.checkRoute();
const afterPerTick = (grace.counts.querySelector + grace.counts.querySelectorAll) / 20;
ok('once the grace expires the tick is cheap again (' + afterPerTick.toFixed(1) + ')',
   afterPerTick <= 1);

// The searches must still happen when their answer can have changed, or the button quietly
// stops appearing on any page that rebuilds its header.
live.plugin._headerContainer = null;
live.plugin._headerSearchedAt = 0;
live.reset();
live.plugin.checkRoute();
ok('a detached header is searched for again ('
   + (live.counts.querySelector + live.counts.querySelectorAll) + ' queries)',
   live.counts.querySelector + live.counts.querySelectorAll > 1);

console.log('\n── Menu mode still gets its rows re-asserted ───────────────────');

// The saving above comes from a guard: in button mode there is no row to place, so the
// menus are not queried. That guard must not also silence menu mode, where React rebuilding
// a menu is why the poll touches it, and a menu opens on a click that changes no URL.
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

// Switching from menu mode back to button mode must remove the row. The guard skips the
// menus in button mode, so it must remember that one was placed.
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
// fallback for a page still carrying an older head script; see the comment there before
// removing them. Anything beyond that is new.
const keys = [...new Set(env.storageKeys)];
console.log('  storage keys read per tick: ' + (keys.join(', ') || 'none'));
ok('an unchanged tick reads only the switching flag (' + perTick.storageReads.toFixed(2)
   + ' reads of: ' + (keys.join(', ') || 'nothing') + ')',
   keys.length <= 1 && (keys.length === 0 || keys[0] === 'jpf-sw'));

// 1.6 to 1.7.1 let a device keep its own answer to the startup question in localStorage,
// and shouldAskOnStartup, called every tick of the home route, read it. 1.7.2 cut that
// setting; a TV upgraded from 1.7.1 still carries the entry until init removes it, and
// this build skips init. So the leftover must be ignored and cost nothing per tick.
const withDevice = build({
    storage: {
        // Filed under the sandbox's signed-in user, normalized the way the client does.
        'jellyfin_profiles_device_gate': JSON.stringify({
            '8e3cdfa579a84bb9bd9a0e96b7dc974a': { synced: false, askOnStartup: true }
        })
    }
});
// The account does not ask and the leftover says it should: the settled no-gate state the
// rest of this file measures, unless the leftover is honored.
withDevice.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
withDevice.plugin._panicLinkAvailable = false;
withDevice.plugin._libraryArtLoaded = true;

withDevice.plugin.checkRoute();          // primes whatever caching exists
ok('a leftover device answer is ignored: the account\'s answer is the one in force',
   withDevice.plugin.shouldAskOnStartup() === false);

withDevice.reset();
for (let i = 0; i < TICKS; i++) withDevice.plugin.checkRoute();
const deviceKeys = [...new Set(withDevice.storageKeys)];
ok('a device with a leftover answer reads no extra storage per tick ('
   + (withDevice.counts.storageReads / TICKS).toFixed(2) + ' reads of: '
   + (deviceKeys.join(', ') || 'nothing') + ')',
   deviceKeys.length <= 1 && (deviceKeys.length === 0 || deviceKeys[0] === 'jpf-sw'));
ok('and parses no JSON per tick ('
   + (withDevice.counts.jsonParse / TICKS).toFixed(2) + ')',
   withDevice.counts.jsonParse === 0);

console.log('\n── But it still notices the things it exists to notice ─────────');

// The poll is the only thing that sees these, because none fires an event: dropping the
// work must not drop the watch.
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

// Preferences that have not arrived must keep being retried, or the gate never appears on
// the first home screen after a fresh sign-in.
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

// Playback is where the plugin has nothing to do and the device has least to spare. The
// poll backs off rather than stopping, because the tick is also what notices playback
// ending on a client whose URL does not change.
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

// Back out of playback and it speeds up again; otherwise the bubble would take seconds to
// return, and a poll that only slows down eventually stops being a safety net.
paced.sandbox.location.hash = '#/home.html';
paced.sandbox.location.href = 'https://jf.local/web/#/home.html';
intervals().slice(-1)[0].fn();
ok('leaving playback restores 500 ms (' + (intervals().slice(-1)[0] || {}).ms + ')',
   (intervals().slice(-1)[0] || {}).ms === 500);
ok('and still exactly one poll is running', intervals().length === 1);

console.log('\n── Things the tick must do, or must not repeat ─────────────────');

// The other retries on the tick. Only the preferences one was measured, so dropping the
// others stayed green: a browser that signed in after init never learned the emergency
// link or the artwork rules.
function settled(opts) {
    const e = build(opts || {});
    e.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
    e.plugin._panicLinkAvailable = false;
    e.plugin._libraryArtLoaded = true;
    return e;
}
const panicWait = settled({});
panicWait.plugin._panicLinkAvailable = null;
let prefRetries = 0;
panicWait.plugin.loadSwitcherPrefs = () => { prefRetries++; };
panicWait.plugin.checkRoute(); panicWait.plugin.checkRoute(); panicWait.plugin.checkRoute();
ok('an emergency link that has not been learned is asked for on every tick, with the prefs known ('
   + prefRetries + ' of 3)', prefRetries === 3);

const artWait = settled({});
artWait.plugin._libraryArtLoaded = false;
let artRetries = 0;
artWait.plugin.loadLibraryArtwork = () => { artRetries++; };
artWait.plugin.checkRoute(); artWait.plugin.checkRoute(); artWait.plugin.checkRoute();
ok('library artwork that has not arrived is asked for on every tick (' + artRetries + ' of 3)',
   artRetries === 3);
const artDone = settled({});
let artDoneCalls = 0;
artDone.plugin.loadLibraryArtwork = () => { artDoneCalls++; };
artDone.plugin.checkRoute(); artDone.plugin.checkRoute();
ok('and once it has arrived it is not asked for again (' + artDoneCalls + ')', artDoneCalls === 0);

// Playback seen from the DOM alone: the URL says nothing, the OSD appears. The poll exists
// for this, and only the URL route to 'videoosd' was exercised, so the OSD query could be
// answered and ignored.
const osdOnly = settled({ present: { '.videoOsdBottom, .osdControls, .upNextContainer, .btnExitVideo': { tagName: 'DIV' } } });
osdOnly.plugin.checkRoute();
ok('an OSD in the DOM on an ordinary URL is classified as playback (' + osdOnly.plugin._lastRouteType + ')',
   osdOnly.plugin._lastRouteType === 'videoosd');
const noOsd = settled({});
noOsd.plugin.checkRoute();
ok('and without one the same URL is home (' + noOsd.plugin._lastRouteType + ')',
   noOsd.plugin._lastRouteType === 'home');

// The Dashboard -> Users list is the one route that asks the server something from the
// tick, guarded by the address changing. The guard is a string compare; losing it would
// send a request twice a second for as long as an administrator sat on that page.
const usersPage = settled({ hash: '#/dashboard/users', href: 'https://jf.local/web/#/dashboard/users' });
let usersFetches = 0;
usersPage.sandbox.fetch = () => { usersFetches++; return new Promise(() => {}); };
for (let i = 0; i < 20; i++) usersPage.plugin.checkRoute();
ok('twenty ticks on Dashboard -> Users ask the server once, not twenty times (' + usersFetches + ')',
   usersFetches === 1);
const awayPage = settled({ hash: '#/home.html' });
let awayFetches = 0;
awayPage.sandbox.fetch = () => { awayFetches++; return new Promise(() => {}); };
for (let i = 0; i < 20; i++) awayPage.plugin.checkRoute();
ok('and on the home screen the tick asks the server nothing (' + awayFetches + ')', awayFetches === 0);

// The tick takes the startup cover down only when one is up. Called unconditionally it
// would write to <html> and cancel a timer every tick for the life of the page.
const noCover = settled({});
let drops = 0;
noCover.plugin._dropCover = () => { drops++; };
noCover.plugin._coverUp = false;
for (let i = 0; i < 20; i++) noCover.plugin.checkRoute();
ok('with no startup cover up, ticks never take it down (' + drops + ' of 20)', drops === 0);
const cover = settled({});
let coverDrops = 0;
cover.plugin._dropCover = () => { coverDrops++; };
cover.plugin._coverUp = true;
cover.plugin.checkRoute();
ok('and with one up on a settled page, the tick does take it down (' + coverDrops + ')', coverDrops === 1);

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');

}
