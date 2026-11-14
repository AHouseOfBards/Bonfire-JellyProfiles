// The deprecated switcherMode field is gone from the wire (R-1333).
//
// It was the 1.3.1-beta combined setting. 1.3.2 replaced it with askOnStartup and
// switcherLocation and kept it as a deprecated alias on the preferences route. Under the
// 2.0 LTS contract a deprecated field could never be removed, so it goes before the
// freeze: saved values are still migrated on load (ProfileMapping.LegacySwitcherMode,
// covered by tests/cs/matrix and tests/cs/goldenconfig), but nothing reads or writes the
// field over the API any more.
//
//   node tests/js/switchermode.js [path/to/profiles.js]
//
// Red against 76353c8, where the request carries SwitcherMode, the answer sends it back,
// the client reads it and the reference lists it.

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

// The other files are found from the script's own checkout, so pointing argv[2] at an
// older checkout's Web/profiles.js reads that checkout's controller too.
const SRC_PATH = process.argv[2] ? path.resolve(process.argv[2]) : null;
const ROOT = SRC_PATH ? path.resolve(path.dirname(SRC_PATH), '..') : path.resolve(__dirname, '..', '..');
const read = rel => fs.readFileSync(path.join(ROOT, rel), 'utf8');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const client = L.readProfiles();
const controller = read('Controllers/ProfilesController.cs');
const request = read('Models/UpdatePreferencesRequest.cs');
const docs = read('docs/developer-api.md');

ok('the client does not read switcherMode from the server',
    !/prefs\.switcherMode|prefs\.SwitcherMode/.test(client));
ok('the preferences request has no SwitcherMode',
    !/\bSwitcherMode\b/.test(request));
ok('the preferences answers do not send SwitcherMode',
    !/\bSwitcherMode\s*=/.test(controller));
ok('the preferences route does not expand a posted SwitcherMode',
    !/request\.SwitcherMode/.test(controller));
ok('the reference does not list switcherMode',
    !/switcherMode/.test(docs));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
