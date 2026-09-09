/*
 * Where the Switch Profile button goes, and — the part that was never checked — whether
 * the container it goes into is one a person can see.
 *
 * jellyfin-web keeps its entire legacy header in the DOM and hides it whenever the modern
 * layout is active. components/AppHeader.tsx says so itself: "these components are not
 * used with the new layouts, but legacy views interact with the elements directly so they
 * need to be present in the DOM. We use display: none to hide them and prevent errors."
 * RootAppRouter renders it as <AppHeader isHidden={layoutManager.modern || isNewLayoutPath} />
 * — `isExperimentalLayout` in 10.11, `layoutManager.modern` in 12.0 — and libraryMenu.js
 * still fills that hidden .skinHeader with .headerRight, .headerButton and
 * .headerUserButton.
 *
 * So every named strategy matched, the button was inserted into a display:none container,
 * and the geometric fallback that would have found the real MUI toolbar never ran because
 * the search had already "succeeded". Reported against 1.6.0.1 with a screenshot of a
 * header with no Bonfire button in it.
 *
 * Modern is the default layout in 12.0, so this stopped being a minority path.
 *
 *     node tests/js/headerinject.js                    # the working tree
 *     node tests/js/headerinject.js path/to/old.js     # must fail
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const SRC_PATH = process.argv[2] || L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PP = ProfilesPlugin;');

/* ── A DOM where "in the document" and "on the screen" are different things ────── */

let idSeq = 0;
function el(opts) {
    const o = opts || {};
    const node = {
        __id: 'n' + (++idSeq),
        tagName: (o.tag || 'DIV').toUpperCase(),
        className: o.className || '',
        id: o.id || '',
        children: [],
        parentElement: null,
        // laidOut is the whole point: an element inside a display:none wrapper is in the
        // document and has no client rects. Inherited from the parent, the way display
        // actually works.
        _ownLaidOut: o.laidOut === undefined ? true : !!o.laidOut,
        rect: o.rect || null,
        getClientRects() {
            return this.isLaidOut() ? [{ width: 10, height: 10 }] : [];
        },
        getBoundingClientRect() {
            return this.rect || { top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0 };
        },
        isLaidOut() {
            let n = this;
            while (n) {
                if (!n._ownLaidOut) return false;
                n = n.parentElement;
            }
            return true;
        },
        matchesSel(sel) { return matches(this, sel); },
        querySelectorAll(sel) { return descendants(this).filter(n => matches(n, sel)); },
        querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
        contains(other) { return other === this || descendants(this).indexOf(other) !== -1; },
        insertBefore(n) { this.children.push(n); n.parentElement = this; return n; },
        appendChild(n) { this.children.push(n); n.parentElement = this; return n; },
        remove() {
            if (this.parentElement) {
                const i = this.parentElement.children.indexOf(this);
                if (i >= 0) this.parentElement.children.splice(i, 1);
            }
            this.parentElement = null;
        },
        setAttribute() {}, addEventListener() {},
        classList: { add() {}, remove() {}, contains: () => false },
        style: {}
    };
    (o.children || []).forEach(c => { node.children.push(c); c.parentElement = node; });
    return node;
}

function descendants(node) {
    const out = [];
    (function walk(n) {
        n.children.forEach(c => { out.push(c); walk(c); });
    })(node);
    return out;
}

