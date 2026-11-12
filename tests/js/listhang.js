/*
 * A /list request that hangs must not let the poll send a fresh one every tick (r2-27-01).
 *
 * _listPending treats a request older than POLL_RETRY_MS as lost, so the poll sends another.
 * The stamp used to be written only when nothing was in flight; the hung request kept it, so
 * after the first 30 s every tick found the stamp old and sent again, about twice a second.
 *
 * Runs the real _listPending, _listPollMayRequest and _requestList on a fake clock with a
 * fetch that never answers.
 *
 *   node tests/js/listhang.js [path-to-profiles.js]
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

let now = 1000000;
Date.now = () => now;
let sent = 0;
global.fetch = () => { sent++; return new Promise(() => {}); };

const obj = eval('({ POLL_RETRY_MS: 30000, _listInFlight: 0, _listSentAt: 0, _listRetryAt: 0,'
    + 'getAuthHeaders: function () { return {}; },'
    + method('_listPending') + ',' + method('_listPollMayRequest') + ',' + method('_requestList') + '})');
const api = { getUrl: p => p };

let bad = 0, pass = 0;
function check(ok, msg) { if (!ok) { bad++; console.log('FAIL ' + msg); } else { pass++; } }

obj._requestList(api, 't');
check(!obj._listPollMayRequest(), 'a request just sent should hold the poll back');
now += 31000;
check(obj._listPollMayRequest(), 'a request silent for 30 s is treated as lost');
obj._requestList(api, 't');
for (let i = 0; i < 10; i++) {
    now += 500;
    if (obj._listPollMayRequest()) { check(false, 'the poll may send again ' + (i + 1) * 500 + ' ms after a resend'); break; }
}
console.log(bad ? bad + ' failed' : 'ok: a hung list request is retried once per ' + obj.POLL_RETRY_MS + ' ms, not every tick (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
