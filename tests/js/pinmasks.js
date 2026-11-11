/*
 * Every PIN field is masked. A PIN box is <input type="text"> masked by
 * -webkit-text-security, so a new one is in the clear until its id is added to the
 * stylesheet's list: #account-pin-input (the main PIN on Your account) was left out and
 * showed the digits as they were typed (R-1226).
 *
 * Enumerates every text input whose id has "pin" in it and resolves, per id, the last rule
 * in Web/styles.css that names it alone and sets -webkit-text-security (all are one id
 * selector, so source order decides). #pin-reset-code is the one deliberate exception:
 * it is read off a phone and typed, so it must resolve to "none".
 *
 *     node tests/js/pinmasks.js [/path/to/profiles.js]
 */
'use strict';

const L = require('./_lib');
const src = L.readProfiles();
const css = L.stylesOnly().replace(/\/\*[\s\S]*?\*\//g, '');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const VISIBLE = { 'pin-reset-code': true };
const ids = [];
const re = /<input\b[^>]*>/g;
let m;
while ((m = re.exec(src))) {
    const tag = m[0];
    const id = /\bid="([^"]*)"/.exec(tag);
    if (!id || !/pin/i.test(id[1])) continue;
    if (!/\btype="text"/.test(tag)) continue;
    if (ids.indexOf(id[1]) === -1) ids.push(id[1]);
}
ok('found the PIN inputs (at least 8)', ids.length >= 8);

function resolved(id) {
    let value = null;
    const rule = /([^{}]+)\{([^{}]*)\}/g;
    let r;
    while ((r = rule.exec(css))) {
        const named = r[1].split(',').some(s => s.trim() === '#' + id);
        if (!named) continue;
        const t = /-webkit-text-security:\s*([a-z]+)/.exec(r[2]);
        if (t) value = t[1];
    }
    return value;
}
ids.forEach(id => {
    const want = VISIBLE[id] ? 'none' : 'disc';
    ok('#' + id + ' resolves to text-security ' + want, resolved(id) === want);
});

console.log(pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
