/*
 * The main account's PIN, on the client.
 *
 * What it holds the script to:
 *
 *   - Manage Profiles and Settings ask for the main PIN whenever the account HAS one,
 *     not only when opening it asks for one. On the home network with the bypass on,
 *     requiresPin is false; Manage Profiles used to open without asking, and every save
 *     then failed as a wrong PIN.
 *   - The prompt checks the PIN at account/verify-pin, which checks it. /verify-pin says
 *     yes without a PIN wherever opening needs none, so whatever was typed was kept.
 *   - Every call that changes the household sends the PIN, in a header where there is no
 *     body for it: each one checked by name.
 *   - With main PINs required and none set, a PIN is set first.
 *   - Setting one posts it to account, and the PIN is then known for what follows.
 *
 *   node tests/js/mainpin.js [path/to/profiles.js]
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const SRC_PATH = process.argv[2] || L.profilesPath();
const raw = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (raw.split(INIT).length - 1 !== 1) { console.error('could not find the single init() call'); process.exit(1); }
const src = raw.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

// A content area whose innerHTML is remembered, and whose elements are made on request.
function makeContent() {
    const els = {};
    const make = (id) => {
        const listeners = {};
        return els[id] = els[id] || {
            id, value: '', hidden: true, textContent: '',
            focus() {},
            addEventListener(type, fn) { (listeners[type] = listeners[type] || []).push(fn); },
            fire(type, e) { (listeners[type] || []).forEach(fn => fn(e || {})); }
        };
    };
    return {
        innerHTML: '',
        els,
        querySelector: (sel) => sel.charAt(0) === '#' ? make(sel.slice(1)) : null
    };
}

function load(serve) {
    const calls = [];
    const content = makeContent();
    const sandbox = {
        console: { log() {}, warn() {}, error() {}, info() {}, debug() {} },
        setTimeout: (fn) => { fn(); return 0; }, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
        document: {
            documentElement: { classList: { add() {}, remove() {}, contains: () => false, toggle() {} } },
            getElementById: () => null,
            querySelector: (sel) => sel === '.profiles-modal-content' ? content : null,
            querySelectorAll: () => [],
            addEventListener() {}, createElement: () => ({ style: {}, classList: { add() {} }, appendChild() {}, setAttribute() {} }),
            head: { appendChild() {} }, body: { appendChild() {} }
        },
        localStorage: { getItem: () => JSON.stringify({ masterUserId: 'main' }), setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: '' },
        location: { href: '', hash: '', pathname: '/', reload() {} },
        fetch: (url, o) => { calls.push({ url, o }); return serve(url, o); },
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        ApiClient: {
            getUrl: p => 'http://server/' + p,
            accessToken: () => 'tok',
            getCurrentUserId: () => 'main',
            appName: () => 'Jellyfin Web', deviceName: () => 'T', deviceId: () => 'd', appVersion: () => '1'
        }
    };
    sandbox.window = sandbox;
    sandbox.addEventListener = () => {};
    sandbox.removeEventListener = () => {};
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox, { filename: SRC_PATH });
    return { P: sandbox.__PROFILES, calls, content };
}
const json = b => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(b), text: () => Promise.resolve('') });
const settle = async () => { for (let i = 0; i < 6; i++) await new Promise(r => setImmediate(r)); };
const info = (required) => () => json({ features: { requireMainPin: required } });

(async function () {
    console.log('\n── Asking for the main PIN ─────────────────────────────────────');
    let env = load(info(false));
    ok('the script has withMainPin', typeof env.P.withMainPin === 'function');
    let asked = 0, setAsked = null, ran = 0;
    if (typeof env.P.withMainPin === 'function') {
    const spy = (e) => {
        e.P.promptMasterPinEntry = (kind, then) => { asked++; e.P.masterPin = '2468'; then(); };
        e.P.promptSetMainPin = (people, required, then) => { setAsked = required; };
    };

    // Home network, bypass on: opening the account asks nothing, but it has a PIN.
    const atHome = [{ isMaster: true, isBonfire: false, hasPin: true, requiresPin: false }];
    spy(env);
    env.P.withMainPin(atHome, () => ran++);
    await settle();
    ok('a main account with a PIN is asked for it, though opening it asks for none', asked === 1 && ran === 1, 'asked ' + asked + ', ran ' + ran);
    env.P.withMainPin(atHome, () => ran++);
    await settle();
    ok('once typed, it is not asked again', asked === 1 && ran === 2);

    env = load(info(false)); spy(env); asked = 0; ran = 0; setAsked = null;
    env.P.withMainPin([{ isMaster: true, isBonfire: false, hasPin: false }], () => ran++);
    await settle();
    ok('no PIN and none required: nothing to ask', asked === 0 && setAsked === null && ran === 1);

    env = load(info(true)); spy(env); asked = 0; ran = 0; setAsked = null;
    env.P.withMainPin([{ isMaster: true, isBonfire: false, hasPin: false }], () => ran++);
    await settle();
    ok('no PIN but the server requires one: it is set first, with no way to skip', setAsked === true && ran === 0, 'setAsked ' + setAsked + ', ran ' + ran);
    }

    console.log('\n── Checking it ─────────────────────────────────────────────────');
    // The method as loaded: an object-literal member, which extractFunction can't find.
    const prompt = String(load(info(false)).P.promptMasterPinEntry || '');
    ok('the PIN prompt is there to check', prompt.length > 0);
    ok('the prompt checks the PIN at account/verify-pin', /plugins\/profiles\/account\/verify-pin/.test(prompt));
    ok('and never at /verify-pin, which says yes wherever opening needs no PIN', !/plugins\/profiles\/verify-pin/.test(prompt));

    console.log('\n── Sending it ──────────────────────────────────────────────────');
    env = load(info(false));
    ok('the script has manageHeaders', typeof env.P.manageHeaders === 'function');
    if (typeof env.P.manageHeaders === 'function') {
        env.P.masterPin = '2468';
        const h = env.P.manageHeaders('tok');
        ok('it carries the PIN once typed', h['X-Bonfire-Master-Pin'] === '2468' && /Token="tok"/.test(h.Authorization));
        ok('as JSON by default', h['Content-Type'] === 'application/json');
        ok('or without a body', !('Content-Type' in env.P.manageHeaders('tok', false)));
        env.P.masterPin = null;
        ok('and no header before it is typed', !('X-Bonfire-Master-Pin' in env.P.manageHeaders('tok')));
    }

    // Every call to a route that checks the PIN, one at a time. devices/delete has no
    // fetch of its own since 1.7.2: it goes through the same devices/' + path call as
    // rename and merge, and tests/js/accountsettings.js runs all four and reads the header
    // off each request.
    const guarded = [
        "plugins/profiles/devices/' + path", "plugins/profiles/devices/merge-duplicates",
        "plugins/profiles/bonfire/kick", "plugins/profiles/bonfire/regenerate-code", "plugins/profiles/bonfire/delete-group",
        "plugins/profiles/bonfire/leave", "plugins/profiles/bonfire/generate", "plugins/profiles/bonfire/join",
        "plugins/profiles/bonfire/settings",
    ];
    guarded.forEach(route => {
        const at = raw.indexOf(route);
        // The fetch carrying this URL: either around it, or the next line when the URL is
        // put in a variable first. Then its options up to the end of the headers line.
        const before = raw.lastIndexOf('fetch(', at);
        const after = raw.indexOf('fetch(', at);
        const call = at - before < 200 ? before : (after - at < 200 ? after : -1);
        let opts = call === -1 ? '' : raw.slice(call, raw.indexOf('headers:', call) + 120);
        // fetch(url, init): the options were built just above, in init.
        if (/^fetch\([^\n]*, init\)/.test(opts)) {
            const built = raw.lastIndexOf('const init = {', call);
            opts = call - built < 600 ? raw.slice(built, call) : '';
        }
        ok(route.replace("' + path", '/<action>') + ' sends the PIN',
           at !== -1 && call !== -1 && /headers: [^\n]*this\.manageHeaders\(/.test(opts.split('\n').slice(0, 4).join('\n')),
           at === -1 ? 'not found' : opts.split('\n').filter(l => /headers:/.test(l)).join(' ').trim());
    });
    const prefsAt = raw.indexOf("return fetch(apiClient.getUrl('plugins/profiles/preferences'), {");
    ok('saving switcher settings sends the PIN',
       prefsAt !== -1 && /headers: this\.manageHeaders\(/.test(raw.slice(prefsAt, prefsAt + 200)));

    console.log('\n── Setting one ─────────────────────────────────────────────────');
    let posted = null;
    env = load((url, o) => {
        if (/plugins\/profiles\/account$/.test(url)) { posted = JSON.parse(o.body); return json({ hasPin: true }); }
        return json({});
    });
    ok('the script can ask for a main PIN to be set', typeof env.P.promptSetMainPin === 'function');
    if (typeof env.P.promptSetMainPin === 'function') {
        const people = [{ isMaster: true, isBonfire: false, hasPin: false }];
        let done = 0;
        env.P.promptSetMainPin(people, false, () => done++);
        ok('offered, it can be skipped', /Not now/.test(env.content.innerHTML));
        const input = env.content.els['jpf-main-pin-new'];
        input.value = '12';
        env.content.els['main-pin-save'].fire('click');
        await settle();
        ok('a PIN that is too short is refused here', posted === null && env.content.els['main-pin-new-error'].hidden === false);
        input.value = '4321';
        env.content.els['main-pin-save'].fire('click');
        await settle();
        ok('a valid one is posted to account, and nothing else with it',
           posted && posted.pin === '4321' && Object.keys(posted).length === 1, JSON.stringify(posted));
        ok('and is then known, for what follows', env.P.masterPin === '4321' && people[0].hasPin === true && done === 1);

        env = load(info(true));
        env.P.promptSetMainPin([{ isMaster: true, isBonfire: false, hasPin: false }], true, () => {});
        ok('required, there is no "Not now"', !/Not now/.test(env.content.innerHTML));
    }

    finish();
})();

function finish() {
    console.log('');
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
}
