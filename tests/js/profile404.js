/*
 * An unknown profile answers 404 on every route (R-1329).
 *
 * The limits routes and library artwork answered 400 "Profile not found." while switch,
 * delete, update and the self routes answered 404 with the same message. A native client
 * cannot tell "gone" from "bad input" if the code depends on the route.
 *
 * Pairwise: every `return <Result>("Profile not found.")` in the controllers is listed and
 * each one must be NotFound.
 *
 *   node tests/js/profile404.js [path-to-Controllers-dir]
 */
'use strict';
const fs = require('fs');
const path = require('path');
const dir = process.argv[2] || path.join(__dirname, '..', '..', 'Controllers');
let bad = 0, seen = 0;
fs.readdirSync(dir).filter(function (f) { return /\.cs$/.test(f); }).forEach(function (f) {
    fs.readFileSync(path.join(dir, f), 'utf8').split(/\r?\n/).forEach(function (line, i) {
        const m = /return\s+(\w+)\(\s*"Profile not found\."/.exec(line);
        if (!m) return;
        seen++;
        if (m[1] !== 'NotFound') { bad++; console.log('FAIL ' + f + ':' + (i + 1) + ' returns ' + m[1]); }
    });
});
if (seen < 8) { bad++; console.log('FAIL only ' + seen + ' sites found; the extractor is blind'); }
console.log(bad ? bad + ' failed' : 'ok: ' + seen + ' sites all NotFound');
process.exit(bad ? 1 : 0);
