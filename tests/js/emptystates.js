// An empty list says what to add, not only that it is empty.
//
// "None yet." told a new household nothing: the Sharing section, the announcements list and
// the themes list each said it and left the person to guess what comes next. This reads the
// English of every empty state in both pages and needs each to carry a next step, then
// checks the translations were redone rather than left saying only "none".
//
//   node tests/js/emptystates.js [path/to/profiles.js [path/to/profilesDashboard.html]]
//
// Red against 1.7.5: three states are the bare "None yet." and the rest stop after the fact.

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

const SRC = L.readProfiles();
const DASH = L.readDashboard();
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

function english(src, from, to) {
    const start = src.indexOf(from);
    const body = src.slice(start, src.indexOf(to, start));
    const out = {};
    const kv = /'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/g;
    let m;
    while ((m = kv.exec(body)) !== null) out[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\(['"\\])/g, '$1');
    return out;
}
const en = english(SRC, 'const EN_STRINGS = {', '\n    };');
const adminEn = english(DASH, 'const ADMIN_EN = {', '\n            };');

// Every state where a list can be empty, and the page that says it. A state whose words are
// news, not a gap (nobody signed in right now; everything is tidy), is not here.
const STATES = [
    ['links.none', en, 'accounts the household added'],
    ['profileForm.noDevicesYet', en, 'devices'],
    ['bonfire.noMembersYet', en, 'Bonfire members'],
    ['admin.households.none', adminEn, 'main accounts'],
    ['admin.links.none', adminEn, 'accounts to add'],
    ['admin.audit.empty', adminEn, 'activity'],
    ['admin.audit.noMatch', adminEn, 'activity under a filter'],
    ['admin.ann.none', adminEn, 'announcements'],
    ['admin.themes.none', adminEn, 'custom themes'],
    ['admin.themes.noFonts', adminEn, 'custom fonts'],
    ['admin.email.noAccounts', adminEn, 'email addresses'],
    ['admin.avatars.none', adminEn, 'avatars'],
    ['admin.avatars.emptyFolder', adminEn, 'a scanned folder']
];

console.log('\n-- Each empty state has a next step ---------------------------');
for (const [key, table, what] of STATES) {
    const text = table[key] || '';
    const sentences = (text.match(/[.!?](\s|$)/g) || []).length;
    ok(what + ' (' + key + '): says more than that it is empty', text.length > 0 && sentences >= 2, JSON.stringify(text));
}

console.log('\n-- And in every language --------------------------------------');
const dir = L.i18nDir();
for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.json'))) {
    const j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
    const bare = STATES.filter(([k]) => j[k] === undefined || (j[k].match(/[.!?](\s|$)/g) || []).length < 2).map(([k]) => k);
    ok(f + ' has the next step too', bare.length === 0, bare.join(', '));
}

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
