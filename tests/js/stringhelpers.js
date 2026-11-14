/*
 * escapeHtml() and t(), the two helpers every piece of text on screen goes through.
 *
 * R-1176: escapeHtml(0) answered '', so a count of zero drew as nothing.
 * R-1174: t() replaced {tokens} one after another, so a value that itself contained a
 * later token (a profile named "{n}") was replaced a second time.
 *
 * Each function is cut out of the source and run on its own.
 *
 *     node tests/js/stringhelpers.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  - ' + detail : '')); }
}

const src = fs.readFileSync(L.profilesPath(), 'utf8');
const ctx = vm.createContext({});
vm.runInContext(
    'var activeStrings = { greet: "Hi {name}, you have {n} left", only: "{a}{b}" };' +
    'var EN_STRINGS = {};' +
    L.extractFunction(src, 'escapeHtml') + ';' +
    L.extractFunction(src, 't') + ';', ctx);

const esc = ctx.escapeHtml;
const t = ctx.t;

ok('escapeHtml(0) is "0"', esc(0) === '0', JSON.stringify(esc(0)));
ok('escapeHtml(null), (undefined) and ("") are empty', esc(null) === '' && esc(undefined) === '' && esc('') === '');
ok('escapeHtml still escapes markup', esc('<a href="x">&\'') === '&lt;a href=&quot;x&quot;&gt;&amp;&#039;', esc('<a href="x">&\''));

ok('t() fills every token', t('greet', { name: 'Sam', n: 3 }) === 'Hi Sam, you have 3 left');
ok('a value holding a later token is left as it is',
    t('greet', { name: '{n}', n: 3 }) === 'Hi {n}, you have 3 left', t('greet', { name: '{n}', n: 3 }));
ok('and so is one holding an earlier token', t('only', { a: 'x', b: '{a}' }) === 'x{a}', t('only', { a: 'x', b: '{a}' }));
ok('a token with no value stays visible', t('greet', { name: 'Sam' }) === 'Hi Sam, you have {n} left');
ok('a value of 0 is written', t('greet', { name: 'Sam', n: 0 }) === 'Hi Sam, you have 0 left');
ok('an unknown key answers the key', t('nope') === 'nope');

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
