/*
 * Settings that do nothing while another is off must look unavailable (P2-606, P2-609).
 *
 *  - "Open PIN-less and device-limited profiles from anywhere" only matters when PIN login or
 *    the profile list is on.
 *  - The first-time setup theme row only matters while profiles can choose a theme. Its boxes
 *    must stay enabled: a disabled step is not sent on Save, and the choice would be lost.
 *
 * Runs the real syncDependentToggles against stub elements, and the real saveConfiguration
 * path is covered by adminsave.js. Older dashboard: argv[3].
 *
 *     node tests/js/admindependents.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const script = L.dashboardScript(L.readDashboard());
if (script.indexOf('function syncDependentToggles') === -1) {
    ok('the page has syncDependentToggles', false);
    console.log('\n0 passed, 1 failed');
    process.exit(1);
}
const source = L.extractFunction(script, 'syncDependentToggles');

function run(state) {
    const off = {};
    const els = {
        '#enableClientPinLogin': { checked: state.pin, disabled: false },
        '#enableClientProfileList': { checked: state.list, disabled: false },
        '#clientPinLoginFromAnywhere': { checked: true, disabled: false },
        '#allowProfileThemes': { checked: state.themes },
        '.jpf-ob-row[data-step="theme"]': {
            classList: { toggle: function (c, on) { off[c] = !!on; } }
        }
    };
    const showBox = { checked: true, disabled: false };
    const page = { querySelector: function (s) { return els[s] || showBox; } };
    new Function('page', source + '; syncDependentToggles(page);')(page);
    return { els: els, off: off, showBox: showBox };
}

const none = run({ pin: false, list: false, themes: true });
ok('From anywhere is disabled when PIN login and the profile list are both off', none.els['#clientPinLoginFromAnywhere'].disabled === true);
ok('and keeps its stored value', none.els['#clientPinLoginFromAnywhere'].checked === true);
ok('PIN login alone enables it', run({ pin: true, list: false, themes: true }).els['#clientPinLoginFromAnywhere'].disabled === false);
ok('the profile list alone enables it', run({ pin: false, list: true, themes: true }).els['#clientPinLoginFromAnywhere'].disabled === false);

const noThemes = run({ pin: true, list: true, themes: false });
ok('the theme step row is marked unavailable when profile themes are off', noThemes.off['jpf-ob-off'] === true);
ok('and its boxes are not disabled, so Save still sends the step', noThemes.showBox.disabled === false);
ok('the row is live when profile themes are on', run({ pin: true, list: true, themes: true }).off['jpf-ob-off'] === false);

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
