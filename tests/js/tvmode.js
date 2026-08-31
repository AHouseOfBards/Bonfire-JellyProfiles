/*
 * P4: television mode — the `jpf-tv` and `jpf-no-flex-gap` classes, and the CSS
 * baseline the two sets in the open issues can actually render.
 *
 * Run against the shipped 1.5.7 file and it fails, but be honest about *how*: the
 * detection assertions fail structurally, because applyDeviceClasses does not exist
 * there at all. That is a missing method, not a bisect. The CSS assertions further
 * down are the real discrimination — they run against the same extracted stylesheet
 * in both builds and fail on content.
 *
 *   node tests/js/tvmode.js                 # the working tree
 *   node tests/js/tvmode.js path/to/old.js  # 1.5.7, expected to fail
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

const src = fs.readFileSync(L.profilesPath(), 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

/* ── a DOM stub with a real classList and a measurable flex-gap probe ────────── */

function makeClassList(initial) {
    const set = new Set(initial || []);
    const api = {
        add: c => set.add(c),
        remove: c => set.delete(c),
        contains: c => set.has(c),
        toggle: (c, force) => {
            const on = force === undefined ? !set.has(c) : !!force;
            if (on) set.add(c); else set.delete(c);
            return on;
        },
        __all: () => [...set],
    };
    return api;
}

/*
 * The probe measures scrollHeight on a two-child column flex box with row-gap:1px.
 * `flexGap` decides what this fake browser reports, so both branches are exercised.
 *
 * routetick.js was measuring a state no browser is ever in because its stub was too
 * forgiving, so this one refuses to answer unless the code built the probe properly:
 * scrollHeight is only non-zero when the element really was given a column flex
 * display with a row-gap and really was put in the document.
 */
function makeDoc(opts) {
    const body = {
        children: [],
        classList: makeClassList(),
        appendChild(el) { el.parentNode = body; body.children.push(el); return el; },
        removeChild(el) {
            body.children = body.children.filter(c => c !== el);
            el.parentNode = null;
            return el;
        },
    };
    const root = { style: {}, classList: makeClassList(opts.rootClasses) };
    return {
        __body: body,
        body: opts.noBody ? null : body,
        documentElement: root,
        head: { appendChild() {} },
        addEventListener() {}, removeEventListener() {},
        querySelector: () => null, querySelectorAll: () => [],
        getElementById: () => null,
        createElement() {
            const el = {
                style: {
                    cssText: '',
                },
                children: [],
                parentNode: null,
                classList: makeClassList(),
                appendChild(c) { el.children.push(c); return c; },
            };
            Object.defineProperty(el, 'scrollHeight', {
                get() {
                    const css = String(el.style.cssText || '');
                    const isColumnFlex = /display\s*:\s*flex/.test(css)
                        && /flex-direction\s*:\s*column/.test(css);
                    const gapPx = /row-gap\s*:\s*(\d+)px/.exec(css);
                    if (!isColumnFlex || !gapPx) return 0;
                    if (el.parentNode !== body) return 0;        // never attached
                    if (el.children.length < 2) return 0;        // nothing to space
                    return opts.flexGap ? (el.children.length - 1) * Number(gapPx[1]) : 0;
                },
            });
            return el;
        },
    };
}

function build(opts) {
    const doc = makeDoc(opts);
    const observers = [];
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: opts.ua || 'Mozilla/5.0' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
        document: doc,
        MutationObserver: function (cb) {
            this.cb = cb;
            this.observe = (target, init) => observers.push({ target, init, cb });
            this.disconnect = () => {};
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set,
        Uint8ClampedArray,
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    return { P: sandbox.__PROFILES, doc, observers, sandbox };
}

/* Real strings, not invented ones. */
const UA = {
    webos5: 'Mozilla/5.0 (Web0S; Linux/SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) '
        + 'Chrome/68.0.3440.106 Safari/537.36 WebAppManager',
    tizen6: 'Mozilla/5.0 (SMART-TV; LINUX; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) '
        + '76.0.3809.146/6.0 TV Safari/537.36',
    desktop: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) '
        + 'Chrome/120.0.0.0 Safari/537.36',
    mac: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) '
        + 'Version/17.0 Safari/605.1.15',
    android: 'Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) '
        + 'Chrome/119.0.0.0 Mobile Safari/537.36',
    oculus: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) '
        + 'OculusBrowser/23.0 SamsungBrowser/4.0 Chrome/104.0.0.0 VR Safari/537.36',
};

