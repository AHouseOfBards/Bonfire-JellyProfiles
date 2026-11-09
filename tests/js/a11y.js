// Screen reader and remote behaviour of the client screens (P2 accessibility findings).
//
// Each block draws a real screen through the stub DOM and reads the markup a screen
// reader would read. Point it at an older profiles.js to see it fail:
//
//   node tests/js/a11y.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const { PP, content, settle } = env;
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.currentProfiles = [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true }];

function tagOf(id) {
    const m = new RegExp('<[a-z]+[^>]*\\bid="' + id + '"[^>]*>').exec(content.innerHTML);
    return m ? m[0] : '';
}

(async () => {
    // 2. A wrong PIN is spoken, and the box says it is invalid.
    const pinScreens = [
        ['the profile PIN', 'profile-pin-input', 'pin-error-msg', () => PP.promptPinEntry('kid')],
        ['the main PIN', 'master-pin-input', 'master-pin-error-msg', () => PP.promptMasterPinEntry('manage', () => {})]
    ];
    for (const s of pinScreens) {
        s[3]();
        await settle();
        const err = tagOf(s[2]);
        ok(s[0] + ': the error line is an alert', /role="alert"/.test(err), err);
        const box = tagOf(s[1]);
        ok(s[0] + ': the box points at its error', new RegExp('aria-describedby="' + s[2] + '"').test(box), box);
    }

    // 3, 4, 13. A switch that fails, or never answers, puts the person back somewhere
    // they can act, and says why in words that are not the browser's.
    {
        const sb = env.sandbox;
        const timers = [];
        sb.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; };
        sb.clearTimeout = (id) => { if (timers[id - 1]) timers[id - 1].dead = true; };
        const fireAll = () => timers.splice(0).forEach(x => { if (!x.dead) x.fn(); });
        PP.interceptHomeAndShowProfiles = () => { PP._redrawn = true; };
        let alerted = null;
        PP.showAlert = (title, body) => { alerted = { title, body }; };
        PP.setSwitchBusy = (id, busy) => { PP._busy = busy; };
        const UNREACHABLE = "Can't reach the server. Try again.";

        // A request that never answers.
        let release;
        sb.fetch = () => new Promise(r => { release = r; });
        PP._switchLock = false;
        let msg = null;
        PP.executeProfileSwitch('kid', '1234', m => { msg = m; });
        await settle();
        ok('a hung switch is still waiting before the timer fires', msg === null && PP._switchLock === true);
        fireAll();
        await settle();
        ok('a hung switch gives the controls back', PP._switchLock === false && PP._busy === false,
            'lock=' + PP._switchLock + ' busy=' + PP._busy);
        ok('and says the server cannot be reached', msg === UNREACHABLE, String(msg));
        // The reply turning up late must not carry on into a reload nobody is waiting for.
        let stored = false;
        PP.updateStoredCredentials = () => { stored = true; return false; };
        release({ ok: true, json: () => Promise.resolve({ activeProfileToken: 't', jellyfinUserId: 'u' }) });
        await settle();
        ok('a reply after the timeout is ignored', stored === false);

        // The browser's own words.
        sb.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
        msg = null;
        PP.executeProfileSwitch('kid', '1234', m => { msg = m; });
        await settle();
        ok('"Failed to fetch" is not shown to anyone', msg === UNREACHABLE, String(msg));

        // The server's own words still go through.
        sb.fetch = () => Promise.resolve({ ok: false, status: 403, text: () => Promise.resolve('Too many attempts') });
        msg = null;
        PP.executeProfileSwitch('kid', '1234', m => { msg = m; });
        await settle();
        ok('a refusal from the server is shown as written', msg === 'Too many attempts', String(msg));

        // A tap on a profile with no PIN has no screen to report on.
        PP._redrawn = false;
        alerted = null;
        PP.executeProfileSwitch('kid', null);
        await settle();
        ok('a refused tap puts the picker back', PP._redrawn === true);
        ok('and tells the person why', alerted !== null && /Too many attempts/.test(alerted.body), JSON.stringify(alerted));
        sb.fetch = () => Promise.reject(new TypeError('Load failed'));
        alerted = null;
        PP.executeProfileSwitch('kid', null);
        await settle();
        ok('an unreachable server on a tap is said in plain words',
            alerted !== null && alerted.body.replace('&#039;', "'") === UNREACHABLE, JSON.stringify(alerted));
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