/// Only the selector shapes the code under test actually uses. Anything else throws,
/// so a new strategy cannot be silently unexamined by this file.
function matches(node, sel) {
    return sel.split(',').map(s => s.trim()).some(one => {
        if (one === 'button' || one === 'div' || one === 'nav' || one === 'ul' || one === 'span') {
            return node.tagName === one.toUpperCase();
        }
        if (one === 'a[role="button"]') return node.tagName === 'A';
        if (one === 'button:not(#profiles-floating-bubble)') {
            return node.tagName === 'BUTTON' && node.id !== 'profiles-floating-bubble';
        }
        if (one === 'a[role="button"]:not(#profiles-floating-bubble)') {
            return node.tagName === 'A' && node.id !== 'profiles-floating-bubble';
        }
        if (one === '.headerButton:not(#profiles-floating-bubble)') {
            return hasClass(node, 'headerButton') && node.id !== 'profiles-floating-bubble';
        }
        if (one === '[class*="skinHeader"]') return node.className.indexOf('skinHeader') !== -1;
        if (one === '[class*="topBar"]') return node.className.indexOf('topBar') !== -1;
        if (one.charAt(0) === '.') return hasClass(node, one.slice(1));
        throw new Error('the fixture does not model the selector ' + JSON.stringify(one));
    });
}
const hasClass = (n, c) => (' ' + n.className + ' ').indexOf(' ' + c + ' ') !== -1;

let NOW = 1000000;
function makePlugin(root) {
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0' },
        setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
        requestAnimationFrame(fn) { fn(); },
        fetch: () => Promise.reject(new Error('no network')),
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        Date: { now: () => NOW },
        document: {
            documentElement: el({ className: 'layout-modern' }),
            head: el({}), body: root,
            createElement: tag => el({ tag }),
            querySelector: sel => root.querySelector(sel),
            querySelectorAll: sel => root.querySelectorAll(sel),
            getElementById: () => null,
            contains: node => root.contains(node),
            addEventListener() {}, removeEventListener() {}
        },
        window: {
            innerHeight: 900, innerWidth: 1600,
            location: { hash: '', pathname: '/web/', href: 'https://x/web/', origin: 'https://x' },
            addEventListener() {}, removeEventListener() {}, PointerEvent: function () {}
        },
        history: { pushState() {}, replaceState() {} },
        JSON, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map, URL
    };
    sandbox.globalThis = sandbox;
    sandbox.window.localStorage = sandbox.localStorage;
    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    return sandbox.__PP;
}

/* ── Fixtures ──────────────────────────────────────────────────────────────────── */

/// What jellyfin-web actually renders on the modern layout: the legacy header, populated
/// by libraryMenu.js, inside the display:none wrapper AppHeader.tsx puts it in — plus the
/// MUI toolbar that is the real one.
function modernLayout() {
    const hiddenWrapper = el({ laidOut: false, children: [
        el({ className: 'skinHeader focuscontainer-x', children: [
            el({ className: 'headerLeft' }),
            el({ className: 'headerRight', children: [
                el({ tag: 'button', className: 'headerButton headerButtonRight headerCastButton' }),
                el({ tag: 'button', className: 'headerButton headerButtonRight headerUserButton' })
            ] })
        ] })
    ] });

    const toolbar = el({ className: 'MuiToolbar-root', rect: rect(0, 0, 1600, 56), children: [
        el({ tag: 'button', className: 'MuiIconButton-root', rect: rect(1400, 8, 40, 40) }),
        el({ tag: 'button', className: 'MuiIconButton-root', rect: rect(1450, 8, 40, 40) }),
        el({ tag: 'button', className: 'MuiIconButton-root', rect: rect(1500, 8, 40, 40) })
    ] });

    return el({ children: [hiddenWrapper, toolbar] });
}

/// The legacy layout: the same header, not hidden.
function legacyLayout() {
    return el({ children: [
        el({ children: [
            el({ className: 'skinHeader focuscontainer-x', rect: rect(0, 0, 1600, 56), children: [
                el({ className: 'headerRight', rect: rect(1300, 8, 260, 40), children: [
                    el({ tag: 'button', className: 'headerButton headerUserButton', rect: rect(1500, 8, 40, 40) })
                ] })
            ] })
        ] })
    ] });
}

const rect = (left, top, w, h) => ({
    left, top, width: w, height: h, right: left + w, bottom: top + h
});

