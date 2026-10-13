/*
 * Who the Switch Profile button belongs to, and when it is there at all.
 *
 * Four defects in 1.6.4, all reported from one household's iPad and fixed first in
 * OleRaehcke/Bonfire-JellyProfiles, a fork that tracks this repository:
 *
 *   1. With "Ask who's watching on startup" off there was no button, ever. The bubble
 *      needed an active-profile marker, only the gate sets one, and it lives in
 *      sessionStorage — so in button mode with the gate off there was no way to switch.
 *   2. The iOS app signs out without reloading. validateSessionState forgets the previous
 *      account on the sign-in screen, but only at init, so the old account's bubble sat on
 *      the sign-in screen and the next account inherited its master.
 *   3. With the stored master id missing, the household's own profiles were drawn as a
 *      linked "Kindred's Bonfire" and Manage Profiles told the master to open the master.
 *   4. jellyfin-web scales every div inside .paper-icon-button-light by 1.8 for icon
 *      glyphs, and the header avatar is such a div.
 *
 * Each section is behavioural: the plugin is loaded whole and driven through checkRoute or
 * its real click handler, and what is asserted is what a person would see — whether the
 * button exists, which group is "yours", whether Manage Profiles refuses.
 *
 *   node tests/js/bubblesession.js                                   # the working tree
 *   node tests/js/bubblesession.js old/Web/profiles.js old/Web/styles.css   # must fail
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

const SRC_PATH = L.profilesPath();
// argv[3], so the stylesheet can be bisected along with the script: L.extractCss reads the
// working tree's styles.css whatever script it is handed.
const CSS_PATH = process.argv[3] || L.stylesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PP = ProfilesPlugin;');

const MASTER = '8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a';
const KID = '1b2c3d4e-0000-4000-8000-000000000001';
const OTHER_MASTER = 'aaaaaaaa-0000-4000-8000-0000000000aa';
const OTHER_KID = 'aaaaaaaa-0000-4000-8000-0000000000ab';
const MASTER_KEY = 'jellyfin_profiles_master_state';

/* ── A sandbox where the bubble's existence is observable ─────────────────────── */

