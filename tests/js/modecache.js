// Exercises the switcher-preference cache in Web/profiles.js without a browser.
// The file is an IIFE that self-starts; we swap the bootstrap line for an export and
// stub just enough of the DOM for the object literal to be constructed.

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

const src = fs.readFileSync(L.profilesPath(), 'utf8')
    .replace('ProfilesPlugin.init();', 'globalThis.__PP = ProfilesPlugin;');

if (!src.includes('globalThis.__PP')) {
    console.error('Could not find the bootstrap line to replace — harness is out of date.');
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
        addEventListener: () => {},
        documentElement: noopEl
    },
    window: { location: { hash: '', pathname: '/web/' }, addEventListener: () => {} },
    history: { pushState: () => {}, replaceState: () => {} }
};
sandbox.globalThis = sandbox;
sandbox.window.localStorage = sandbox.localStorage;

vm.createContext(sandbox);
vm.runInContext(src, sandbox);

const PP = sandbox.__PP;
if (!PP) { console.error('profiles.js did not export ProfilesPlugin'); process.exit(1); }

const MASTER = '11111111-1111-1111-1111-111111111111';
const SUB = '22222222-2222-2222-2222-222222222222';
const OTHER = '99999999-9999-9999-9999-999999999999';

let pass = 0, fail = 0;
function check(name, actual, expected) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a === e) { pass++; console.log(`  PASS  ${name}`); }
    else { fail++; console.log(`  FAIL  ${name} — expected ${e}, got ${a}`); }
}

function reset(currentUserId, cache, masterState, deviceGate) {
    sandbox.localStorage.clear();
    PP._switcherPrefs = null;
    // The per-device answer is cached in memory too, because it is read from the 500 ms
    // route poll. Left set, it would carry between cases and every result here would be
    // whatever the previous case happened to store.
    PP._deviceGateLoaded = false;
    PP._deviceGate = null;
    sandbox.ApiClient = { getCurrentUserId: () => currentUserId, accessToken: () => 'tok', getUrl: p => p };
    if (cache) sandbox.localStorage.setItem(PP.config.switcherModeKey, JSON.stringify(cache));
    if (masterState) sandbox.localStorage.setItem(PP.config.masterStorageKey, JSON.stringify(masterState));
    if (deviceGate && PP.config.deviceGateKey) {
        sandbox.localStorage.setItem(PP.config.deviceGateKey, JSON.stringify(deviceGate));
    }
}

console.log('Switcher preferences — all four combinations');
console.log('--------------------------------------------');

for (const ask of [true, false]) {
    for (const loc of ['button', 'menu']) {
        reset(MASTER, { askOnStartup: ask, location: loc, masterUserId: MASTER }, null);
        const label = `ask=${String(ask).padEnd(5)} loc=${loc.padEnd(6)}`;
        check(`${label} -> shouldAskOnStartup`, PP.shouldAskOnStartup(), ask);
        check(`${label} -> isMenuLocation`, PP.isMenuLocation(), loc === 'menu');
    }
}

console.log('');
console.log('Migration from the 1.3.1-beta single-mode cache');
console.log('-----------------------------------------------');

reset(MASTER, { mode: 'gate', masterUserId: MASTER }, null);
check('legacy "gate" -> prefs', PP.getSwitcherPrefs(), { askOnStartup: true, location: 'button' });

reset(MASTER, { mode: 'native', masterUserId: MASTER }, null);
check('legacy "native" -> prefs', PP.getSwitcherPrefs(), { askOnStartup: false, location: 'menu' });
check('legacy "native" -> no gate', PP.shouldAskOnStartup(), false);
check('legacy "native" -> menu location', PP.isMenuLocation(), true);

console.log('');
console.log('Unknown state must never raise the gate or hide the button');
console.log('----------------------------------------------------------');

reset(MASTER, null, null);
check('no cache -> prefs null', PP.getSwitcherPrefs(), null);
check('no cache -> no gate', PP.shouldAskOnStartup(), false);
check('no cache -> button location', PP.isMenuLocation(), false);

reset(MASTER, { masterUserId: MASTER }, null);
check('cache with neither shape -> null', PP.getSwitcherPrefs(), null);

