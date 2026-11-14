/*
 * Profile accounts stay off Jellyfin's Dashboard -> Users page, and an administrator can
 * still reach one from Bonfire's own Households tab. A profile account is Bonfire's, not
 * the administrator's to list beside real people; main accounts and accounts outside
 * Bonfire still appear.
 *
 * What the page renders is read from jellyfin-web, not remembered:
 *
 *   release-10.11.z  src/apps/dashboard/routes/users/index.tsx   (div.localUsers)
 *                    src/components/dashboard/users/UserCardBox.tsx
 *                      <div data-userid=ID class="card ..."> ... <a href="#/dashboard/users/profile?userId=ID">
 *   master (12.0)    the same two files; the card link is "#/dashboard/users/ID/profile"
 *                    (routes/_asyncRoutes.ts: 'users/:userId/:tab'). The card div keeps
 *                    data-userid, which is why the rule keys on that and not on the link.
 *
 * The mechanism is a stylesheet rule per profile id: no observer and no DOM query on the
 * 500 ms poll. The list comes from /Profiles/admin/mappings, which the server answers only
 * for an administrator, so for anyone else nothing is fetched or written and the ids
 * cannot leak.
 *
 * The CSS is judged by matching its selectors against the card markup, not by looking for
 * a string: a rule that is present and matches nothing proves nothing.
 *
 *   node tests/js/hideprofiles.js [path/to/profiles.js]
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; return; }
    fails.push(name);
    console.log('  FAIL  ' + name + (detail ? '  - ' + detail : ''));
}

let src = fs.readFileSync(L.profilesPath(), 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) { console.error('single init() call not found'); process.exit(1); }
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

const MAIN = '11111111-1111-1111-1111-111111111111';
const PROFILE_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const PROFILE_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const OTHER = '33333333-3333-3333-3333-333333333333';
const n = id => id.replace(/-/g, '');

// ── a small selector matcher: compound selectors and descendant combinators ──────
function matchesCompound(el, comp) {
    const re = /(\.[\w-]+)|(\[[^\]]+\])|(#[\w-]+)|([a-zA-Z][\w-]*)/g;
    let m, any = false;
    while ((m = re.exec(comp)) !== null) {
        any = true;
        if (m[1]) { if ((' ' + (el.attrs['class'] || '') + ' ').indexOf(' ' + m[1].slice(1) + ' ') < 0) return false; }
        else if (m[2]) {
            const a = /^\[\s*([\w-]+)\s*(?:=\s*(?:"([^"]*)"|'([^']*)'|([^\s\]]+))\s*(i)?)?\s*\]$/.exec(m[2]);
            if (!a) return false;
            const has = Object.prototype.hasOwnProperty.call(el.attrs, a[1]);
            if (!has) return false;
            const want = a[2] !== undefined ? a[2] : a[3] !== undefined ? a[3] : a[4];
            if (want !== undefined) {
                const got = String(el.attrs[a[1]]);
                if (a[5] ? got.toLowerCase() !== want.toLowerCase() : got !== want) return false;
            }
        }
        else if (m[3]) { if (el.attrs.id !== m[3].slice(1)) return false; }
        else if (m[4]) { if (el.tag !== m[4].toLowerCase()) return false; }
    }
    return any;
}
function matches(el, selector) {
    const parts = selector.trim().split(/\s+(?![^\[]*\])/);
    let cur = el, i = parts.length - 1;
    if (!matchesCompound(cur, parts[i])) return false;
    i--;
    cur = cur.parent;
    while (i >= 0 && cur) {
        if (matchesCompound(cur, parts[i])) i--;
        cur = cur.parent;
    }
    return i < 0;
}
// display:none wins when any matching rule declares it. All the rules in play are ours.
function hiddenByCss(el, cssTexts) {
    for (const css of cssTexts) {
        const re = /([^{}]+)\{([^{}]*)\}/g;
        let m;
        while ((m = re.exec(css)) !== null) {
            if (/^\s*@/.test(m[1]) || !/display\s*:\s*none/.test(m[2])) continue;
            const sels = m[1].split(',');
            for (const s of sels) if (matches(el, s)) return true;
        }
    }
    return false;
}

function node(tag, attrs, parent) {
    const el = { tag: tag, attrs: attrs || {}, parent: parent || null, children: [], style: {} };
    el.getAttribute = k => (Object.prototype.hasOwnProperty.call(el.attrs, k) ? el.attrs[k] : null);
    el.querySelectorAll = sel => {
        const out = [];
        (function walk(x) { x.children.forEach(function (c) { if (c.tag === sel) out.push(c); walk(c); }); })(el);
        return out;
    };
    if (parent) parent.children.push(el);
    return el;
}

// The users page as upstream renders it, for one jellyfin-web version.
function usersPage(version) {
    const page = node('div', { id: 'userProfilesPage', 'class': 'mainAnimatedPage type-interior userProfilesPage fullWidthContent' });
    const content = node('div', { 'class': 'content-primary' }, page);
    const list = node('div', { 'class': 'localUsers itemsContainer vertical-wrap' }, content);
    const cards = {};
    [[MAIN, n(MAIN)], [PROFILE_A, n(PROFILE_A)], [PROFILE_B, PROFILE_B], [OTHER, n(OTHER)]].forEach(function (p) {
        const card = node('div', { 'data-userid': p[1], 'data-username': 'x', 'class': 'card squareCard scalableCard squareCard-scalable' }, list);
        const box = node('div', { 'class': 'cardBox visualCardBox' }, card);
        const sc = node('div', { 'class': 'cardScalable visualCardBox-cardScalable' }, box);
        node('a', {
            'class': 'cardContent',
            href: version === '10.11'
                ? '#/dashboard/users/profile?userId=' + p[1]
                : '#/dashboard/users/' + p[1] + '/profile'
        }, sc);
        cards[p[0]] = card;
    });
    return { list: list, cards: cards, all: Object.keys(cards).map(k => cards[k]) };
}

function build(opts) {
    const styles = [];       // <style> elements appended to head
    const counts = { qs: 0, qsa: 0, fetches: 0, mappingFetches: 0 };
    const timers = [];
    const store = {};
    const page = opts.page;
    const mappings = {
        SubProfiles: [PROFILE_A, PROFILE_B].map(id => ({ ProfileUserId: id, MasterUserId: MAIN, ProfileName: 'p' })),
        MasterUsers: [{ ProfileUserId: MAIN, ProfileName: 'm' }]
    };
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: k => (k in store ? store[k] : null), setItem: (k, v) => { store[k] = v; }, removeItem: k => { delete store[k]; } },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', language: 'en' },
        innerWidth: 1920, innerHeight: 1080,
        requestAnimationFrame: fn => { timers.push(fn); return timers.length; },
        setTimeout: (fn, ms) => { timers.push(fn); return timers.length; },
        clearTimeout() {},
        setInterval: () => 1, clearInterval() {},
        MutationObserver: function () { this.observe = function () {}; this.disconnect = function () {}; },
        fetch: function (url) {
            counts.fetches++;
            if (/admin\/mappings/.test(url)) {
                counts.mappingFetches++;
                if (!opts.admin) return Promise.resolve({ ok: false, status: 403, json: () => Promise.resolve({}) });
                return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(mappings) });
            }
            return new Promise(() => {});
        },
        getComputedStyle: () => ({ backgroundColor: 'rgb(16, 16, 16)', display: 'block' }),
        history: { pushState() {}, replaceState() {} },
        JSON: JSON,
        document: {
            addEventListener() {}, removeEventListener() {},
            // The old code walked the document for cards; answer truthfully so the old build
            // is judged by what it really does.
            elementFromPoint: () => null,
            querySelector: () => { counts.qs++; return null; },
            querySelectorAll: sel => {
                counts.qsa++;
                return /\.card/.test(sel) ? page.all : [];
            },
            getElementById: id => styles.filter(s => s.id === id)[0] || null,
            createElement: tag => ({
                tag: tag, attrs: {}, style: {}, children: [], textContent: '', innerHTML: '', id: '',
                classList: { add() {}, remove() {}, contains: () => false },
                setAttribute() {}, appendChild() {}, addEventListener() {}, remove() {}
            }),
            head: { appendChild: el => { if (el.tag === 'style') styles.push(el); return el; } },
            body: { appendChild() {}, classList: { add() {}, remove() {}, contains: () => false }, style: {} },
            documentElement: { style: { removeProperty() {}, setProperty() {} }, classList: { add() {}, remove() {}, contains: () => false }, appendChild() {} },
            contains: () => true
        },
        ApiClient: {
            getCurrentUserId: () => MAIN,
            getUrl: u => '/' + u,
            serverAddress: () => 'https://jf.local',
            accessToken: () => 'token',
            setAuthenticationInfo() {}
        },
        Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    sandbox.addEventListener = function () {};
    sandbox.location = { href: 'https://jf.local/web/#/dashboard/users', hash: '#/dashboard/users', pathname: '/web/' };
    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    const plugin = sandbox.__PROFILES;
    plugin.loadSwitcherPrefs = () => {};
    plugin.loadLibraryArtwork = () => {};
    plugin.validateSessionState = () => {};
    // The floating button is out of scope here, and its fallback needs a real layout.
    plugin.evaluateFloatingBubbleVisibility = () => {};
    plugin._switcherPrefs = { askOnStartup: false, location: 'button' };
    plugin._panicLinkAvailable = false;
    plugin._libraryArtLoaded = true;
    plugin._headerAnchorWaitFrom = 0;
    return { plugin: plugin, sandbox: sandbox, styles: styles, counts: counts, page: page };
}

function settle() { return new Promise(r => setImmediate(r)).then(() => new Promise(r => setImmediate(r))); }
function cssOf(env) { return env.styles.map(s => s.textContent || ''); }
function isHidden(env, el) {
    return hiddenByCss(el, cssOf(env)) || el.style.display === 'none';
}

async function main() {
    for (const version of ['10.11', 'master']) {
        const tag = '[' + version + '] ';

        // ── an administrator on the users page ────────────────────────────────
        {
            const env = build({ admin: true, page: usersPage(version) });
            const c = env.page.cards;
            env.plugin.checkRoute();
            await settle();
            ok(tag + 'profile card (id as 32 hex, mapping dashed) is hidden', isHidden(env, c[PROFILE_A]));
            ok(tag + 'profile card (id dashed, mapping dashed) is hidden', isHidden(env, c[PROFILE_B]));
            ok(tag + 'main account card is shown', !isHidden(env, c[MAIN]));
            ok(tag + 'an account outside Bonfire is shown', !isHidden(env, c[OTHER]));

            // Steady state: 100 unchanged ticks ask nothing of the document or the server.
            env.counts.qs = env.counts.qsa = env.counts.fetches = 0;
            for (let i = 0; i < 100; i++) env.plugin.checkRoute();
            await settle();
            ok(tag + '100 unchanged ticks fetch nothing', env.counts.fetches === 0, env.counts.fetches + ' fetches');
            ok(tag + '100 unchanged ticks query no cards', env.counts.qsa === 0, env.counts.qsa + ' querySelectorAll calls');

            // React renders the cards after the route settles and again on each list reload;
            // the rule must not depend on when they appeared.
            const late = usersPage(version);
            env.page = late;
            ok(tag + 'cards rendered after the fetch are hidden too', isHidden(env, late.cards[PROFILE_A]) && !isHidden(env, late.cards[MAIN]));
        }

        // ── somebody who is not an administrator ───────────────────────────────
        {
            const env = build({ admin: false, page: usersPage(version) });
            env.plugin.checkRoute();
            await settle();
            const c = env.page.cards;
            ok(tag + 'non-admin: no stylesheet is written', env.styles.length === 0);
            ok(tag + 'non-admin: nothing is hidden', env.page.all.every(el => !isHidden(env, el)));
            ok(tag + 'non-admin: no profile id appears in the page', !cssOf(env).join('').toLowerCase().includes('aaaaaaaa'));
            ok(tag + 'non-admin: a refused request is not repeated every tick', (function () {
                env.counts.mappingFetches = 0;
                for (let i = 0; i < 20; i++) env.plugin.checkRoute();
                return env.counts.mappingFetches === 0;
            })(), '');
        }
    }

    // ── routes that are not the users list do no work ────────────────────────────
    for (const h of ['#/home.html', '#/dashboard/users/profile?userId=' + n(PROFILE_A), '#/dashboard/users/' + n(PROFILE_A) + '/profile', '#/userprofile.html?userId=x']) {
        const env = build({ admin: true, page: usersPage('10.11') });
        env.sandbox.location.hash = h;
        env.plugin.checkRoute();
        await settle();
        ok('route ' + h.slice(0, 40) + ' asks the server for nothing', env.counts.mappingFetches === 0, env.counts.mappingFetches + ' fetches');
    }

    // ── the way back to a profile's user page: Bonfire's own admin page ───────────
    const dash = L.readDashboard();
    const row = dash.slice(dash.indexOf('function subProfileRowHtml'), dash.indexOf('function standaloneFormHtml'));
    ok('Households profile row has a link to the Jellyfin user page', /<a [^>]*href="' \+ escapeHtml\(jellyfinUserPageUrl\(pId\)\)/.test(row) && /dashboard\/users\//.test(dash));
    ok('the link text goes through t()', /t\('admin\.households\.openUser'\)/.test(row));
    ok('the link text is declared in the admin English', /'admin\.households\.openUser':/.test(dash));
    ok('the link builds the 12.0 address as well as the 10.11 one',
       /users\/profile\?userId=/.test(dash) && /users\/' *\+[^;]*\+ *'\/profile/.test(dash));
    const fs2 = require('fs'), path = require('path');
    const dir = L.i18nDir();
    fs2.readdirSync(dir).filter(f => /\.json$/.test(f)).forEach(function (f) {
        const j = JSON.parse(fs2.readFileSync(path.join(dir, f), 'utf8'));
        ok(f + ' has admin.households.openUser', typeof j['admin.households.openUser'] === 'string' && j['admin.households.openUser'].length > 0);
    });

    console.log('');
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
}
main();