function build(opts) {
    opts = opts || {};
    const byId = {};
    const store = Object.assign({}, opts.storage || {});
    const session = Object.assign({}, opts.session || {});
    const removed = [];
    const alerts = [];

    function makeEl(tag) {
        const classes = new Set();
        const listeners = {};
        const el = {
            tagName: String(tag || 'div').toUpperCase(),
            style: {}, attrs: {}, children: [], dataset: {}, parentElement: null,
            __attached: false,
            classList: {
                add: c => classes.add(c), remove: c => classes.delete(c),
                contains: c => classes.has(c),
                toggle: (c, f) => { const on = f === undefined ? !classes.has(c) : !!f;
                                    if (on) classes.add(c); else classes.delete(c); return on; }
            },
            get className() { return [...classes].join(' '); },
            set className(v) { classes.clear(); String(v || '').split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
            addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
            removeEventListener() {},
            fire: type => (listeners[type] || []).forEach(fn => fn({ preventDefault() {}, stopPropagation() {}, target: el })),
            appendChild: n => attach(el, n), insertBefore: n => attach(el, n),
            remove: () => { el.__attached = false; if (el.id && byId[el.id] === el) delete byId[el.id]; },
            getAttribute: k => (k in el.attrs ? el.attrs[k] : null),
            setAttribute: (k, v) => { el.attrs[k] = v; },
            removeAttribute: k => { delete el.attrs[k]; },
            hasAttribute: k => k in el.attrs,
            contains: n => !!n && (n === el || n.parentElement === el),
            querySelector: () => null, querySelectorAll: () => [],
            closest: () => null, focus() {}, click() {}, blur() {},
            getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 }),
            getClientRects: () => [{}],
            offsetParent: {},
            textContent: '',
            // Markup assigned here is what a person would see. Every id it declares
            // becomes findable, so the plugin can bind its handlers the way it does in a
            // browser — and the harness can then press them.
            set innerHTML(html) {
                el._html = html;
                const re = /id="([^"]+)"/g;
                let m;
                while ((m = re.exec(html))) {
                    const child = makeEl('div');
                    child.id = m[1];
                    child.__attached = true;
                    byId[m[1]] = child;
                }
            },
            get innerHTML() { return el._html || ''; }
        };
        return el;
    }
    function attach(parent, n) {
        n.parentElement = parent;
        n.__attached = true;
        if (n.id) byId[n.id] = n;
        return n;
    }

    const header = makeEl('div');
    header.className = 'headerRight';
    header.__attached = true;

    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: {
            getItem: k => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); },
            removeItem: k => { removed.push(k); delete store[k]; }
        },
        sessionStorage: {
            getItem: k => (k in session ? session[k] : null),
            setItem: (k, v) => { session[k] = String(v); },
            removeItem: k => { delete session[k]; }
        },
        navigator: { userAgent: 'Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)', language: 'en' },
        innerWidth: 1024, innerHeight: 768,
        requestAnimationFrame: () => 0,
        setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
        fetch: () => new Promise(() => {}),
        getComputedStyle: () => ({ backgroundColor: 'rgb(16, 16, 16)', display: 'block',
                                   getPropertyValue: () => '' }),
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        history: { pushState() {}, replaceState() {} },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector: () => null, querySelectorAll: () => [],
            getElementById: id => byId[id] || null,
            elementFromPoint: () => null,
            createElement: tag => makeEl(tag),
            head: makeEl('head'),
            body: makeEl('body'),
            documentElement: Object.assign(makeEl('html'), { appendChild: n => attach(header, n) }),
            contains: n => !!(n && n.__attached),
            activeElement: null
        },
        ApiClient: {
            getCurrentUserId: () => opts.userId === undefined ? MASTER : opts.userId,
            getUrl: u => '/' + u,
            serverAddress: () => 'https://jf.local',
            accessToken: () => (opts.token === undefined ? 'token' : opts.token),
            setAuthenticationInfo() {},
            getPluginConfiguration: () => new Promise(() => {})
        },
        Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map, JSON
    };
    sandbox.document.body.__attached = true;
    sandbox.addEventListener = function () {};
    sandbox.removeEventListener = function () {};
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    const href = opts.href || 'https://jf.local/web/#/home.html';
    sandbox.location = {
        href: href,
        hash: opts.hash === undefined ? '#/home.html' : opts.hash,
        pathname: opts.pathname || '/web/'
    };

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    const p = sandbox.__PP;

    // Out of scope: the network, and the search for a header to stand in. The header is
    // handed over directly, so what is under test is the decision to show the button and
    // not the long search that follows it (headerinject.js owns that).
    p.loadSwitcherPrefs = () => {};
    p.loadLibraryArtwork = () => {};
    p.validateSessionState = () => {};
    p.interceptHomeAndShowProfiles = () => {};
    p._findHeaderContainer = () => header;
    p._headerContainer = header;
    p._headerAnchorWaitFrom = undefined;
    p._panicLinkAvailable = false;
    p._libraryArtLoaded = true;
    p._i18nReady = Promise.resolve();
    p.showAlert = (title, body) => { alerts.push(String(body)); };
    if (opts.prefs !== undefined) p._switcherPrefs = opts.prefs;

    const go = (hash, pathname) => {
        sandbox.location.hash = hash;
        sandbox.location.pathname = pathname || '/web/';
        sandbox.location.href = 'https://jf.local' + sandbox.location.pathname + hash;
    };
    const bubble = () => {
        const b = byId['profiles-floating-bubble'];
        return b && b.__attached ? b : null;
    };
    return { p, sandbox, store, session, removed, alerts, byId, go, bubble, makeEl };
}

const GATE_OFF = { askOnStartup: false, location: 'button' };
const GATE_ON = { askOnStartup: true, location: 'button' };
const MENU = { askOnStartup: false, location: 'menu' };
const masterState = id => JSON.stringify({ masterUserId: id });

/* ── 1. The button with the startup gate off ───────────────────────────────────── */

console.log('\n── With "Ask who\'s watching" off, there is still a way to switch ─');

