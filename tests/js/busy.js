// Behavioural test of the switch-in-progress feedback, against the real profiles.js.
//
// Reported from a phone: tapping a profile does nothing visible for a couple of seconds
// (a round trip plus a full reload), so the tap reads as missed and gets repeated.
// _switchLock already made the repeats harmless — it just never said so on screen.
//
// Same technique as session.js: evaluate the shipped file with init() swapped for an
// export, so these are the methods that ship. The DOM here is only as real as
// setSwitchBusy needs — classList, ids, and querySelectorAll for one class.
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

function makeClassList(el) {
    const set = new Set();
    return {
        add: c => set.add(c),
        remove: c => set.delete(c),
        contains: c => set.has(c),
        toggle: (c, force) => {
            const on = force === undefined ? !set.has(c) : !!force;
            if (on) set.add(c); else set.delete(c);
            return on;
        },
        _set: set
    };
}

function makeEl(attrs) {
    const el = {
        attrs: Object.assign({}, attrs),
        style: {},
        getAttribute: k => (k in el.attrs ? el.attrs[k] : null),
        setAttribute: (k, v) => { el.attrs[k] = v; },
        removeAttribute: k => { delete el.attrs[k]; }
    };
    el.classList = makeClassList(el);
    return el;
}

// Two profiles, written with a dashed id — the plugin stores them dashless in places, so
// the match has to normalise rather than compare strings.
const DASHED = '3f2a1b4c-5d6e-7f80-9a1b-2c3d4e5f6071';
const DASHLESS = DASHED.replace(/-/g, '');
const OTHER = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

function build() {
    const cards = [makeEl({ 'data-id': DASHLESS }), makeEl({ 'data-id': OTHER })];
    const overlay = makeEl({});
    overlay.querySelectorAll = sel => (sel === '.profile-card' ? cards : []);

    const byId = {
        'profiles-gate-overlay': overlay,
        'pin-submit-btn': makeEl({}),
        'master-pin-submit-btn': makeEl({})
    };

    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0 (Linux; Android 14)' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector() { return null; }, querySelectorAll() { return []; },
            getElementById: id => byId[id] || null,
            createElement: () => makeEl({}),
            head: { appendChild() {} },
            body: { classList: makeClassList() },
            documentElement: { style: { cssText: '', removeProperty() {} }, classList: makeClassList() }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    return { plugin: sandbox.__PROFILES, overlay, cards, byId };
}

console.log();
console.log('── Tapping a profile shows it is working ───────────────────────');

const env = build();
if (typeof env.plugin.setSwitchBusy !== 'function') {
    ok('there is a busy state at all', false);
} else {
    env.plugin.setSwitchBusy(DASHED, true);

    ok('the picker is marked busy', env.overlay.classList.contains('is-switching'));
    ok('and says so to a screen reader', env.overlay.getAttribute('aria-busy') === 'true');
    ok('the tapped profile carries the spinner',
        env.cards[0].classList.contains('is-switching'));
    ok('a dashless stored id still matches a dashed one',
        env.cards[0].classList.contains('is-switching'));
    ok('the other profiles do not',
        !env.cards[1].classList.contains('is-switching'));
    ok('the PIN button shows it is working',
        env.byId['pin-submit-btn'].classList.contains('is-busy'));
    ok('so does the master PIN button',
        env.byId['master-pin-submit-btn'].classList.contains('is-busy'));

    console.log();
    console.log('── A failed switch gives the picker back ───────────────────────');

    env.plugin.setSwitchBusy(DASHED, false);
    ok('the picker is usable again', !env.overlay.classList.contains('is-switching'));
    ok('aria-busy is dropped, not set to false', env.overlay.getAttribute('aria-busy') === null);
    ok('the spinner is gone from the card', !env.cards[0].classList.contains('is-switching'));
    ok('and from the PIN button', !env.byId['pin-submit-btn'].classList.contains('is-busy'));

    console.log();
    console.log('── Switching from a PIN prompt, with no gate behind it ─────────');

    const noGate = build();
    noGate.byId['profiles-gate-overlay'] = null;
    noGate.plugin.setSwitchBusy(DASHED, true);
    ok('the button still reports progress',
        noGate.byId['pin-submit-btn'].classList.contains('is-busy'));
}

