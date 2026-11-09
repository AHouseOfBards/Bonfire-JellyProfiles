// Television input, overlay by overlay: can a remote use every screen and leave it?
//
// What is new here. focustrap.js proves the trap's mechanics on a hand-built grid of
// fixtures; backkey.js proves four dialogs' cancel buttons are on the Back key's list;
// tvmode.js proves the TV class and its CSS. None of them DRAWS a screen and asks the
// question a person with a remote asks. This does, for each overlay, with the real render
// code against a stub DOM and the real trap functions (_overlayFocusables, _stepOverlayFocus,
// _moveOverlayFocus, the keydown handler) on the markup it drew:
//
//   1. every interactive element is focusable by the trap (a button, input, select, link,
//      or a div/label with a role or class that makes it a control and a tabindex that
//      makes it a stop). A div with role="button" and no tabindex is a control the
//      remote cannot land on.
//   2. every one is REACHED: Tab, and the Down arrow, walked from the initial focus with
//      the real functions, visit all of them.
//   3. focus STARTS on the obvious element, named per overlay in the ledger below.
//   4. the Back key (webOS 461, Tizen 10009, XF86Back) pressed through the real handler
//      clicks a control that leaves the screen, or, on the startup gate (a required
//      choice), is absorbed. Escape is checked on the dialogs, which own it; the full
//      screens answer Back only.
//
// Honest limits: the stub has no layout. Arrow reachability runs on a modelled layout (one
// column, document order), so it proves every control is a candidate and nothing in the
// handler blocks it, not that real geometry never strands one. Controls inside a closed
// <details> are not stops until it is opened, as in a browser; its <summary> is.
//
//   node tests/js/tvinput.js [path/to/profiles.js]
//
// Red first: point it at a copy where one overlay's control loses its tabindex, its
// initial focus, or its Back control.

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(L.readProfiles());
const { PP, content, sandbox } = env;

// ── Environment ─────────────────────────────────────────────────────────────────────────
let timers = [];
sandbox.setTimeout = (fn) => { timers.push(fn); return timers.length; };
function flush() {
    for (let i = 0; i < 50 && timers.length; i++) {
        const run = timers; timers = [];
        run.forEach(fn => { try { fn(); } catch (e) { /* a timer for something not drawn here */ } });
    }
}

let active = null;
let focusLog = [];
const winListeners = [];
const dialogs = [];
const doc = sandbox.document;
Object.defineProperty(doc, 'activeElement', { get: () => active, configurable: true });
// Screens that go back to the gate redraw it in this element; the stub has no gate behind them.
const gateBehind = D.makeNode({}, { id: 'profiles-gate-overlay' });
const realById = doc.getElementById;
doc.getElementById = (id) => (id === 'profiles-gate-overlay' ? gateBehind : realById(id));
doc.getElementsByClassName =() => dialogs.filter(d => !d.removed);
doc.createElement = () => {
    const n = D.makeContent();
    n.style = {};
    n.id = '';
    n.classList = { add() {}, remove() {}, contains: () => false, toggle: () => false };
    n.removeChild = () => {};
    n.remove = () => { n.removed = true; };
    n.isConnected = true;
    instrument(n);
    return n;
};
doc.body.appendChild = (n) => { dialogs.push(n); };
sandbox.window.addEventListener = (type, fn, cap) => winListeners.push({ type, fn, cap });
sandbox.window.removeEventListener = () => {};
sandbox.window.innerHeight = 1080;
// Television mode: the class applyDeviceClasses puts on <html>.
doc.documentElement.classList = { add() {}, remove() {}, toggle: () => false, contains: (c) => c === 'jpf-tv' };

