#!/usr/bin/env node
/*
 * The README and the translation guide each list the languages Bonfire speaks.
 *
 * Adding a language is meant to be one file in Web/i18n and nothing else; the build, server
 * and client all discover it. These two lists are prose and nothing derives them, so they
 * are checked both ways: every translation file is named in each list, and each list names
 * nothing that has no file. A contributor adding de.json is told here, not left with a
 * list one language short.
 *
 * Names are English names from Intl.DisplayNames, so 'pt-BR' must be written
 * "Portuguese (Brazil)", which is what a reader would look for.
 *
 * Usage: node tests/js/languagelist.js [README.md] [Web/i18n/README.md]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const README = process.argv[2] || path.join(L.ROOT, 'README.md');
const GUIDE = process.argv[3] || path.join(L.ROOT, 'Web', 'i18n', 'README.md');

const codes = fs.readdirSync(path.join(L.ROOT, 'Web', 'i18n'))
    .filter(f => /\.json$/.test(f)).map(f => f.replace(/\.json$/, '')).sort();
const names = new Intl.DisplayNames(['en'], { type: 'language' });
const shipped = ['English'].concat(codes.map(c => names.of(c)));

ok('there are translations to list (' + codes.length + ')', codes.length > 0);
ok('every code has an English name', shipped.every(n => n && !/^[a-z]{2,3}(-|$)/.test(n)),
   shipped.join(', '));

const ALL_NAMES = ['English', 'Arabic', 'Catalan', 'Chinese', 'Czech', 'Danish', 'Dutch',
    'Finnish', 'French', 'German', 'Greek', 'Hebrew', 'Hungarian', 'Italian', 'Japanese',
    'Korean', 'Norwegian', 'Polish', 'Portuguese', 'Romanian', 'Russian', 'Slovak',
    'Spanish', 'Swedish', 'Turkish', 'Ukrainian', 'Vietnamese'];

console.log('\n── README ─────────────────────────────────────────────────────');
const readme = fs.readFileSync(README, 'utf8').replace(/\r\n/g, '\n');
const line = (readme.match(/^- \*\*[^*]*languages?\.\*\*[^\n]*(\n  [^\n]*)*/mi) || [''])[0];
ok('the Features list has a languages entry', line.length > 0);
shipped.forEach(n => ok('README names ' + n, line.includes(n)));
ALL_NAMES.filter(n => shipped.indexOf(n) === -1).forEach(n => {
    if (line.includes(n)) ok('README does not name ' + n + ', which has no translation file', false);
});
const count = { 2: 'Two', 3: 'Three', 4: 'Four', 5: 'Five', 6: 'Six', 7: 'Seven', 8: 'Eight',
    9: 'Nine', 10: 'Ten', 11: 'Eleven', 12: 'Twelve' }[shipped.length];
ok('README counts them right ("' + count + ' languages")',
   !count || line.includes('**' + count + ' languages.**'));

console.log('\n── Translation guide ──────────────────────────────────────────');
const guide = fs.readFileSync(GUIDE, 'utf8').replace(/\r\n/g, '\n');
const table = (guide.match(/## Available now\n([\s\S]*?)\n## /) || ['', ''])[1];
const rows = table.split('\n').map(r => (/^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|$/.exec(r) || [])).filter(m => m[1]);
const listed = rows.slice(2).map(m => ({ name: m[1], file: m[2].replace(/`/g, '') }));
ok('the guide has an "Available now" table', listed.length > 0);
shipped.forEach(n => ok('the guide lists ' + n, listed.some(r => r.name === n)));
codes.forEach(c => ok('the guide lists ' + c + '.json', listed.some(r => r.file === c + '.json')));
listed.forEach(r => {
    if (r.file === 'built in') return;
    ok('the guide\'s ' + r.file + ' exists', codes.indexOf(r.file.replace(/\.json$/, '')) !== -1);
});

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
