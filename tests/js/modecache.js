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
    // 1.7.1 cached its per-device answer in memory. Cleared so that, run against that
    // build, each case reads what it stored rather than the previous case's answer.
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

// ── One answer per account ───────────────────────────────────────────────────────
//
// 1.6 to 1.7.1 had "Use the same answer on all my devices": unticked, a device kept its own
// answer to the startup question in localStorage, outranking the account's. 1.7.2 cut it:
// ask-on-startup is one answer per account. A TV upgraded from 1.7.1 still carries the old
// entry, so nothing may honour it, it must be cleared, and the dialog must offer no way to
// make one.
//
// Every case below is red against 1.7.1, where the entry decided.

console.log('');
console.log('One answer per account');
console.log('----------------------');

const own = (id, ask) => ({ [id.toLowerCase().replace(/[^a-z0-9]/g, '')]: { synced: false, askOnStartup: ask } });
const HOUSEHOLD = { askOnStartup: true, location: 'button', masterUserId: MASTER };
const QUIET = { askOnStartup: false, location: 'button', masterUserId: MASTER };

reset(MASTER, HOUSEHOLD, null, own(MASTER, false));
check('household asks, leftover device entry says no -> the gate is still asked for',
    PP.shouldAskOnStartup(), true);

reset(MASTER, QUIET, null, own(MASTER, true));
check('household does not ask, leftover entry says ask -> no gate', PP.shouldAskOnStartup(), false);

// 1.7.1 answered this from the device entry alone, before the account was known.
reset(MASTER, null, null, own(MASTER, true));
check('a leftover entry with nothing else known does not raise the gate', PP.shouldAskOnStartup(), false);

reset(SUB, HOUSEHOLD, { masterUserId: MASTER, masterToken: 't' }, own(MASTER, false));
check('nor does it decide for a profile of that account', PP.shouldAskOnStartup(), true);

console.log('');
console.log('The leftover is cleared, once');
console.log('-----------------------------');

// Read from the source because init does more than this harness can stand up. The key must
// be cleared at startup and not touched by the route poll, which runs twice a second forever.
{
    const s = L.readProfiles();
    const initAt = s.indexOf('        init: function ()');
    const initBody = initAt === -1 ? '' : s.slice(initAt, s.indexOf('\n        },', initAt));
    check('init removes the stored device answer',
        /localStorage\.removeItem\(this\.config\.deviceGateKey\)/.test(initBody), true);
    const crAt = s.indexOf('        checkRoute: function');
    const crBody = crAt === -1 ? '' : s.slice(crAt, s.indexOf('\n        },', crAt));
    check('and checkRoute never mentions it', crAt !== -1 && crBody.indexOf('deviceGate') === -1, true);
}

(async () => {
    console.log('');
    console.log('The dialog');
    console.log('----------');

    // Nothing else in the suite runs showSwitcherModeModal, and "it parses" is not "it runs"
    // (1.5.2 and 1.5.3-beta shipped dead that way). Render it and read the markup back.
    const boxChecked = (html, id) => new RegExp('id="' + id + '"\\s+checked\\s').test(html);
    const posts = [];

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

        posts.length = 0;
        sandbox.fetch = (url, init) => {
            const body = init && init.body ? JSON.parse(init.body) : null;
            posts.push({ url: String(url), body: body });
            return Promise.resolve({ ok: true, json: () => Promise.resolve(body || {}) });
        };

        let threw = null;
        try { PP.showSwitcherModeModal(); } catch (e) { threw = e; }

        // A browser sets .checked from the rendered attribute, and the handlers read it.
        if (nodes['switcher-ask-startup']) {
            nodes['switcher-ask-startup'].checked = boxChecked(content.innerHTML, 'switcher-ask-startup');
        }
        return { html: content.innerHTML, nodes, listeners, threw };
    }
    const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };

    const MASTER_STATE = { masterUserId: MASTER, masterToken: 't' };

    reset(MASTER, HOUSEHOLD, MASTER_STATE);
    let ui = renderSwitcherModal();
    check('the dialog renders without throwing', ui.threw === null || ui.threw.message, true);
    check('and has no "same answer on all my devices" box',
        ui.html.indexOf('switcher-sync-devices') === -1, true);
    check('the startup box shows the account\'s answer', boxChecked(ui.html, 'switcher-ask-startup'), true);

    // The box shows what the device will do, and a leftover entry no longer changes that.
    reset(MASTER, HOUSEHOLD, MASTER_STATE, own(MASTER, false));
    ui = renderSwitcherModal();
    check('a leftover device entry does not change what the box shows',
        boxChecked(ui.html, 'switcher-ask-startup'), true);

    // Changing the box always goes to the account.
    reset(MASTER, HOUSEHOLD, MASTER_STATE);
    ui = renderSwitcherModal();
    ui.nodes['switcher-ask-startup'].checked = false;
    ui.listeners['switcher-ask-startup'].change();
    await settle();
    const prefPosts = posts.filter(p => /plugins\/profiles\/preferences/.test(p.url));
    check('unticking it saves to the account', prefPosts.length === 1 && prefPosts[0].body.askOnStartup, false);
    check('and the account\'s answer follows', PP.shouldAskOnStartup(), false);
    check('nothing is stored on the device',
        sandbox.localStorage.getItem(PP.config.deviceGateKey), null);

    console.log('');
    console.log(`${pass} passed, ${fail} failed`);
    process.exit(fail === 0 ? 0 : 1);
})().catch(e => { console.error(e); process.exit(1); });
