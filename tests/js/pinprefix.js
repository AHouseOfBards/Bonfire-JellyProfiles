// How many PIN guesses typing a PIN costs (R-1203).
//
// The server counts every PIN check against one allowance per account: five a quarter
// hour, twenty a day, shared by web, apps and the picker. It has to count a "silent"
// check too, or that check would be a free way to try PINs. Both PIN boxes — a
// profile's and the main account's — used to POST verify-pin on every keystroke from the
// fourth digit, so an eight-digit PIN typed correctly spent four wrong prefixes before
// the right one, and a single typo then locked it: the activity line, the PinLocked
// email, and a fifth of the day's guesses, for somebody who knew their PIN.
//
// Runs the real promptPinEntry and promptMasterPinEntry against a stub DOM, types into
// the box one digit at a time, and counts what was sent. Timers are held, and only run
// when this file says the typing has paused.
//
//   node tests/js/pinprefix.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

function setup() {
    const env = D.loadPlugin(SRC);
    const { PP } = env;
    let timers = [];
    env.sandbox.setTimeout = (fn) => { timers.push(fn); return timers.length; };
    env.sandbox.clearTimeout = (id) => { if (id && timers[id - 1]) timers[id - 1] = null; };
    env.pause = async () => {
        const run = timers; timers = [];
        run.forEach(fn => { if (fn) fn(); });
        await env.settle();
    };
    PP.beginNavigation = () => 1;
    PP.navIsCurrent = () => true;
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    PP.showAlert = () => {};
    PP.showProfileOverlay = () => {};
    PP._offerPinReset = () => {};
    PP.handleSessionExpired = () => { env.expired = true; };
    env.switches = [];
    PP.executeProfileSwitch = (id, pin) => { env.switches.push(pin); };
    PP.currentProfiles = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true },
        { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false, hasPin: true }
    ];
    env.verifies = (path) => env.posts.filter(p => p.url === 'plugins/profiles/' + path);
    return env;
}

// Types digits one at a time, firing the input listener after each, with no pause.
async function type(env, input, digits) {
    for (const d of digits) {
        input.value += d;
        input.fire('input');
        await env.settle();
    }
}

const RIGHT = '24681357';

(async () => {
    for (const box of [
        { name: 'profile PIN', open: PP => PP.promptPinEntry('kid'), input: 'jpf-profile-pin-input', path: 'verify-pin' },
        { name: 'main PIN', open: PP => PP.promptMasterPinEntry('manage', () => {}), input: 'jpf-master-pin-input', path: 'account/verify-pin' }
    ]) {
        console.log('\n── The ' + box.name + ' box ' + '─'.repeat(Math.max(0, 50 - box.name.length)));

        // 1. Eight digits typed straight through: one check, of the whole PIN.
        let env = setup();
        env.setRespond((url, body) => (body && body.pin === RIGHT ? { status: 200, body: {} }
                                                                    : { status: 400, body: 'Incorrect PIN.' }));
        box.open(env.PP);
        let input = env.content.querySelector('#' + box.input);
        await type(env, input, RIGHT);
        await env.pause();
        let sent = env.verifies(box.path);
        ok('typing an 8-digit PIN sends exactly one ' + box.path + ' (' + sent.length + ')', sent.length === 1);
        ok('and it is the whole PIN, not a prefix',
           sent.length >= 1 && sent.every(p => p.body.pin === RIGHT), sent.map(p => p.body.pin).join(','));

        // 2. Six digits, then a pause: one check. Wrong, and Enter then sends nothing more.
        env = setup();
        env.setRespond(() => ({ status: 400, body: 'Incorrect PIN.' }));
        box.open(env.PP);
        input = env.content.querySelector('#' + box.input);
        await type(env, input, '123456');
        ok('nothing is checked while the digits are still coming (' + env.verifies(box.path).length + ')',
           env.verifies(box.path).length === 0);
        await env.pause();
        ok('a pause checks once (' + env.verifies(box.path).length + ')', env.verifies(box.path).length === 1);
        const before = env.posts.length;
        input.fire('keydown', { key: 'Enter' });
        await env.settle();
        ok('Enter on the value just refused spends no second guess ('
           + (env.posts.length - before) + ' more requests, ' + env.switches.length + ' switches)',
           env.posts.length === before && env.switches.length === 0);
        const err = env.content.querySelector(box.input === 'jpf-profile-pin-input' ? '#pin-error-msg' : '#master-pin-error-msg');
        ok('and says it was wrong', !!err && err.style.display === 'block' && /^Wrong (main )?PIN\. Try again\.$/.test(err.textContent),
           err ? err.textContent : 'no error node');

        // 3. A 429 stops the box from checking by itself.
        env = setup();
        // The server's sentence is English. The client builds the wait from Retry-After
        // through t(), so it is not the server's words that are shown.
        env.setRespond(() => ({ status: 429, headers: { 'Retry-After': '90' }, body: 'Too many tries. Try again in 2 minutes.' }));
        box.open(env.PP);
        input = env.content.querySelector('#' + box.input);
        await type(env, input, '1234');
        await env.pause();
        const lockErr = env.content.querySelector(box.input === 'jpf-profile-pin-input' ? '#pin-error-msg' : '#master-pin-error-msg');
        ok('a 429 shows the wait built from Retry-After, not the server\'s sentence',
           !!lockErr && lockErr.textContent === 'Too many wrong PINs. Try again in 2 minutes.',
           lockErr ? lockErr.textContent : '');
        for (const [secs, want] of [['30', 'in 30 seconds.'], ['1', 'in a moment.'], ['60', 'in a minute.'], [null, 'in a moment.']]) {
            const e2 = setup();
            const h = secs === null ? {} : { 'Retry-After': secs };
            e2.setRespond(() => ({ status: 429, headers: h, body: 'x' }));
            box.open(e2.PP);
            const i2 = e2.content.querySelector('#' + box.input);
            await type(e2, i2, '1234');
            await e2.pause();
            const m2 = e2.content.querySelector(box.input === 'jpf-profile-pin-input' ? '#pin-error-msg' : '#master-pin-error-msg');
            ok('Retry-After ' + secs + ' reads "' + want + '"', !!m2 && m2.textContent.indexOf(want) > 0, m2 ? m2.textContent : '');
        }
        await type(env, input, '5678');
        await env.pause();
        ok('and the box stops checking by itself (' + env.verifies(box.path).length + ' sent)',
           env.verifies(box.path).length === 1);

        // 4. A right PIN still opens without Enter.
        env = setup();
        let opened = false;
        env.setRespond(() => ({ status: 200, body: {} }));
        if (box.path === 'verify-pin') env.PP.promptPinEntry('kid');
        else env.PP.promptMasterPinEntry('manage', () => { opened = true; });
        input = env.content.querySelector('#' + box.input);
        await type(env, input, '4321');
        await env.pause();
        if (box.path === 'verify-pin') opened = env.switches.length === 1 && env.switches[0] === '4321';
        ok('a right PIN still opens once the typing stops', opened);
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
