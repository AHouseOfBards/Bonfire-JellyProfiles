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

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
