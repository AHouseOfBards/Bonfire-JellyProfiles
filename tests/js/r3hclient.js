// Client fixes from the second review, round 3 (r2-25-03, r2-25-04, r2-27-04, r2-27-05,
// r2-28-01 to r2-28-04).
//
//  - r2-25-03  with no Campground runtime, history was wrapped by assignment with no
//              marker, so a second bindEvents wrapped the wrapper and every navigation
//              ran checkRoute twice.
//  - r2-25-04  init() retried every 100 ms for as long as the page was open when there
//              was no ApiClient.
//  - r2-27-04  the library artwork picker, opened while already open, returned without
//              telling its caller, which was left waiting.
//  - r2-27-05  initLockoutTimer read a 401 as a hiccup: it armed from the cached list
//              and asked again in 30 s with the dead token.
//  - r2-28-01  the PIN keypad's pause timer survived leaving the screen, then spent a
//              guess and could run the switch from a screen already left.
//  - r2-28-02  Enter while the same digits were being checked sent them a second time.
//  - r2-28-03  the set-a-main-PIN save had no in-flight guard.
//  - r2-28-04  a switch refused after a right PIN (auto-check path) said nothing.
//
//   node tests/js/r3hclient.js [path/to/profiles.js]

'use strict';
const vm = require('vm');
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

