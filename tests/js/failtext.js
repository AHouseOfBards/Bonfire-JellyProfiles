/*
 * R-1210: an error raised by the browser ("Failed to fetch", "Load failed") must not be
 * drawn on screen as it is. _failText is the one place that turns it into "can't reach
 * the server" and keeps the server's own reason when there is one.
 *
 * Every catch that hands err.message to something a person reads must go through it.
 * Read line by line, so a caller that is missed names itself.
 *
 *     node tests/js/failtext.js /path/to/old/profiles.js
 */
'use strict';

const L = require('./_lib');

const lines = L.readProfiles().split(/\r?\n/);
const shown = /\b(showAlert|fail|_onboardingError|showError|onError)\s*\(/;
const bad = [];
lines.forEach((line, i) => {
    if (!/\berr\.message\b/.test(line)) return;
    if (!shown.test(line)) return;
    if (/_failText/.test(line)) return;
    // The image loaders reject with their own translated sentences, not the browser's.
    if (/showError\(err\.message\)/.test(line)) return;
    bad.push((i + 1) + ': ' + line.trim());
});

let pass = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { console.log('  FAIL  ' + name + (detail ? '\n' + detail : '')); }
}
ok('_failText exists', /_failText:\s*function/.test(lines.join('\n')));
ok('no caller shows err.message without _failText (' + bad.length + ')', bad.length === 0, bad.map(b => '        ' + b).join('\n'));
console.log('\n' + pass + ' passed, ' + (2 - pass) + ' failed');
process.exit(pass === 2 ? 0 : 1);
