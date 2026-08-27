// Exercises the 1.3.3 client changes that have no browser to test them in:
// the D-pad focus trap (issue #16), the surface resolution that decides what the
// trap applies to, and the server-address rewrite for plugin image URLs (Tizen).

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

const src = fs.readFileSync(L.profilesPath(), 'utf8')
    .replace('ProfilesPlugin.init();', 'globalThis.__PP = ProfilesPlugin;');

if (!src.includes('globalThis.__PP')) {
    console.error('Could not find the bootstrap line to replace - harness is out of date.');
    process.exit(1);
}

function makeStorage() {
    const map = new Map();
    return {
        getItem: k => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => map.set(k, String(v)),
        removeItem: k => map.delete(k),
        clear: () => map.clear()
    };
}

const noopEl = { style: {}, classList: { add() {}, remove() {}, contains: () => false }, appendChild() {}, addEventListener() {} };

// Captures what profiles.js binds on document, so the trap handlers can be invoked.
const documentListeners = [];

const sandbox = {
    console,
    localStorage: makeStorage(),
    sessionStorage: makeStorage(),
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    requestAnimationFrame: () => 0,
    fetch: () => Promise.reject(new Error('no network in harness')),
    MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
    document: {
        head: noopEl, body: noopEl,
        createElement: () => Object.assign({}, noopEl, { innerHTML: '', querySelector: () => null, querySelectorAll: () => [] }),
        querySelector: () => null,
        querySelectorAll: () => [],
        getElementById: () => null,
        addEventListener: (type, fn, capture) => documentListeners.push({ type, fn, capture: !!capture }),
        removeEventListener: (type, fn, capture) => {
            const i = documentListeners.findIndex(l => l.type === type && l.fn === fn && l.capture === !!capture);
            if (i >= 0) documentListeners.splice(i, 1);
        },
        documentElement: noopEl,
        activeElement: null
    },
    window: { location: { hash: '', pathname: '/web/', href: 'https://tv.example.org/web/index.html', origin: 'https://tv.example.org' }, addEventListener: () => {}, PointerEvent: function () {} },
    history: { pushState: () => {}, replaceState: () => {} },
    URL: URL,
    Image: function () {}
};
sandbox.globalThis = sandbox;
sandbox.window.localStorage = sandbox.localStorage;

vm.createContext(sandbox);
vm.runInContext(src, sandbox);

const PP = sandbox.__PP;
if (!PP) { console.error('profiles.js did not export ProfilesPlugin'); process.exit(1); }

let pass = 0, fail = 0;
function check(name, actual, expected) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a === e) { pass++; console.log(`  PASS  ${name}`); }
    else { fail++; console.log(`  FAIL  ${name} - expected ${e}, got ${a}`); }
}

// â”€â”€ Fake elements â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
let focused = null;
function el(id, x, y, w, h, extra) {
    return Object.assign({
        id,
        tagName: 'BUTTON',
        disabled: false,
        offsetParent: {},
        // Nothing in these fixtures claims the arrow keys unless a test says so.
        closest: () => null,
        getBoundingClientRect: () => ({ left: x, top: y, width: w === undefined ? 100 : w, height: h === undefined ? 100 : h }),
        focus() { focused = id; }
    }, extra || {});
}

/// A key event target that is not one of the focusable fixtures.
const other = (tagName, extra) => Object.assign({ tagName: tagName || 'DIV', closest: () => null }, extra || {});

// â”€â”€ Spatial navigation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// A 3x2 grid, 100px cells, 20px gutters.
//   a b c
//   d e f
const grid = {
    a: el('a', 0, 0), b: el('b', 120, 0), c: el('c', 240, 0),
    d: el('d', 0, 120), e: el('e', 120, 120), f: el('f', 240, 120)
};
const items = Object.values(grid);
const root = { contains: node => items.indexOf(node) >= 0 };

const nav = Object.create(PP);
nav._overlayFocusables = () => items;

function move(fromId, dir) {
    sandbox.document.activeElement = grid[fromId];
    focused = null;
    nav._moveOverlayFocus(root, dir);
    return focused;
}

console.log('\nSpatial navigation');
console.log('------------------');
check('right from a -> b', move('a', 'right'), 'b');
check('right from b -> c', move('b', 'right'), 'c');
check('right from c stays put (edge)', move('c', 'right'), null);
check('left from c -> b', move('c', 'left'), 'b');
check('down from a -> d (same column, not e)', move('a', 'down'), 'd');
check('down from b -> e', move('b', 'down'), 'e');
check('up from f -> c', move('f', 'up'), 'c');
check('up from a stays put (edge)', move('a', 'up'), null);
check('down from d stays put (edge)', move('d', 'down'), null);

