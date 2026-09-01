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

function reset(currentUserId, cache, masterState) {
    sandbox.localStorage.clear();
    PP._switcherPrefs = null;
    sandbox.ApiClient = { getCurrentUserId: () => currentUserId, accessToken: () => 'tok', getUrl: p => p };
    if (cache) sandbox.localStorage.setItem(PP.config.switcherModeKey, JSON.stringify(cache));
    if (masterState) sandbox.localStorage.setItem(PP.config.masterStorageKey, JSON.stringify(masterState));
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

console.log('');
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