/** Makes focus() on every node a surface hands out land in the log and in activeElement. */
function instrument(root) {
    const qs = root.querySelector, qsa = root.querySelectorAll;
    const label = (n) => n.id || (n.attrs && n.attrs.id)
        || (n.attrs ? (n.attrs.tag || '') + '.' + String(n.attrs.class || '').split(' ')[0] : '?');
    const patch = (n) => {
        if (n && !n._tv) { n._tv = true; if (!n.select) n.select = () => {}; n.focus = () => { active = n; focusLog.push(label(n)); }; }
        return n;
    };
    // The stub answers '#id' and '.class'. The selectors the screens use to find a first control
    // ('input:not([type="hidden"]), button', 'button[data-action]') are evaluated here over the
    // drawn markup, in document order, so the real code's choice is what gets focused.
    const simple = (sel) => /^[#.][\w-]+$/.test(sel);
    const matcher = (alt) => {
        const m = /^([a-z0-9]*)((?:\[[^\]]+\]|:not\(\[[^\]]+\]\)|:not\(\.[\w-]+\))*)$/.exec(alt.trim());
        if (!m) return null;
        const tests = [];
        (m[2].match(/:not\(\[[^\]]+\]\)|:not\(\.[\w-]+\)|\[[^\]]+\]/g) || []).forEach(part => {
            const neg = part.indexOf(':not(') === 0;
            const inner = neg ? part.slice(5, -1) : part;
            if (inner.charAt(0) === '.') {
                tests.push(p => (String(p.attrs.class || '').split(/\s+/).indexOf(inner.slice(1)) !== -1) !== neg);
                return;
            }
            const a = /^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(inner);
            if (a) tests.push(p => ((a[1] in p.attrs) && (a[2] === undefined || p.attrs[a[1]] === a[2])) !== neg);
        });
        return p => (!m[1] || p.tag === m[1]) && tests.every(f => f(p));
    };
    const evaluate = (sel) => {
        const alts = sel.split(',').map(matcher);
        if (alts.some(a => !a)) return null;
        const html = root.innerHTML;
        if (typeof html !== 'string') return null;
        return parse(html).filter(p => !p.hidden && alts.some(a => a(p)));
    };
    root._nodes = {};
    const nodeFor = (p) => {
        if (p.id) return patch(qs.call(root, '#' + p.id));
        return root._nodes[p.index] || (root._nodes[p.index] = patch(D.makeNode(Object.assign({ tag: p.tag }, p.attrs), { id: '' })));
    };
    root.querySelector = function (sel) {
        if (!simple(sel)) { const hit = evaluate(sel); if (hit && hit.length) return nodeFor(hit[0]); }
        return patch(qs.apply(root, arguments));
    };
    root.querySelectorAll = function (sel) {
        if (!simple(sel) && !/^\.[\w-]+(:checked)?$/.test(sel)) { const hit = evaluate(sel); if (hit) return hit.map(nodeFor); }
        const l = qsa.apply(root, arguments); l.forEach(patch); return l;
    };
}
instrument(content);

// Originals, before the effects counter replaces the navigation functions.
const REAL = {};
['renderOverlayContent', 'showSettingsMenu', 'showEditProfileModal', '_dismissSurface'].forEach(k => { REAL[k] = PP[k]; });

let effects = 0;
const bump = () => { effects++; };
['fetchAndRenderProfiles', 'showSettingsMenu', 'removeProfileOverlay', 'resumePreviousProfile',
    'renderOverlayContent', 'showOnboardingStep', 'showAddedAccount'].forEach(k => { PP[k] = bump; });