/* ── The reported bug ──────────────────────────────────────────────────────────── */

console.log();
console.log('── Modern layout: the legacy header is present but hidden ─────');
{
    const root = modernLayout();
    const PP = makePlugin(root);

    const hiddenRight = root.querySelector('.headerRight');
    ok('the fixture really does contain a .headerRight', !!hiddenRight);
    ok('and it really is not laid out', hiddenRight.getClientRects().length === 0);

    const found = PP._findHeaderContainer();
    ok('no hidden container is offered as the header',
        found === null,
        found ? 'got .' + found.className : '');

    // With no named container, the geometric search runs — and finds the real toolbar.
    const anchor = PP._findGeometricHeaderAnchor();
    ok('the geometric fallback finds the visible toolbar instead',
        !!anchor && anchor.getBoundingClientRect().right === 1540,
        anchor ? 'anchor right=' + anchor.getBoundingClientRect().right : 'nothing found');
}

console.log();
console.log('── Legacy layout: unchanged ───────────────────────────────────');
{
    const root = legacyLayout();
    const PP = makePlugin(root);
    const found = PP._findHeaderContainer();
    ok('a visible .headerRight is still the container',
        !!found && found.className === 'headerRight',
        found ? '.' + found.className : 'null');
}

console.log();
console.log('── A hidden header and a visible one together ─────────────────');
{
    // A theme that builds its own bar while the stock one sits hidden. The stock
    // .skinHeader comes first in document order, so querySelector would return the one
    // that cannot work.
    const themeBar = el({ className: 'topBar', rect: rect(0, 0, 1600, 56), children: [
        el({ className: 'topBar-actions', rect: rect(1300, 8, 260, 40), children: [
            el({ tag: 'button', rect: rect(1400, 8, 40, 40) }),
            el({ tag: 'button', rect: rect(1450, 8, 40, 40) })
        ] })
    ] });
    const root = modernLayout();
    root.appendChild(themeBar);

    const PP = makePlugin(root);
    const found = PP._findHeaderContainer();
    ok('the visible one wins, not the first in document order',
        !!found && found.className === 'topBar-actions',
        found ? '.' + found.className : 'null');
}

console.log();
console.log('── Strategy B skips hidden buttons ────────────────────────────');
{
    // The hidden legacy header supplies the first .headerButton in the document; a theme
    // supplies a visible one further down.
    const visibleCluster = el({ className: 'myThemeBar', rect: rect(0, 0, 1600, 56), children: [
        el({ tag: 'button', className: 'headerButton', rect: rect(1450, 8, 40, 40) })
    ] });
    const root = el({ children: [
        el({ laidOut: false, children: [
            el({ className: 'skinHeader', children: [
                el({ className: 'someOtherName', children: [
                    el({ tag: 'button', className: 'headerButton' })
                ] })
            ] })
        ] }),
        visibleCluster
    ] });

    const PP = makePlugin(root);
    const found = PP._findHeaderContainer();
    ok('the parent of the first VISIBLE header button is used',
        !!found && found.className === 'myThemeBar',
        found ? '.' + found.className : 'null');
}

console.log();
console.log('── The cache notices a layout switch ──────────────────────────');
{
    const root = legacyLayout();
    const PP = makePlugin(root);

    const first = PP._findHeaderContainer();
    ok('found while the legacy layout is on', !!first && first.className === 'headerRight');

    // Someone changes layout in Display settings: React hides the wrapper. The element
    // stays in the document, so document.contains() alone would keep handing it back.
    const wrapper = root.children[0];
    wrapper._ownLaidOut = false;

    ok('within the throttle window the cached answer is reused',
        PP._findHeaderContainer() === first);

    NOW += 4000;
    ok('after it, the now-hidden container is dropped',
        PP._findHeaderContainer() === null,
        'still returning a container inside a display:none wrapper');
}

console.log();
if (fails.length) {
    console.log('  Failures:');
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