console.log();
console.log('── It is actually wired to the switch ──────────────────────────');

const text = fs.readFileSync(SRC_PATH, 'utf8');
const switchAt = text.indexOf('executeProfileSwitch: function');
// executeProfileSwitch runs to roughly 5k characters; the window has to clear it or
// the ordering checks below silently compare against -1.
const body = text.slice(switchAt, switchAt + 8000);
ok('the switch raises it before the request goes out',
    body.indexOf('this.setSwitchBusy(profileId, true);') > 0
    && body.indexOf('this.setSwitchBusy(profileId, true);') < body.indexOf('fetch(url'));
const catchAt = body.indexOf('.catch(err');
const releaseAt = body.indexOf('this.setSwitchBusy(profileId, false);');
ok('and lowers it when the request fails',
    catchAt > 0 && releaseAt > catchAt);

console.log();
console.log('── The screen is not blanked for the length of a reload ─────────');

// The overlay is opaque and covers the viewport, so it alone hides the old page. Blanking
// the document as well is what made the phone show seconds of black with no spinner.
const reloadRegion = body.slice(body.indexOf('updateStoredCredentials(activeProfileToken'));
// Located by shape, not by a fixed literal. This was an indexOf for
// "documentElement.style.cssText = 'opacity:0" and stopped matching the moment the
// colour became a concatenation rather than a constant — reporting the blanking as
// having moved out of the else branch when it had not moved at all. Same mistake
// _lib.js was written to stop: match the shape, and fail loudly if it is not there.
const blankMatch = /documentElement\.style\.cssText\s*=\s*'opacity:0/.exec(reloadRegion);
const blankAt = blankMatch ? blankMatch.index : -1;
const elseAt = reloadRegion.indexOf('} else {');
ok('the blanking is still findable at all', blankAt > 0);
ok('the document is only blanked when there is no overlay to do it',
    blankAt > 0 && elseAt > 0 && elseAt < blankAt);

console.log();
console.log('── Every spinner names a keyframe we define ────────────────────');

const CSS = L.extractCss(text);
ok('jpfSpin is defined', /@keyframes jpfSpin/.test(CSS));
const named = (text.match(/animation:\s*([A-Za-z][\w-]*)/g) || [])
    .map(m => m.split(/\s+/).pop());
const defined = (CSS.match(/@keyframes\s+([\w-]+)/g) || []).map(m => m.split(/\s+/).pop());
const missing = named.filter(n => !defined.includes(n));
ok('no animation names a keyframe that does not exist (' + (missing.join(', ') || 'none') + ')',
    missing.length === 0);

console.log();
console.log('── Touch gets immediate feedback ───────────────────────────────');

ok('a pressed card responds before the network does',
    /\.profile-card:active \.profile-avatar-container \{[^}]*transform: scale/.test(CSS.replace(/\r\n/g, '\n')));
ok('the unchosen profiles step back',
    CSS.includes('#profiles-gate-overlay.is-switching .profile-card:not(.is-switching)'));
ok('the chosen one gets a spinner over its avatar',
    CSS.includes('.profile-card.is-switching .profile-avatar-container::after'));
ok('reduced motion drops the press animation, not the spinner',
    /prefers-reduced-motion[\s\S]{0,400}\.profile-card:active \.profile-avatar-container \{\s*transform: none;/
        .test(CSS.replace(/\r\n/g, '\n')));

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
fails.forEach(f => console.log('  FAIL  ' + f));
process.exit(fails.length ? 1 : 0);
