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
        attrs: o.attrs || {},
        getAttribute(n) { return Object.prototype.hasOwnProperty.call(this.attrs, n) ? this.attrs[n] : null; },
        matchesSel(sel) { return matches(this, sel); },
        querySelectorAll(sel) { return descendants(this).filter(n => matches(n, sel)); },
        querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
        contains(other) { return other === this || descendants(this).indexOf(other) !== -1; },
        // A real insertBefore honours the reference node, and that is exactly what is
        // under test: the button has to land BEFORE the account button, not after it.
        insertBefore(n, ref) {
            const i = ref ? this.children.indexOf(ref) : -1;
            if (i >= 0) this.children.splice(i, 0, n); else this.children.push(n);
            n.parentElement = this;
            return n;
        },
        appendChild(n) { this.children.push(n); n.parentElement = this; return n; },
        remove() {
            if (this.parentElement) {
                const i = this.parentElement.children.indexOf(this);
                if (i >= 0) this.parentElement.children.splice(i, 1);
            }
            this.parentElement = null;
        },
        setAttribute() {}, addEventListener() {},
        // The code asks whether a candidate page is the visible one via offsetParent.
        get offsetParent() { return this.isLaidOut() ? {} : null; },
        // Assigning innerHTML in a browser parses the markup into real children. The only
        // thing the code under test then does with those children is look them up by id,
        // so that is what this models: every id the markup declares becomes findable.
        // Modelling nothing at all would make querySelector return null and the code would
        // throw on a listener, which tests the fixture rather than the plugin.
        set innerHTML(html) {
            this._html = html;
            this.children = this.children.filter(c => !c.__fromHtml);
            const re = /id="([^"]+)"/g;
            let m;
            while ((m = re.exec(html))) {
                const child = el({ id: m[1] });
                child.__fromHtml = true;
                child.parentElement = this;
                this.children.push(child);
            }
        },
        get innerHTML() { return this._html || ''; },
        classList: (() => {
            const set = new Set();
            return {
                add: c => set.add(c),
                remove: c => set.delete(c),
                contains: c => set.has(c),
                toggle: (c, force) => {
                    const on = force === undefined ? !set.has(c) : !!force;
                    if (on) set.add(c); else set.delete(c);
                    return on;
                }
            };
        })(),
        style: {}
    };
    // The code under test reaches for parentNode as well as parentElement; a real element
    // has both and they agree for everything in this fixture.
    Object.defineProperty(node, 'parentNode', { get() { return node.parentElement; } });
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
        if (one === 'form') return node.tagName === 'FORM';
        if (one.charAt(0) === '#') return node.id === one.slice(1);
        if (one === '[aria-controls="app-user-menu"]') {
            return node.getAttribute('aria-controls') === 'app-user-menu';
        }
        if (one === '.headerUserButton') return hasClass(node, 'headerUserButton');
        if (one === '.headerUserButtonRound') return hasClass(node, 'headerUserButtonRound');
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
            getElementById: id => root.querySelectorAll('#' + id)[0] || null,
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

    // The account button is the last child of its own Box, exactly as
    // components/toolbar/AppToolbar.tsx renders it.
    const userBox = el({ className: 'MuiBox-root', rect: rect(1500, 8, 40, 40), children: [
        el({ tag: 'button', className: 'MuiIconButton-root',
             attrs: { 'aria-controls': 'app-user-menu' }, rect: rect(1500, 8, 40, 40) })
    ] });
    const toolbar = el({ className: 'MuiToolbar-root', rect: rect(0, 0, 1600, 56), children: [
        el({ tag: 'button', className: 'MuiIconButton-root', rect: rect(1400, 8, 40, 40) }),
        el({ tag: 'button', className: 'MuiIconButton-root', rect: rect(1450, 8, 40, 40) }),
        userBox
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
    ok('the hidden container is never the answer',
        found !== hiddenRight && !(found && found.className === 'headerRight'),
        found ? 'got .' + found.className : 'null');
    ok('whatever is returned is laid out',
        found === null || found.getClientRects().length > 0);

    // And it is named, not guessed: the account button carries
    // aria-controls="app-user-menu" in both 10.11 and 12.0, so the switcher lands beside
    // the avatar rather than wherever React had got to when the search ran.
    ok('the modern toolbar account button supplies the container',
        !!found && found.className === 'MuiBox-root',
        found ? '.' + found.className : 'null');

    // Inserting marks it, so it can be sized against the 40px MUI avatar next to it.
    // Guarded: pointed at a build with no named strategy for this toolbar, `found` is
    // null and calling through would throw a stack trace instead of reporting, which
    // would stop this file before the sections below it ever ran.
    const bubble = el({ tag: 'button', id: 'profiles-floating-bubble' });
    if (found) {
        PP._insertBeforeUserBtn(found, bubble);
        ok('the button is marked as standing in the modern toolbar',
            bubble.classList.contains('jpf-modern-toolbar-btn'));
        ok('and is inserted before the account button, not after it',
            found.children.indexOf(bubble) === 0,
            'index ' + found.children.indexOf(bubble));
    } else {
        ok('the button is marked as standing in the modern toolbar', false,
            'no container was found to insert into');
        ok('and is inserted before the account button, not after it', false,
            'no container was found to insert into');
    }
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
    // that cannot work. Its buttons carry no class this fixture matches on, so the
    // cluster search inside .topBar is what has to answer.
    const themeBar = el({ className: 'topBar', rect: rect(0, 0, 1600, 56), children: [
        el({ className: 'topBar-actions', rect: rect(1300, 8, 260, 40), children: [
            el({ tag: 'button', rect: rect(1400, 8, 40, 40) }),
            el({ tag: 'button', rect: rect(1450, 8, 40, 40) })
        ] })
    ] });
    // No modern toolbar in this one: Strategy A2 would answer first and this is about
    // the .skinHeader search underneath it.
    const root = el({ children: [
        el({ laidOut: false, children: [
            el({ className: 'skinHeader focuscontainer-x', children: [
                el({ className: 'headerRight', children: [
                    el({ tag: 'button', className: 'headerButton headerUserButton' })
                ] })
            ] })
        ] }),
        themeBar
    ] });

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
console.log('── The section on the Jellyfin profile page ───────────────────');
{
    // userprofile.tsx puts both the avatar block and the password form inside
    // .padded-left .padded-right, and centres them. .padded-left is 3.3% padding, so that
    // container is very nearly full width — a plain block-level section dropped into it
    // lands hard against the left edge while the page's own content floats in the middle.
    const padded = el({ className: 'padded-left padded-right padded-bottom-page', children: [
        el({ className: 'readOnlyContent' }),
        el({ tag: 'form', className: 'updatePasswordForm passwordSection' })
    ] });
    const root = el({ children: [el({ id: 'userProfilePage', className: 'page', children: [padded] })] });

    const PP = makePlugin(root);
    PP.getCachedActiveProfile = () => ({ name: 'Bard', color: '#00A4DC', initial: 'B', profileImage: null });
    PP.injectProfilePageSection();

    const section = padded.children.find(c => c.id === 'profiles-userprofile-section');
    ok('the section is added to the padded page container',
        !!section, 'children: ' + padded.children.map(c => c.className || c.tagName).join(', '));
    ok('as a sibling of the avatar block and the form, after both',
        !!section && padded.children.indexOf(section) === 2);

    // The alignment itself is a stylesheet rule, so it is asserted there rather than by
    // reading back an inline style that no longer exists.
    const CSS = L.extractCss(L.readSourceAndStyles(fs.readFileSync(SRC_PATH, 'utf8')));
    ok('it carries the class the stylesheet aligns',
        !!section && (' ' + section.className + ' ').indexOf(' jpf-userprofile-section ') !== -1,
        section ? section.className : 'no section');
    ok('and that class centres it the way .readOnlyContent centres itself',
        /\.jpf-userprofile-section\s*\{[^}]*margin:\s*2em\s+auto/.test(CSS));
    ok('and caps it at the 54em jellyfin-web caps the siblings at',
        /min-width:\s*50em[^{]*\{\s*\.jpf-userprofile-section\s*\{[^}]*max-width:\s*54em/.test(CSS));
    // Comments out first. Written as a plain indexOf over the whole sheet, this went red
    // on the comment that explains the 44em is gone — prose, not a declaration. That is
    // the same failure themetest.js had this week and the navMenu substring bug before
    // it: ask about the declarations, not about the characters.
    const declarations = CSS.replace(/\/\*[\s\S]*?\*\//g, '');
    ok('no declaration still carries the arbitrary 44em',
        declarations.indexOf('44em') === -1);
}

console.log();
if (fails.length) {
    console.log('  Failures:');
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
