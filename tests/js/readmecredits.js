/*
 * The README credits the fonts it ships (R-1316).
 *
 * Ten fonts ship under the SIL Open Font License, each with its licence text beside it in
 * Web/themes/fonts. The README credited the avatars and said nothing about them.
 * Pairwise: every <name>-OFL.txt must have its font named in the README, and the README
 * must say which licence and where the texts are.
 *
 *   node tests/js/readmecredits.js
 */
'use strict';
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..', '..');
const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8').toLowerCase();
const dir = path.join(root, 'Web', 'themes', 'fonts');
const slugs = fs.readdirSync(dir).filter(function (f) { return /-OFL\.txt$/.test(f); })
    .map(function (f) { return f.replace(/-OFL\.txt$/, ''); });
let bad = 0;
if (slugs.length < 10) { bad++; console.log('FAIL found ' + slugs.length + ' licence files, expected 10'); }
slugs.forEach(function (s) {
    const name = s.replace(/-/g, ' ');
    if (readme.indexOf(name) < 0) { bad++; console.log('FAIL README does not name ' + name); }
});
if (readme.indexOf('open font license') < 0) { bad++; console.log('FAIL README does not name the licence'); }
if (readme.indexOf('web/themes/fonts') < 0) { bad++; console.log('FAIL README does not say where the licence texts are'); }
console.log(bad ? bad + ' failed' : 'ok: ' + slugs.length + ' fonts credited (' + slugs.length + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
