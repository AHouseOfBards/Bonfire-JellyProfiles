/*
 * The Bonfire settings toggles must not keep a state the server never accepted.
 *
 * A failed save used to be a console.error and nothing else. The checkbox stayed where
 * the user put it while the server still held the old value, and nothing on screen said
 * so. One of the three is allowHouseholdLanBypass, which decides whether anyone in your
 * Bonfire can open your account on your home network without your PIN — so the panel
 * could read "allowed" when it is not, or "not allowed" when it is. Either way the
 * person believes a security setting that is not true, and the only way to find out is
 * to reopen the panel.
 *
 * Same technique as busy.js and session.js: evaluate the shipped file with init() swapped
 * for an export, then give renderBonfireStatus only as much DOM as it touches. The point
 * is to run the real handler against a real 500, not to read the source and check it
 * mentions the word "revert".
 *
 * Point it at an older client to watch it fail:
 *
 *     node tests/js/bonfiretoggle.js /path/to/old/profiles.js
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

const SRC_PATH = L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

const IDS = [
    '#bonfire-hide-mine-checkbox',
    '#bonfire-hide-others-checkbox',
    '#bonfire-lan-bypass-checkbox',
    '#bonfire-settings-status'
];

/**
 * @param respond  what the settings POST returns: 'ok', 'http500' or 'network'
 * @param initial  the state the panel is rendered with, i.e. what the server holds
 */
function build(respond, initial) {
    initial = initial || { hideMine: false, hideOthers: false, lanBypass: false };

    // Controllable timers. The save is debounced 300ms, so a no-op setTimeout — which is
    // what the other harnesses install — would mean nothing was ever sent and every
    // assertion below would be about a request that never happened.
    const timers = [];
    function setTimeoutStub(fn, ms) { timers.push({ fn: fn, ms: ms }); return timers.length; }
    function clearTimeoutStub(id) { if (timers[id - 1]) timers[id - 1].cancelled = true; }

    function makeEl(props) {
        const listeners = {};
        const el = Object.assign({
            style: {},
            attrs: {},
            checked: false,
            disabled: false,
            textContent: '',
            addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
            removeEventListener: () => {},
            hasAttribute: k => k in el.attrs,
            getAttribute: k => (k in el.attrs ? el.attrs[k] : null),
            setAttribute: (k, v) => { el.attrs[k] = v; },
            focus: () => {},
            fire: type => (listeners[type] || []).forEach(fn => fn({ key: '', preventDefault() {} })),
            listeners: listeners
        }, props);
        return el;
    }

    const els = {};
    IDS.forEach(id => { els[id] = makeEl({}); });
    els['#bonfire-hide-mine-checkbox'].checked = initial.hideMine;
    els['#bonfire-hide-others-checkbox'].checked = initial.hideOthers;
    els['#bonfire-lan-bypass-checkbox'].checked = initial.lanBypass;

    let rendered = '';
    const container = {
        set innerHTML(v) { rendered = v; },
        get innerHTML() { return rendered; },
        querySelector: sel => els[sel] || null,
        querySelectorAll: () => []
    };

    const requests = [];
    function fetchStub(url, opts) {
        requests.push({ url: url, opts: opts, body: JSON.parse((opts && opts.body) || '{}') });
        if (respond === 'network') return Promise.reject(new Error('offline'));
        if (respond === 'http500') {
            return Promise.resolve({
                ok: false, status: 500, text: () => Promise.resolve('boom')
            });
        }
        return Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve('') });
    }

    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', language: 'en' },
        setTimeout: setTimeoutStub, clearTimeout: clearTimeoutStub,
        setInterval() {}, clearInterval() {},
        fetch: fetchStub,
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector() { return null; }, querySelectorAll() { return []; },
            getElementById: () => null,
            createElement: () => makeEl({ appendChild() {}, remove() {} }),
            head: { appendChild() {} },
            body: { classList: { add() {}, remove() {}, contains: () => false }, appendChild() {} },
            documentElement: { style: { cssText: '', removeProperty() {}, setProperty() {} },
                               classList: { add() {}, remove() {}, contains: () => false } }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);

    const plugin = sandbox.__PROFILES;

    // The confirm dialog is a different unit with its own DOM. Stubbed so both branches
    // can be driven deliberately: onConfirm for the save path, onCancel for the revert.
    let lastConfirm = null;
    plugin.showConfirmDialog = (title, body, onConfirm, onCancel) => {
        lastConfirm = { title: title, body: body, confirm: onConfirm, cancel: onCancel };
    };
    plugin.showAlert = () => {};
    plugin.loadBonfireStatus = () => {};
    plugin.getAuthHeaders = () => ({ 'X-Emby-Token': 'token' });

    const apiClient = { getUrl: u => '/' + u };

    plugin.renderBonfireStatus(
        container,
        { },
        { isOwner: true, isMember: false, ownedCode: 'B7F8XA', ownedMembers: [],
          hideMySubProfilesFromOthers: initial.hideMine,
          hideOthersSubProfilesFromMe: initial.hideOthers,
          allowHouseholdLanBypass: initial.lanBypass,
          isAdministrator: false, hasPin: true },
        apiClient,
        'master-token'
    );

    // Runs every timer queued so far, including ones queued while draining. Bounded, so a
    // handler that re-arms itself forever fails the test rather than hanging it.
    function runTimers() {
        for (let round = 0; round < 20; round++) {
            const due = timers.filter(x => !x.cancelled && !x.done);
            if (!due.length) return;
            due.forEach(x => { x.done = true; x.fn(); });
        }
        throw new Error('timers never settled');
    }

    // Lets the fetch promise chain resolve before assertions look at the result.
    function settle() {
        return new Promise(r => setImmediate(r)).then(() => new Promise(r => setImmediate(r)));
    }

    return { els, container, requests, rendered: () => rendered, runTimers, settle,
             confirm: () => lastConfirm };
}

