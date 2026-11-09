/*
 * Dashboard, Look tab: with no avatars the library grid holds one line, "No avatars yet...".
 * The grid is auto-fill columns of 110px minimum, so that line was one grid item squeezed into a
 * single column of 120 to 280px at every window size.
 *
 * Asked in a real browser: the message must be as wide as its grid, at each size.
 *
 *     node tests/js/avatarsempty.js
 */
'use strict';
const fs = require('fs');
const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ICONS = path.join(__dirname, '..', 'screens', '.cache', 'material-icons.woff2');
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    const s = await fixture.start(0, fs.existsSync(ICONS) ? { materialIcons: ICONS } : {});
    const c = await chrome.launch();
    try {
        for (const device of ['desktop', 'tablet', 'phone']) {
            const r = await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=' + device, device);
            if (r.error) { ok(device + ': the page draws', false, r.error); continue; }
            const m = await c.evaluate("(function(){var g=document.getElementById('avatarLibraryGrid');"
                + "var d=g && g.querySelector('.fieldDescription');"
                + "if(!d) return null; return {grid:g.getBoundingClientRect().width,msg:d.getBoundingClientRect().width};})()");
            ok(device + ': the empty-library message is on the page', !!m);
            if (m) ok(device + ': the message spans its grid', m.msg >= m.grid - 1, m.msg + 'px in a ' + m.grid + 'px grid');
        }
    } finally { await c.close(); await s.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
