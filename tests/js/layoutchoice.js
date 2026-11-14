/*
 * The layout switch: Automatic (the default), TV layout, Standard layout, chosen per device.
 *
 * Detection (tvmode.js) is unchanged and is what "Automatic" means. This is the answer for
 * the two sets it gets wrong: a television whose browser does not say so (a Fire TV, a
 * projector on a laptop) and a TV-class browser on a screen close enough to read the
 * ordinary layout. It is stored on the device only, in localStorage, because the same
 * account is signed in on a phone and a television and they want different answers.
 *
 * It is read where the device classes are applied, once and then cached, never on the route
 * tick: checkRoute is checked below for any mention of it.
 *
 *     node tests/js/layoutchoice.js /path/to/old/profiles.js     (red: no such setting)
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
const src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function makeClassList(initial) {
    const set = new Set(initial || []);
    return {
        add: c => set.add(c), remove: c => set.delete(c), contains: c => set.has(c),
        toggle: (c, force) => {
            const on = force === undefined ? !set.has(c) : !!force;
            if (on) set.add(c); else set.delete(c);
            return on;
        }
    };
}

// storage: what localStorage holds, by key. reads counts getItem calls, so "read once" is
// a measurement.
function build(opts) {
    const store = Object.assign({}, opts.storage || {});
    const counts = { reads: 0, writes: 0 };
    const body = {
        children: [], classList: makeClassList(),
        appendChild(el) { el.parentNode = body; body.children.push(el); return el; },
        removeChild(el) { body.children = body.children.filter(c => c !== el); el.parentNode = null; return el; }
    };
    const root = { style: {}, classList: makeClassList(opts.rootClasses) };
    const observers = [];
    const doc = {
        body, documentElement: root, head: { appendChild() {} },
        addEventListener() {}, removeEventListener() {},
        querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
        createElement() {
            const el = { style: { cssText: '' }, children: [], parentNode: null, classList: makeClassList(), appendChild(c) { el.children.push(c); return c; } };
            // Honours flex gap, so jpf-no-flex-gap stays out of what is being asked.
            Object.defineProperty(el, 'scrollHeight', { get() { return el.parentNode === body && el.children.length > 1 ? el.children.length - 1 : 0; } });
            return el;
        }
    };
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: {
            getItem: k => { counts.reads++; return Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null; },
            setItem: (k, v) => { counts.writes++; store[k] = String(v); },
            removeItem: k => { delete store[k]; }
        },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: opts.ua || 'Mozilla/5.0' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
        document: doc,
        MutationObserver: function (cb) { this.observe = (target, init) => observers.push({ target, init, cb }); this.disconnect = () => {}; },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Uint8ClampedArray
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    return { P: sandbox.__PROFILES, root, store, counts, observers };
}

const UA_TV = 'Mozilla/5.0 (Web0S; Linux/SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/68.0.3440.106 Safari/537.36 WebAppManager';
const UA_DESKTOP = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';

const probe = build({});
const KEY = probe.P.config && probe.P.config.layoutKey;
console.log();
console.log('── the choice is a device setting with a key of its own ───────');
ok('there is a storage key for it', typeof KEY === 'string' && /layout/.test(KEY));
ok('and a way to set it', typeof probe.P.setLayoutChoice === 'function');

if (typeof KEY === 'string') {
    console.log();
    console.log('── what each answer does at start-up ──────────────────────────');
    function start(ua, rootClasses, stored) {
        const storage = {};
        if (stored !== undefined) storage[KEY] = stored;
        const e = build({ ua, rootClasses, storage });
        e.P.applyDeviceClasses();
        return e;
    }
    ok('TV layout on a desktop browser is a television', start(UA_DESKTOP, [], 'tv').root.classList.contains('jpf-tv'));
    ok('TV layout beats jellyfin-web saying layout-desktop', start(UA_DESKTOP, ['layout-desktop'], 'tv').root.classList.contains('jpf-tv'));
    ok('Standard layout on a television user agent is not', !start(UA_TV, [], 'standard').root.classList.contains('jpf-tv'));
    ok('Standard layout beats jellyfin-web saying layout-tv', !start(UA_TV, ['layout-tv'], 'standard').root.classList.contains('jpf-tv'));
    ok('Automatic (nothing stored) still finds a television', start(UA_TV, [], undefined).root.classList.contains('jpf-tv'));
    ok('Automatic (nothing stored) still leaves a desktop alone', !start(UA_DESKTOP, [], undefined).root.classList.contains('jpf-tv'));
    ok('"auto" stored is Automatic', start(UA_TV, [], 'auto').root.classList.contains('jpf-tv'));
    ok('anything else stored is Automatic, not a crash or a TV', !start(UA_DESKTOP, [], 'sideways').root.classList.contains('jpf-tv')
        && start(UA_TV, [], 'sideways').root.classList.contains('jpf-tv'));

    console.log();
    console.log('── changing it takes effect at once and is remembered ─────────');
    const e = start(UA_DESKTOP, [], undefined);
    ok('a desktop starts as standard', !e.root.classList.contains('jpf-tv'));
    e.P.setLayoutChoice('tv');
    ok('choosing TV layout applies without a reload', e.root.classList.contains('jpf-tv'));
    ok('and is written for the next load', e.store[KEY] === 'tv');
    e.P.setLayoutChoice('standard');
    ok('choosing Standard removes it', !e.root.classList.contains('jpf-tv') && e.store[KEY] === 'standard');
    e.P.setLayoutChoice('auto');
    ok('choosing Automatic clears the stored answer, so it can never go stale', !(KEY in e.store));
    ok('and returns to detection', !e.root.classList.contains('jpf-tv'));
    const t = start(UA_TV, [], 'standard');
    t.P.setLayoutChoice('auto');
    ok('Automatic on a television finds it again', t.root.classList.contains('jpf-tv'));
    e.P.setLayoutChoice('banana');
    ok('an unknown value is ignored rather than stored', e.store[KEY] === undefined);
    ok('one more observer is not added by changing it', e.observers.length === 1);

    console.log();
    console.log('── it follows a layout change from jellyfin-web, still ────────');
    const o = start(UA_DESKTOP, [], 'standard');
    o.root.classList.add('layout-tv');
    o.observers[0].cb();
    ok('Standard holds when jellyfin-web switches to layout-tv', !o.root.classList.contains('jpf-tv'));
    const f = start(UA_DESKTOP, [], 'tv');
    f.root.classList.add('layout-desktop');
    f.observers[0].cb();
    ok('TV holds when jellyfin-web switches to layout-desktop', f.root.classList.contains('jpf-tv'));

    console.log();
    console.log('── read once, and never on the route tick ─────────────────────');
    const r = start(UA_TV, [], 'tv');
    const before = r.counts.reads;
    for (let i = 0; i < 20; i++) r.observers[0].cb();
    ok('a class change re-applies from the cached choice, not from storage', r.counts.reads === before);
    const tick = r.P.checkRoute ? r.P.checkRoute.toString() : '';
    ok('checkRoute does not mention it', tick.length > 0 && !/layout(Choice|Key)/.test(tick));
}

console.log();
console.log('── the control, in Settings under More ────────────────────────');
const account = probe.P._renderAccountSettings ? probe.P._renderAccountSettings.toString() : '';
const bind = probe.P._bindAccountScreen ? probe.P._bindAccountScreen.toString() : '';
const rare = /const rare = `([\s\S]*?)`;\r?\n/.exec(account);
ok('the account page has a layout choice inside its More fold (the "rare" block)',
    !!rare && /account-layout-select/.test(rare[1]));
ok('with exactly the three answers, in the order Automatic, TV, Standard',
    !!rare && /value="auto"[\s\S]*value="tv"[\s\S]*value="standard"/.test(rare[1]));
ok('its labels go through t()', !!rare && /t\('account\.layoutAuto'\)/.test(rare[1]) && /t\('account\.layoutTv'\)/.test(rare[1])
    && /t\('account\.layoutStandard'\)/.test(rare[1]));
ok('changing it calls setLayoutChoice, and does not post to the server (it is per device)',
    /account-layout-select[\s\S]*setLayoutChoice\(/.test(bind)
    && !/saveAccount\(\{\s*layout/.test(bind));

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
