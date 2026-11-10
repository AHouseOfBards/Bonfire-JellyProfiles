/*
 * Glass, Sharing: the content card sat inside a second outer glass card, a double frame no
 * other Glass screen has. Measured in a real browser, per screen: does the form's container
 * paint a frame (fill, border, shadow, blur)? On Sharing it must not, and its own card must
 * still be framed; on the other Glass screens the container must still be.
 *
 *     node tests/js/glasssheet.js [/path/to/old/profiles.js]
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

const PROBE = `(function () {
  function painted(e) {
    var s = getComputedStyle(e), a = /rgba\\(.*,\\s*([0-9.]+)\\)$/.exec(s.backgroundColor);
    var fill = s.backgroundColor !== 'rgba(0, 0, 0, 0)' && !(a && parseFloat(a[1]) === 0);
    var line = parseFloat(s.borderTopWidth) > 0 && s.borderTopStyle !== 'none' && !/, 0\\)$/.test(s.borderTopColor);
    var bf = s.backdropFilter || s.webkitBackdropFilter;
    return { fill: fill, line: line, shadow: s.boxShadow !== 'none', blur: !!bf && bf !== 'none' };
  }
  var c = document.querySelector('#profiles-gate-overlay .create-profile-container');
  if (!c) return null;
  var card = c.querySelector('.profile-section');
  return { container: painted(c), card: card ? painted(card) : null };
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: fs.existsSync(ICONS) ? ICONS : undefined, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    try {
        for (const device of ['phone', 'tablet']) {
            for (const scene of ['sharing', 'settings', 'edit']) {
                const where = 'glass ' + scene + ' ' + device;
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=glass&scene=' + scene + '&device=' + device, device, { reducedMotion: true });
                if (r.error) { ok(where + ': draws', false, r.error); continue; }
                const m = await c.evaluate(PROBE);
                if (!m) { ok(where + ': has a form container', false); continue; }
                const k = m.container;
                const framed = k.fill || k.line || k.shadow || k.blur;
                if (scene === 'sharing') {
                    ok(where + ': the outer container paints no frame', !framed, JSON.stringify(k));
                    ok(where + ': its own card is still framed', !!(m.card && m.card.fill && m.card.line), JSON.stringify(m.card));
                } else {
                    ok(where + ': the container is still a glass card', k.fill && k.line && k.shadow, JSON.stringify(k));
                }
            }
        }
    } finally { await c.close(); await server.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