{
    const e = build({ prefs: GATE_OFF });
    e.p.checkRoute();
    ok('gate off, signed in, no profile session: the button is on the home screen',
       !!e.bubble());

    e.go('#/movies.html');
    e.p.checkRoute();
    ok('and stays there on any other page', !!e.bubble());

    // The marker is never written by showing it: what that marker means is "somebody
    // chose a profile through the gate", and nobody did.
    ok('showing it does not pretend a profile was chosen',
       !e.p.isProfileSessionActive());

    // Pressed as built: the handler was bound when the button was made.
    let clicked = 0;
    e.p.handleBubbleClick = () => { clicked++; };
    if (e.bubble()) e.bubble().fire('click');
    ok('and pressing it opens the switcher (' + clicked + ' call)', clicked === 1);
}

{
    // The guard against "always show it": with the gate on, the gate is what provides the
    // session, and a button standing beside a gate that is about to cover the page is two
    // ways in where there should be one.
    const e = build({ prefs: GATE_ON });
    e.go('#/movies.html');
    e.p.checkRoute();
    ok('gate ON and no profile session: still no button (the gate provides one)', !e.bubble());
}

{
    const e = build({ prefs: GATE_OFF, token: '' });
    e.p.checkRoute();
    ok('gate off but nobody signed in: no button', !e.bubble());
}

{
    // Preferences not learned yet. Guessing would show a button the gate is about to
    // cover, on every load, for everyone with the gate on.
    const e = build({ prefs: null });
    e.p.getSwitcherPrefs = () => null;
    e.go('#/movies.html');
    e.p.checkRoute();
    ok('preferences not known yet: no button', !e.bubble());
}

{
    const e = build({ prefs: MENU });
    e.p.checkRoute();
    ok('menu mode: no button, the switcher is in the menu (issue #14)', !e.bubble());
}

{
    // A device keeping its own answer outranks the account's, and this must agree with
    // the gate: a television told to ask must not also grow a button.
    const e = build({
        prefs: GATE_OFF,
        storage: {
            'jellyfin_profiles_device_gate': JSON.stringify({
                [MASTER.replace(/-/g, '')]: { synced: false, askOnStartup: true }
            })
        }
    });
    e.go('#/movies.html');
    ok('(the device\'s own answer is in force)', e.p.shouldAskOnStartup() === true);
    e.p.checkRoute();
    ok('a device that asks on startup gets no button, whatever the account says', !e.bubble());
}

{
    // With a profile active nothing changed: the button stands with the gate on or off.
    const e = build({ prefs: GATE_ON });
    e.p.isProfileSessionActive = () => true;
    e.go('#/movies.html');
    e.p.checkRoute();
    ok('a profile in use still has its button with the gate on', !!e.bubble());
}

/* ── 2. The sign-in screen belongs to nobody ───────────────────────────────────── */

console.log('\n── Signing out without a reload forgets the previous account ───');

// Every spelling validateSessionState recognises. 10.11 moved routes into the path.
const SIGN_IN = [
    ['#/login.html', '/web/'],
    ['#/selectserver.html', '/web/'],
    ['', '/web/login'],
    ['', '/web/selectserver']
];

SIGN_IN.forEach(([hash, pathname]) => {
    const where = hash || pathname;
    // The iOS app's state after a sign-out that fired no event and did not reload: the
    // previous account's master, its profile session, its prefetched list, its bubble,
    // and an ApiClient that still answers with a token.
    const e = build({ prefs: GATE_OFF, storage: { [MASTER_KEY]: masterState(OTHER_MASTER) } });
    e.p.isProfileSessionActive = function () {
        return !!this._sessionGet(this.config.activeSessionKey);
    };
    e.p._sessionSet(e.p.config.activeSessionKey, 'old-profile-token');
    e.p.cachedProfiles = [{ profileUserId: OTHER_MASTER, isMaster: true }];
    e.p.checkRoute();
    ok(where + ': (the stale button was standing before sign-out)', !!e.bubble());

    e.go(hash, pathname);
    e.p.checkRoute();
    ok(where + ': classified as a sign-in screen (' + e.p._lastRouteType + ')',
       e.p._lastRouteType === 'signin');
    ok(where + ': the previous account\'s master is forgotten', !(MASTER_KEY in e.store));
    ok(where + ': and its profile session', !e.p._sessionGet(e.p.config.activeSessionKey));
    ok(where + ': and its prefetched list, which would be the next account\'s first grid',
       !(e.p.cachedProfiles && e.p.cachedProfiles.length));
    ok(where + ': and the button is gone, though the client still holds a token', !e.bubble());
});

