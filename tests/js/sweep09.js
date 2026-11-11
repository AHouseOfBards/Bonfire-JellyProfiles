/*
 * Round 1 sweep, batch 9: client-script defects that a source-level check can pin.
 *
 *   R-1229  a value set through textContent must not be escapeHtml'd first, or the page
 *           shows the entity (&#039;) instead of the character.
 *
 *   node tests/js/sweep09.js [path/to/profiles.js]
 */
'use strict';
const L = require('./_lib');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

console.log('\n-- R-1229: textContent takes text, not markup --');
const lines = SRC.split(/\r?\n/);
const bad = [];
lines.forEach(function (ln, i) {
    // textContent / innerText / .value / .title assigned from escapeHtml(...) shows the entity.
    if (/\.(textContent|innerText|title|placeholder)\s*=\s*escapeHtml\(/.test(ln)) bad.push((i + 1) + ': ' + ln.trim());
});
ok('no textContent (or title, placeholder) is assigned an escapeHtml() result', bad.length === 0, bad.join(' | '));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
