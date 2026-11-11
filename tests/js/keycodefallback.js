#!/usr/bin/env node
/*
 * R-1301-adjacent sweep leftover: keyboard handlers that read only e.key.
 *
 * e.key is Chromium 51. Tizen 3.0 is Chromium 47, where it is undefined, so an Enter
 * handler that reads only e.key does nothing there and the remote cannot confirm
 * anything. Every comparison goes through keyName/isKey, which fall back to keyCode.
 *
 * Two questions, asked separately:
 *   1. Does any handler still compare e.key to a key name directly? (pairwise: each site
 *      is named, not counted)
 *   2. Does the helper itself give the right answer when e.key is missing?
 *
 *   node tests/js/keycodefallback.js [path/to/profiles.js]
 */
'use strict';

const L = require('./_lib');
const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

console.log('\n── No handler compares e.key directly ───');

// Not keyboard names: the TV Back key (already paired with keyCode 10009 and 461 where
// it is used) and a storage event's key.
const EXEMPT = /XF86Back|jellyfin_profiles_settings_epoch/;
const lines = SRC.split('\n');
let direct = 0;
lines.forEach((line, i) => {
    const code = line.replace(/^\s*\/\/.*$/, '');
    const cmp = /\b\w+\.key\s*[!=]==?\s*'[^']*'/g;
    let m;
    while ((m = cmp.exec(code))) {
        if (EXEMPT.test(m[0])) continue;
        direct++;
        ok('line ' + (i + 1) + ' goes through isKey', false, m[0]);
    }
    if (/switch\s*\(\s*\w+\.key\s*\)/.test(code)) {
        direct++;
        ok('line ' + (i + 1) + ' switches through keyName', false, code.trim());
    }
});
ok('no direct e.key comparison is left', direct === 0, direct + ' found');

console.log('\n── The helper answers when e.key is missing ───');

const block = /\/\/ BEGIN keyName[\s\S]*?\/\/ END keyName/.exec(SRC);
ok('the helper block is present', !!block);
if (block) {
    const api = new Function(block[0] + '\nreturn { keyName: keyName, isKey: isKey };')();
    const k = (key, keyCode) => ({ key: key, keyCode: keyCode });
    const cases = [
        ['Enter', 13], ['Escape', 27], ['Esc', 27], [' ', 32], ['Tab', 9], ['Backspace', 8],
        ['ArrowLeft', 37], ['ArrowUp', 38], ['ArrowRight', 39], ['ArrowDown', 40]
    ];
    cases.forEach(c => {
        ok(c[0] + ' by keyCode alone', api.isKey(k(undefined, c[1]), c[0]));
        ok(c[0] + ' by key alone', api.isKey(k(c[0], 0), c[0]));
        ok(c[0] + ' by "Unidentified" plus keyCode', api.isKey(k('Unidentified', c[1]), c[0]));
    });
    ok('Enter is not Escape', !api.isKey(k(undefined, 13), 'Escape'));
    ok('a real key wins over a stale keyCode', !api.isKey(k('a', 13), 'Enter'));
    ok('old "Left" is ArrowLeft', api.keyName(k('Left', 0)) === 'ArrowLeft');
    ok('old "Esc" is Escape', api.keyName(k('Esc', 0)) === 'Escape');
    ok('B by keyCode', api.isKey(k(undefined, 66), 'B'));
    ok('no key and no code is nothing', api.keyName(k(undefined, 0)) === '');
    ok('a missing event is nothing', api.keyName(null) === '');
}

console.log('');
if (fails.length) {
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