{
    const e = build({ prefs: GATE_OFF, storage: { [MASTER_KEY]: masterState(OTHER_MASTER) } });
    e.go('#/login.html');
    e.p.checkRoute();
    const after = e.removed.length;
    for (let i = 0; i < 20; i++) e.p.checkRoute();
    ok('it is forgotten once, not on every tick of the route poll ('
       + (e.removed.length - after) + ' further removals)', e.removed.length === after);

    // Back in, a list fetched, out again: the second sign-out must forget too.
    e.go('#/home.html');
    e.p.checkRoute();
    e.store[MASTER_KEY] = masterState(MASTER);
    e.go('#/login.html');
    e.p.checkRoute();
    ok('and again after the next sign-out', !(MASTER_KEY in e.store));
}

{
    // Menu mode returns early for the button, and must not return before this.
    const e = build({ prefs: MENU, storage: { [MASTER_KEY]: masterState(OTHER_MASTER) } });
    e.go('#/login.html');
    e.p.checkRoute();
    ok('menu mode forgets the previous account too', !(MASTER_KEY in e.store));
}

{
    // Nothing that merely contains "login" in a query string or an item name should be
    // taken for the sign-in screen — but the plugin's own test is a substring test, and
    // this harness pins that the bubble and validateSessionState agree on it rather than
    // pretending it is precise. Ordinary pages must not be.
    const e = build({ prefs: GATE_OFF, storage: { [MASTER_KEY]: masterState(MASTER) } });
    ['#/home.html', '#/movies.html', '#/details?id=abc', '#/mypreferencesmenu.html']
        .forEach(h => { e.go(h); e.p.checkRoute(); });
    ok('ordinary pages leave the master alone', (MASTER_KEY in e.store));
}

/* ── 3. The list says whose household this is ──────────────────────────────────── */

console.log('\n── A missing master id does not turn your house into a guest\'s ─');

const LIST = [
    { profileUserId: MASTER, profileName: 'Kindred', isMaster: true, isBonfire: false,
      masterUserId: MASTER, requiresPin: false, maxSubProfiles: 5, avatarColor: '#335577' },
    { profileUserId: KID, profileName: 'Robin', isMaster: false, isBonfire: false,
      masterUserId: MASTER, requiresPin: false, avatarColor: '#557733' },
    // A linked household. It must stay somebody else's.
    { profileUserId: OTHER_MASTER, profileName: 'Ash', isMaster: true, isBonfire: true,
      masterUserId: OTHER_MASTER, requiresPin: false, avatarColor: '#773355' },
    { profileUserId: OTHER_KID, profileName: 'Wren', isMaster: false, isBonfire: true,
      masterUserId: OTHER_MASTER, requiresPin: false, avatarColor: '#775533' }
];

function renderGrid(e) {
    const overlay = e.makeEl('div');
    overlay.id = 'profiles-gate-overlay';
    overlay.__attached = true;
    e.byId['profiles-gate-overlay'] = overlay;
    e.p.renderOverlayContent(overlay, LIST);
    return overlay.innerHTML;
}

