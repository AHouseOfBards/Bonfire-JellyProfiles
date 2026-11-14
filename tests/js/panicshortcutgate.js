/*
 * ni-C3: Ctrl+Shift+B is the plugin's only when an emergency code is set.
 *
 *  - bindPanicShortcut: took Ctrl+Shift+B on every page with preventDefault even with no
 *    emergency code set, killing the browser's own use of the key.
 *
 * The real methods are cut out of the source and run against stubs.
 *
 *   node tests/js/panicshortcutgate.js [path-to-profiles.js]
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
function between(a, b) {
    const i = src.indexOf(a), j = src.indexOf(b, i);
    if (i < 0 || j < 0) throw new Error('no block ' + a);
    return src.slice(i, j);
}

let pass = 0, bad = 0;
function check(ok, msg) { if (ok) pass++; else { bad++; console.log('FAIL ' + msg); } }
const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); };

let now = 1000000;
Date.now = () => now;

(async function () {
    // ── panic shortcut ─────────────────────────────────────────────────
    let handler = null;
    global.document = { addEventListener: (t, f) => { if (t === 'keydown') handler = f; } };
    const helpers = between('    // BEGIN keyName', '    // END keyName');
    const pp = eval('(function () {' + helpers + 'return { _panicLinkAvailable: null, shown: 0,'
        + 'showPanicPrompt() { this.shown++; },' + method('bindPanicShortcut') + '}; })()');
    pp.bindPanicShortcut();
    function press() {
        let prevented = false;
        handler({ ctrlKey: true, shiftKey: true, key: 'B', preventDefault() { prevented = true; } });
        return prevented;
    }
    check(press() === false && pp.shown === 0, 'with the server not heard from, Ctrl+Shift+B is left to the browser');
    pp._panicLinkAvailable = false;
    check(press() === false && pp.shown === 0, 'with no emergency code set, Ctrl+Shift+B is left to the browser');
    pp._panicLinkAvailable = true;
    check(press() === true && pp.shown === 1, 'with a code set, it opens the prompt and is taken');

    console.log(pass + ' passed, ' + bad + ' failed');
    process.exit(bad ? 1 : 0);
})();