// The per-library artwork rows are found with row.querySelector, which the stub cannot do.
// They add no control the other checks do not see; the rest of the edit form is real.
PP.initLibraryArtworkEditor = () => ({ save: () => Promise.resolve() });
// The viewing-hours editor draws into a slot of the form. The stub's slot cannot hold markup,
// so give it a real one and append what it drew to the screen's markup (extraSlots).
const extraSlots = [];
const realViewing = PP.initViewingEditor;
PP.initViewingEditor = function (slot, profile, creating) {
    const own = D.makeContent();
    extraSlots.push(own);
    return realViewing.call(this, own, profile, creating);
};
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.clearManagedDocumentListeners = () => {};
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.handleSessionExpired = () => {};
PP.masterPin = '2468';
PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
PP.currentProfiles = [
    { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true, avatarInitial: 'B', avatarColor: '#E50914' },
    { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false, requiresPin: true, avatarInitial: 'K', avatarColor: '#E50914' }
];
PP.fetchSharedFormData = () => Promise.resolve({
    libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
    devices: [{ deviceId: 'tv-1', deviceName: 'TV', client: 'Jellyfin Web' }],
    libraryTags: [], avatarLibrary: { allowCustomUploads: true, avatars: [] }, ratings: null
});
env.setRespond((url) => {
    if (/Users\//.test(url)) return { status: 200, body: { Policy: {} } };
    if (/avatars$/.test(url)) return { status: 200, body: { allowCustomUploads: true, avatars: [] } };
    if (/themes/.test(url)) return { status: 200, body: {} };
    return { status: 200, body: {} };
});
const settle = async () => { for (let i = 0; i < 5; i++) { await env.settle(); flush(); } };

// ── Reading what was drawn ──────────────────────────────────────────────────────────────
/**
 * The elements of `html` in document order, each with its attributes and whether a person
 * could see it: not display:none, not hidden, not type=hidden, and not inside a closed
 * <details> other than its <summary>.
 */
function parse(html, labels) {
    // tabindex the code set at runtime on .library-check-label (initTVCheckboxes), in order.
    let labelAt = 0;
    html = html.replace(/<!--[\s\S]*?-->/g, '').replace(/<script[\s\S]*?<\/script>/g, '');
    const VOID = /^(input|br|img|hr|meta|link|source)$/;
    const stack = [], out = [];
    const re = /<(\/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*?)(\/?)>/g;
    let m;
    while ((m = re.exec(html))) {
        const tag = m[2].toLowerCase(), raw = m[3];
        if (m[1] === '/') {
            for (let i = stack.length - 1; i >= 0; i--) if (stack[i].tag === tag) { stack.length = i; break; }
            continue;
        }
        const attrs = {};
        raw.replace(/([a-zA-Z][a-zA-Z0-9:-]*)(?:="([^"]*)")?/g, (all, k, v) => { if (!(k in attrs)) attrs[k] = v === undefined ? '' : v; return all; });
        if (/\blibrary-check-label\b/.test(attrs.class || '') && labels && labels[labelAt]) {
            const set = labels[labelAt++].attrs;
            if (set && set.tabindex !== undefined) attrs.tabindex = set.tabindex;
        }
        const own = /display:\s*none/.test(attrs.style || '') || 'hidden' in attrs || attrs.type === 'hidden';
        const inSummary = stack.some(s => s.tag === 'summary');
        const closedFold = !inSummary && stack.some(s => s.tag === 'details' && !s.open);
        const hiddenAncestor = stack.some(s => s.hidden);
        const hidden = own || closedFold || hiddenAncestor;
        out.push({ tag: tag, attrs: attrs, id: attrs.id || '', hidden: hidden, index: out.length });
        if (!VOID.test(tag) && !m[4]) stack.push({ tag: tag, hidden: own, open: 'open' in attrs });
    }
    return out;
}

/** Whether `n` is matched by the trap's own selector list (see _overlayFocusables). */
function trapMatches(n) {
    const a = n.attrs;
    if (/^(button|input|select|textarea)$/.test(n.tag)) return true;
    if (n.tag === 'a' && 'href' in a) return true;
    if (a.role === 'radio') return true;
    return 'tabindex' in a && a.tabindex !== '-1';
}

const CONTROL_ROLES = /^(button|radio|checkbox|switch|tab|link|menuitem)$/;
const CONTROL_CLASSES = ['focusable', 'profile-card', 'color-dot', 'avatar-library-item', 'jpf-choice', 'settings-menu-entry',
    'library-check-label', 'image-upload-btn', 'jpf-text-link'];
function isControl(n) {
    const a = n.attrs;
    if (n.hidden || 'disabled' in a) return false;
    if (/^(button|select|textarea)$/.test(n.tag)) return true;
    if (n.tag === 'input') return true;
    if (n.tag === 'a') return 'href' in a;
    if (n.tag === 'summary') return true;
    const classes = String(a.class || '').split(/\s+/);
    return CONTROL_ROLES.test(a.role || '') || CONTROL_CLASSES.some(c => classes.indexOf(c) !== -1) || 'onclick' in a;
}
/** A control the trap can actually land on: matched by the selector and not tabindex -1 for a non-radio. */
function isStop(n) { return !n.hidden && !('disabled' in n.attrs) && trapMatches(n); }

/**
 * A surface object over drawn markup that behaves enough like a DOM root for the real
 * trap functions: querySelectorAll with the trap's selector, contains, getBoundingClientRect
 * in a one-column model, focus recorded.
 */
function surfaceOf(html, labels) {
    const nodes = parse(html, labels).map((p, i) => {
        const n = {
            tagName: p.tag.toUpperCase(), nodeType: 1, attrs: p.attrs, id: p.id, parsed: p,
            disabled: 'disabled' in p.attrs, offsetParent: p.hidden ? null : {},
            getAttribute: k => (k in p.attrs ? p.attrs[k] : null),
            setAttribute: (k, v) => { p.attrs[k] = String(v); },
            closest: () => null,
            getBoundingClientRect: () => ({ left: 0, top: i * 50, width: 100, height: 40, bottom: i * 50 + 40 }),
            scrollIntoView() {},
            focus() { active = n; focusLog.push(p.id || '#' + i); },
            addEventListener() {}
        };
        return n;
    });
    const root = {
        nodeType: 1, nodes: nodes,
        contains: (n) => nodes.indexOf(n) !== -1,
        querySelectorAll: () => nodes.filter(n => trapMatches(n.parsed)),
        querySelector: (sel) => {
            // The Back key's list: the first match in document order, as a browser answers.
            const ids = (sel.match(/#[\w-]+/g) || []).map(s => s.slice(1));
            const hit = nodes.filter(n => ids.indexOf(n.id) !== -1 && !n.parsed.hidden)[0];
            return hit ? { id: hit.id, click() {} } : null;
        }
    };
    return root;
}

// ── The overlays ────────────────────────────────────────────────────────────────────────
// first: the id (or a prefix ending in *) focus must start on. back: 'leaves' when Back
// must click a control and the screen change; 'absorbed' for the required startup choice.
const OVERLAYS = [
    { name: 'the gate, opened from inside a profile', gate: true, resume: true, first: 'profile-card*', back: 'leaves' },
    { name: 'the startup gate, a required choice', gate: true, resume: false, first: 'profile-card*', back: 'absorbed' },
    { name: 'the profile PIN prompt', first: 'profile-pin-input', back: 'leaves', screen: () => PP.promptPinEntry('kid') },
    { name: 'the main PIN prompt', first: 'master-pin-input', back: 'leaves', screen: () => PP.promptMasterPinEntry('x', () => {}) },
    { name: 'Settings', first: '*', back: 'leaves', screen: () => { PP.showSettingsMenu = REAL.showSettingsMenu; PP.showSettingsMenu(); PP.showSettingsMenu = bump; } },
    { name: 'Settings, Your account', first: 'account-*', back: 'leaves', screen: () => PP.showAccountSettings() },
    { name: 'Settings, Switcher', first: '*', back: 'leaves', screen: () => PP.showSwitcherModeModal() },
    { name: 'Settings, Look', first: '*', back: 'leaves', screen: () => PP.showThemeSettings(null) },
    { name: 'Settings, Sharing', first: '*', back: 'leaves', screen: () => { PP.loadBonfireStatus = () => {}; PP.showBonfireModal(); } },
    { name: 'the Add profile form', first: 'create-name-input', back: 'leaves', screen: () => PP.showAddProfileModal() },
    { name: 'the Edit profile form', first: 'edit-name-input', back: 'leaves', screen: () => PP.showEditProfileModal(PP.currentProfiles[1]) },
    { name: 'a profile\'s own name and picture', first: 'self-name-input', back: 'leaves',
      screen: () => { PP._resumeState = { token: 'kid-token', userId: 'kid' }; PP.showOwnProfileForm(PP.currentProfiles[1]); } },
    { name: 'first-time setup, welcome (nothing to skip)', first: 'onboarding-next', back: 'absorbed',
      screen: () => { PP._onboarding = { steps: [{ id: 'welcome' }, { id: 'theme' }], index: 0, welcome: '' }; PP._onboardingWelcome(() => {}); } },
    { name: 'first-time setup, a step that can be skipped', first: '*', back: 'leaves',
      screen: () => { PP._onboarding = { steps: [{ id: 'welcome' }, { id: 'switcher' }], index: 1, welcome: '' }; PP._onboardingSwitcher(() => {}, bump); } },
    { name: 'first-time setup, the main PIN', first: '*', back: 'leaves',
      screen: () => { PP._onboarding = { steps: [{ id: 'welcome' }, { id: 'mainPin' }], index: 1, welcome: '' }; PP.promptSetMainPin([], false, () => {}, { onSkip: bump }); } },
    { name: 'the prompt dialog', first: 'profiles-prompt-input', back: 'leaves', escape: true, dialog: () => PP.showPromptDialog('Rename', 'Name', 'TV', () => {}) },
    { name: 'the select dialog', first: '*', back: 'leaves', escape: true, dialog: () => PP.showSelectDialog('Pick', 'Which?', [{ id: 'a', name: 'A' }], () => {}) },
    { name: 'the confirm dialog', first: '*', back: 'leaves', escape: true, dialog: () => PP.showConfirmDialog('Delete', 'Sure?', () => {}) },
    { name: 'the actions dialog (time up)', first: '*', back: 'leaves', escape: true,
      dialog: () => PP.showTimeUp({ profileUserId: 'kid', profileName: 'Kid', viewing: { allowed: false, reason: 'limit' } }, () => {}) },
    { name: 'the alert dialog', first: '*', back: 'leaves', escape: true, dialog: () => PP.showAlert('Oops', 'Something', () => {}) },
    { name: 'the emergency-disable dialog', first: 'profiles-panic-input', back: 'leaves', dialog: () => PP.showPanicPrompt() }
];

function matchesFirst(spec, id) {
    if (spec === '*') return !!id;
    if (spec.slice(-1) === '*') return id.indexOf(spec.slice(0, -1)) === 0;
    return id === spec;
}

/**
 * Draws one overlay fresh and settles it. Returns the markup, the node Back's clicks are
 * resolved on, and what the screen focused by itself (the first focus() call).
 */
async function openOverlay(o) {
    active = null; focusLog = []; timers = []; dialogs.length = 0; winListeners.length = 0; effects = 0;
    PP._overlayTrap = null;
    content.innerHTML = '';
    let html, holder, initial = null;
    if (o.gate) {
        PP._resumeState = o.resume ? { token: 'kid-token', userId: 'kid' } : null;
        PP.isManageMode = false;
        PP.drawLogo = PP.fitGateToScreen = PP.showAnnouncements = () => {};
        PP._syncMasterFromProfiles = () => {};
        const overlay = D.makeContent();
        instrument(overlay);
        PP.renderOverlayContent = REAL.renderOverlayContent;
        PP.renderOverlayContent(overlay, PP.currentProfiles);
        PP.renderOverlayContent = bump;
        html = overlay.innerHTML; holder = overlay;
        // showProfileOverlay's own auto-focus is `overlay.querySelector('[tabindex="0"], button,
        // input')`, which a stub cannot evaluate: apply the same rule to the drawn markup.
        const first = parse(html).filter(n => !n.hidden && (n.attrs.tabindex === '0' || /^(button|input)$/.test(n.tag)))[0];
        initial = first ? (first.id || 'profile-card:' + (first.attrs['data-id'] || '')) : null;
        if (first && /\bprofile-card\b/.test(first.attrs.class || '')) initial = 'profile-card:' + first.attrs['data-id'];
    } else if (o.dialog) {
        o.dialog();
        await settle();
        const d = dialogs[dialogs.length - 1];
        html = d ? d.innerHTML : ''; holder = d;
        initial = focusLog.length ? focusLog[0] : null;
    } else {
        o.screen();
        await settle();
        html = content.innerHTML + extraSlots.map(s => s.innerHTML).join(''); holder = content;
        initial = focusLog.length ? focusLog[0] : null;
    }
    const slotLabels = [];
    extraSlots.forEach(s => s.querySelectorAll('.library-check-label').forEach(l => slotLabels.push(l)));
    extraSlots.length = 0;
    // jellyfin's label tabindex is set at runtime (initTVCheckboxes); read what the code did.
    const labels = (holder && holder.querySelectorAll ? Array.prototype.slice.call(holder.querySelectorAll('.library-check-label')) : [])
        .concat(slotLabels);
    return { html, holder, initial, labels };
}

(async () => {
    for (const o of OVERLAYS) {
        console.log('\n-- ' + o.name + ' ' + '-'.repeat(Math.max(2, 56 - o.name.length)));
        let first;
        try { first = await openOverlay(o); } catch (e) {
            ok(o.name + ': opens', false, String(e && e.stack || e).split('\n').slice(0, 2).join(' | '));
            continue;
        }
        const { html, initial, labels } = first;
        ok(o.name + ': draws something', html.length > 0);
        if (!html) continue;

        // 1. Every control is a stop.
        const parsed = parse(html, labels);
        const controls = parsed.filter(isControl);
        const stranded = controls.filter(n => !isStop(n));
        ok(o.name + ': every interactive element is focusable by the remote (' + controls.length + ')',
            stranded.length === 0,
            stranded.map(n => n.tag + (n.id ? '#' + n.id : '.' + String(n.attrs.class || '').split(' ')[0])).join(', '));

        // 2. Reached by Tab and by the arrows, from where focus starts.
        const root = surfaceOf(html, labels);
        const real = PP._overlayFocusables(root);
        const stops = root.nodes.filter(n => isStop(n.parsed));
        ok(o.name + ': the trap\'s own filter finds them all', real.length === stops.length, real.length + ' of ' + stops.length);
        const start = () => {
            if (initial === null) return null;
            const byId = root.nodes.filter(n => n.id === initial)[0];
            if (byId) return byId;
            const m = /^profile-card:(.*)$/.exec(initial);
            return m ? (root.nodes.filter(n => n.attrs['data-id'] === m[1])[0] || null) : null;
        };
        active = start();
        const tabbable = real.filter(n => !(n.getAttribute('role') === 'radio' && n.getAttribute('tabindex') === '-1'));
        const seenTab = new Set();
        for (let i = 0; i < real.length + 2; i++) { PP._stepOverlayFocus(root, 1); seenTab.add(active); }
        const missedTab = tabbable.filter(n => !seenTab.has(n));
        ok(o.name + ': Tab visits every stop', missedTab.length === 0, missedTab.map(n => n.id || n.tagName).join(', '));
        active = start();
        const seenArrow = new Set(active ? [active] : []);
        for (let i = 0; i < real.length + 2; i++) { PP._moveOverlayFocus(root, 'down'); seenArrow.add(active); }
        // Focus may start mid-screen; what is above it is reached with Up.
        active = start();
        for (let i = 0; i < real.length + 2; i++) { PP._moveOverlayFocus(root, 'up'); seenArrow.add(active); }
        const missedArrow = real.filter(n => !seenArrow.has(n));
        ok(o.name + ': the arrow keys reach every stop from the first one', missedArrow.length === 0,
            missedArrow.map(n => n.id || n.tagName).join(', '));

        // 3. Focus starts on the obvious element.
        const shown = initial === null ? 'nothing' : initial;
        ok(o.name + ': focus starts on ' + o.first, initial !== null && matchesFirst(o.first, initial.replace(/^profile-card:.*/, 'profile-card')),
            'started on ' + shown);

        // 4. Back, through the real keydown handler, once per key and on a freshly drawn screen
        // each time (a screen that was left has no listeners to press again).
        for (const key of [{ keyCode: 461 }, { keyCode: 10009 }, { key: 'XF86Back', keyCode: 0 }]) {
            const again = await openOverlay(o);
            const r = surfaceOf(again.html, again.labels);
            let clicked = [];
            const e0 = effects, h0 = content.innerHTML;
            r.querySelector = (sel) => {
                const ids = (sel.match(/#[\w-]+/g) || []).map(s => s.slice(1));
                const hit = r.nodes.filter(n => ids.indexOf(n.id) !== -1 && !n.parsed.hidden)[0];
                if (!hit) return null;
                const real = again.holder.querySelector('#' + hit.id);
                return { id: hit.id, click: () => { clicked.push(hit.id); if (real) real.click(); } };
            };
            PP._dismissSurface = REAL._dismissSurface;
            PP._activeTrapSurface = () => r;
            PP._bindOverlayFocusTrap();
            const removed0 = dialogs.filter(d => d.removed).length;
            const handler = winListeners.filter(l => l.type === 'keydown')[0];
            handler.fn({ keyCode: key.keyCode, key: key.key || '', target: r, ctrlKey: false, altKey: false, metaKey: false,
                preventDefault() {}, stopPropagation() {} });
            flush();
            const label = key.keyCode ? String(key.keyCode) : key.key;
            if (o.back === 'absorbed') {
                ok(o.name + ': Back (' + label + ') is absorbed, there is nothing to go back to', clicked.length === 0 && effects === e0);
            } else {
                const gone = effects > e0 || content.innerHTML !== h0 || dialogs.filter(d => d.removed).length > removed0;
                ok(o.name + ': Back (' + label + ') leaves it', clicked.length > 0 && gone,
                    'clicked [' + clicked.join(',') + ']' + (clicked.length && !gone ? ', nothing changed' : ''));
            }
            PP._overlayTrap = null;
        }

        // Escape belongs to the dialogs, which bind it themselves.
        if (o.escape) {
            const again = await openOverlay(o);
            again.holder.fire('keydown', { key: 'Escape' });
            flush();
            ok(o.name + ': Escape closes it', again.holder.removed === true);
        }
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