[['no stored master (the iPad report)', {}],
 ['a stored master from another account', { [MASTER_KEY]: masterState(OTHER_MASTER) }]]
    .forEach(([label, storage]) => {
        const e = build({ prefs: GATE_OFF, storage: storage });
        let html = '';
        let threw = null;
        try { html = renderGrid(e); } catch (err) { threw = err; }
        ok(label + ': the grid renders', !threw, threw && threw.stack.split('\n').slice(0, 3).join(' | '));
        ok(label + ': your own profiles are "Your Bonfire"', html.indexOf('Your Bonfire') !== -1);
        ok(label + ': and not "Kindred\'s Bonfire"', html.indexOf('Kindred&#39;s Bonfire') === -1
           && html.indexOf("Kindred's Bonfire") === -1);
        ok(label + ': a linked household is still somebody else\'s ("Ash\'s Bonfire")',
           html.indexOf('Ash&#39;s Bonfire') !== -1 || html.indexOf("Ash's Bonfire") !== -1);
        ok(label + ': the stored master is now this household\'s',
           e.store[MASTER_KEY] && JSON.parse(e.store[MASTER_KEY]).masterUserId === MASTER);
    });

{
    // A linked Bonfire's master is isMaster too. Only the one that is not a Bonfire is
    // this household's, and that is the only one that may be stored — a list holding
    // nothing but linked households must store nothing.
    const e = build({ prefs: GATE_OFF });
    const overlay = e.makeEl('div');
    overlay.__attached = true;
    try { e.p.renderOverlayContent(overlay, LIST.filter(p => p.isBonfire)); } catch (err) { /* reported below */ }
    ok('a linked household\'s master is never stored as yours', !(MASTER_KEY in e.store));
}

{
    // The manage check reads the stored id at the moment of the press, which can be well
    // after the grid was drawn. On the iPad it was gone by then.
    const e = build({ prefs: GATE_OFF });
    renderGrid(e);
    delete e.store[MASTER_KEY];
    const btn = e.byId['profiles-toggle-manage-btn'];
    ok('(the grid has a Manage Profiles button to press)', !!btn);
    if (btn) btn.fire('click');
    const refused = e.alerts.filter(a => /first, then choose Manage Profiles/.test(a));
    ok('the master pressing Manage Profiles is not told to open the master first'
       + (refused.length ? ' — "' + refused[0] + '"' : ''), refused.length === 0);
    ok('and is in manage mode', e.p.isManageMode === true);
}

{
    // The other direction: a profile pressing it is still sent to the master, because the
    // list names the master and the session is not it.
    const e = build({ prefs: GATE_OFF, userId: KID });
    renderGrid(e);
    const btn = e.byId['profiles-toggle-manage-btn'];
    if (btn) btn.fire('click');
    ok('a profile pressing Manage Profiles is still told to open the master',
       e.alerts.some(a => /Open Kindred first/.test(a)) && e.p.isManageMode !== true);
}

/* ── 4. The header avatar is drawn at its size ─────────────────────────────────── */

console.log('\n── The header avatar is not scaled by jellyfin-web\'s icon rule ──');

// jellyfin-web, src/elements/emby-button/emby-button.scss — identical in release-10.11.z
// and master, read 2026-10-13:
//
//     .paper-icon-button-light > div { max-height: 100%; transform: scale(1.8); ... }
//
// It is not in our stylesheet, so it is modelled here as the competitor. Its sheet loads
// before ours, so on a specificity tie ours wins; it is given index -1 for that.
const UPSTREAM = {
    selector: '.paper-icon-button-light > div',
    props: { 'transform': 'scale(1.8)', 'max-height': '100%' },
    index: -1
};

