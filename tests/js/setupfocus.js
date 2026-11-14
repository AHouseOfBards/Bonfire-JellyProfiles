/*
 * First-time setup: each step opens with focus on its own control, on a television too.
 *
 * _onboardingFrame asks for Next to be focused on a TV (so a screen never opens with nothing
 * focused) and says the step's own control "focuses it afterwards". It did not: the step
 * focused its control at 50 ms and the frame's TV focus landed on Next at 100 ms, so on a
 * TV the theme step opened on Next instead of the checked card, and the email and profile
 * steps on Next instead of their field. Seen in a render of the theme step, 2026-11-13.
 *
 * Asked per step and device, after the screen has settled: which element has focus.
 *
 * Needs Chrome; says it was skipped, exiting 0, without it.
 *
 *     node tests/js/setupfocus.js [/path/to/old/profiles.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');

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
const DEVICES = ['desktop', 'tv'];
// Scene -> the selector focus must be on once the step has settled.
const STEPS = [
    ['ob-welcome', '#onboarding-next'],
    ['ob-email', '.jpf-onboarding input[type="email"], .jpf-onboarding input'],
    ['ob-profile', '.jpf-onboarding input'],
    ['ob-switcher', '.switcher-location-option[aria-checked="true"]'],
    ['ob-theme', '.jpf-theme-option[aria-checked="true"]'],
];

const settle = ms => 'new Promise(function (r) { setTimeout(r, ' + ms + '); })';
const FOCUS = sel => `(function () {
  var a = document.activeElement, want = document.querySelector(${JSON.stringify(sel)});
  function name(e) { return !e ? 'nothing' : e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\\s+/).join('.') : ''); }
  return { found: !!want, on: !!want && a === want, active: name(a) };
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    if (!fs.existsSync(ICONS)) {
        console.log('  SKIPPED  the Material Icons font is not cached (run tests/screens/shoot.js once): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    try {
        for (const device of DEVICES) {
            for (const [scene, sel] of STEPS) {
                const where = scene + ' ' + device;
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=' + scene + '&device=' + device, device);
                if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                // Past every timer the step and the frame set (50 and 100 ms).
                await c.evaluate(settle(400));
                const f = await c.evaluate(FOCUS(sel));
                ok(where + ': the step has its control', f.found, sel);
                ok(where + ': focus is on it', f.on, 'focus is on ' + f.active);
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