// Focus outside the surface lands on the first item, whatever the direction.
sandbox.document.activeElement = el('outside', 999, 999);
focused = null;
nav._moveOverlayFocus(root, 'up');
check('focus outside the surface enters at the first item', focused, 'a');

// Hidden and disabled controls are skipped.
const mixed = [
    el('vis1', 0, 0),
    el('hidden', 120, 0, 100, 100, { offsetParent: null }),
    el('off', 240, 0, 100, 100, { disabled: true }),
    el('vis2', 360, 0)
];
const mixedRoot = {
    contains: n => mixed.indexOf(n) >= 0,
    querySelectorAll: () => mixed
};
sandbox.document.activeElement = mixed[0];
focused = null;
PP._moveOverlayFocus(mixedRoot, 'right');
check('hidden and disabled controls are skipped', focused, 'vis2');

// â”€â”€ Which surface the trap applies to â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
console.log('\nActive surface');
console.log('--------------');

function withSurfaces(list) {
    sandbox.document.querySelectorAll = sel =>
        (sel === PP.TRAP_SURFACE_SELECTOR ? list : []);
}

withSurfaces([]);
check('nothing on screen -> no surface', PP._activeTrapSurface(), null);

const gate = { id: 'profiles-gate-overlay', isConnected: true };
const crop = { id: 'profiles-crop-dialog', isConnected: true, getAttribute: n => (n === 'data-profiles-own-keys' ? '1' : null) };

withSurfaces([gate]);
check('gate alone is the surface', PP._activeTrapSurface().id, 'profiles-gate-overlay');

withSurfaces([gate, crop]);
check('a dialog above the gate wins', PP._activeTrapSurface().id, 'profiles-crop-dialog');

withSurfaces([Object.assign({}, gate), { id: 'profiles-alert-dialog', isConnected: false }]);
check('a detached surface is ignored', PP._activeTrapSurface(), null);

check('the crop view owns the arrow keys', PP._ownsArrowKeys({ closest: s => (s.includes('own-keys') ? crop : null) }), true);
check('an ordinary control does not', PP._ownsArrowKeys({ closest: () => null }), false);
check('a missing node does not', PP._ownsArrowKeys(null), false);

// â”€â”€ Trap key handling â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
console.log('\nTrap key handling');
console.log('-----------------');

PP._overlayTrap = null;
documentListeners.length = 0;
PP._bindOverlayFocusTrap();
const keyHandler = documentListeners.find(l => l.type === 'keydown' && l.capture);
check('trap binds keydown in the capture phase', !!keyHandler, true);
check('trap binds focusin in the capture phase',
    !!documentListeners.find(l => l.type === 'focusin' && l.capture), true);

function press(key, target, opts) {
    let prevented = false, stopped = false;
    const e = Object.assign({
        key,
        keyCode: 0,
        target,
        ctrlKey: false, altKey: false, metaKey: false, shiftKey: false,
        preventDefault() { prevented = true; },
        stopPropagation() { stopped = true; }
    }, opts || {});
    focused = null;
    keyHandler.fn(e);
    return { prevented, stopped, focused };
}

// Nothing on screen: the trap must be completely transparent.
withSurfaces([]);
check('no surface -> arrow keys pass through untouched',
    press('ArrowRight', other('DIV')), { prevented: false, stopped: false, focused: null });

// Gate up, focus on a card.
// A PIN field lives inside the overlay, so the trap sees it as contained.
const pinField = other('INPUT');
const gateSurface = {
    id: 'profiles-gate-overlay',
    isConnected: true,
    contains: n => items.indexOf(n) >= 0 || n === pinField,
    querySelectorAll: () => items,
    getAttribute: () => null
};
withSurfaces([gateSurface]);
sandbox.document.activeElement = grid.a;

let r = press('ArrowRight', grid.a);
check('gate: arrow moves within the overlay', r.focused, 'b');
check('gate: arrow is stopped before Jellyfin sees it', r.stopped && r.prevented, true);

sandbox.document.activeElement = grid.a;
r = press('ArrowRight', pinField);
check('gate: left/right inside a text field is left to the caret', r.stopped, false);

sandbox.document.activeElement = grid.a;
r = press('ArrowDown', pinField);
check('gate: up/down in a text field still navigates', r.focused, 'd');

const dropdown = other('SELECT');
gateSurface.contains = n => items.indexOf(n) >= 0 || n === pinField || n === dropdown;
sandbox.document.activeElement = grid.a;
r = press('ArrowDown', dropdown);
check('gate: up/down on a dropdown changes its value', r.stopped, false);

sandbox.document.activeElement = grid.a;
r = press('ArrowRight', dropdown);
check('gate: left/right off a dropdown still navigates', r.focused, 'b');

r = press('ArrowRight', other('INPUT'));
check('gate: a text field outside the overlay gets no reprieve', r.stopped, true);

