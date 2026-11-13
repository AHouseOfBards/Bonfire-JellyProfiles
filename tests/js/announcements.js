/*
 * Announcements on the profile screen, and on the settings page that writes them.
 *
 * The message is typed by an administrator and shown to every household on the server,
 * so the one thing that must never happen is for it to be read as markup. Both renderers
 * are run here against a hostile message, and every element they build is inspected:
 * reading the source for "textContent" would pass a version that uses it for one field
 * and innerHTML for the next.
 *
 * Also on the client:
 *   - severity decides the role (critical is an alert) and whether it can be dismissed;
 *   - an unknown severity is treated as info rather than dropped or trusted;
 *   - a dismissed info announcement stays dismissed on this device, but one the
 *     administrator rewrites shows again;
 *   - the server is asked at most once a minute, with the token, and a failure shows
 *     nothing rather than throwing.
 *
 * Against 1.7.1.2 the first assertion fails: there is no showAnnouncements.
 *
 *     node tests/js/announcements.js [/path/to/profiles.js] [/path/to/profilesDashboard.html]
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

/* ── a small DOM: enough elements to inspect what was built ───────────────── */

const htmlWrites = [];
function makeEl(tag) {
    const attrs = {};
    const listeners = {};
    const classes = new Set();
    const el = {
        tagName: String(tag || 'div').toUpperCase(),
        children: [],
        style: {},
        dataset: {},
        textContent: '',
        type: '', title: '',
        get className() { return Array.from(classes).join(' '); },
        set className(v) { classes.clear(); String(v).split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
        classList: {
            add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c),
            toggle: c => (classes.has(c) ? classes.delete(c) : classes.add(c))
        },
        setAttribute: (k, v) => { attrs[k] = String(v); },
        getAttribute: k => (k in attrs ? attrs[k] : null),
        appendChild: c => { el.children.push(c); return c; },
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        click: () => (listeners.click || []).forEach(fn => fn({ preventDefault() {} })),
        focused: 0,
        focus() { el.focused++; },
        // Just enough for '.cls' and 'button' lists, which is all the client asks.
        querySelector(sel) {
            let found = null;
            sel.split(',').map(s => s.trim()).forEach(s => {
                walk(el, e => {
                    if (found || e === el) return;
                    if (s.charAt(0) === '.' ? e.classList.contains(s.slice(1)) : e.tagName === s.toUpperCase()) found = e;
                });
            });
            return found;
        },
        _attrs: attrs
    };
    let inner = '';
    Object.defineProperty(el, 'innerHTML', {
        get: () => inner,
        set: v => { inner = String(v); htmlWrites.push(inner); if (inner === '') el.children = []; }
    });
    return el;
}
function walk(el, fn) { fn(el); (el.children || []).forEach(c => walk(c, fn)); }
function byClass(root, cls) {
    const out = [];
    walk(root, e => { if (e.classList && e.classList.contains(cls)) out.push(e); });
    return out;
}

/* ── the client ───────────────────────────────────────────────────────────── */

const SRC_PATH = process.argv[2] || L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function load(serve) {
    const store = {};
    const calls = [];
    const sandbox = {
        console: { log() {}, warn() {}, error() {}, info() {}, debug() {} },
        setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
        document: {
            documentElement: { classList: makeEl().classList },
            getElementById: () => null, querySelector: () => null, querySelectorAll: () => [],
            addEventListener() {}, createElement: makeEl,
            head: { appendChild() {} }, body: { appendChild() {}, classList: makeEl().classList }
        },
        localStorage: {
            getItem: k => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); },
            removeItem: k => { delete store[k]; }
        },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: '' }, location: { href: '', hash: '', pathname: '/' },
        fetch: (url, opts) => { calls.push({ url, opts }); return serve(url, opts); },
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        ApiClient: {
            getUrl: p => 'http://server/' + p,
            accessToken: () => 'tok',
            appName: () => 'Jellyfin Web', deviceName: () => 'Test', deviceId: () => 'dev', appVersion: () => '10.11'
        }
    };
    sandbox.window = sandbox;
    sandbox.addEventListener = () => {};
    sandbox.removeEventListener = () => {};
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox, { filename: SRC_PATH });
    const P = sandbox.__PROFILES;
    if (P) {
        P.readMasterState = () => ({ masterUserId: 'm', token: 'master-token' });
        P.fitGateToScreen = () => {};
    }
    return { P, calls, store };
}

function json(body) {
    return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });
}
function overlay() {
    const box = makeEl('div');
    box.style.display = 'none';
    return { box, querySelector: s => (s === '#profiles-announcements' ? box : null) };
}
const settle = () => new Promise(r => setImmediate(r));

