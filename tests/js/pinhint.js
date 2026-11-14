// The PIN boxes say what to type instead of showing four dots (P2-203).
//
// A placeholder of "••••" looks like a PIN already typed, and on a television nothing told
// the person to use the number keys. Draws the three PIN screens and reads each box's
// placeholder and label.
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
        ['the profile PIN', 'jpf-profile-pin-input', () => PP.promptPinEntry('kid')],
        ['the main PIN', 'jpf-master-pin-input', () => PP.promptMasterPinEntry('manage', () => {})],
        ['setting the main PIN', 'jpf-main-pin-new', () => PP.promptSetMainPin(PP.currentProfiles, false, () => {})]
    ];
    for (const s of screens) {
        s[2]();
        await settle();
        const box = boxOf(s[1]);
        ok(s[0] + ': the box is drawn', box !== '');
        ok(s[0] + ': no row of dots posing as a typed PIN', box.indexOf('••') === -1, box);
        // A placeholder was tried first: the box is 180px wide with 0.6rem letter-spacing,
        // so on a television it read "Use the number" at the bottom of the box.
        // A single space is allowed and is not words: it lets :placeholder-shown draw the
        // empty box's four dots (pinbox.js) and draws nothing itself.
        ok(s[0] + ': no placeholder in a box too narrow for one', !/placeholder="[^"]*[^"\s][^"]*"/.test(box), box);
        ok(s[0] + ': the hint is its own line', /class="jpf-pin-keys-hint">Use the number keys</.test(content.innerHTML));
        ok(s[0] + ': the box is named after what it asks for', /aria-label="(?!Use the number)[^"]+"/.test(box), box);
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
