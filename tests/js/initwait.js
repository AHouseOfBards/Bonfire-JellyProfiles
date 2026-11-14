/*
 * init() waits for jellyfin-web's ApiClient, and keeps waiting (r3-05-01).
 *
 * It used to give up after 600 tries at 100 ms, about a minute, and nothing started it
 * again: a page that took longer to get an ApiClient (a select-server page, a slow TV)
 * never got Bonfire until a reload. After the first minute it now asks less often, and
 * never stops.
 *
 * Runs the real init() with ApiClient undefined and a fake setTimeout.
 *
 *   node tests/js/initwait.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const lib = require('./_lib');
const src = fs.readFileSync(lib.profilesPath(), 'utf8');

function method(name) {
    const at = src.indexOf('\n        ' + name + ': function');
    if (at < 0) throw new Error('no method ' + name);
    const open = src.indexOf('{', src.indexOf(')', at));
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(at + 1, i + 1);
    }
    throw new Error('unterminated ' + name);
}

let pass = 0, bad = 0;
function check(ok, msg) { if (ok) pass++; else { bad++; console.log('FAIL ' + msg); } }

const queue = [];
const setTimeout = (fn, ms) => { queue.push({ fn, ms }); return queue.length; }; // eslint-disable-line no-unused-vars
const BONFIRE_OFF = false; // eslint-disable-line no-unused-vars
const obj = eval('({' + method('init') + '})');

obj.init();
const delays = [];
for (let i = 0; i < 2000 && queue.length; i++) {
    const next = queue.shift();
    delays.push(next.ms);
    next.fn();
}
check(queue.length === 1, 'after 2,000 tries it is still waiting for ApiClient (' + queue.length + ' timer pending)');
check(delays[0] <= 100, 'it asks quickly at first (' + delays[0] + ' ms)');
check(delays[delays.length - 1] >= 1000, 'and less often once the first minute is gone (' + delays[delays.length - 1] + ' ms)');
const fastTotal = delays.filter(d => d < 1000).reduce((a, b) => a + b, 0);
check(fastTotal <= 70000, 'the quick asking lasts about a minute (' + fastTotal + ' ms)');

console.log(bad ? bad + ' failed' : 'ok: init keeps waiting for ApiClient (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