const rawCss = fs.readFileSync(CSS_PATH, 'utf8');
const bare = rawCss.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));
function rules() {
    const out = [];
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(bare)) !== null) {
        const sel = m[1].trim();
        if (!sel || sel.startsWith('@')) continue;
        sel.split(',').forEach(s => {
            s = s.trim().replace(/\s+/g, ' ');
            if (s) out.push({ selector: s, body: m[2], index: m.index });
        });
    }
    return out;
}
function specificity(sel) {
    const ids = (sel.match(/#[\w-]+/g) || []).length;
    const classes = (sel.match(/\.[\w-]+|\[[^\]]*\]|:(?!:)[\w-]+/g) || []).length;
    const types = (sel.replace(/#[\w-]+|\.[\w-]+|\[[^\]]*\]|::?[\w-]+/g, ' ')
        .match(/(^|[\s>+~])[a-z][\w-]*/gi) || []).length;
    return [ids, classes, types];
}
function beats(a, b) {
    const x = specificity(a.selector), y = specificity(b.selector);
    for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] > y[i];
    return a.index > b.index;
}
function valueOf(body, prop) {
    const m = new RegExp('(?:^|[;{\\s])' + prop + '\\s*:\\s*([^;]+)').exec(body);
    return m ? m[1].replace(/!important/, '').trim() : null;
}

// The two elements, as _buildHeaderBubble makes them, in both layouts.
const AVATAR = { tag: 'div', id: '', classes: ['profiles-header-avatar'] };
const BUTTONS = {
    'legacy header': { tag: 'button', id: 'profiles-floating-bubble',
                       classes: ['paper-icon-button-light', 'headerButton', 'focusable'] },
    'modern toolbar': { tag: 'button', id: 'profiles-floating-bubble',
                        classes: ['paper-icon-button-light', 'headerButton', 'focusable',
                                  'jpf-modern-toolbar-btn'] }
};
function compoundMatches(compound, node) {
    if (/:/.test(compound)) return false;              // :hover and friends are other states
    const tag = (compound.match(/^[a-z][\w-]*/i) || [])[0];
    if (tag && tag.toLowerCase() !== node.tag) return false;
    const id = (compound.match(/#([\w-]+)/) || [])[1];
    if (id && id !== node.id) return false;
    const cls = (compound.match(/\.[\w-]+/g) || []).map(c => c.slice(1));
    if (compound.indexOf('[') !== -1) return false;
    return cls.every(c => node.classes.indexOf(c) !== -1);
}
// Avatar, then its button, then "some ancestor" — which, as the worst case, matches any
// compound naming no id. A rule scoped to a root class (.jpf-tv, say) can apply.
function selectorMatchesAvatar(sel, button) {
    const parts = sel.split(/\s*([>\s])\s*/).filter(s => s !== '');
    const compounds = [], combs = [];
    parts.forEach((t, i) => { if (i % 2 === 0) compounds.push(t); else combs.push(t); });
    if (!compoundMatches(compounds[compounds.length - 1], AVATAR)) return false;
    let at = 0;  // 0 = avatar, 1 = button, 2 = above
    for (let i = compounds.length - 2; i >= 0; i--) {
        const comb = combs[i];
        const c = compounds[i];
        if (at === 0) {
            if (compoundMatches(c, button)) { at = 1; continue; }
            if (comb === '>') return false;
            if (/#/.test(c)) return false;
            at = 2; continue;
        }
        if (/#/.test(c)) return false;
        at = 2;
    }
    return true;
}

const ours = rules();
Object.keys(BUTTONS).forEach(layout => {
    const button = BUTTONS[layout];
    ok(layout + ': (the upstream rule applies to the avatar at all)',
       selectorMatchesAvatar(UPSTREAM.selector, button));
    Object.keys(UPSTREAM.props).forEach(prop => {
        let winner = { selector: UPSTREAM.selector, index: UPSTREAM.index, value: UPSTREAM.props[prop] };
        ours.forEach(r => {
            if (!selectorMatchesAvatar(r.selector, button)) return;
            const v = valueOf(r.body, prop);
            if (v === null) return;
            if (beats(r, winner)) winner = { selector: r.selector, index: r.index, value: v };
        });
        ok(layout + ': ' + prop + ' resolves to "none", not jellyfin-web\'s "'
           + UPSTREAM.props[prop] + '" (won by ' + winner.selector + ')',
           winner.value === 'none');
    });
});

// An inline style outranks every rule, so if _buildHeaderBubble ever wrote either property
// inline the cascade above would be beside the point.
const builder = src.slice(src.indexOf('_buildHeaderBubble: function'),
                          src.indexOf('_buildHeaderBubble: function') + 2500);
const inline = (builder.match(/class="profiles-header-avatar" style="([^"]*)/) || [])[1] || '';
ok('(the avatar\'s inline style was found)', inline.length > 0);
ok('and sets neither transform nor max-height', !/transform|max-height/.test(inline));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