sandbox.document.activeElement = grid.a;
r = press('b', other('DIV'), { ctrlKey: true, shiftKey: true });
check('gate: Ctrl+Shift+B is never swallowed', r.stopped, false);

sandbox.document.activeElement = grid.a;
r = press('Tab', grid.a);
check('gate: Tab steps forward in document order', r.focused, 'b');

sandbox.document.activeElement = grid.a;
r = press('Tab', grid.a, { shiftKey: true });
check('gate: Shift+Tab wraps backwards', r.focused, 'f');

sandbox.document.activeElement = grid.f;
r = press('Tab', grid.f);
check('gate: Tab wraps forwards', r.focused, 'a');

// Tab used to be routed through the geometric search, which found nothing at all in a
// stacked form because every candidate sits above or below rather than beside.
const stacked = [el('s1', 0, 0), el('s2', 0, 120), el('s3', 0, 240)];
const stackedRoot = { contains: n => stacked.indexOf(n) >= 0, querySelectorAll: () => stacked, isConnected: true, id: 'profiles-gate-overlay' };
withSurfaces([stackedRoot]);
sandbox.document.activeElement = stacked[0];
r = press('Tab', stacked[0]);
check('gate: Tab works in a vertically stacked form', r.focused, 's2');
withSurfaces([gateSurface]);

sandbox.document.activeElement = grid.a;
r = press('Enter', grid.a);
check('gate: Enter inside the overlay reaches our own handlers', r.stopped, false);

r = press('Enter', other('DIV'));
check('gate: Enter from outside is blocked and focus returns', [r.stopped, r.focused], [true, 'a']);

sandbox.document.activeElement = grid.a;
r = press('Escape', grid.a);
check('gate: Escape is left alone for our dialogs', r.stopped, false);

// Legacy TV browsers report keyCode only.
sandbox.document.activeElement = grid.a;
r = press(undefined, grid.a, { keyCode: 39 });
check('gate: keyCode-only arrow still navigates', r.focused, 'b');

// Crop dialog: arrows belong to the picture, not to control navigation.
const cropSurface = {
    id: 'profiles-crop-dialog',
    isConnected: true,
    contains: () => true,
    querySelectorAll: () => items,
    getAttribute: n => (n === 'data-profiles-own-keys' ? '1' : null)
};
withSurfaces([gateSurface, cropSurface]);

// The crop view declares the arrows as its own; the buttons beside it do not.
const cropView = other('DIV', { closest: s => (s.indexOf('own-keys') >= 0 ? {} : null) });
r = press('ArrowRight', cropView);
check('crop: arrows on the picture pass through to the pan handler', [r.stopped, r.focused], [false, null]);

sandbox.document.activeElement = grid.a;
r = press('ArrowRight', other('BUTTON'));
check('crop: arrows on its buttons still navigate', [r.stopped, r.focused], [true, 'b']);

sandbox.document.activeElement = null;
r = press('Tab', other('DIV'));
check('crop: Tab still moves between its buttons', r.focused, 'a');

// Release must make the trap inert.
PP._releaseOverlayFocusTrap();
check('release removes both listeners', documentListeners.length, 0);
check('release is idempotent', (PP._releaseOverlayFocusTrap(), PP._overlayTrap), null);

// â”€â”€ Plugin image URLs (Tizen) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
console.log('\nPlugin image URLs');
console.log('-----------------');

// loadImageFromUrl is the only reachable caller of the module-level rewrite.
let requested = null, cors;
sandbox.Image = function () {
    const self = this;
    Object.defineProperty(this, 'src', { set(v) { requested = v; cors = self.crossOrigin; } });
};
sandbox.ApiClient = { getUrl: p => 'https://media.example.org/' + p };

PP.loadImageFromUrl('/plugins/profiles/avatars/abc?size=thumb');
check('root-relative avatar goes through the server address',
    requested, 'https://media.example.org/plugins/profiles/avatars/abc?size=thumb');
check('an off-origin request asks for CORS so the canvas stays usable', cors, 'anonymous');

sandbox.ApiClient = { getUrl: p => 'https://tv.example.org/' + p };
PP.loadImageFromUrl('/plugins/profiles/image/abc');
check('same-origin request does not set crossOrigin', cors, undefined);

sandbox.ApiClient = null;
requested = null;
PP.loadImageFromUrl('/plugins/profiles/image/abc');
check('no ApiClient -> the relative path is still used', requested, '/plugins/profiles/image/abc');

// ── Active-profile session storage ──────────────────────────────────────────
// Moved to session.js. The tests here simulated a reload by clearing sessionStorage
// on the SAME plugin object, which stopped being a faithful reload once the storage
// helpers gained an in-memory tier — a real reload builds a new object. session.js
// re-evaluates the file per page load instead, and covers the mirror, the switching
// flag, sign-out and the revert-loop backstop against the runtimes that actually
// misbehave. Keeping a weaker second copy here is how a false green gets built.

