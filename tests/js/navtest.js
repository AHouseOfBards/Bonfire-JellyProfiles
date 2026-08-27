// Exercises the 1.4.2 navigation work: the ticket that stops a slow form drawing
// itself over the screen you actually asked for, and the cache that stops four of
// every five requests being made at all.
//
// This is a race, so most of it is about interleaving: the assertions below run
// the *reported* sequence — click one screen, click another before the first has
// returned, then let the first return — and check the second one survives.

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

const noopEl = {
    style: { removeProperty() {}, setProperty() {}, getPropertyValue: () => '' },
    classList: { add() {}, remove() {}, contains: () => false },
    appendChild() {}, addEventListener() {}, removeEventListener() {}, remove() {}
};

// Every request the code makes, in order, so we can count what a second open costs.
const requests = [];
let failNext = false;

const sandbox = {
    console,
    localStorage: makeStorage(),
    sessionStorage: makeStorage(),
    setTimeout: (fn) => { if (typeof fn === 'function') fn(); return 0; },
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {},
    requestAnimationFrame: () => 0,
    MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
    fetch: (url) => {
        requests.push(String(url));
        if (failNext) return Promise.reject(new Error('server unreachable'));
        return Promise.resolve({ ok: true, json: () => Promise.resolve([]) });
    },
    document: {
        head: noopEl, body: noopEl, documentElement: noopEl,
        createElement: () => Object.assign({}, noopEl, { innerHTML: '', querySelector: () => null, querySelectorAll: () => [] }),
        querySelector: () => null,
        querySelectorAll: () => [],
        getElementById: () => null,
        addEventListener() {}, removeEventListener() {}
    },
    window: { location: { hash: '', reload() {} }, addEventListener() {}, navigator: { userAgent: 'harness' } },
    navigator: { userAgent: 'harness' },
    ApiClient: {
        getUrl: (p) => '/' + p,
        serverAddress: () => 'http://localhost:8096'
    }
};
sandbox.window.localStorage = sandbox.localStorage;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
vm.runInContext(src, sandbox);

const PP = sandbox.__PP;
if (!PP) { console.error('plugin object not exported'); process.exit(1); }

let pass = 0, fail = 0;
function check(name, actual, expected) {
    const ok = Object.is(actual, expected);
    if (ok) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + '\n          expected: ' + expected + '\n          actual:   ' + actual); }
}

const masterState = { masterToken: 'tok', masterUserId: 'abc' };

(async () => {

console.log('── A ticket is claimed by every screen change ────────────────');
{
    const a = PP.beginNavigation();
    const b = PP.beginNavigation();
    check('tickets are distinct', a === b, false);
    check('later ticket wins', PP.navIsCurrent(b), true);
    check('earlier ticket is stale', PP.navIsCurrent(a), false);

    // The synchronous renders are the ones a slow form loses the race to, so they
    // must claim the screen too — that is the whole reported bug.
    const before = PP._navTicket;
    PP.renderOverlayContent({
        innerHTML: '',
        querySelector: () => null,
        querySelectorAll: () => [],
        addEventListener() {}
    }, []);
    check('renderOverlayContent claims the screen', PP._navTicket > before, true);
}

console.log();
console.log('── The reported race ─────────────────────────────────────────');
{
    // t=0 the user opens a form; its data has not arrived.
    const formTicket = PP.beginNavigation();

    // t=0.5 nothing has drawn, so they click something else, which renders at once.
    const otherTicket = PP.beginNavigation();

    // t=2.8 the form's response finally lands.
    check('the slow form does NOT draw', PP.navIsCurrent(formTicket), false);
    check('the screen they asked for holds', PP.navIsCurrent(otherTicket), true);
}

console.log();
console.log('── Four of five requests stop happening ──────────────────────');
{
    PP.clearSharedFormData();
    requests.length = 0;

    const first = await PP.fetchSharedFormData(sandbox.ApiClient, masterState);
    const firstCount = requests.length;
    check('the first open fetches the shared data', firstCount > 0, true);
    check('it returns all four parts',
        ['libraries', 'devices', 'libraryTags', 'avatarLibrary'].every(k => k in first), true);

    requests.length = 0;
    const second = await PP.fetchSharedFormData(sandbox.ApiClient, masterState);
    check('a second open makes NO requests', requests.length, 0);
    check('and gets the same object back', second === first, true);

    // Two profiles opened at once must not double-fetch either.
    PP.clearSharedFormData();
    requests.length = 0;
    const [p1, p2] = await Promise.all([
        PP.fetchSharedFormData(sandbox.ApiClient, masterState),
        PP.fetchSharedFormData(sandbox.ApiClient, masterState)
    ]);
    check('concurrent opens share one fetch', requests.length, firstCount);
    check('and resolve to the same object', p1 === p2, true);
}

console.log();
console.log('── A failure is never cached ─────────────────────────────────');
{
    // A server that was briefly unreachable must not leave every later form broken
    // until the overlay is closed.
    PP.clearSharedFormData();
    failNext = true;
    let threw = false;
    try { await PP.fetchSharedFormData(sandbox.ApiClient, masterState); }
    catch (e) { threw = true; }
    check('the failure propagates', threw, true);
    check('nothing was cached', PP._sharedForm, null);

    failNext = false;
    requests.length = 0;
    const recovered = await PP.fetchSharedFormData(sandbox.ApiClient, masterState);
    check('the next attempt retries', requests.length > 0, true);
    check('and succeeds', !!recovered, true);
}

console.log();
console.log('── Closing the gate drops the cache ──────────────────────────');
{
    await PP.fetchSharedFormData(sandbox.ApiClient, masterState);
    check('cache is warm', PP._sharedForm !== null, true);

    PP.removeProfileOverlay();
    check('closing the overlay clears it', PP._sharedForm, null);

    // Otherwise a library added on the server would never appear until reload.
    requests.length = 0;
    await PP.fetchSharedFormData(sandbox.ApiClient, masterState);
    check('reopening re-reads the server', requests.length > 0, true);
}

console.log();
console.log('── The prefetch is best-effort ───────────────────────────────');
{
    // No master state yet: the gate must still open rather than throwing.
    PP.clearSharedFormData();
    sandbox.localStorage.removeItem(PP.config.masterStorageKey);
    let threw = false;
    try {
        PP.showProfileOverlay([]);
    } catch (e) { threw = true; }
    check('no master state does not break the gate', threw, false);

    // A failing prefetch must not surface as an unhandled rejection.
    PP.clearSharedFormData();
    sandbox.localStorage.setItem(PP.config.masterStorageKey, JSON.stringify(masterState));
    failNext = true;
    threw = false;
    try { PP.showProfileOverlay([]); } catch (e) { threw = true; }
    check('a failing prefetch is swallowed', threw, false);
    failNext = false;
}

console.log();
console.log(pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);

})();
