/*
 * Route changes reach Bonfire through the Campground runtime when it is there, and still
 * reach it when it is not.
 *
 * Methodology B6: a plugin wraps history only through Campground.patch, and learns of a
 * route change from the runtime's `route` event. B7: window.Campground can be missing (a
 * Content-Security-Policy blocked runtime.js, or the brake is on), so Bonfire never
 * depends on it. Bonfire used to assign history.pushState / replaceState itself, which a
 * second copy of the script wraps twice and the brake cannot undo.
 *
 * The runtime's own route event covers hashchange, popstate and viewshow but not
 * pushState, and jellyfin-web 10.11 navigates by pushState (the route moved into the
 * path), so the pushState hook stays, installed through patch().
 *
 *     node tests/js/runtimeroute.js /path/to/old/profiles.js
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

let src = fs.readFileSync(L.profilesPath(), 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

// mode: 'missing' (no window.Campground), 'present', or 'braked' (patch returns null).
function build(mode) {
    const listeners = {};
    const docListeners = {};
    const timers = [];
    const bus = {};
    const state = { inPatch: false, outsideWrites: 0, patched: [], intervals: 0 };

    const history = {};
    const origs = {
        pushState() { state.pushed = (state.pushed || 0) + 1; },
        replaceState() { state.replaced = (state.replaced || 0) + 1; }
    };
    ['pushState', 'replaceState'].forEach(name => {
        let fn = origs[name];
        Object.defineProperty(history, name, {
            get: () => fn,
            set: v => { if (!state.inPatch) state.outsideWrites++; fn = v; }
        });
    });

    const noop = () => {};
    const el = () => ({
        style: {}, classList: { add: noop, remove: noop, contains: () => false, toggle: () => false },
        addEventListener: noop, removeEventListener: noop, appendChild: noop, setAttribute: noop,
        querySelector: () => null, querySelectorAll: () => []
    });
    const sandbox = {
        console: { log: noop, warn: noop, error: noop },
        localStorage: { getItem: () => null, setItem: noop, removeItem: noop },
        sessionStorage: { getItem: () => null, setItem: noop, removeItem: noop },
        navigator: { userAgent: 'Mozilla/5.0', language: 'en' },
        setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout: noop,
        setInterval: () => { state.intervals++; return 1; }, clearInterval: noop,
        history,
        document: {
            addEventListener: (e, fn) => { (docListeners[e] = docListeners[e] || []).push(fn); },
            removeEventListener: noop, querySelector: () => null, querySelectorAll: () => [],
            getElementById: () => null, createElement: el, head: el(), body: el(),
            documentElement: el()
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.addEventListener = (e, fn) => { (listeners[e] = listeners[e] || []).push(fn); };
    sandbox.removeEventListener = noop;
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    sandbox.location = { href: 'https://jf.local/web/', hash: '', pathname: '/web/' };

    if (mode !== 'missing') {
        // A faithful enough runtime: patch() hands wrapper the original and installs the result.
        sandbox.Campground = {
            version: '0.10.0',
            on: (name, fn) => { (bus[name] = bus[name] || []).push(fn); },
            patch: (target, name, wrapper, id) => {
                if (mode === 'braked') return null;
                state.patched.push(id + ':' + name);
                state.inPatch = true;
                try { target[name] = wrapper(target[name]); } finally { state.inPatch = false; }
                return { id: id, name: name, remove: noop };
            },
            isDisabled: () => false
        };
    }

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    const plugin = sandbox.__PROFILES;
    plugin._bindSettingsRefresh = noop;
    let checks = 0;
    plugin.checkRoute = () => { checks++; };

    plugin.bindEvents();

    function runTimers() { const t = timers.splice(0); t.forEach(fn => fn()); }
    return {
        plugin, sandbox, state, history, runTimers,
        checks: () => checks,
        emitRoute: () => (bus.route || []).forEach(fn => fn({ path: '/home', params: {} })),
        fire: e => (listeners[e] || []).forEach(fn => fn({ type: e })),
        hasRouteListener: () => (bus.route || []).length > 0
    };
}

console.log('\n── runtime present ─────────────────────────────────────────────');
{
    const env = build('present');
    ok('Bonfire does not assign history.pushState / replaceState itself (' + env.state.outsideWrites + ' writes)',
       env.state.outsideWrites === 0);
    ok('it wraps both through Campground.patch under its own id (' + env.state.patched.join(', ') + ')',
       env.state.patched.indexOf('bonfire:pushState') !== -1 && env.state.patched.indexOf('bonfire:replaceState') !== -1);
    ok('it listens for the runtime\'s route event', env.hasRouteListener());

    env.runTimers();
    const before = env.checks();
    env.emitRoute();
    ok('a route event reaches checkRoute (' + (env.checks() - before) + ')', env.checks() - before >= 1);

    const b2 = env.checks();
    env.history.pushState({}, '', '/web/#/movies');
    env.runTimers();
    ok('a pushState still reaches checkRoute, which the route event does not cover (' + (env.checks() - b2) + ')',
       env.checks() - b2 >= 1);
    ok('and still calls the real pushState', env.state.pushed === 1);

    const b3 = env.checks();
    env.history.replaceState({}, '', '/web/#/tv');
    env.runTimers();
    ok('replaceState too (' + (env.checks() - b3) + ')', env.checks() - b3 >= 1 && env.state.replaced === 1);
    ok('the safety-net poll is armed', env.state.intervals === 1);
}

console.log('\n── runtime missing (blocked or braked off) ─────────────────────');
{
    const env = build('missing');
    env.runTimers();
    for (const evt of ['popstate', 'hashchange']) {
        const b = env.checks();
        env.fire(evt);
        ok(evt + ' reaches checkRoute (' + (env.checks() - b) + ')', env.checks() - b >= 1);
    }
    const b = env.checks();
    env.history.pushState({}, '', '/web/#/movies');
    env.runTimers();
    ok('pushState reaches checkRoute (' + (env.checks() - b) + ')', env.checks() - b >= 1 && env.state.pushed === 1);
    const b2 = env.checks();
    env.history.replaceState({}, '', '/web/#/tv');
    env.runTimers();
    ok('replaceState reaches checkRoute (' + (env.checks() - b2) + ')', env.checks() - b2 >= 1 && env.state.replaced === 1);
    ok('the safety-net poll is armed', env.state.intervals === 1);
}

console.log('\n── runtime present, brake on (patch returns null) ──────────────');
{
    const env = build('braked');
    ok('Bonfire leaves history alone (' + env.state.outsideWrites + ' writes)', env.state.outsideWrites === 0);
    env.runTimers();
    const b = env.checks();
    env.emitRoute();
    ok('the route event still reaches checkRoute', env.checks() - b >= 1);
    ok('the safety-net poll is armed, so navigation is still noticed', env.state.intervals === 1);
}

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
