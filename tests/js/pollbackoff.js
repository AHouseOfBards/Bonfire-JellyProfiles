/*
 * What the 500 ms route poll sends to a server that is slow or failing, and what one
 * resize costs it afterwards.
 *
 * routetick.js measures the synchronous DOM work of an unchanged tick with a fetch that
 * never answers and nothing else. That cannot see the poll's network behaviour, which is
 * where four defects sat (beta 9 review):
 *
 *   R-1183  Users page: a failed admin/mappings fetch reset its guard, so the next tick
 *           asked again — a request every 500 ms for as long as the page stayed open.
 *   R-1190  Home with the gate due: every tick sent its own /list until the gate existed,
 *           and every failure forgot the saved master, even on a 500 or a network blip.
 *   R-1242  The button's prefetch cleared its pending flag on failure with no pause, and
 *           swallowed a 401 instead of treating the session as expired.
 *   R-1241  A resize marked the corner pill dirty and only the pill path cleared it, so on
 *           a theme whose geometric header search finds an anchor the walk re-ran forever.
 *
 * Ticks are counted, not timed: the clock is fake and only moves when this file moves it,
 * so "at most one request in 20 ticks" means exactly that.
 *
 *     node tests/js/pollbackoff.js /path/to/old/profiles.js
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

const MASTER_ID = '8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a';

function flush() { return new Promise(r => setImmediate(r)); }

// opts.fetch(url) returns what the page's fetch returns. opts.hash picks the route.
function build(opts) {
    const clock = { now: 1700000000000 };
    const FakeDate = class extends Date {
        constructor() { if (arguments.length) super(...arguments); else super(clock.now); }
    };
    FakeDate.now = () => clock.now;

    const requests = [];
    const listeners = {};
    const reloads = { n: 0 };

    function makeClassList(set) {
        set = set || new Set();
        return {
            add: c => set.add(c), remove: c => set.delete(c),
            contains: c => set.has(c),
            toggle: (c, f) => { const on = f === undefined ? !set.has(c) : !!f;
                                if (on) set.add(c); else set.delete(c); return on; }
        };
    }
    const byId = {};
    function adopt(el) { if (el && el.id) byId[el.id] = el; return el; }
    function makeEl() {
        const classes = new Set();
        const el = {
            style: {}, attrs: {}, children: [], dataset: {}, textContent: '', innerHTML: '',
            classList: makeClassList(classes),
            get className() { return [...classes].join(' '); },
            set className(v) {
                classes.clear();
                String(v || '').split(/\s+/).filter(Boolean).forEach(c => classes.add(c));
            },
            addEventListener() {}, removeEventListener() {},
            appendChild: adopt, insertBefore: adopt,
            remove() { if (el.id && byId[el.id] === el) delete byId[el.id]; },
            click() {}, focus() {},
            getAttribute: k => (k in el.attrs ? el.attrs[k] : null),
            setAttribute: (k, v) => { el.attrs[k] = v; },
            removeAttribute: k => { delete el.attrs[k]; },
            hasAttribute: k => k in el.attrs,
            contains: () => true,
            cloneNode: () => makeEl(),
            querySelector: () => null, querySelectorAll: () => [],
            offsetParent: null,
            getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 })
        };
        return el;
    }

    const store = Object.assign({}, opts.storage || {});
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: {
            getItem: k => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); },
            removeItem: k => { delete store[k]; }
        },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', language: 'en' },
        innerWidth: 1920, innerHeight: 1080,
        requestAnimationFrame: () => 1,
        setTimeout: () => 1, clearTimeout() {},
        setInterval: () => 1, clearInterval() {},
        fetch: function (url, init) {
            requests.push(String(url));
            return opts.fetch(String(url), init);
        },
        getComputedStyle: () => ({ backgroundColor: 'rgb(16, 16, 16)', display: 'block' }),
        history: { pushState() {}, replaceState() {} },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector: () => null, querySelectorAll: () => [],
            getElementById: id => byId[id] || null,
            elementFromPoint: () => null,
            createElement: () => makeEl(),
            head: { appendChild: adopt },
            body: makeEl(),
            documentElement: {
                style: { cssText: '', opacity: '', removeProperty() {}, setProperty() {} },
                classList: makeClassList(),
                appendChild: adopt, contains: () => true
            },
            contains: () => true
        },
        ApiClient: {
            getCurrentUserId: () => MASTER_ID,
            getUrl: u => '/' + u,
            serverAddress: () => 'https://jf.local',
            accessToken: () => 'token',
            setAuthenticationInfo() {}, clearUser() {}
        },
        JSON, Date: FakeDate, Math, Object, Array, String, Number, Boolean, RegExp, Error,
        Promise, Set, Map
    };
    sandbox.addEventListener = function (evt, fn) { (listeners[evt] = listeners[evt] || []).push(fn); };
    sandbox.removeEventListener = function () {};
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    const hash = opts.hash || '#/home.html';
    sandbox.location = { href: 'https://jf.local/web/' + hash, hash: hash, pathname: '/web/',
                         reload() { reloads.n++; } };

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    const plugin = sandbox.__PROFILES;

    plugin.loadSwitcherPrefs = () => {};
    plugin.loadLibraryArtwork = () => {};
    plugin.validateSessionState = () => {};
    plugin._panicLinkAvailable = false;
    plugin._libraryArtLoaded = true;

    function count(part) { return requests.filter(u => u.indexOf(part) !== -1).length; }
    function fire(evt) { (listeners[evt] || []).forEach(fn => fn({ type: evt })); }

    return { plugin, store, clock, requests, count, fire, reloads, sandbox };
}

const neverAnswers = () => new Promise(() => {});
const rejects = () => Promise.reject(new TypeError('Failed to fetch'));
const answers = status => () => Promise.resolve({
    ok: status >= 200 && status < 300, status: status,
    json: () => Promise.resolve({ error: 'boom' })
});

async function ticks(env, n) {
    for (let i = 0; i < n; i++) { env.plugin.checkRoute(); await flush(); }
}

function gateDue(env) {
    env.plugin._switcherPrefs = { askOnStartup: true, location: 'button' };
    env.plugin.shouldAskOnStartup = () => true;
    env.plugin.isProfileSessionActive = () => false;
}

main().catch(e => { console.error(e); process.exit(1); });
async function main() {

console.log('\n── R-1183: Users page, admin/mappings failing ──────────────────');
{
    const env = build({ hash: '#/dashboard/users', fetch: rejects });
    env.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
    await ticks(env, 20);
    const n = env.count('admin/mappings');
    ok('20 ticks on the users page with the fetch failing send at most one request (' + n + ')',
       n <= 1);
    ok('and do send one — the page still asks (' + n + ')', n >= 1);

    env.clock.now += 31000;
    await ticks(env, 1);
    ok('it asks again once 30 s have passed (' + env.count('admin/mappings') + ')',
       env.count('admin/mappings') === 2);
}
{
    const env = build({ hash: '#/dashboard/users', fetch: answers(403) });
    env.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
    await ticks(env, 20);
    ok('a 403 (a non-admin on the page) settles the same way (' + env.count('admin/mappings') + ')',
       env.count('admin/mappings') <= 1);
}

console.log('\n── R-1190: home with the gate due ──────────────────────────────');
const masterKeyOf = env => env.plugin.config.masterStorageKey;
function withMaster(fetchImpl) {
    const env = build({ fetch: fetchImpl });
    env.store[masterKeyOf(env)] = JSON.stringify({ masterUserId: MASTER_ID });
    gateDue(env);
    return env;
}
{
    const env = withMaster(neverAnswers);
    for (let i = 0; i < 100; i++) env.plugin.checkRoute();
    await flush();
    const n = env.count('plugins/profiles/list');
    ok('100 ticks with an unanswered /list send exactly one (' + n + ')', n === 1);
}
{
    const env = withMaster(rejects);
    await ticks(env, 20);
    ok('a server that is down gets one /list in 20 ticks, not 20 (' + env.count('plugins/profiles/list') + ')',
       env.count('plugins/profiles/list') === 1);
    ok('a network failure keeps the saved master',
       env.store[masterKeyOf(env)] !== undefined);
    env.clock.now += 31000;
    await ticks(env, 1);
    ok('and the gate is asked for again after 30 s (' + env.count('plugins/profiles/list') + ')',
       env.count('plugins/profiles/list') === 2);
}
{
    const env = withMaster(answers(500));
    await ticks(env, 20);
    ok('a 500 gets one /list in 20 ticks (' + env.count('plugins/profiles/list') + ')',
       env.count('plugins/profiles/list') === 1);
    ok('and keeps the saved master', env.store[masterKeyOf(env)] !== undefined);
}
{
    const env = withMaster(answers(401));
    await ticks(env, 1);
    ok('a 401 still forgets the saved master', env.store[masterKeyOf(env)] === undefined);
    ok('and goes to sign-in (' + env.reloads.n + ' reload)', env.reloads.n >= 1);
}

console.log('\n── R-1242: the button\'s prefetch ───────────────────────────────');
function prefetchEnv(fetchImpl) {
    const env = build({ fetch: fetchImpl });
    env.store[masterKeyOf(env)] = JSON.stringify({ masterUserId: MASTER_ID });
    env.plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
    env.plugin.isProfileSessionActive = () => true;
    env.plugin._headerAnchorWaitFrom = 0;
    return env;
}
{
    const env = prefetchEnv(rejects);
    // Called directly as well as through the tick: whether the tick reaches it depends on
    // the bubble's placement, which is not what is under test here.
    for (let i = 0; i < 20; i++) { env.plugin.checkRoute(); env.plugin._prefetchProfiles(); await flush(); }
    ok('a failing prefetch sends one /list in 20 ticks (' + env.count('plugins/profiles/list') + ')',
       env.count('plugins/profiles/list') === 1);
}
{
    const env = prefetchEnv(answers(401));
    env.plugin._prefetchProfiles();
    await flush();
    ok('a 401 on the prefetch is handled as an expired session ('
       + env.reloads.n + ' reload)', env.reloads.n >= 1
       && env.store[masterKeyOf(env)] === undefined);
}
{
    // The gate's request and the prefetch share the guard: one out means the other waits.
    const env = withMaster(neverAnswers);
    env.plugin.checkRoute();
    env.plugin._prefetchProfiles();
    ok('the prefetch does not send a second /list while the gate\'s is out ('
       + env.count('plugins/profiles/list') + ')', env.count('plugins/profiles/list') === 1);
}

console.log('\n── R-1247: a prefetch in flight when the cache is invalidated ───');
{
    // The list request is out; an edit invalidates the cache; the pre-edit answer then lands.
    // It must not be written back over the invalidated cache.
    let release = null;
    const slow = () => new Promise(resolve => {
        release = () => resolve({ ok: true, status: 200, json: () => Promise.resolve([{ profileUserId: 'old', profileName: 'Before the edit' }]) });
    });
    const env = prefetchEnv(slow);
    env.plugin._prefetchProfiles();
    await flush();
    ok('the prefetch is out', typeof release === 'function');
    env.plugin.invalidateProfileCache();
    release();
    await flush(); await flush();
    ok('the pre-edit list does not come back after invalidateProfileCache ('
       + ((env.plugin.cachedProfiles || []).length) + ' cached)', !(env.plugin.cachedProfiles && env.plugin.cachedProfiles.length));
}

console.log('\n── R-1241: one resize on a theme with no named header ──────────');
{
    const env = build({ fetch: neverAnswers });
    const p = env.plugin;
    p._switcherPrefs = { askOnStartup: false, location: 'button' };
    p.isProfileSessionActive = () => true;
    p._prefetchProfiles = () => {};
    let walks = 0;
    const headerBar = {
        contains: () => true,
        insertBefore: el => el
    };
    const anchor = { parentElement: headerBar };
    p._findGeometricHeaderAnchor = () => { walks++; return anchor; };
    p.bindEvents();
    p._headerAnchorWaitFrom = 0;
    p.checkRoute();
    p.checkRoute();
    const primed = walks;
    ok('the geometric search found the anchor (' + primed + ' walk' + (primed === 1 ? '' : 's') + ')',
       primed >= 1);

    walks = 0;
    for (let i = 0; i < 100; i++) p.checkRoute();
    ok('100 settled ticks do not walk (' + walks + ')', walks === 0);

    env.fire('resize');
    walks = 0;
    for (let i = 0; i < 100; i++) p.checkRoute();
    ok('one resize then 100 ticks walk once, to answer it (' + walks + ')', walks === 1);
}

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');

}