console.log();
console.log('── the user-agent heuristic, copied from upstream isTv() ──────');

const base = build({ flexGap: true });
const P = base.P;

ok('_uaLooksLikeTv exists', typeof P._uaLooksLikeTv === 'function');
if (typeof P._uaLooksLikeTv === 'function') {
    ok('webOS 5 (LG CX) is a television', P._uaLooksLikeTv(UA.webos5) === true);
    ok('Tizen 6.0 is a television', P._uaLooksLikeTv(UA.tizen6) === true);
    ok('desktop Chrome is not', P._uaLooksLikeTv(UA.desktop) === false);
    ok('Safari on a Mac is not', P._uaLooksLikeTv(UA.mac) === false);
    ok('an Android phone is not', P._uaLooksLikeTv(UA.android) === false);
    // Upstream's one explicit exclusion: the Oculus browser carries samsungbrowser.
    ok('the Oculus browser is excluded despite samsungbrowser',
        P._uaLooksLikeTv(UA.oculus) === false);
    // web0s is spelt with a zero upstream because that is what LG sends. Spelling it
    // with the letter O here would miss every webOS set and fail silently.
    ok('web0s is matched with a zero, not the letter O',
        P._uaLooksLikeTv('x web0s y') === true && P._uaLooksLikeTv('x webos y') === false);
    ok('an empty or missing UA is not a television',
        P._uaLooksLikeTv('') === false && P._uaLooksLikeTv(undefined) === false);
}

console.log();
console.log('── jpf-tv, and why the class beats the user agent ─────────────');

function classesFor(opts) {
    const env = build(opts);
    if (typeof env.P.applyDeviceClasses !== 'function') return null;
    env.P.applyDeviceClasses();
    return { env, has: c => env.doc.documentElement.classList.contains(c) };
}

ok('applyDeviceClasses exists', typeof P.applyDeviceClasses === 'function');

if (typeof P.applyDeviceClasses === 'function') {
    let r = classesFor({ flexGap: true, ua: UA.desktop, rootClasses: ['layout-tv'] });
    ok('layout-tv on <html> means TV even on a desktop user agent', r.has('jpf-tv'));

    r = classesFor({ flexGap: true, ua: UA.webos5, rootClasses: ['layout-desktop'] });
    ok('a forced desktop layout beats a television user agent', !r.has('jpf-tv'));

    r = classesFor({ flexGap: true, ua: UA.webos5, rootClasses: ['layout-mobile'] });
    ok('a forced mobile layout beats a television user agent', !r.has('jpf-tv'));

    r = classesFor({ flexGap: true, ua: UA.webos5, rootClasses: [] });
    ok('with no layout class yet, webOS falls back to the user agent', r.has('jpf-tv'));

    r = classesFor({ flexGap: true, ua: UA.tizen6, rootClasses: [] });
    ok('with no layout class yet, Tizen falls back to the user agent', r.has('jpf-tv'));

    r = classesFor({ flexGap: true, ua: UA.desktop, rootClasses: [] });
    ok('desktop Chrome with no layout class is not a television', !r.has('jpf-tv'));

    console.log();
    console.log('── it follows a layout change instead of polling for one ──────');

    r = classesFor({ flexGap: true, ua: UA.desktop, rootClasses: [] });
    ok('an observer is registered', r.env.observers.length === 1);
    if (r.env.observers.length === 1) {
        const o = r.env.observers[0];
        ok('it watches <html>', o.target === r.env.doc.documentElement);
        // Watching every attribute on the root would wake on Jellyfin's own writes.
        ok('and only the class attribute',
            !!o.init && o.init.attributes === true
            && Array.isArray(o.init.attributeFilter)
            && o.init.attributeFilter.length === 1
            && o.init.attributeFilter[0] === 'class');

        ok('not TV to begin with', !r.has('jpf-tv'));
        r.env.doc.documentElement.classList.add('layout-tv');
        o.cb();
        ok('switching to TV mode adds the class without a reload', r.has('jpf-tv'));
        r.env.doc.documentElement.classList.remove('layout-tv');
        o.cb();
        ok('and switching back removes it', !r.has('jpf-tv'));
    }
}