reset(MASTER, null, null);
sandbox.localStorage.setItem(PP.config.switcherModeKey, '{not json');
check('corrupt cache -> null, no throw', PP.getSwitcherPrefs(), null);

console.log('');
console.log('Account scoping');
console.log('---------------');

reset(SUB, { askOnStartup: false, location: 'menu', masterUserId: MASTER }, { masterUserId: MASTER, masterToken: 't' });
check('sub-profile resolves via master state', PP.getSwitcherPrefs(), { askOnStartup: false, location: 'menu' });

reset(OTHER, { askOnStartup: false, location: 'menu', masterUserId: MASTER }, null);
check('different user -> cache rejected', PP.getSwitcherPrefs(), null);

reset('{' + MASTER.toUpperCase() + '}', { askOnStartup: false, location: 'menu', masterUserId: MASTER }, null);
check('guid formatting differences tolerated', PP.isMenuLocation(), true);

console.log('');
console.log('Write-through');
console.log('-------------');

reset(MASTER, null, null);
PP._cacheSwitcherPrefs(true, 'menu', MASTER);
check('cache write -> in memory', PP.getSwitcherPrefs(), { askOnStartup: true, location: 'menu' });
PP._switcherPrefs = null;
check('cache write -> persisted', PP.getSwitcherPrefs(), { askOnStartup: true, location: 'menu' });
check('ask+menu is reachable (issue #14)', [PP.shouldAskOnStartup(), PP.isMenuLocation()], [true, true]);

// ── One device keeping its own answer ────────────────────────────────────────────
//
// askOnStartup is a household setting, which is wrong for the home where the living-room
// TV should always ask and the tablet one person uses never should. Unchecking "use the
// same answer on all my devices" takes this machine out of the household answer without
// touching the others; re-checking it pushes this machine's answer back up.
//
// Every case below is red against 1.6.1, which has no such thing.

console.log('');
console.log('One device keeping its own answer');
console.log('---------------------------------');

const HAS_DEVICE_GATE = typeof PP.setDeviceGate === 'function'
    && typeof PP.isGateSynced === 'function'
    && typeof PP.getDeviceGatePref === 'function';

// Named rather than assumed, so an older build reports one clear failure here instead of
// throwing partway through and leaving the rest of the section unrun and unaccounted for.
check('the client can be told to keep a per-device answer', HAS_DEVICE_GATE, true);

