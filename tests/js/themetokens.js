/*
 * Every --jpf-* token the stylesheet reads is defined for every theme (R-1276).
 *
 * A var(--jpf-x) with no fallback and no definition computes to nothing: the property it
 * sits in is dropped, and the control draws in whatever the browser decides. A theme file
 * that forgot a token is invisible until someone opens that one theme at that one screen.
 *
 * Enumerated pairwise: for each theme, each token the stylesheet uses without a fallback
 * must be declared by the sheet's own defaults, by the CSS that themeCss writes for that
 * theme, or set from the client script (setProperty), with the member named on failure.
 *
 *     node tests/js/themetokens.js [/path/to/old/profiles.js]
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) pass++;
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const SRC = process.argv[2] ? fs.readFileSync(process.argv[2], 'utf8') : L.readProfiles();
const SHEET = L.stylesOnly();
const strip = s => s.replace(/\/\*[\s\S]*?\*\//g, '');
const sheet = strip(SHEET);

// Used with no fallback: var(--jpf-x) and nothing else inside the brackets.
const used = new Set();
let m;
const re = /var\(\s*(--jpf-[a-z0-9-]+)\s*\)/g;
while ((m = re.exec(sheet))) used.add(m[1]);

const declared = css => {
    const out = new Set();
    const d = /(--jpf-[a-z0-9-]+)\s*:/g;
    let x;
    while ((x = d.exec(strip(css)))) out.add(x[1]);
    return out;
};
const base = declared(sheet);
const fromScript = new Set();
const sp = /setProperty\(\s*'(--jpf-[a-z0-9-]+)'/g;
while ((m = sp.exec(SRC))) fromScript.add(m[1]);

const gen = L.themeGenerator(SRC);
const payload = L.themesPayload();
ok('there are themes to check', payload.themes.length > 0);
ok('the stylesheet uses tokens', used.size > 20, used.size + ' tokens');

payload.themes.forEach(theme => {
    const mine = declared(gen.themeCss(theme, payload.patterns));
    used.forEach(tok => {
        ok(theme.id + ': ' + tok + ' is defined', base.has(tok) || mine.has(tok) || fromScript.has(tok));
    });
});

console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