(async function () {
    console.log('\n── The profile screen ──────────────────────────────────────────');

    const items = [
        { id: '11111111-1111-1111-1111-111111111111', message: 'Down at 2am. ' + EVIL, severity: 'critical' },
        { id: '22222222-2222-2222-2222-222222222222', message: 'Disk nearly full.', severity: 'warning' },
        { id: '33333333-3333-3333-3333-333333333333', message: 'New films added.', severity: 'info' },
        { Id: '44444444-4444-4444-4444-444444444444', Message: 'Odd one.', Severity: 'purple' }
    ];
    let served = items;
    const env = load(() => json({ announcements: served }));
    const P = env.P;
    ok('the client draws announcements', !!(P && typeof P.showAnnouncements === 'function'));
    if (!P || typeof P.showAnnouncements !== 'function') return finish();

    const o = overlay();
    htmlWrites.length = 0;
    P.showAnnouncements(o);
    await settle();

    ok('the server is asked once', env.calls.length === 1);
    const call = env.calls[0] || {};
    ok('at the startup route', /plugins\/profiles\/startup$/.test(call.url || ''));
    ok('with the token', /Token="master-token"/.test(((call.opts || {}).headers || {}).Authorization || ''));

    const rows = byClass(o.box, 'profiles-announcement');
    ok('every announcement is shown', rows.length === 4);
    ok('in the order the server sent', rows.map(r => r.getAttribute('data-severity')).join(',') === 'critical,warning,info,info');
    ok('the box is shown', o.box.style.display === '');

    const texts = byClass(o.box, 'profiles-announcement-text');
    ok('a hostile message is shown as text', texts[0] && texts[0].textContent === 'Down at 2am. ' + EVIL);
    ok('and never written as markup', htmlWrites.every(h => h === ''));
    let anyHtml = false;
    walk(o.box, e => { if (e.innerHTML && e.innerHTML.indexOf('<img') !== -1) anyHtml = true; });
    ok('by any element', !anyHtml);
    ok('PascalCase fields are read too', texts[3] && texts[3].textContent === 'Odd one.');

    ok('critical is announced as an alert', rows[0].getAttribute('role') === 'alert');
    ok('the rest as a status', rows.slice(1).every(r => r.getAttribute('role') === 'status'));
    ok('an unknown severity is info', rows[3].getAttribute('data-severity') === 'info');

    const dismissOf = r => byClass(r, 'profiles-announcement-dismiss')[0];
    ok('critical cannot be dismissed', !dismissOf(rows[0]));
    ok('warning cannot be dismissed', !dismissOf(rows[1]));
    ok('info can', !!dismissOf(rows[2]));
    ok('and the button is reachable by a remote', dismissOf(rows[2]) && dismissOf(rows[2]).classList.contains('focusable'));
    ok('and has a name', dismissOf(rows[2]) && dismissOf(rows[2]).getAttribute('aria-label') === 'Dismiss');

    dismissOf(rows[2]).click();
    let after = byClass(o.box, 'profiles-announcement');
    ok('a dismissed one goes at once', after.length === 3
        && after.every(r => byClass(r, 'profiles-announcement-text')[0].textContent !== 'New films added.'));

    // The button that was clicked is gone; focus must land on something, not on the body (R-1198).
    const left = byClass(o.box, 'profiles-announcement-dismiss');
    ok('after a dismissal focus moves to the next dismiss button', left.length > 0 && left[0].focused > 0, left.length + ' left, focused ' + (left[0] && left[0].focused));

    // The gate is drawn again inside the minute: from the cache, not the server.
    const o2 = overlay();
    P.showAnnouncements(o2);
    await settle();
    ok('a redraw inside a minute does not ask again', env.calls.length === 1);
    ok('and the dismissed one stays gone', byClass(o2.box, 'profiles-announcement').length === 3);

    // The administrator rewrites it; a minute passes.
    P._announcementCache.at -= 61000; P._startupHeld.at -= 61000;
    served = items.map(a => a.id === '33333333-3333-3333-3333-333333333333'
        ? Object.assign({}, a, { message: 'New films added, and a series.' }) : a);
    const o3 = overlay();
    P.showAnnouncements(o3);
    await settle();
    ok('after a minute it asks again', env.calls.length === 2);
    ok('and a rewritten announcement shows again', byClass(o3.box, 'profiles-announcement').length === 4);

    // Everything dismissed or nothing to show: the box goes away.
    P._announcementCache.at -= 61000; P._startupHeld.at -= 61000;
    served = [{ id: '33333333-3333-3333-3333-333333333333', message: 'New films added.', severity: 'info' }];
    const o4 = overlay();
    o4.box.style.display = '';
    P.showAnnouncements(o4);
    await settle();
    ok('with nothing left to show, the box is hidden', o4.box.style.display === 'none');

    // A server that refuses or is unreachable.
    for (const [label, serve] of [
        ['a 404', () => Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve({}) })],
        ['a network failure', () => Promise.reject(new Error('offline'))],
        ['a body that is not a list', () => json({ nope: true })]
    ]) {
        const e = load(serve);
        const ov = overlay();
        let threw = false;
        try { e.P.showAnnouncements(ov); await settle(); } catch (x) { threw = true; }
        ok(label + ' shows nothing and does not throw', !threw && byClass(ov.box, 'profiles-announcement').length === 0);
        // R-1211: a failed read was cached as [] for a minute, hiding a critical notice
        // the server would have returned on the next draw.
        const before = e.calls.length;
        e.P.showAnnouncements(overlay());
        await settle();
        ok(label + ' is not remembered: the next draw asks again', e.calls.length === before + 1);
    }

    // Never from the route poll: it runs twice a second on every page.
    const checkRoute = L.extractFunction(src.replace(/checkRoute\s*:\s*function/, 'function checkRoute'), 'checkRoute');
    ok('checkRoute never asks for announcements', checkRoute.indexOf('showAnnouncements') === -1);

    // The focus trap hands focus to the first control in the DOM. Above the grid in the
    // markup, an info announcement's Dismiss would be where a remote lands, and OK would
    // dismiss it instead of choosing a profile. So it is last in the markup and moved up
    // by the stylesheet.
    const gate = L.extractFunction(src.replace(/renderOverlayContent\s*:\s*function/, 'function renderOverlayContent'),
                                   'renderOverlayContent');
    const boxAt = gate.indexOf('id="profiles-announcements"');
    ok('the gate has an announcement box', boxAt !== -1);
    ok('after every profile and button in the markup',
       boxAt > gate.indexOf('${sectionsHtml}') && boxAt > gate.indexOf('id="profiles-panic-link"'));
    const css = L.extractCss();
    const orderRules = (css.match(/[^{}]*\{[^}]*\}/g) || [])
        .filter(r => /profiles-announcements/.test(r.split('{')[0]) && /(^|[;{\s])order\s*:/.test(r));
    ok('and drawn above the title by one rule',
       orderRules.length === 1 && /(^|[;{\s])order\s*:\s*-1\s*;/.test(orderRules[0]));

    console.log('\n── The settings page ───────────────────────────────────────────');

    const html = process.argv[3] ? fs.readFileSync(process.argv[3], 'utf8') : L.readDashboard();
    const script = L.dashboardScript(html);
    let render = null;
    try {
        const fns = L.dashboardI18n(script) + ['field', 'namedAria', 'formatWhen', 'announcementSchedule', 'isoToLocalInput', 'errorText',
                     'resetAnnouncementForm', 'showAnnouncementResult', 'showOutcome', 'paintOutcome', 'openAdvancedFor', 'loadAnnouncements', 'renderAnnouncements']
            .map(n => L.extractFunction(script, n)).join('\n');
        render = new Function('document', 'Dashboard', 'ApiClient', fns + '\nreturn renderAnnouncements;');
    } catch (e) { render = null; }
    ok('the list is built by a function a test can run', render !== null);
    if (render) {
        const list = makeEl('div');
        const page = { dataset: {}, querySelector: s => (s === '#annList' ? list : makeEl('div')) };
        htmlWrites.length = 0;
        const fn = render({ createElement: makeEl }, { confirm: () => Promise.resolve() }, {});
        fn(page, [
            { Id: 'a', Message: EVIL, Severity: 'critical', StartsUtc: null, EndsUtc: null, Showing: true },
            { id: 'b', message: 'Old', severity: 'info', startsUtc: null, endsUtc: '2020-01-01T00:00:00Z', showing: false }
        ]);
        const msgs = byClass(list, 'jpf-ann-msg');
        ok('a hostile message is shown as text', msgs[0] && msgs[0].textContent === EVIL);
        ok('and never written as markup', htmlWrites.every(h => h === ''));
        const items2 = byClass(list, 'jpf-ann-item');
        ok('severity is on the row', items2[0] && items2[0].getAttribute('data-severity') === 'critical');
        ok('one that is not showing is marked', items2[1] && items2[1].classList.contains('is-off'));
        const meta = byClass(list, 'jpf-ann-meta');
        ok('and says it ended', meta[1] && /^Info · Ended /.test(meta[1].textContent));
        ok('one with no end says so', meta[0] && /Showing until deleted$/.test(meta[0].textContent));
    }

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
