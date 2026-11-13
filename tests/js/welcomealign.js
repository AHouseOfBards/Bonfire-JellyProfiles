/*
 * First-time setup: the welcome step is a title, one sentence and one button, and it read as
 * unfinished with the sentence and button pinned left under a centred title. The welcome was
 * centred and the steps after it left-aligned (2026-11-10); the owner then asked for one
 * centred column on every step, so every sentence is centred here. setuplook.js measures the
 * whole column; this one keeps the sentence's text-align, and on Welcome, which has one
 * button, whether Next sits on the sheet's centre (with Skip beside it the pair is centred,
 * not Next).
 *
 * Needs Chrome and the Material Icons font, like formfactors.js, and says it was skipped,
 * exiting 0, without them.
 *
 *     node tests/js/welcomealign.js [/path/to/old/profiles.js]
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

const MEASURE = `(function () {
  var sheet = document.querySelector('.jpf-onboarding');
  var text = document.querySelector('.jpf-onboarding .jpf-onboarding-text');
  var next = document.getElementById('onboarding-next');
  if (!sheet || !text || !next) return { missing: true };
  var s = sheet.getBoundingClientRect(), n = next.getBoundingClientRect();
  return {
    align: getComputedStyle(text).textAlign,
    offset: Math.abs((n.left + n.width / 2) - (s.left + s.width / 2))
  };
})()`;

// scene -> whether it should be centred
const CASES = [['ob-welcome', true], ['ob-profile', true], ['ob-email', true]];

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
        for (const device of ['phone', 'tablet']) {
            for (const [scene, centred] of CASES) {
                const label = scene + ' ' + device;
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=' + scene + '&device=' + device, device);
                if (r.error) { ok(label + ' draws', false, r.error); continue; }
                const m = await c.evaluate(MEASURE);
                if (m.missing) { ok(label + ' has a sheet, a sentence and a Next button', false); continue; }
                if (centred) {
                    ok(label + ': the sentence is centred', m.align === 'center', m.align);
                    if (scene === 'ob-welcome') ok(label + ': Next sits on the sheet\'s centre', m.offset <= 2, m.offset.toFixed(1) + 'px off');
                } else {
                    ok(label + ': the form sentence stays left-aligned', m.align === 'left' || m.align === 'start', m.align);
                    ok(label + ': the form\'s buttons are not centred', m.offset > 2, m.offset.toFixed(1) + 'px off');
                }
            }
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
