/*
 * The home screen must not be on view in the two moments around the gate.
 *
 * Reported as: "when loaded on any app that loads our switcher, there's a brief flash of
 * the dashboard before our switcher pops up on screen. The same thing happens when
 * selecting a profile". Under half a second, on web, Android and the televisions.
 *
 *  1. Cold start with "Ask on startup" on. Nothing covered the page: jellyfin-web drew
 *     home as soon as it had a user, while the gate waited for the first route check, the
 *     profile list, and then faded in from opacity 0 over the top of it.
 *  2. Picking a profile from the switcher anywhere but home. executeProfileSwitch marks
 *     the new profile active before it reloads, and checkRoute's "overlay stuck off home"
 *     cleanup matched exactly that, so the next 500 ms tick took the opaque gate down and
 *     showed the old profile's page until the navigation landed.
 *
 * This runs the real thing on a clock it controls: the inline head script reassembled
 * from WebInjection.cs, then profiles.js with init() swapped for an export, against a
 * page where jellyfin-web paints home at a chosen moment and the server answers after a
 * chosen delay. "Home is visible" is asked every 10 ms of that timeline, and means: home
 * has been painted, and none of these is on top of it —
 *
 *   - the startup cover (html[data-jpf-cover] with its rule in the document),
 *   - the gate, opaque: in the document, its inline opacity cleared, and its 220 ms fade
 *     finished,
 *   - the head script's switch hide (opacity 0 on <html>).
 *
 * Then the other half, which matters more: every way the cover can be left up by
 * mistake has to bring it down, because a screen that stays blank is far worse than a
 * flash.
 *
 *     node tests/js/gatecover.js [profiles.js] [WebInjection.cs]
 *
 * Both arguments point at another checkout, which is how this was shown to fail against
 * the build carrying the bug:
 *
 *     git show HEAD~1:Web/profiles.js > old.js; git show HEAD~1:Injection/WebInjection.cs > old.cs
 *     node tests/js/gatecover.js old.js old.cs
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const JS_PATH = process.argv[2] || L.profilesPath();
const CS_PATH = process.argv[3] || path.join(L.ROOT, 'Injection', 'WebInjection.cs');

let src = L.readClientBundle(fs.readFileSync(JS_PATH, 'utf8'));
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');
const script = new vm.Script(src, { filename: 'profiles.js' });

// ── the head script, reassembled from the C# constant (as tests/js/headscript.js does) ─
function headScriptBody() {
    const cs = fs.readFileSync(CS_PATH, 'utf8');
    const at = cs.indexOf('internal const string HeadScript =');
    if (at === -1) throw new Error('HeadScript not found in ' + CS_PATH);
    const end = cs.indexOf('";', at);
    const region = cs.slice(at, end + 2).split('\n')
        .map(line => { const c = line.indexOf('//'); return c === -1 ? line : line.slice(0, c); })
        .join('\n');
    const tag = (region.match(/"(?:[^"\\]|\\.)*"/g) || [])
        .map(p => p.slice(1, -1).replace(/\\(["'\\])/g, '$1')).join('');
    return tag.replace(/^<script id="jpf-eh">/, '').replace(/<\/script>$/, '');
}
const HEAD = new vm.Script(headScriptBody(), { filename: 'headscript' });

// ── fixtures ────────────────────────────────────────────────────────────────
const MASTER = '8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a';
const KID = '1f0c3a77-1111-4222-8333-944455556666';
const SERVER = 'a1b2c3d4e5f6478899aabbccddeeff00';

function baseStorage(over) {
    const s = {
        jellyfin_credentials: JSON.stringify({ Servers: [
            { Id: SERVER, AccessToken: 'master-token', UserId: MASTER, ManualAddress: 'http://jf.local' }
        ] }),
        jellyfin_profiles_master_state: JSON.stringify({ masterUserId: MASTER }),
        jellyfin_profiles_switcher_mode: JSON.stringify({ askOnStartup: true, location: 'button', masterUserId: MASTER })
    };
    Object.keys(over || {}).forEach(k => {
        if (over[k] === null) delete s[k]; else s[k] = over[k];
    });
    return s;
}

const PROFILE_LIST = [
    { ProfileUserId: MASTER, ProfileName: 'Parent', IsMaster: true, RequiresPin: false, AvatarColor: '#00A4DC' },
    { ProfileUserId: KID, ProfileName: 'Kid', IsMaster: false, RequiresPin: false, AvatarColor: '#FF0000' }
];

// ── a page on a clock ───────────────────────────────────────────────────────
function makePage(opts) {
    opts = opts || {};
    let now = 0;
    const timers = [];
    let seq = 0;
    const errors = [];
    const navigations = [];

    function schedule(fn, ms, repeat) {
        // At least 1 ms, as a browser clamps it: a zero-delay timer that re-arms itself
        // would otherwise never let this clock move.
        const t = { id: ++seq, fn, at: now + Math.max(1, ms || 0), every: repeat ? Math.max(1, ms || 0) : 0 };
        timers.push(t);
        return t.id;
    }
    function cancel(id) {
        const i = timers.findIndex(t => t.id === id);
        if (i !== -1) timers.splice(i, 1);
    }

    // Elements: just enough of the DOM for the code paths that run here, with ids that
    // resolve, attributes that stick, and removal that actually removes.
    const byId = Object.create(null);
    function register(el) { if (el && el.id) byId[el.id] = el; }
    function makeEl(tag) {
        const attrs = Object.create(null);
        const classes = new Set();
        const el = {
            tagName: String(tag || 'div').toUpperCase(),
            children: [], parentNode: null,
            textContent: '', innerHTML: '', value: '',
            dataset: {},
            style: {
                cssText: '',
                setProperty(k, v) { this[k] = v; },
                removeProperty(k) { delete this[k]; },
                getPropertyValue(k) { return this[k] || ''; }
            },
            classList: {
                add: (...c) => c.forEach(x => classes.add(x)),
                remove: (...c) => c.forEach(x => classes.delete(x)),
                contains: c => classes.has(c),
                toggle: (c, f) => { const on = f === undefined ? !classes.has(c) : !!f;
                                    if (on) classes.add(c); else classes.delete(c); return on; }
            },
            get className() { return Array.from(classes).join(' '); },
            set className(v) { classes.clear(); String(v || '').split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
            setAttribute(k, v) { attrs[k] = String(v); if (k === 'id') { el.id = String(v); register(el); } },
            getAttribute(k) { return k in attrs ? attrs[k] : null; },
            removeAttribute(k) { delete attrs[k]; },
            hasAttribute(k) { return k in attrs; },
            addEventListener() {}, removeEventListener() {}, dispatchEvent() {},
            appendChild(c) { el.children.push(c); c.parentNode = el; register(c); return c; },
            insertBefore(c) { return el.appendChild(c); },
            removeChild(c) { const i = el.children.indexOf(c); if (i !== -1) el.children.splice(i, 1); },
            remove() {
                if (el.id && byId[el.id] === el) delete byId[el.id];
                if (el.parentNode) el.parentNode.removeChild(el);
                el.parentNode = null;
            },
            focus() {}, blur() {}, click() {},
            contains: () => false, closest: () => null,
            querySelector: () => null, querySelectorAll: () => [],
            getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 }),
            get offsetParent() { return null; },
            scrollHeight: 0, clientHeight: 0, offsetHeight: 0, offsetWidth: 0
        };
        Object.defineProperty(el, 'id', {
            get() { return attrs.id || ''; },
            set(v) { attrs.id = String(v); register(el); },
            enumerable: true
        });
        return el;
    }

    const docListeners = {};
    const html = makeEl('html');
    const head = makeEl('head');
    const body = makeEl('body');

    const store = Object.assign({}, opts.storage || {});
    const session = Object.assign({}, opts.session || {});
    const pathname = opts.pathname || '/web/';
    const location = {
        pathname, search: '', hash: opts.hash || '',
        get href() { return 'http://jf.local' + location.pathname + location.search + location.hash; },
        set href(v) { navigations.push({ kind: 'href', url: v }); },
        reload() { navigations.push({ kind: 'reload' }); },
        replace(url) { navigations.push({ kind: 'replace', url }); }
    };

    // The server. Each route answers after the delay given for it; a missing delay
    // means the request never comes back.
    const server = Object.assign({ list: 300, preferences: 250, switch: 100 }, opts.server || {});
    function respond(body, status) {
        return {
            ok: status === undefined || (status >= 200 && status < 300),
            status: status || 200,
            json: () => Promise.resolve(body),
            text: () => Promise.resolve(typeof body === 'string' ? body : JSON.stringify(body))
        };
    }
    function route(url) {
        if (/plugins\/profiles\/list/.test(url)) {
            return ['list', opts.listStatus ? respond('boom', opts.listStatus) : respond(PROFILE_LIST)];
        }
        if (/plugins\/profiles\/preferences/.test(url)) {
            return ['preferences', respond(Object.assign(
                { askOnStartup: true, switcherLocation: 'button', masterUserId: MASTER, panicAvailable: false },
                opts.serverPrefs || {}))];
        }
        if (/plugins\/profiles\/switch/.test(url)) {
            return ['switch', respond({ activeProfileToken: 'kid-token', jellyfinUserId: KID })];
        }
        return [null, respond({})];
    }
    function fetchImpl(url) {
        const r = route(String(url));
        const delay = r[0] ? server[r[0]] : 50;
        if (delay === undefined || delay === null) return new Promise(() => {});
        return new Promise(resolve => schedule(() => resolve(r[1]), delay));
    }

    const sandbox = {
        console: {
            log() {}, warn() {}, info() {}, debug() {},
            error(...a) { errors.push(a.map(String).join(' ')); }
        },
        localStorage: {
            getItem: k => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); },
            removeItem: k => { delete store[k]; }
        },
        sessionStorage: {
            getItem: k => (k in session ? session[k] : null),
            setItem: (k, v) => { session[k] = String(v); },
            removeItem: k => { delete session[k]; }
        },
        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0) Chrome/120', language: 'en', languages: ['en'] },
        innerWidth: 1920, innerHeight: 1080,
        setTimeout: (fn, ms) => schedule(fn, ms, false),
        clearTimeout: id => cancel(id),
        setInterval: (fn, ms) => schedule(fn, ms, true),
        clearInterval: id => cancel(id),
        requestAnimationFrame: fn => schedule(fn, 16, false),
        cancelAnimationFrame: id => cancel(id),
        fetch: fetchImpl,
        getComputedStyle: () => ({ backgroundColor: 'rgb(16, 16, 16)', display: 'block', getPropertyValue: () => '' }),
        matchMedia: () => ({ matches: false, addListener() {}, addEventListener() {} }),
        history: { pushState() {}, replaceState() {} },
        MutationObserver: function () { return { observe() {}, disconnect() {} }; },
        Image: function () { return makeEl('img'); },
        KeyboardEvent: function () { return {}; },
        document: {
            documentElement: html, head, body,
            createElement: makeEl,
            getElementById: id => byId[id] || null,
            querySelector: () => null,
            querySelectorAll: () => [],
            addEventListener: (type, fn) => { (docListeners[type] = docListeners[type] || []).push(fn); },
            removeEventListener() {}, dispatchEvent() {},
            contains: () => true,
            activeElement: null
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map,
        parseInt, parseFloat, isNaN, encodeURIComponent, decodeURIComponent
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    sandbox.location = location;
    sandbox.addEventListener = () => {};
    sandbox.removeEventListener = () => {};
    vm.createContext(sandbox);

    const page = {
        sandbox, store, session, html, head, body, byId, navigations, errors, location,
        homeAt: null,
        get now() { return now; },
        plugin: null,
        overlayOpaqueSince: null,

        runHead() { if (opts.head !== false) HEAD.runInContext(sandbox); },
        loadScript() {
            if (opts.scriptLoads === false) return;
            script.runInContext(sandbox);
            page.plugin = sandbox.__PROFILES;
        },
        fire(type) { (docListeners[type] || []).slice().forEach(fn => { try { fn({}); } catch (e) { errors.push(String(e)); } }); },
        defineApiClient(user) {
            const state = { token: user === null ? '' : 'master-token', user: user === null ? '' : (user || MASTER) };
            sandbox.ApiClient = {
                getCurrentUserId: () => state.user,
                accessToken: () => state.token,
                getUrl: u => 'http://jf.local/' + String(u).replace(/^\//, ''),
                serverAddress: () => 'http://jf.local',
                serverId: () => SERVER,
                setAuthenticationInfo(t, u) { state.token = t; state.user = u; },
                getPluginConfiguration: () => Promise.resolve({})
            };
        },
        paintHome() {
            page.homeAt = now;
            page.fire('viewshow');
        },

        covered() {
            // The gate's fade is tracked first and on every sample, whatever else is on
            // top: the fade runs whether or not anything can see it.
            const ov = byId['profiles-gate-overlay'];
            let gateOpaque = false;
            if (ov && ov.style.opacity !== '0') {
                if (page.overlayOpaqueSince === null) page.overlayOpaqueSince = now;
                gateOpaque = now - page.overlayOpaqueSince >= 220;
            } else {
                page.overlayOpaqueSince = null;
            }
            if (html.style.opacity === '0') return 'switch-hide';
            const rule = byId['jpf-cover-css'];
            if (html.getAttribute('data-jpf-cover') !== null && rule && /html\[data-jpf-cover\]::before/.test(rule.textContent)) {
                return 'cover';
            }
            return gateOpaque ? 'gate' : null;
        },
        homeVisible() { return page.homeAt !== null && !page.covered(); },

        /// Runs the clock forward, firing each timer at its moment and draining promise
        /// callbacks after each, and sampling every 10 ms in between.
        async advance(ms, onSample) {
            const until = now + ms;
            const drain = () => new Promise(r => setImmediate(r));
            while (now < until) {
                timers.sort((a, b) => a.at - b.at || a.id - b.id);
                const next = timers.length ? timers[0].at : Infinity;
                const sampleAt = (Math.floor(now / 10) + 1) * 10;
                const step = Math.min(next, sampleAt, until);
                now = Math.max(now, step);
                for (;;) {
                    timers.sort((a, b) => a.at - b.at || a.id - b.id);
                    const t = timers[0];
                    if (!t || t.at > now) break;
                    if (t.every) t.at += t.every; else timers.shift();
                    try { t.fn(); } catch (e) { errors.push('timer threw: ' + (e && e.stack || e)); }
                    await drain();
                }
                await drain();
                if (onSample && now % 10 === 0) onSample(now);
            }
        }
    };
    return page;
}

/// The standard cold start: head script while <head> parses, profiles.js at defer time,
/// DOMContentLoaded, then jellyfin-web's own boot — ApiClient, then home.
async function coldStart(opts) {
    opts = opts || {};
    const page = makePage(opts);
    const seen = { visibleAt: [], coverMaxMs: 0, coverFrom: null, everCovered: false };
    const sample = t => {
        if (page.homeVisible()) seen.visibleAt.push(t);
        const c = page.html.getAttribute('data-jpf-cover') !== null;
        if (c) {
            seen.everCovered = true;
            if (seen.coverFrom === null) seen.coverFrom = t;
            seen.coverMaxMs = Math.max(seen.coverMaxMs, t - seen.coverFrom);
        } else {
            seen.coverFrom = null;
        }
    };

    page.runHead();
    sample(0);
    page.loadScript();
    if (process.env.GATECOVER_DEBUG && page.plugin && page.plugin._dropCover) {
        const orig = page.plugin._dropCover;
        page.plugin._dropCover = function () {
            console.log('    drop at ' + page.now + ' ms\n' + new Error().stack.split('\n').slice(2, 6).join('\n'));
            return orig.apply(this, arguments);
        };
    }
    page.fire('DOMContentLoaded');
    sample(0);

    const apiAt = opts.apiAt === undefined ? 60 : opts.apiAt;
    const homeAt = opts.homeAt === undefined ? 400 : opts.homeAt;
    const events = [
        { at: apiAt, run: () => { page.defineApiClient(opts.user); if (page.plugin) page.plugin.init(); } },
        { at: homeAt, run: () => page.paintHome() }
    ].concat(opts.events || [])
        // An event after the run ends is one that never happens.
        .filter(e => e.at <= (opts.runFor || 2500))
        .sort((a, b) => a.at - b.at);

    for (const e of events) {
        if (e.at > page.now) await page.advance(e.at - page.now, sample);
        e.run(page);
        sample(page.now);
    }
    await page.advance((opts.runFor || 2500) - page.now, sample);
    return { page, seen };
}

function span(list) {
    if (!list.length) return 'never';
    return list.length + ' samples, ' + list[0] + '–' + list[list.length - 1] + ' ms';
}

async function main() {

console.log('\n── 1. Cold start with "Ask on startup" on ──────────────────────');
{
    const { page, seen } = await coldStart({ storage: baseStorage() });
    const gate = page.byId['profiles-gate-overlay'];
    ok('the gate is drawn (otherwise everything below is about nothing)', !!gate);
    ok('home is never on screen between the first paint and the gate', seen.visibleAt.length === 0,
        'home was visible at ' + span(seen.visibleAt));
    ok('the cover is down once the gate is up',
        page.html.getAttribute('data-jpf-cover') === null, 'still up at ' + page.now + ' ms');
    ok('and no startup step failed on the way', page.errors.length === 0, page.errors.join(' | '));
}
{
    // A television: home lands late, the list later still.
    const { page, seen } = await coldStart({ storage: baseStorage(), apiAt: 900, homeAt: 1500,
                                             server: { list: 900 }, runFor: 4000 });
    ok('on a slow television too', !!page.byId['profiles-gate-overlay'] && seen.visibleAt.length === 0,
        'gate=' + !!page.byId['profiles-gate-overlay'] + ', home visible at ' + span(seen.visibleAt));
}
{
    // Home painted before profiles.js has even started. Seen on 10.11 web when the home
    // chunk is cached and profiles.js is not.
    const { page, seen } = await coldStart({ storage: baseStorage(), apiAt: 500, homeAt: 200 });
    ok('when home paints before init', !!page.byId['profiles-gate-overlay'] && seen.visibleAt.length === 0,
        'home visible at ' + span(seen.visibleAt));
}
{
    // The Tizen package: no head script in its index.html, profiles.js pulled in by the
    // loader after the app has already drawn home.
    const { page, seen } = await coldStart({ storage: baseStorage(), head: false, apiAt: 700, homeAt: 300 });
    const after = seen.visibleAt.filter(t => t > 700);
    ok('without a head script, home is covered from init until the gate',
        !!page.byId['profiles-gate-overlay'] && after.length === 0, 'home visible after init at ' + span(after));
}
{
    // A device keeping its own answer, against the household's.
    const dev = ask => JSON.stringify({ [MASTER.replace(/-/g, '')]: { synced: false, askOnStartup: ask } });
    const r1 = await coldStart({ storage: baseStorage({
        jellyfin_profiles_switcher_mode: JSON.stringify({ askOnStartup: false, location: 'button', masterUserId: MASTER }),
        jellyfin_profiles_device_gate: dev(true) }), serverPrefs: { askOnStartup: false } });
    ok('a device that asks, in a household that does not, is covered until its gate',
        !!r1.page.byId['profiles-gate-overlay'] && r1.seen.visibleAt.length === 0,
        'home visible at ' + span(r1.seen.visibleAt));
}

console.log('\n── 2. Picking a profile from the switcher off home ─────────────');
for (const where of [{ label: 'a details page', pathname: '/web/details', hash: '' },
                     { label: 'the user menu', pathname: '/web/mypreferencesmenu', hash: '' }]) {
    const page = makePage({ storage: baseStorage({
        jellyfin_profiles_switcher_mode: JSON.stringify({ askOnStartup: false, location: 'button', masterUserId: MASTER }) }),
        serverPrefs: { askOnStartup: false }, pathname: where.pathname, hash: where.hash });
    page.runHead();
    page.loadScript();
    page.fire('DOMContentLoaded');
    page.defineApiClient();
    page.plugin.init();
    page.paintHome();
    await page.advance(600);

    page.plugin.handleBubbleClick();
    await page.advance(700);
    const before = page.byId['profiles-gate-overlay'];
    ok('the switcher is open on ' + where.label, !!before);

    page.plugin.executeProfileSwitch(KID, null);
    // The navigation is in flight and has not landed. The ladder's first escalation is
    // at 1200 ms; this watches the whole stretch before it.
    const gone = [];
    let reloaded = false;
    await page.advance(1150, t => {
        if (page.navigations.length) reloaded = true;
        if (reloaded && !page.byId['profiles-gate-overlay']) gone.push(t);
    });
    ok('the switch reloads', reloaded, 'navigations: ' + JSON.stringify(page.navigations));
    ok('and the gate stays over the old page until the reload lands (' + where.label + ')',
        reloaded && gone.length === 0, 'the gate was gone at ' + span(gone));
}
{
    // The same from the startup gate on home, which was already right; held so it stays so.
    const { page } = await coldStart({ storage: baseStorage() });
    page.plugin.executeProfileSwitch(KID, null);
    const gone = [];
    await page.advance(1150, t => { if (!page.byId['profiles-gate-overlay']) gone.push(t); });
    ok('and from the startup gate on home', page.navigations.length > 0 && gone.length === 0,
        'the gate was gone at ' + span(gone));
}
{
    // Reported after the startup cover shipped: the cold start was fixed, but picking a
    // profile still flashed home before the reload. Nothing in our code takes the gate
    // down on home during a switch, so this asks the question that does not depend on
    // knowing what did: with the gate gone mid-switch, is home still covered until the
    // navigation lands?
    const { page } = await coldStart({ storage: baseStorage() });
    page.plugin.executeProfileSwitch(KID, null);
    await page.advance(150);
    const ov = page.byId['profiles-gate-overlay'];
    ok('the switch is under way with the gate up', !!ov);
    if (ov) ov.remove();
    const seen = [];
    await page.advance(1000, t => { if (page.homeVisible()) seen.push(t); });
    ok('with the gate taken away mid-switch, home stays covered until the reload',
        seen.length === 0, 'home visible at ' + span(seen));
}

console.log('\n── 3. Nothing is covered when the gate is not coming ───────────');
const noCover = [
    ['the household does not ask', { jellyfin_profiles_switcher_mode:
        JSON.stringify({ askOnStartup: false, location: 'button', masterUserId: MASTER }) }, {}],
    ['this device keeps its own answer, and it is no', { jellyfin_profiles_device_gate:
        JSON.stringify({ [MASTER.replace(/-/g, '')]: { synced: false, askOnStartup: false } }) }, {}],
    ['the cached answer belongs to somebody else', { jellyfin_profiles_switcher_mode:
        JSON.stringify({ askOnStartup: true, location: 'button', masterUserId: 'ffffffff-0000-0000-0000-000000000000' }),
        jellyfin_profiles_master_state: null }, {}],
    ['nothing has ever been cached', { jellyfin_profiles_switcher_mode: null }, {}],
    ['nobody is signed in', { jellyfin_credentials: JSON.stringify({ Servers: [{ Id: SERVER }] }) }, {}],
    ['the emergency disable is on', { jellyfin_profiles_disabled: String(Date.now()) }, {}],
    ['a profile was already chosen this session', {}, { session: { jellyfin_profiles_active_token: 'kid-token' } }],
    ['the page is the sign-in screen', {}, { pathname: '/web/login.html' }],
    ['the page is not home', {}, { pathname: '/web/details' }],
    ['the page is a hash-routed non-home', {}, { pathname: '/web/index.html', hash: '#!/details?id=1' }]
];
for (const c of noCover) {
    const page = makePage(Object.assign({ storage: baseStorage(c[1]) }, c[2]));
    page.runHead();
    ok('no cover when ' + c[0], page.html.getAttribute('data-jpf-cover') === null);
}
{
    // And the cover is raised for the shapes home really comes in.
    for (const shape of [['/web/', ''], ['/web/index.html', ''], ['/web/index.html', '#/home.html'],
                         ['/web/index.html', '#!/home.html'], ['/web/home', ''], ['/jf/web/home', '']]) {
        const page = makePage({ storage: baseStorage(), pathname: shape[0], hash: shape[1] });
        page.runHead();
        ok('the cover goes up on home as ' + shape[0] + shape[1], page.html.getAttribute('data-jpf-cover') !== null);
    }
}
{
    const page = makePage({ storage: baseStorage({ 'jpf-sw': '1', 'jpf-bg': '#101010|dark' }) });
    page.runHead();
    ok('a reload landing from a switch keeps its own hide and gets no second cover',
        page.html.style.opacity === '0' && page.html.getAttribute('data-jpf-cover') === null);
}

console.log('\n── 4. The cover always comes down ──────────────────────────────');
{
    const { page, seen } = await coldStart({ storage: baseStorage(), scriptLoads: false, runFor: 50 });
    ok('profiles.js never loaded: down at DOMContentLoaded, not after a timeout',
        seen.everCovered && page.html.getAttribute('data-jpf-cover') === null && seen.coverMaxMs === 0,
        'up for ' + seen.coverMaxMs + ' ms');
}
{
    const { page, seen } = await coldStart({ storage: baseStorage(), apiAt: 1e9, runFor: 5000 });
    ok('profiles.js loaded but never started (no ApiClient): the backstop takes it down by 3 s',
        seen.everCovered && page.html.getAttribute('data-jpf-cover') === null && seen.coverMaxMs <= 3010,
        'up for ' + seen.coverMaxMs + ' ms');
}
{
    const { page, seen } = await coldStart({ storage: baseStorage(), server: { list: null }, runFor: 9000 });
    ok('the profile list never answers: down within two allowances, never stuck',
        seen.everCovered && page.html.getAttribute('data-jpf-cover') === null && seen.coverMaxMs <= 6010,
        'up for ' + seen.coverMaxMs + ' ms, still up=' + (page.html.getAttribute('data-jpf-cover') !== null));
}
{
    const { page, seen } = await coldStart({ storage: baseStorage(), listStatus: 500, runFor: 1200 });
    ok('the profile list fails: down as soon as it does',
        seen.everCovered && seen.coverMaxMs < 1000,
        'up for ' + seen.coverMaxMs + ' ms');
    void page;
}
{
    // The cache said ask; the server, answering first, says it does not any more.
    const { page, seen } = await coldStart({ storage: baseStorage(), serverPrefs: { askOnStartup: false },
                                             server: { preferences: 50, list: 2000 }, apiAt: 60, homeAt: 400,
                                             events: [] , runFor: 1500 });
    void page;
    ok('the cached "ask" is overruled by the server before the gate request: down when it answers',
        seen.everCovered && seen.coverMaxMs < 1000, 'up for ' + seen.coverMaxMs + ' ms');
}
{
    // Token expired: jellyfin-web goes to the sign-in page instead of home.
    const { page, seen } = await coldStart({ storage: baseStorage(), user: null, homeAt: 1e9, runFor: 1500,
        events: [{ at: 300, run: p => { p.location.pathname = '/web/login.html'; p.sandbox.history.pushState(); p.plugin.checkRoute(); } }] });
    ok('jellyfin-web goes to sign-in instead: down on the next route check',
        seen.everCovered && page.html.getAttribute('data-jpf-cover') === null && seen.coverMaxMs <= 400,
        'up for ' + seen.coverMaxMs + ' ms');
}
{
    const { page, seen } = await coldStart({ storage: baseStorage(), server: { list: null }, runFor: 400,
        events: [{ at: 350, run: p => p.plugin.applyPanicDisable(false) }] });
    ok('the emergency disable: down at once', seen.everCovered && page.html.getAttribute('data-jpf-cover') === null);
}
{
    // A server-side emergency disable serves the inert script instead of profiles.js.
    const ctl = fs.readFileSync(path.join(L.ROOT, 'Controllers', 'ProfilesController.cs'), 'utf8');
    const inert = (/private const string InertProfilesJs =([\s\S]*?)";\s*\n/.exec(ctl) || [])[1] || '';
    ok('the inert script served under an emergency disable takes the cover down too',
        /removeAttribute\('data-jpf-cover'\)/.test(inert));
}

console.log('\n── 5. One rule, in the gate\'s colour ───────────────────────────');
{
    const headRule = (/s\.textContent='([^']*)'/.exec(headScriptBody()) || [])[1] || '';
    const js = fs.readFileSync(JS_PATH, 'utf8');
    const jsRule = (/const GATE_COVER_CSS = '([^']*)';/.exec(js) || [])[1] || '';
    ok('the head script and profiles.js carry the same cover rule', !!headRule && headRule === jsRule,
        'head: ' + headRule + '\n          js:   ' + jsRule);

    const css = L.extractCss(js);
    const gate = (/#profiles-gate-overlay\s*\{[^}]*?background:\s*([^;]+);/.exec(css) || [])[1] || '';
    const fallback = (/var\(--jpf-gate-bg,(radial-gradient\([^;]*\))\)/.exec(headRule) || [])[1] || '';
    // Since themes, the gate reads --jpf-gate-bg, and Bonfire's value for it lives in the
    // block every theme starts from rather than the first token block resolveCssValue
    // reads. The cover's fallback must be that value: it is what shows before any theme.
    const bonfireBg = (/:root, body, \[data-jpf-theme="bonfire"\]\s*\{[^}]*?--jpf-gate-bg:\s*([^;]+);/.exec(css) || [])[1];
    const squash = s => L.resolveCssValue(css, s)
        .replace(/^var\(--jpf-gate-bg\)$/, bonfireBg || 'var(--jpf-gate-bg)')
        .replace(/\s+/g, '').replace(/^var\(--jpf-gate-bg,(.*)\)$/, '$1');
    ok('and its colour is the gate\'s background, so dropping it under the gate shows nothing',
        !!fallback && squash(gate) === squash(fallback),
        'gate: ' + gate + '\n          cover: ' + fallback);
    ok('through --jpf-gate-bg, so a theme can set both in one place', /var\(--jpf-gate-bg,/.test(headRule));
}

console.log('');
if (fails.length) {
    console.log('  Failures:');
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
}

main().catch(e => { console.error(e); process.exit(1); });
