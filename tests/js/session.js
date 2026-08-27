// Behavioural test of the active-profile session marker, against the real profiles.js.
//
// The Android report was: tapping a profile shows the picker again, forever. The marker
// that says "a profile is active" lives in sessionStorage, and on some runtimes
// sessionStorage does not survive the reload that finishes a switch. When it vanishes,
// validateSessionState() decides the app was closed and reverts to the master — by
// reloading, which loses it again. That is the loop.
//
// This does not reimplement anything: it evaluates the shipped file with init() swapped
// for an export, so the methods under test are the ones that ship. A "reload" is a fresh
// evaluation (so _sessionMemory starts empty, as it would) against the same localStorage,
// with sessionStorage either carried over or thrown away.
const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SRC_PATH = L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function makeStorage(initial, broken) {
    const data = Object.assign({}, initial || {});
    return {
        data,
        getItem(k) {
            if (broken) throw new Error('storage disabled');
            return Object.prototype.hasOwnProperty.call(data, k) ? data[k] : null;
        },
        setItem(k, v) {
            if (broken) throw new Error('storage disabled');
            data[k] = String(v);
        },
        removeItem(k) {
            if (broken) throw new Error('storage disabled');
            delete data[k];
        }
    };
}

// One page load. localStorage is handed in so it persists across "reloads"; sessionStorage
// is whatever that runtime does on reload.
function load(localData, sessionData, opts) {
    opts = opts || {};
    const localStorage = makeStorage(localData);
    const sessionStorage = makeStorage(sessionData, opts.sessionBroken);

    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage,
        sessionStorage,
        navigator: { userAgent: opts.userAgent || 'Mozilla/5.0 (Linux; Android 14)' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        document: {
            addEventListener() {}, removeEventListener() {}, querySelector() { return null; },
            querySelectorAll() { return []; }, getElementById() { return null; },
            createElement() { return { style: {}, classList: { add() {}, remove() {} }, appendChild() {} }; },
            head: { appendChild() {} }, body: { classList: { add() {}, remove() {} } },
            documentElement: { style: { cssText: '', removeProperty() {} }, classList: { add() {}, remove() {} } }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;

    // profiles.js is deferred, so anything the injected head script did has already
    // happened by the time it runs. Modelling that is the whole point: reading the flag
    // from localStorage looked correct and was dead, because the head script had taken it.
    if (opts.headScriptRan) {
        sandbox.__jpSwitching = 1;
        delete localStorage.data['jpf-sw'];
    }

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);

    return { plugin: sandbox.__PROFILES, localStorage, sessionStorage };
}

const ACTIVE = 'jellyfin_profiles_active_token';
const SWITCHING = 'jpf-sw';

console.log();
console.log('── The marker survives our own reload, on any runtime ──────────');

// A switch: write the marker, raise the switching flag, then reload.
let page = load({}, {});
page.plugin._sessionSet(ACTIVE, 'token-for-kids-profile');
page.localStorage.setItem(SWITCHING, '1');
ok('the marker is written to sessionStorage',
    page.sessionStorage.data[ACTIVE] === 'token-for-kids-profile');
ok('and mirrored into localStorage',
    typeof page.localStorage.data['jpf-persist-' + ACTIVE] === 'string');

let carried = Object.assign({}, page.localStorage.data);

// A runtime that keeps sessionStorage across reload — desktop, and what we always assumed.
let keeps = load(carried, Object.assign({}, page.sessionStorage.data));
ok('a reload that keeps sessionStorage still sees the profile',
    keeps.plugin.isProfileSessionActive() === true);

// A runtime that throws sessionStorage away on reload — Tizen, and the Android report.
let loses = load(carried, {});
ok('a reload that loses sessionStorage still sees the profile (the bug)',
    loses.plugin.isProfileSessionActive() === true);
ok('and it is the same token, not a truthy placeholder',
    loses.plugin._sessionGet(ACTIVE) === 'token-for-kids-profile');
ok('which is put back into sessionStorage for the rest of the page',
    loses.sessionStorage.data[ACTIVE] === 'token-for-kids-profile');

console.log();
console.log('── …including once the head script has eaten the flag ──────────');

// This is the real sequence, and the one that was never modelled: <head> runs the
// injected script, which removes jpf-sw and sets window.__jpSwitching, and only then
// does the deferred profiles.js get to look. Reading localStorage alone returns null
// every time, so the mirror is never consulted and the switch loop comes back.
let handedOver = load(carried, {}, { headScriptRan: true });
ok('the flag survives the head script as a window global',
    handedOver.plugin.isProfileSessionActive() === true);
ok('and it is still the right token',
    handedOver.plugin._sessionGet(ACTIVE) === 'token-for-kids-profile');

// Without the head script having run and without the key, there is no reload in flight.
const noFlag = Object.assign({}, carried);
delete noFlag['jpf-sw'];
let neither = load(noFlag, {});
ok('no global and no key means no reload in flight',
    neither.plugin.isProfileSessionActive() === false);

console.log();
console.log('── Closing the app still drops back to the picker ──────────────');

// Same lost sessionStorage, but no switching flag: this is a cold start, not our reload.
const cold = Object.assign({}, carried);
delete cold[SWITCHING];
let closed = load(cold, {});
ok('no switching flag means the mirror is not trusted',
    closed.plugin.isProfileSessionActive() === false);

// The flag is set but the mirror is old — a flag nobody cleared, not a reload.
const stale = Object.assign({}, carried);
stale['jpf-persist-' + ACTIVE] = JSON.stringify({ v: 'token-for-kids-profile', t: Date.now() - 120000 });
let stalePage = load(stale, {});
ok('a mirror older than the window is not trusted',
    stalePage.plugin.isProfileSessionActive() === false);

console.log();
console.log('── Upgrading from the Tizen-only mirror ────────────────────────');

// Written by 1.4.5 and earlier: a bare string with no timestamp. Must not sign anyone out.
const legacy = Object.assign({}, cold);
legacy['jpf-persist-' + ACTIVE] = 'token-from-an-older-build';
legacy[SWITCHING] = '1';
let legacyPage = load(legacy, {});
ok('a timestampless mirror is still honoured mid-reload',
    legacyPage.plugin._sessionGet(ACTIVE) === 'token-from-an-older-build');

console.log();
console.log('── Signing out clears every copy ───────────────────────────────');

let out = load(Object.assign({}, carried), Object.assign({}, page.sessionStorage.data));
out.plugin._sessionGet(ACTIVE);           // pull it into the in-memory tier first
out.plugin._sessionRemove(ACTIVE);
ok('sessionStorage copy is gone', out.sessionStorage.data[ACTIVE] === undefined);
ok('the mirror is gone', out.localStorage.data['jpf-persist-' + ACTIVE] === undefined);
ok('and the in-memory copy is gone too', out.plugin._sessionGet(ACTIVE) === null);

console.log();
console.log('── A runtime with no working sessionStorage at all ─────────────');

let noSession = load({}, {}, { sessionBroken: true });
noSession.plugin._sessionSet(ACTIVE, 'token-x');
ok('the value is still readable within the page',
    noSession.plugin._sessionGet(ACTIVE) === 'token-x');
ok('and it still reached the mirror',
    typeof noSession.localStorage.data['jpf-persist-' + ACTIVE] === 'string');

console.log();
console.log('── The revert loop has a backstop ──────────────────────────────');

let guard = load({}, {});
if (typeof guard.plugin._revertReloadAllowed !== 'function') {
    ok('the revert path has a loop backstop at all', false);
} else {
const first = [
    guard.plugin._revertReloadAllowed(),
    guard.plugin._revertReloadAllowed(),
    guard.plugin._revertReloadAllowed()
];
ok('three reverts in a row are allowed', first.every(Boolean));
ok('the fourth within the window is not', guard.plugin._revertReloadAllowed() === false);
ok('and it stays refused while the loop continues',
    guard.plugin._revertReloadAllowed() === false);

// The guard lives in localStorage, so it counts across reloads — which is the only place
// it matters, since each revert IS a reload.
let guardCarry = load(Object.assign({}, guard.localStorage.data), {});
ok('the count survives a reload', guardCarry.plugin._revertReloadAllowed() === false);

// A revert half an hour later is not part of that loop.
const old = Object.assign({}, guard.localStorage.data);
old['jpf-revert-guard'] = JSON.stringify({ n: 9, t: Date.now() - 1800000 });
let later = load(old, {});
ok('a revert long afterwards starts a fresh window',
    later.plugin._revertReloadAllowed() === true);
}

console.log();
console.log('── No platform sniffing left ───────────────────────────────────');

const text = fs.readFileSync(SRC_PATH, 'utf8');
ok('the runtime sniff is gone', !/_isTizenRuntime/.test(text));
ok('the mirror key is unchanged, so upgrades keep their marker',
    text.includes("SESSION_MIRROR_PREFIX: 'jpf-persist-'"));

// Structural, not behavioural: _revealPage depends on DOM state this sandbox does not
// model. What matters is that the flag the mirror trusts is cleared once the reload it
// was raised for has landed, and not while another is in flight.
ok('every reload we perform marks itself',
    (text.match(/this._reloading = true;/g) || []).length === 2);
const revealAt = text.indexOf('this._pageRevealed = true;');
const clearAt = text.indexOf('removeItem(this.config.switchingKey)', revealAt);
ok('the reveal clears the switching flag',
    revealAt > 0 && clearAt > revealAt && clearAt - revealAt < 900);
const guardAt = text.indexOf('if (!this._reloading) {');
ok('but not when another reload is already in flight',
    guardAt > 0 && guardAt < clearAt && clearAt - guardAt < 200);

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
fails.forEach(f => console.log('  FAIL  ' + f));
process.exit(fails.length ? 1 : 0);
