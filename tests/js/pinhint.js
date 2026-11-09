// The PIN boxes say what to type instead of showing four dots (P2-203).
//
// A placeholder of "••••" looks like a PIN already typed, and on a television nothing told
// the person to use the number keys. Draws the three screens that ask for a PIN and reads
// each box's placeholder and label.
//
//   node tests/js/pinhint.js [path/to/profiles.js]

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

const env = D.loadPlugin(SRC);
const { PP, content, settle } = env;
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.currentProfiles = [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true }];

function boxOf(id) {
    const m = new RegExp('<input[^>]*\\bid="' + id + '"[^>]*>').exec(content.innerHTML);
    return m ? m[0] : '';
}

(async () => {
    const screens = [
        ['the profile PIN', 'profile-pin-input', () => PP.promptPinEntry('kid')],
        ['the main PIN', 'master-pin-input', () => PP.promptMasterPinEntry('manage', () => {})],
        ['setting the main PIN', 'main-pin-new', () => PP.promptSetMainPin(PP.currentProfiles, false, () => {})]
    ];
    for (const s of screens) {
        s[2]();
        await settle();
        const box = boxOf(s[1]);
        ok(s[0] + ': the box is drawn', box !== '');
        ok(s[0] + ': no row of dots posing as a typed PIN', box.indexOf('••') === -1, box);
        ok(s[0] + ': it says to use the number keys', /placeholder="Use the number keys"/.test(box), box);
        ok(s[0] + ': and is labelled for a screen reader', /aria-label="Use the number keys"/.test(box), box);
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
