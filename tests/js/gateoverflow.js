/*
 * The PIN box was 180px wide for a field that takes up to eight digits (maxlength 8), so
 * from the fifth digit the typed number scrolled inside it (R-1274). Eight digits must fit
 * without the field scrolling sideways, in every theme and on small screens.
 * (The maintenance cover half of this sweep, R-1273, was fixed in 1011d98.)
 *
 * Needs Chrome and the Material Icons font; skips (exit 0) without them.
 *
 *     node tests/js/gateoverflow.js [/path/to/old/profiles.js]
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

const THEMES = fs.readdirSync(path.join(ROOT, 'Web', 'themes'))
    .filter(f => /\.json$/.test(f) && f !== 'fonts.json').map(f => f.replace(/\.json$/, '')).sort();

const PIN = `(function () {
  var b = document.getElementById('jpf-profile-pin-input');
  if (!b) return null;
  b.value = '88888888';
  return { scrollW: b.scrollWidth, clientW: b.clientWidth, w: b.offsetWidth, vw: window.innerWidth };
})()`;

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
            for (const device of ['phone', 'phone-land', 'tablet', 'tv720']) {
                const n = theme + ' ' + device;
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=pin&device=' + device, device);
                if (r.error) throw new Error(n + ': ' + r.error);
                const p = await c.evaluate(PIN);
                ok(n + ' pin: there is a box', !!p);
                if (p) {
                    ok(n + ' pin: eight digits do not scroll inside the box', p.scrollW <= p.clientW, 'content ' + p.scrollW + ' in ' + p.clientW);
                    ok(n + ' pin: the box fits the screen', p.w <= p.vw, p.w + ' in ' + p.vw);
                }
            }
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