if (HAS_DEVICE_GATE) {
    const HOUSEHOLD = { askOnStartup: true, location: 'button', masterUserId: MASTER };
    const own = (id, ask) => ({ [id.toLowerCase().replace(/[^a-z0-9]/g, '')]: { synced: false, askOnStartup: ask } });

    // The default. Every device that has never been told otherwise still follows the
    // household, which is the whole of the old behaviour and must not have moved.
    reset(MASTER, HOUSEHOLD, null);
    check('by default a device is synced', PP.isGateSynced(), true);
    check('by default it has no answer of its own', PP.getDeviceGatePref(), null);
    check('so the household answer still decides', PP.shouldAskOnStartup(), true);

    // The point of the feature: the device overrules the household, both ways round.
    reset(MASTER, HOUSEHOLD, null, own(MASTER, false));
    check('an unsynced device is reported unsynced', PP.isGateSynced(), false);
    check('household says ask, device says no -> no gate', PP.shouldAskOnStartup(), false);

    reset(MASTER, { askOnStartup: false, location: 'menu', masterUserId: MASTER }, null, own(MASTER, true));
    check('household says no, device says ask -> gate', PP.shouldAskOnStartup(), true);

    // Only the startup question is per-device. The location decides whether the floating
    // button exists at all, and a household with no consistent answer to that has no
    // consistent place to reach the switcher.
    check('the location is untouched by the device answer', PP.isMenuLocation(), true);
    check('and the household answer is still readable underneath',
        PP.getSwitcherPrefs(), { askOnStartup: false, location: 'menu' });

    // A device that knows its own answer does not need the server to raise the gate, so
    // this settles on the first paint instead of after the preferences request.
    reset(MASTER, null, null, own(MASTER, true));
    check('a device answer needs no household cache', PP.shouldAskOnStartup(), true);
    check('even though the household is still unknown', PP.getSwitcherPrefs(), null);

    console.log('');
    console.log('The device answer is filed per account');
    console.log('--------------------------------------');

    // Same rule the preferences cache follows. An entry left by whoever used this browser
    // last must never decide anything for the person using it now.
    // Given its own household cache, so this shows the entry being ignored rather than
    // the account simply having nothing to read — the preferences cache is account-scoped
    // too, and would have rejected itself and produced the same answer for a different
    // reason. The two answers are opposites, so only one of them can be the one reported.
    reset(OTHER, { askOnStartup: true, location: 'button', masterUserId: OTHER }, null, own(MASTER, false));
    check('another account\'s entry is ignored', PP.getDeviceGatePref(), null);
    check('and that account gets its own household answer', PP.shouldAskOnStartup(), true);

    // One answer per device for the whole household, not one per profile: a child landing
    // on the TV must get the same treatment the TV was set up with.
    reset(SUB, HOUSEHOLD, { masterUserId: MASTER, masterToken: 't' }, own(MASTER, false));
    check('a sub-profile reads the master\'s device answer', PP.getDeviceGatePref(), false);

    reset('{' + MASTER.toUpperCase() + '}', HOUSEHOLD, null, own(MASTER, false));
    check('guid formatting differences tolerated', PP.getDeviceGatePref(), false);

    reset(MASTER, HOUSEHOLD, null);
    sandbox.localStorage.setItem(PP.config.deviceGateKey, '{not json');
    check('a corrupt entry falls back to the household, no throw', PP.shouldAskOnStartup(), true);

    console.log('');
    console.log('Leaving and rejoining the household');
    console.log('-----------------------------------');

    reset(MASTER, HOUSEHOLD, null);
    PP.setDeviceGate(false, true);
    check('leaving records the answer it was given', PP.getDeviceGatePref(), true);
    PP._deviceGateLoaded = false;
    check('and it survives being re-read from storage', PP.getDeviceGatePref(), true);

    PP.setDeviceGate(false, false);
    check('the device answer can then be changed on its own', PP.getDeviceGatePref(), false);
    check('without disturbing the household answer',
        PP.getSwitcherPrefs(), { askOnStartup: true, location: 'button' });

    PP.setDeviceGate(true);
    check('rejoining clears the device answer', PP.getDeviceGatePref(), null);
    check('and the device is synced again', PP.isGateSynced(), true);
    check('so the household answer decides once more', PP.shouldAskOnStartup(), true);
    check('and nothing is left behind in storage',
        sandbox.localStorage.getItem(PP.config.deviceGateKey), null);

    // Rejoining is what carries this device's answer to the rest of the house, so the
    // request has to go out before the local entry is cleared — otherwise a server that
    // refuses drops the device onto a household answer nobody asked for. Read from the
    // source because it is the ordering inside one handler, which no stub can observe.
    const src = L.readProfiles();
    const handler = src.slice(src.indexOf("syncCb.addEventListener('change'"),
                              src.indexOf("syncCb.addEventListener('change'") + 1400);
    check('rejoining posts before it clears',
        handler.indexOf('save(') !== -1
        && handler.indexOf('save(') < handler.indexOf('setDeviceGate(true)'), true);
    check('and stays unsynced if the server refuses',
        /if\s*\(!applied\)\s*\{\s*syncCb\.checked = false;\s*return;/.test(handler), true);

    console.log('');
    console.log('The dialog that sets it');
    console.log('-----------------------');

    // Nothing else in the suite runs showSwitcherModeModal, and "it parses" is not "it
    // runs" — that distinction shipped 1.5.2 and 1.5.3-beta dead on arrival. Render it for
    // real and read the markup back.
    const boxChecked = (html, id) => new RegExp('id="' + id + '"\\s+checked\\s').test(html);

    function renderSwitcherModal() {
        const nodes = {};
        const listeners = {};
        const node = id => (nodes[id] = nodes[id] || {
            id: id, checked: false, style: {}, textContent: '', attrs: {},
            getAttribute: k => (k in nodes[id].attrs ? nodes[id].attrs[k] : null),
            setAttribute: (k, v) => { nodes[id].attrs[k] = v; },
            hasAttribute: k => k in nodes[id].attrs,
            addEventListener: (type, fn) => { (listeners[id] = listeners[id] || {})[type] = fn; },
            querySelector: () => null, querySelectorAll: () => [], focus() {}
        });

        const content = {
            innerHTML: '',
            querySelector: sel => (sel.charAt(0) === '#' ? node(sel.slice(1)) : null),
            querySelectorAll: () => [],
            addEventListener() {}
        };
        sandbox.document.querySelector = sel =>
            (sel === '.profiles-modal-content' ? content : null);

        let threw = null;
        try { PP.showSwitcherModeModal(); } catch (e) { threw = e; }

        // A browser sets .checked from the rendered attribute, and the handlers read
        // .checked. Without this the stub reports every box unticked no matter what was
        // drawn, and a handler that read the wrong box would still look right here.
        ['switcher-ask-startup', 'switcher-sync-devices'].forEach(id => {
            if (nodes[id]) nodes[id].checked = boxChecked(content.innerHTML, id);
        });

        return { html: content.innerHTML, nodes, listeners, threw };
    }

    const MASTER_STATE = { masterUserId: MASTER, masterToken: 't' };

    reset(MASTER, HOUSEHOLD, MASTER_STATE);
    let ui = renderSwitcherModal();
    check('the dialog renders without throwing',
        ui.threw === null || ui.threw.message, true);
    check('and puts the sync box on screen',
        ui.html.indexOf('id="switcher-sync-devices"') !== -1, true);
    check('a synced device shows the box ticked', boxChecked(ui.html, 'switcher-sync-devices'), true);
    check('and the startup box shows the household answer',
        boxChecked(ui.html, 'switcher-ask-startup'), true);
    check('both boxes are wired to something',
        !!(ui.listeners['switcher-ask-startup'] && ui.listeners['switcher-ask-startup'].change
           && ui.listeners['switcher-sync-devices'] && ui.listeners['switcher-sync-devices'].change), true);

    // The box has to show what the device will actually do, not what the household holds,
    // or somebody reads "ask on startup" off a screen that will never ask.
    reset(MASTER, HOUSEHOLD, MASTER_STATE, own(MASTER, false));
    ui = renderSwitcherModal();
    check('an unsynced device shows the box unticked',
        boxChecked(ui.html, 'switcher-sync-devices'), false);
    check('and the startup box shows the device answer, not the household one',
        boxChecked(ui.html, 'switcher-ask-startup'), false);

    // Unticking is the one path that touches no network at all: it takes the answer
    // already on screen so that leaving the household changes nothing by itself.
    reset(MASTER, HOUSEHOLD, MASTER_STATE);
    ui = renderSwitcherModal();
    ui.nodes['switcher-sync-devices'].checked = false;
    ui.listeners['switcher-sync-devices'].change();
    check('unticking it records the answer already on screen', PP.getDeviceGatePref(), true);
    check('and leaves the household answer alone',
        PP.getSwitcherPrefs(), { askOnStartup: true, location: 'button' });

    // And once unsynced, changing the startup box stays local.
    ui.nodes['switcher-ask-startup'].checked = false;
    ui.listeners['switcher-ask-startup'].change();
    check('the startup box then writes only to this device', PP.getDeviceGatePref(), false);
    check('still without moving the household answer',
        PP.getSwitcherPrefs(), { askOnStartup: true, location: 'button' });

    console.log('');
    console.log('What a sign-out does to it');
    console.log('--------------------------');

    // The one Bonfire key that outlives a sign-out. A TV told to ignore the household
    // answer must not quietly rejoin it because somebody signed out on it, and the
    // per-account filing above is what makes leaving it there safe.
    const signOut = L.readProfiles();
    const block = signOut.slice(signOut.indexOf("document.addEventListener('usersignedout'"),
                                signOut.indexOf("document.addEventListener('usersignedout'") + 1400);
    check('sign-out does not remove the stored device answer',
        block.indexOf('removeItem(this.config.deviceGateKey)') === -1, true);
    check('but it does drop the in-memory copy',
        /_deviceGateLoaded = false/.test(block), true);
}

console.log('');
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