main();
async function main() {

console.log('\n── The panel has somewhere to report a failure ─────────────────');

const env = build('ok');
ok('the settings section renders a status line',
   env.rendered().indexOf('id="bonfire-settings-status"') !== -1);
ok('announced to a screen reader without moving focus',
   /id="bonfire-settings-status"[^>]*aria-live/.test(env.rendered()));
ok('and it starts hidden', /id="bonfire-settings-status"[\s\S]{0,200}display:\s*none/.test(env.rendered()));

console.log('\n── A successful save is confirmed ──────────────────────────────');

const good = build('ok');
good.els['#bonfire-hide-mine-checkbox'].checked = true;
good.els['#bonfire-hide-mine-checkbox'].fire('change');
good.runTimers();
await good.settle();

ok('the request went out (' + good.requests.length + ')', good.requests.length === 1);
ok('to bonfire/settings',
   good.requests.length > 0 && good.requests[0].url.indexOf('bonfire/settings') !== -1);
ok('carrying the new value',
   good.requests.length > 0 && good.requests[0].body.hideMySubProfilesFromOthers === true);
ok('the box keeps the new state', good.els['#bonfire-hide-mine-checkbox'].checked === true);
ok('and the panel says it saved',
   good.els['#bonfire-settings-status'].textContent.length > 0
   && good.els['#bonfire-settings-status'].style.display === 'block');

console.log('\n── A rejected save is reverted, and says why ───────────────────');

const bad = build('http500');
bad.els['#bonfire-hide-mine-checkbox'].checked = true;
bad.els['#bonfire-hide-mine-checkbox'].fire('change');
bad.runTimers();
await bad.settle();

ok('the request was attempted', bad.requests.length === 1);
ok('the box goes back to what the server actually holds',
   bad.els['#bonfire-hide-mine-checkbox'].checked === false);
ok('the failure is shown on screen, not only in the console',
   bad.els['#bonfire-settings-status'].style.display === 'block'
   && bad.els['#bonfire-settings-status'].textContent.length > 0);
ok('and it does not claim to have saved',
   bad.els['#bonfire-settings-status'].textContent.toLowerCase().indexOf('saved') === -1);
ok('the boxes are usable again afterwards',
   bad.els['#bonfire-hide-mine-checkbox'].disabled === false
   && bad.els['#bonfire-lan-bypass-checkbox'].disabled === false);

console.log('\n── A dropped connection is treated the same as a rejection ─────');

const offline = build('network');
offline.els['#bonfire-hide-others-checkbox'].checked = true;
offline.els['#bonfire-hide-others-checkbox'].fire('change');
offline.runTimers();
await offline.settle();

ok('the box is reverted', offline.els['#bonfire-hide-others-checkbox'].checked === false);
ok('and the failure is on screen',
   offline.els['#bonfire-settings-status'].style.display === 'block');

console.log('\n── The LAN bypass is the one that matters ──────────────────────');

// Turning it on widens who can reach the account, so it is confirmed first. If the save
// then fails, showing it as on is worse here than anywhere else on the panel.
const lan = build('http500', { hideMine: false, hideOthers: false, lanBypass: false });
lan.els['#bonfire-lan-bypass-checkbox'].checked = true;
lan.els['#bonfire-lan-bypass-checkbox'].fire('change');

ok('turning it on asks first', lan.confirm() !== null);
ok('no request goes out before the answer', lan.requests.length === 0);

lan.confirm().cancel();
ok('backing out puts the box back', lan.els['#bonfire-lan-bypass-checkbox'].checked === false);

const lan2 = build('http500', { hideMine: false, hideOthers: false, lanBypass: false });
lan2.els['#bonfire-lan-bypass-checkbox'].checked = true;
lan2.els['#bonfire-lan-bypass-checkbox'].fire('change');
lan2.confirm().confirm();
lan2.runTimers();
await lan2.settle();

ok('confirming sends it', lan2.requests.length === 1);
ok('a rejected LAN bypass does not stay looking enabled',
   lan2.els['#bonfire-lan-bypass-checkbox'].checked === false);
ok('and the panel says so',
   lan2.els['#bonfire-settings-status'].style.display === 'block');

// Turning it off is always safe, so it saves without asking.
const lanOff = build('ok', { hideMine: false, hideOthers: false, lanBypass: true });
lanOff.els['#bonfire-lan-bypass-checkbox'].checked = false;
lanOff.els['#bonfire-lan-bypass-checkbox'].fire('change');
ok('turning it off does not ask', lanOff.confirm() === null);
lanOff.runTimers();
await lanOff.settle();
ok('and saves straight away', lanOff.requests.length === 1);
ok('sending false', lanOff.requests[0].body.allowHouseholdLanBypass === false);

console.log('\n── A second failure reverts to the server state, not the first attempt ──');

// The revert target is the last state the server acknowledged, so two failed toggles in
// a row both land back on what is actually stored rather than on each other.
const twice = build('http500', { hideMine: true, hideOthers: false, lanBypass: false });
twice.els['#bonfire-hide-mine-checkbox'].checked = false;
twice.els['#bonfire-hide-mine-checkbox'].fire('change');
twice.runTimers();
await twice.settle();
ok('first failure restores the stored value',
   twice.els['#bonfire-hide-mine-checkbox'].checked === true);

twice.els['#bonfire-hide-mine-checkbox'].checked = false;
twice.els['#bonfire-hide-mine-checkbox'].fire('change');
twice.runTimers();
await twice.settle();
ok('so does the second', twice.els['#bonfire-hide-mine-checkbox'].checked === true);

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');

}