// ── r2-25-03 and r2-25-04: startup, in a bare sandbox ────────────────────────────────
function bare(withApiClient) {
    const timers = [];
    const noop = () => {};
    const el = () => ({
        style: {}, classList: { add: noop, remove: noop, contains: () => false, toggle: () => false },
        addEventListener: noop, removeEventListener: noop, appendChild: noop, setAttribute: noop,
        querySelector: () => null, querySelectorAll: () => []
    });
    const history = { pushState() {}, replaceState() {} };
    const sandbox = {
        console: { log: noop, warn: noop, error: noop },
        localStorage: { getItem: () => null, setItem: noop, removeItem: noop },
        sessionStorage: { getItem: () => null, setItem: noop, removeItem: noop },
        navigator: { userAgent: 'Mozilla/5.0', language: 'en' },
        setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout: noop,
        setInterval: () => 1, clearInterval: noop,
        history,
        document: {
            addEventListener: noop, removeEventListener: noop, querySelector: () => null,
            querySelectorAll: () => [], getElementById: () => null, createElement: el,
            head: el(), body: el(), documentElement: el()
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.addEventListener = noop;
    sandbox.removeEventListener = noop;
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    sandbox.location = { href: 'https://jf.local/web/', hash: '', pathname: '/web/' };
    if (withApiClient) sandbox.ApiClient = { getUrl: p => p };
    const code = SRC.replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');
    vm.createContext(sandbox);
    new vm.Script(code, { filename: 'profiles.js' }).runInContext(sandbox);
    return { plugin: sandbox.__PROFILES, sandbox, history, timers };
}

{
    console.log('\n── r2-25-03: history is wrapped once ───────────────────────────');
    const env = bare(true);
    let checks = 0;
    env.plugin._bindSettingsRefresh = () => {};
    env.plugin.checkRoute = () => { checks++; };
    env.plugin.bindEvents();
    env.plugin.bindEvents();
    env.timers.splice(0);
    env.history.pushState({}, '', '/web/#/movies');
    env.timers.splice(0).forEach(fn => fn());
    ok('two bindEvents, one pushState, one checkRoute (' + checks + ')', checks === 1);
}

{
    // r2-25-04 stopped init asking every 100 ms forever on a page with no ApiClient. It did
    // that by giving up after a minute, and r3-05-01 found the cost of that: a page slower
    // than a minute (a TV, a server-select page) never got Bonfire until a reload. So the
    // quick asking still ends after about a minute, and after that it asks every few
    // seconds instead of never. tests/js/initwait.js holds the second half.
    console.log('\n── r2-25-04: init stops asking every 100 ms ────────────────────');
    const env = bare(false);
    const delays = [];
    env.sandbox.setTimeout = (fn, ms) => { delays.push(ms); env.timers.push(fn); return env.timers.length; };
    env.plugin.init();
    let rounds = 0;
    while (env.timers.length && rounds < 5000) {
        env.timers.splice(0).forEach(fn => fn());
        rounds++;
    }
    const quick = delays.filter(d => !(d >= 1000)).length;
    ok('the quick retry stops by itself (' + quick + ' asks under a second, of ' + delays.length + ')', quick <= 620 && delays.length > quick);
    ok('and it asked quickly for about a minute, not a moment (' + quick + ' rounds of 100 ms)', quick >= 590);
    ok('after that it never asks more often than every few seconds',
       delays.slice(quick).every(d => d >= 1000), delays.slice(quick, quick + 5).join(','));
}

// ── r2-27-04 and r2-27-05 ────────────────────────────────────────────────────────────
function plugin() {
    const env = D.loadPlugin(SRC);
    const timers = [];
    env.sandbox.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; };
    env.sandbox.clearTimeout = () => {};
    env.timers = timers;
    env.PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    env.PP.getAuthHeaders = () => ({});
    return env;
}

(async function () {
    {
        console.log('\n── r2-27-04: the artwork picker opened twice ───────────────────');
        const env = plugin();
        env.PP.fetchAvatarLibrary = () => Promise.resolve({ allowCustomUploads: true, avatars: [] });
        env.sandbox.document.getElementById = id => (id === 'profiles-libart-dialog' ? D.makeNode({}) : null);
        let cancelled = 0, picked = 0;
        env.PP.pickLibraryArtwork(() => { picked++; }, () => { cancelled++; });
        await env.settle();
        ok('the second open calls onCancel (' + cancelled + ')', cancelled === 1 && picked === 0);
        env.PP.pickLibraryArtwork(() => {});
        await env.settle();
        ok('and no callback is fine', true);
    }

    {
        console.log('\n── r2-27-05: a 401 from the lockout list ───────────────────────');
        const env = plugin();
        env.PP.isProfileSessionActive = () => true;
        env.PP.cachedProfiles = [{ profileUserId: 'main', requiresPin: true, lockoutMinutes: 5 }];
        let expired = 0, armed = null;
        env.PP.handleSessionExpired = () => { expired++; };
        env.PP.startInactivityTimer = m => { armed = m; };
        env.setRespond(() => ({ status: 401, body: '' }));
        env.PP.initLockoutTimer();
        await env.settle();
        ok('the session is ended (' + expired + ')', expired === 1);
        ok('the lock is not armed from the cache (' + armed + ')', armed === null);
        ok('and nothing is queued to ask again in 30 s (' + env.timers.length + ')', env.timers.length === 0);

        const env2 = plugin();
        env2.PP.isProfileSessionActive = () => true;
        env2.PP.cachedProfiles = [{ profileUserId: 'main', requiresPin: true, lockoutMinutes: 5 }];
        let armed2 = null;
        env2.PP.startInactivityTimer = m => { armed2 = m; };
        env2.setRespond(() => ({ status: 500, body: '' }));
        env2.PP.initLockoutTimer();
        await env2.settle();
        ok('a 500 still falls back to the cached list (' + armed2 + ')', armed2 === 5);
    }

    // ── the keypads ───────────────────────────────────────────────────────────────────
    function keypad() {
        const env = D.loadPlugin(SRC);
        const { PP } = env;
        let timers = [];
        env.sandbox.setTimeout = (fn, ms) => { timers.push({ fn, ms, live: true }); return timers.length; };
        env.sandbox.clearTimeout = id => { if (id && timers[id - 1]) timers[id - 1].live = false; };
        // Only the typing pause: the request timeout must not fire under a held request.
        env.pause = async () => {
            const run = timers.filter(t => t.live && t.ms === PP.PIN_PAUSE_MS);
            run.forEach(t => { t.live = false; t.fn(); });
            await env.settle();
        };
        PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
        PP.showAlert = () => {};
        PP.showProfileOverlay = () => {};
        PP._offerPinReset = () => {};
        PP.handleSessionExpired = () => {};
        env.switches = [];
        env.switchOutcome = null;
        PP.executeProfileSwitch = (id, pin, onError) => {
            env.switches.push(pin);
            if (env.switchOutcome && typeof onError === 'function') onError(env.switchOutcome);
        };
        PP.currentProfiles = [
            { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true },
            { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false, hasPin: true }
        ];
        // Verify requests are counted when sent and answered when release() is called.
        env.sent = [];
        let waiters = [];
        const real = env.sandbox.fetch;
        env.sandbox.fetch = (url, init) => {
            env.sent.push(String(url));
            if (!/verify-pin|plugins\/profiles\/account$/.test(String(url))) return real(url, init);
            return new Promise(res => { waiters.push(() => res(real(url, init))); });
        };
        env.release = async () => { const w = waiters; waiters = []; w.forEach(f => f()); await env.settle(); await env.settle(); };
        env.count = re => env.sent.filter(u => re.test(u)).length;
        env.setRespond((url, body) => (body && body.pin === '2468' ? { status: 200, body: {} }
                                                                    : { status: 400, body: 'Incorrect PIN.' }));
        return env;
    }
    async function typeIn(env, input, digits) {
        input.value = digits;
        input.fire('input');
        await env.settle();
    }
    let boxCalled = 0;
    const enter = input => input.fire('keydown', { key: 'Enter' });

    for (const box of [
        { name: 'profile', open: PP => PP.promptPinEntry('kid'), input: 'jpf-profile-pin-input', path: /plugins\/profiles\/verify-pin$/ },
        { name: 'main', open: PP => PP.promptMasterPinEntry('manage', () => { boxCalled++; }), input: 'jpf-master-pin-input', path: /account\/verify-pin$/ }
    ]) {
        boxCalled = 0;
        console.log('\n── r2-28-01: the ' + box.name + ' keypad is left before the pause ends ' + '─'.repeat(10));
        let env = keypad();
        box.open(env.PP);
        let input = env.content.querySelector('#' + box.input);
        await typeIn(env, input, '2468');
        env.PP.beginNavigation();                    // Forgot PIN, Back, expiry, the next screen
        await env.pause();
        ok('no guess is spent after navigating away (' + env.count(box.path) + ')', env.count(box.path) === 0);

        env = keypad();
        box.open(env.PP);
        input = env.content.querySelector('#' + box.input);
        await typeIn(env, input, '2468');
        env.sandbox.document.body.contains = () => false;   // the input is out of the document
        await env.pause();
        ok('nor when the input has left the document (' + env.count(box.path) + ')', env.count(box.path) === 0);

        env = keypad();
        box.open(env.PP);
        input = env.content.querySelector('#' + box.input);
        await typeIn(env, input, '2468');
        await env.pause();                           // the check is out
        ok('(the check is out)', env.count(box.path) === 1);
        env.PP.beginNavigation();                    // the screen is left while it is out
        await env.release();                         // and the answer is a right PIN
        ok('a right answer after leaving does not run the switch (' + env.switches.length + ', ' + boxCalled + ')',
           env.switches.length === 0 && boxCalled === 0);

        console.log('\n── r2-28-02: Enter while the same digits are being checked (' + box.name + ') ──');
        env = keypad();
        box.open(env.PP);
        input = env.content.querySelector('#' + box.input);
        await typeIn(env, input, '2468');
        await env.pause();
        enter(input);
        await env.settle();
        ok('Enter sends nothing more while that check is out (' + env.count(box.path) + ' sent)', env.count(box.path) === 1);
        await env.release();
        ok('and the right answer is acted on once (' + (env.switches.length + boxCalled) + ')',
           box.name === 'profile' ? env.switches.length === 1 && boxCalled === 0 : boxCalled === 1);
        ok('still one check in all (' + env.count(box.path) + ')', env.count(box.path) === 1);

        env = keypad();
        box.open(env.PP);
        input = env.content.querySelector('#' + box.input);
        await typeIn(env, input, '1357');
        await env.pause();
        enter(input);
        enter(input);
        await env.release();
        const err = env.content.querySelector('#' + (box.name === 'profile' ? 'pin-error-msg' : 'master-pin-error-msg'));
        ok('a wrong answer is shown to the person who pressed Enter', err && err.style.display === 'block' && /\S/.test(err.textContent),
           err && err.style.display + ' / ' + err.textContent);
        ok('and no further guess was sent (' + env.count(box.path) + ', ' + env.switches.length + ')',
           env.count(box.path) === 1 && env.switches.length === 0);
    }

    {
        console.log('\n── r2-28-04: a refused switch after a right PIN says why ───────');
        const env = keypad();
        env.PP.promptPinEntry('kid');
        const input = env.content.querySelector('#jpf-profile-pin-input');
        env.switchOutcome = 'This device is not allowed.';
        await typeIn(env, input, '2468');
        await env.pause();
        await env.release();
        const err = env.content.querySelector('#pin-error-msg');
        ok('the reason is on screen (' + (err && err.textContent) + ')',
           err && err.style.display === 'block' && err.textContent === 'This device is not allowed.');
        ok('the switch was tried once (' + env.switches.length + ')', env.switches.length === 1);
    }

    {
        console.log('\n── r2-28-03: set-a-main-PIN saves once ─────────────────────────');
        const env = keypad();
        let done = 0;
        env.PP.promptSetMainPin(env.PP.currentProfiles, false, () => { done++; });
        const input = env.content.querySelector('#jpf-main-pin-new');
        input.value = '2468';
        enter(input);
        enter(input);
        env.content.querySelector('#main-pin-save').click();
        await env.settle();
        ok('three submits, one request (' + env.count(/plugins\/profiles\/account$/) + ')',
           env.count(/plugins\/profiles\/account$/) === 1);
        await env.release();
        ok('and the next step runs once (' + done + ')', done === 1);

        const env2 = keypad();
        env2.setRespond(() => ({ status: 400, body: 'Too simple.' }));
        env2.PP.promptSetMainPin(env2.PP.currentProfiles, false, () => {});
        const in2 = env2.content.querySelector('#jpf-main-pin-new');
        in2.value = '2468';
        enter(in2);
        await env2.release();
        in2.value = '1357';
        enter(in2);
        await env2.settle();
        ok('after a refusal the person can try again (' + env2.count(/plugins\/profiles\/account$/) + ')',
           env2.count(/plugins\/profiles\/account$/) === 2);
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
