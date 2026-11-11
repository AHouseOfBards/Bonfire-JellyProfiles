/*
 * Closed for maintenance, as the web client shows it.
 *
 * The client asks /info once per page load (it needs no token: the people this is for
 * cannot sign in) and then:
 *   - on the sign-in screen, a strip across the top;
 *   - for an administrator, the same strip, worded for them;
 *   - for anyone else signed in, a cover over the whole page with a Sign out button,
 *     because every request they make is refused;
 *   - when the server is open, or cannot be reached, nothing.
 *
 * The message is the administrator's own words and reaches everyone, so every element
 * built is inspected for markup rather than the source read for "textContent".
 *
 * Against the announcements commit the first assertion fails: there is no checkMaintenance.
 *
 *     node tests/js/maintenance.js [/path/to/profiles.js]
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

const EVIL = '<img src=x onerror=alert(1)>';

function makeEl(tag) {
    const attrs = {};
    const listeners = {};
    const classes = new Set();
    let inner = '';
    const el = {
        tagName: String(tag || 'div').toUpperCase(),
        children: [], style: {}, dataset: {}, textContent: '', type: '', id: '',
        get className() { return Array.from(classes).join(' '); },
        set className(v) { classes.clear(); String(v).split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
        classList: { add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c),
                     toggle: c => (classes.has(c) ? classes.delete(c) : classes.add(c)) },
        setAttribute: (k, v) => { attrs[k] = String(v); },
        getAttribute: k => (k in attrs ? attrs[k] : null),
        appendChild: c => { c.parent = el; el.children.push(c); return c; },
        remove: () => { if (el.parent) el.parent.children = el.parent.children.filter(c => c !== el); },
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        click: () => (listeners.click || []).forEach(fn => fn({ preventDefault() {} })),
        querySelector: s => { let hit = null; walk(el, e => { if (!hit && e !== el && s === 'button' && e.tagName === 'BUTTON') hit = e; }); return hit; },
        focused: false,
        focus() { el.focused = true; }
    };
    Object.defineProperty(el, 'innerHTML', { get: () => inner, set: v => { inner = String(v); } });
    return el;
}
function walk(el, fn) { fn(el); (el.children || []).forEach(c => walk(c, fn)); }
function byClass(root, cls) { const out = []; walk(root, e => { if (e.classList && e.classList.contains(cls)) out.push(e); }); return out; }

const SRC_PATH = process.argv[2] || L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) { console.error('could not find the single init() call'); process.exit(1); }
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function load(opts) {
    const body = makeEl('body');
    const calls = [];
    const timers = [];
    let loggedOut = 0, reloaded = 0;
    const sandbox = {
        console: { log() {}, warn() {}, error() {}, info() {}, debug() {} },
        setTimeout: () => 0, clearTimeout() {},
        setInterval: fn => { timers.push({ fn, live: true }); return timers.length; },
        clearInterval: id => { if (timers[id - 1]) timers[id - 1].live = false; },
        document: {
            documentElement: { classList: makeEl().classList },
            getElementById: id => { let hit = null; walk(body, e => { if (!hit && e.id === id) hit = e; }); return hit; },
            querySelector: () => null, querySelectorAll: () => [],
            addEventListener() {}, createElement: makeEl,
            head: { appendChild() {} }, body
        },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: '' },
        location: { href: '', hash: '', pathname: '/', reload: () => { reloaded++; } },
        fetch: (url, o) => { calls.push({ url, o }); return opts.serve(url); },
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        ApiClient: {
            getUrl: p => 'http://server/' + p,
            accessToken: () => (opts.user ? 'tok' : null),
            getCurrentUserId: () => (opts.user ? 'u1' : null),
            getCurrentUser: () => (opts.userError ? Promise.reject(new Error('offline')) : Promise.resolve(opts.user)),
            logout: () => { loggedOut++; return Promise.resolve(); },
            appName: () => 'Jellyfin Web', deviceName: () => 'T', deviceId: () => 'd', appVersion: () => '1'
        }
    };
    sandbox.window = sandbox;
    sandbox.addEventListener = () => {};
    sandbox.removeEventListener = () => {};
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox, { filename: SRC_PATH });
    return { P: sandbox.__PROFILES, body, calls, sandbox, timers, counts: () => ({ loggedOut, reloaded }) };
}
const json = b => Promise.resolve({ ok: true, json: () => Promise.resolve(b) });
const closed = { name: 'Bonfire', maintenance: { message: 'Upgrading. ' + EVIL, untilUtc: '2099-11-05T03:00:00Z' } };
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(r => setImmediate(r)); };
const notice = env => env.sandbox.document.getElementById('profiles-maintenance');

(async function () {
    console.log('\n── Signed out ──────────────────────────────────────────────────');
    let env = load({ serve: () => json(closed), user: null });
    ok('the client checks for maintenance', !!(env.P && typeof env.P.checkMaintenance === 'function'));
    if (!env.P || typeof env.P.checkMaintenance !== 'function') return finish();

    // dateStyle/timeStyle are Chromium 76; on a webOS 4.5 television they are ignored and
    // the whole date prints in full (R-1199). Spelled out as fields, they work everywhere.
    const fmtOptions = [];
    const dateProto = vm.runInContext('Date.prototype', env.sandbox);
    const realLocale = dateProto.toLocaleString;
    dateProto.toLocaleString = function (loc, opts) { fmtOptions.push(opts || {}); return realLocale.call(this, loc, opts); };
    env.P.checkMaintenance();
    await settle();
    ok('the expected time is formatted with fields, not dateStyle', fmtOptions.length > 0 && fmtOptions.every(o => !('dateStyle' in o) && !('timeStyle' in o)), JSON.stringify(fmtOptions));
    ok('it asks /info', env.calls.length === 1 && /plugins\/profiles\/info$/.test(env.calls[0].url));
    ok('with no token', !(env.calls[0].o && env.calls[0].o.headers));
    let n = notice(env);
    ok('the sign-in screen gets a strip', !!n && !n.classList.contains('is-blocking'));
    ok('announced as a status', n && n.getAttribute('role') === 'status');
    ok('with the message as text', n && byClass(n, 'profiles-maintenance-text')[0].textContent === 'Upgrading. ' + EVIL);
    let markup = false;
    walk(env.body, e => { if (e.innerHTML && e.innerHTML.indexOf('<img') !== -1) markup = true; });
    ok('and never as markup', !markup);
    ok('and when it is expected back', n && /^Back by /.test(byClass(n, 'profiles-maintenance-when')[0].textContent));
    ok('no Sign out button, since nobody is signed in', n && !n.querySelector('button'));

    // A time that has gone by is not "back by" anything (P2-209). The fixture above is a
    // fixed date, which was the future when written and is not now, so it is built here.
    const lapsed = { name: 'Bonfire', maintenance: { message: 'Still working.', untilUtc: '2020-01-01T03:00:00Z' } };
    const late = load({ serve: () => json(lapsed), user: null });
    late.P.checkMaintenance();
    await settle();
    const ln = notice(late);
    ok('a notice whose time has passed is still shown', !!ln && byClass(ln, 'profiles-maintenance-text')[0].textContent === 'Still working.');
    ok('but with no "Back by" in the past', !!ln && byClass(ln, 'profiles-maintenance-when').length === 0,
       ln && byClass(ln, 'profiles-maintenance-when').map(e => e.textContent).join('|'));

    env.P.checkMaintenance();
    await settle();
    ok('a second check does not add a second notice',
       env.body.children.filter(c => c.id === 'profiles-maintenance').length === 1);

    console.log('\n── A household member, signed in ───────────────────────────────');
    env = load({ serve: () => json(closed), user: { Policy: { IsAdministrator: false } } });
    env.P.checkMaintenance();
    await settle();
    n = notice(env);
    ok('the whole page is covered', !!n && n.classList.contains('is-blocking'));
    ok('as a dialog that must be answered', n && n.getAttribute('role') === 'alertdialog');
    // The trap that pulls a TV remote's focus back into a dialog only knows the surfaces
    // carrying this class. Without it, the arrow keys walk off the cover into the page
    // underneath, which the remote can still drive.
    ok('which keeps a remote\'s focus inside it', n && n.classList.contains('jpf-trap-surface'));
    // The cover has two buttons since P2-202 (Check again, Sign out), so this finds Sign
    // out by its label rather than by being the first button.
    const buttonNamed = (root, text) => { let hit = null; walk(root, e => { if (e.tagName === 'BUTTON' && e.textContent === text) hit = e; }); return hit; };
    const out = n && buttonNamed(n, 'Sign out');
    ok('with a Sign out button', !!out);
    ok('and a button has focus, for a remote', !!(out && (out.focused || buttonNamed(n, 'Check again').focused)));
    ok('and a remote can reach it', out && out.classList.contains('focusable'));
    if (out) { out.click(); await settle(); }
    ok('signing out signs out, then reloads', env.counts().loggedOut === 1 && env.counts().reloaded === 1);

    console.log('\n── An administrator ────────────────────────────────────────────');
    env = load({ serve: () => json(closed), user: { Policy: { IsAdministrator: true } } });
    env.P.checkMaintenance();
    await settle();
    n = notice(env);
    ok('gets a strip, not a cover', !!n && !n.classList.contains('is-blocking'));
    ok('worded for them', n && /Only administrators/.test(byClass(n, 'profiles-maintenance-title')[0].textContent));

    console.log('\n── The cover can be re-checked, and checks itself (P2-202) ─────');
    const member = { Policy: { IsAdministrator: false } };
    const live = e => e.timers.filter(x => x.live);
    {
        let open = false;
        let offline = false;
        const serve = () => (offline ? Promise.reject(new Error('offline')) : json(open ? { name: 'Bonfire', maintenance: null } : closed));
        env = load({ serve, user: member });
        env.P.checkMaintenance();
        await settle();
        n = notice(env);
        const again = n && buttonNamed(n, 'Check again');
        ok('the cover offers Check again beside Sign out', !!again && !!buttonNamed(n, 'Sign out'));
        ok('and a timer, owned by the cover, is running', live(env).length === 1);

        const before = env.calls.length;
        if (again) { again.click(); await settle(); }
        ok('Check again asks the server again, past the one-minute cache', env.calls.length === before + 1);
        ok('still closed: the cover stays and says so',
           !!notice(env) && byClass(n, 'profiles-maintenance-when').some(e => e.textContent === 'Still closed.'));

        offline = true;
        if (again) { again.click(); await settle(); }
        ok('an answer that cannot be read leaves the cover up', !!notice(env) && env.counts().reloaded === 0);
        offline = false;

        const tick = live(env)[0];
        ok('the timer re-checks on its own', !!tick);
        open = true;
        if (tick) { tick.fn(); await settle(); }
        ok('maintenance over: the cover comes down by itself', !notice(env));
        ok('and the page reloads, since everything behind it was refused', env.counts().reloaded === 1);
        ok('and the timer stops', live(env).length === 0);

        open = false;
        env = load({ serve, user: member });
        env.P.checkMaintenance();
        await settle();
        n = notice(env);
        open = true;
        const b = n && buttonNamed(n, 'Check again');
        if (b) { b.click(); await settle(); }
        ok('Check again takes it down once maintenance has ended', !notice(env) && env.counts().reloaded === 1);

        open = false;
        env = load({ serve, user: null });
        env.P.checkMaintenance();
        await settle();
        ok('the strip is re-checked too', !!notice(env) && live(env).length === 1);
        open = true;
        if (live(env)[0]) live(env)[0].fn();
        await settle();
        ok('and goes without a reload', !notice(env) && env.counts().reloaded === 0 && live(env).length === 0);
    }

    console.log('\n── Who is looking changes, or cannot be read (R-1209) ──────────');
    {
        const opts = { serve: () => json(closed), user: null };
        env = load(opts);
        env.P.checkMaintenance();
        await settle();
        ok('signed out: a strip', !!notice(env) && !notice(env).classList.contains('is-blocking'));
        opts.user = { Policy: { IsAdministrator: false } };
        live(env)[0].fn();
        await settle();
        n = notice(env);
        ok('a member signs in on the same page: the strip becomes the cover', !!n && n.classList.contains('is-blocking'));
        ok('and there is one notice, with one timer', env.body.children.filter(c => c.id === 'profiles-maintenance').length === 1 && live(env).length === 1);

        const opts2 = { serve: () => json(closed), user: { Policy: { IsAdministrator: true } } };
        env = load(opts2);
        env.P.checkMaintenance();
        await settle();
        ok('an administrator: the strip says only administrators', /Only administrators/.test(byClass(notice(env), 'profiles-maintenance-title')[0].textContent));
        opts2.user = null;
        live(env)[0].fn();
        await settle();
        n = notice(env);
        ok('signed out again: no longer worded for an administrator', !!n && !/Only administrators/.test(byClass(n, 'profiles-maintenance-title')[0].textContent) && !n.classList.contains('is-blocking'));

        const opts3 = { serve: () => json(closed), user: { Policy: { IsAdministrator: true } }, userError: true };
        env = load(opts3);
        env.P.checkMaintenance();
        await settle();
        n = notice(env);
        ok('an account that cannot be read does not cover the page', !!n && !n.classList.contains('is-blocking'));
        opts3.userError = false;
        live(env)[0].fn();
        await settle();
        ok('and once it can be read, an administrator still has a strip', !!notice(env) && !notice(env).classList.contains('is-blocking'));

        const opts4 = { serve: () => json(closed), user: member };
        env = load(opts4);
        env.P.checkMaintenance();
        await settle();
        opts4.userError = true;
        live(env)[0].fn();
        await settle();
        ok('a cover is not lifted by a failed account read', !!notice(env) && notice(env).classList.contains('is-blocking'));
    }

    console.log('\n── Open, or unknown ────────────────────────────────────────────');
    for (const [label, serve] of [
        ['open', () => json({ name: 'Bonfire', maintenance: null })],
        ['an older server with no such field', () => json({ name: 'Bonfire' })],
        ['a 404', () => Promise.resolve({ ok: false, json: () => Promise.resolve({}) })],
        ['offline', () => Promise.reject(new Error('offline'))]
    ]) {
        env = load({ serve, user: { Policy: { IsAdministrator: false } } });
        let threw = false;
        try { env.P.checkMaintenance(); await settle(); } catch (e) { threw = true; }
        ok(label + ': nothing shown, nothing thrown', !threw && !notice(env));
    }

    console.log('\n── Where it runs ───────────────────────────────────────────────');
    const named = s => src.replace(new RegExp(s + '\\s*:\\s*function'), 'function ' + s);
    ok('checkRoute never asks', L.extractFunction(named('checkRoute'), 'checkRoute').indexOf('checkMaintenance') === -1);
    // Since R-1186 init hands the once-per-load checks to _startPageChecks, which a
    // remembered panic that ends can also call; its flag keeps it to one run.
    const initSrc = L.extractFunction(named('init'), 'init');
    const startSrc = L.extractFunction(named('_startPageChecks'), '_startPageChecks');
    ok('init does, once', (initSrc.match(/_startPageChecks\(\)/g) || []).length === 1
        && (initSrc.match(/checkMaintenance\(\)/g) || []).length === 0
        && (startSrc.match(/checkMaintenance\(\)/g) || []).length === 1
        && /if\s*\(this\._pageChecksStarted\)\s*return;/.test(startSrc));
    ok('and checkRoute never starts the page checks',
       L.extractFunction(named('checkRoute'), 'checkRoute').indexOf('_startPageChecks') === -1);

    finish();
})().catch(e => { console.error(e); process.exit(1); });

function finish() {
    console.log();
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
}