console.log();
console.log('── flex gap, measured rather than asked ───────────────────────');

ok('_supportsFlexGap exists', typeof P._supportsFlexGap === 'function');

if (typeof P.applyDeviceClasses === 'function') {
    let r = classesFor({ flexGap: false, ua: UA.webos5, rootClasses: [] });
    ok('a browser that ignores flex gap gets jpf-no-flex-gap', r.has('jpf-no-flex-gap'));

    r = classesFor({ flexGap: true, ua: UA.desktop, rootClasses: [] });
    ok('a browser that honours it does not', !r.has('jpf-no-flex-gap'));

    // The probe must be cleaned up. A hidden element left in <body> on every page is
    // exactly the kind of thing that is invisible until someone counts nodes.
    r = classesFor({ flexGap: false, ua: UA.webos5, rootClasses: [] });
    ok('the probe is removed from the document again',
        r.env.doc.__body.children.length === 0);

    // Running before <body> exists must not throw, and must not claim a modern browser
    // is broken -- assuming "no gap" there would put fallback margins on every desktop.
    const early = build({ flexGap: true, ua: UA.desktop, rootClasses: [], noBody: true });
    let threw = false;
    try { early.P.applyDeviceClasses(); } catch (e) { threw = true; }
    ok('running before <body> exists does not throw', !threw);
    ok('and does not assume the browser is broken',
        !early.doc.documentElement.classList.contains('jpf-no-flex-gap'));
}

console.log();
console.log('── init wires it up before the stylesheet ─────────────────────');

const initBody = /init:\s*function\s*\(\)\s*\{[\s\S]*?\n        \},/.exec(src);
ok('init() body located', !!initBody);
if (initBody) {
    // Match the _step() calls, not the bare names. The first draft of this used
    // indexOf('injectStyles') and found the word inside the comment sitting above the
    // applyDeviceClasses call -- so it reported the ordering backwards while the code
    // was right. Comments are prose; do not order code by where prose mentions it.
    const b = initBody[0];
    const atDevice = b.indexOf("_step('applyDeviceClasses'");
    const atStyles = b.indexOf("_step('injectStyles'");
    ok('init calls applyDeviceClasses', atDevice !== -1);
    // After injectStyles the first paint happens without the TV rules and the viewer
    // sees the desktop sizing flash by.
    ok('and calls it before injectStyles', atDevice !== -1 && atStyles !== -1 && atDevice < atStyles);
    ok('inside a _step, so a failure cannot take the rest of init with it',
        /_step\('applyDeviceClasses'/.test(b));
}

/* ── the stylesheet ─────────────────────────────────────────────────────────── */

const css = L.extractCss(src);

console.log();
console.log('── the CSS baseline for Chromium 68 ───────────────────────────');

// Blank comments so a feature named only in prose is not mistaken for a use.
const bare = css.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));

function ruleFor(sel) {
    const re = new RegExp(sel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*\\{([^}]*)\\}');
    const m = re.exec(bare);
    return m ? m[1] : null;
}

ok('the sheet carries TV rules at all', /\.jpf-tv\b/.test(bare));
ok('and flex-gap fallback rules', /\.jpf-no-flex-gap\b/.test(bare));