// ── TV Back key ──────────────────────────────────────────────────────────────
console.log('\nTV Back key');
console.log('-----------');

PP._overlayTrap = null;
documentListeners.length = 0;
PP._bindOverlayFocusTrap();
const backHandler = documentListeners.find(l => l.type === 'keydown' && l.capture);

let clicked = null;
const backBtn = { id: 'pin-cancel-btn', click() { clicked = 'pin-cancel-btn'; } };
const pinScreen = {
    id: 'profiles-gate-overlay',
    isConnected: true,
    contains: () => true,
    querySelectorAll: () => items,
    querySelector: sel => (sel.indexOf('#pin-cancel-btn') >= 0 ? backBtn : null),
    getAttribute: () => null
};

function pressBack(keyCode, key) {
    clicked = null;
    let prevented = false, stopped = false;
    backHandler.fn({
        key: key, keyCode: keyCode, target: other('DIV'),
        ctrlKey: false, altKey: false, metaKey: false, shiftKey: false,
        preventDefault() { prevented = true; },
        stopPropagation() { stopped = true; }
    });
    return { prevented, stopped, clicked };
}

withSurfaces([]);
check('no surface -> Back is left to the platform', pressBack(10009), { prevented: false, stopped: false, clicked: null });

withSurfaces([pinScreen]);
let b = pressBack(10009);
check('Samsung Back (10009) is swallowed', b.prevented && b.stopped, true);
check('Samsung Back steps back one screen', b.clicked, 'pin-cancel-btn');

b = pressBack(461);
check('LG Back (461) works too', b.clicked, 'pin-cancel-btn');

b = pressBack(0, 'XF86Back');
check('XF86Back works too', b.clicked, 'pin-cancel-btn');

// On the picker there is nowhere to go back to, but the press must still not reach
// Tizen — unhandled, it raises an exit prompt behind the overlay.
const picker = Object.assign({}, pinScreen, { querySelector: () => null });
withSurfaces([picker]);
b = pressBack(10009);
check('picker: Back is absorbed rather than exiting the app', b.prevented && b.stopped, true);
check('picker: nothing is clicked', b.clicked, null);

// A voluntarily opened picker carries a Cancel control, and Back must find it — otherwise
// a TV user who opens the switcher by accident has no way back to what they were watching.
const resumeBtn = { id: 'profiles-resume-btn', click() { clicked = 'profiles-resume-btn'; } };
withSurfaces([Object.assign({}, pinScreen, {
    querySelector: sel => (sel.indexOf('#profiles-resume-btn') >= 0 ? resumeBtn : null)
})]);
b = pressBack(10009);
check('Back reaches the picker Cancel when there is one', b.clicked, 'profiles-resume-btn');

PP._releaseOverlayFocusTrap();

// ── Backing out of a voluntarily opened switcher ─────────────────────────────
console.log('\nResume previous profile');
console.log('-----------------------');

PP._tizenRuntime = false;
sandbox.localStorage.clear();
sandbox.sessionStorage.clear();

let authSetTo = null;
sandbox.ApiClient = {
    getUrl: p => '/' + p,
    getCurrentUserId: () => 'user-child',
    setAuthenticationInfo: (t, u) => { authSetTo = t + '/' + u; }
};

const overlayRemoved = [];
const resumeTest = Object.create(PP);
resumeTest.updateStoredCredentials = () => {};
resumeTest.removeProfileOverlay = () => overlayRemoved.push(1);
resumeTest.checkRoute = () => {};
resumeTest.isManageMode = true;
resumeTest.masterPin = '1234';
resumeTest._resumeState = { token: 'child-token', userId: 'user-child', info: '{"name":"Kid"}' };

resumeTest.resumePreviousProfile();
check('resume restores the session marker',
    sandbox.sessionStorage.getItem(PP.config.activeSessionKey), 'child-token');
check('resume restores the cached profile info',
    sandbox.sessionStorage.getItem('jellyfin_profiles_active_info'), '{"name":"Kid"}');
check('resume re-authenticates as that profile', authSetTo, 'child-token/user-child');
check('resume leaves manage mode', resumeTest.isManageMode, false);
check('resume forgets the master PIN', resumeTest.masterPin, null);
check('resume takes the overlay down', overlayRemoved.length, 1);
check('resume is spent once used', resumeTest._resumeState, null);

// Calling it again must be inert rather than restoring a stale token.
sandbox.sessionStorage.clear();
resumeTest.resumePreviousProfile();
check('resume does nothing a second time',
    sandbox.sessionStorage.getItem(PP.config.activeSessionKey), null);
check('resume does not re-remove the overlay', overlayRemoved.length, 1);

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

