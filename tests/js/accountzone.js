/*
 * Your account -> Time zone. The first option names the server's zone in full,
 * "Server default (America/Chicago)", and on a phone the select cut it off mid-word
 * ("America/Chicag") under its arrow: a native select does not shorten its text with an
 * ellipsis (P2-1002).
 *
 * Asked per theme on a phone: does the selected option's text, in the select's own font, fit
 * inside the select's content box (its width less both paddings, the right one holding the
 * arrow).
 *
 * Needs Chrome and the Material Icons font, like formfactors.js, and says it was skipped,
 * exiting 0, without them.
 *
 *     node tests/js/accountzone.js [/path/to/old/profiles.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');

const ROOT = path.resolve(__dirname, '..', '..');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const ICONS = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');
const ICONS_URL = 'https://cdn.jsdelivr.net/npm/material-icons@1.13.12/iconfont/material-icons.woff2';
function fetchIcons() {
    if (fs.existsSync(ICONS)) return Promise.resolve(true);
    fs.mkdirSync(path.dirname(ICONS), { recursive: true });
    return new Promise(resolve => https.get(ICONS_URL, res => {
        if (res.statusCode !== 200) return resolve(false);
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => { fs.writeFileSync(ICONS, Buffer.concat(chunks)); resolve(true); });
    }).on('error', () => resolve(false)));
}

// Waits for the zone list, which arrives after the screen draws.
const MEASURE = `new Promise(function (resolve) {
  var tries = 0;
  (function look() {
    var sel = document.getElementById('account-zone-select');
    if (!sel && tries++ < 40) return setTimeout(look, 50);
    if (!sel) return resolve({ missing: true });
    var cs = getComputedStyle(sel);
    var ctx = document.createElement('canvas').getContext('2d');
    ctx.font = cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily;
    var label = sel.options[sel.selectedIndex].text;
    var room = sel.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
    resolve({ label: label, text: ctx.measureText(label).width, room: room });
  })();
})`;

const THEMES = ['bonfire', 'harbor', 'crimson', 'glass', 'bloom'];

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    if (!await fetchIcons()) {
        console.log('  SKIPPED  the Material Icons font could not be fetched: nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    try {
        for (const theme of THEMES) {
            const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=account&device=phone', 'phone');
            if (r.error) { ok(theme + ': the account screen draws', false, r.error); continue; }
            const m = await c.evaluate(MEASURE);
            if (m.missing) { ok(theme + ': the time zone select is drawn', false); continue; }
            ok(theme + ': "' + m.label + '" fits its select on a phone', m.text <= m.room,
               m.text.toFixed(0) + 'px of text in ' + m.room.toFixed(0) + 'px');
        }
    } finally {
        await c.close();
        await server.close();
    }
    return finish();
}

function finish() {
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