console.log();
console.log('── the television sizing actually wins ────────────────────────');

/* Resolve the cascade, do not string-match. A rule has shipped here before that was
 * present and correct and simply outranked by a later one of equal specificity. For
 * each override, require BOTH that .jpf-tv adds specificity AND that it comes later
 * in the sheet, which is the only pair of facts that settles it. */
function ruleAt(selector) {
    const re = new RegExp('(^|[};])\\s*' + selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
        + '\\s*\\{([^}]*)\\}');
    const m = re.exec(bare);
    return m ? { body: m[2], index: m.index } : null;
}

function overrides(tvSelector, baseSelector, prop) {
    const tv = ruleAt(tvSelector);
    const base = ruleAt(baseSelector);
    if (!tv) return ok(tvSelector + ' exists', false);
    if (!base) return ok(baseSelector + ' exists to be overridden', false);
    const has = new RegExp('(^|[;{\\s])' + prop + '\\s*:').test(tv.body);
    ok(tvSelector + ' sets ' + prop, has);
    ok(tvSelector + ' comes after ' + baseSelector + ' in the sheet', tv.index > base.index);
}

// P4-2: cards sized for a screen across a room, in plain px because the TV
// baselines drop clamp().
overrides('.jpf-tv .profile-card', '.profile-card', 'width');
const tvCard = ruleAt('.jpf-tv .profile-card');
ok('the TV card is around 300px', !!tvCard && /width:\s*300px/.test(tvCard.body));
ok('and is not itself written with clamp()',
    !!tvCard && !/clamp\s*\(/.test(tvCard.body));

// P4-3 / P4-5: bigger text and looser spacing.
const tvTokens = ruleAt('.jpf-tv, .jpf-tv body');
ok('the gap tokens are raised for TV', !!tvTokens && /--jpf-gap\s*:/.test(tvTokens.body));
// The base tokens are declared on the selector list ":root, body". A declaration on
// body beats one on html for everything inside it, so a TV override written on
// .jpf-tv alone would parse, look right, and change nothing at all. Check the body
// half is really in the selector rather than trusting the lookup key above.
const tokenSelector = /([^{}]*)\{[^}]*--jpf-gap\s*:\s*20px/.exec(bare);
ok('the TV token rule names body as well as the root element',
    !!tokenSelector && /\.jpf-tv\s+body/.test(tokenSelector[1]));
overrides('.jpf-tv .profiles-title', '.profiles-title', 'font-size');
overrides('.jpf-tv .profile-name', '.profile-name', 'font-size');

// P4-4: a focus ring findable from three metres.
const tvFocus = ruleAt('.jpf-tv .profile-section :focus-visible');
ok('TV focus ring is thicker', !!tvFocus && /outline:\s*4px/.test(tvFocus.body));
// And it must be alone: one unparseable selector invalidates a whole comma list,
// which would delete the ring on exactly the browsers it is for.
ok('the TV :focus-visible rule is not folded into a selector list',
    bare.indexOf('.jpf-tv .profile-section :focus-visible,') === -1);

console.log();
console.log('── Save stays reachable without a scrollbar (P4-14) ───────────');

const sticky = ruleAt('.create-profile-container > .profile-dialog-actions');
ok('the dialog actions are sticky', !!sticky && /position:\s*sticky/.test(sticky.body));
ok('pinned to the bottom', !!sticky && /bottom:\s*0/.test(sticky.body));
// A translucent bar lets the form scroll through the buttons.
ok('with an opaque backdrop',
    !!sticky && /background:\s*#[0-9a-fA-F]{3,8}\s*;/.test(sticky.body));
// Scoped to the scrolling container: sticky resolves against the nearest scrolling
// ancestor, and this row only ever renders inside that one.
const scroller = ruleAt('.create-profile-container');
ok('and its container really is the scrolling one',
    !!scroller && /overflow-y:\s*auto/.test(scroller.body));

console.log();
console.log('  totals: ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
