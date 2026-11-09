/*
 * The ornaments (snowflakes, petals, stars, leaves) belong behind the screen. On a phone they were seen over
 * "Remove", the "Sharing" title, avatar letters and inside the PIN box.
 *
 * Measured in a real browser by looking at pixels, not at z-index: each scene is captured twice, with the
 * ornament layer and without it, and for each element on its own we count the pixels that differ.
 *  - text: a pixel the element's own text colour paints (without ornaments) must be unchanged with them.
 *  - controls (input, button): the box itself must be unchanged, border to border.
 * Motion is off (reduced) so both captures are the same frame.
 *
 *     node tests/js/decostack.js [/path/to/old/profiles.js]
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

const THEMES = ['bloom', 'snowfall', 'harvest'];
const SCENES = ['picker', 'settings', 'edit', 'sharing', 'pin'];

// Runs in the page. `A.on` and `A.off` are PNG data URLs: with the ornaments, and without.
const COMPARE = `(async function (A) {
  function load(u) { return new Promise(function (r) { var i = new Image(); i.onload = function () { r(i); }; i.src = u; }); }
  function px(i) { var c = document.createElement('canvas'); c.width = i.width; c.height = i.height;
    var g = c.getContext('2d'); g.drawImage(i, 0, 0); return g.getImageData(0, 0, i.width, i.height); }
  var ia = await load(A.on), ib = await load(A.off);
  var on = px(ia), off = px(ib), k = ia.width / window.innerWidth;
  function diff(x, y) { var p = (y * on.width + x) * 4, d = 0;
    for (var j = 0; j < 3; j++) d += Math.abs(on.data[p + j] - off.data[p + j]); return d; }
  function rgb(s) { var m = /rgba?\\(([^)]+)\\)/.exec(s); if (!m) return null; return m[1].split(',').map(parseFloat); }
  var root = document.getElementById('profiles-gate-overlay'), out = [];
  var els = [].slice.call(root.querySelectorAll('*'));
  els.forEach(function (e) {
    var cs = getComputedStyle(e); if (cs.visibility === 'hidden' || cs.display === 'none' || parseFloat(cs.opacity) < 0.5) return;
    var r = e.getBoundingClientRect(); if (r.width < 4 || r.height < 4) return;
    var x0 = Math.max(0, Math.floor(r.left * k)), x1 = Math.min(on.width, Math.ceil(r.right * k));
    var y0 = Math.max(0, Math.floor(r.top * k)), y1 = Math.min(on.height, Math.ceil(r.bottom * k));
    if (x1 - x0 < 4 || y1 - y0 < 4) return;
    var label = e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\\s+/)[0] : '')
      + ' "' + (e.value || e.textContent || '').trim().slice(0, 24) + '"';
    var control = e.tagName === 'INPUT' || e.tagName === 'BUTTON';
    var own = [].some.call(e.childNodes, function (n) { return n.nodeType === 3 && n.textContent.trim(); });
    var n = 0, bad = 0, x, y;
    if (control) {
      for (y = y0; y < y1; y++) for (x = x0; x < x1; x++) { n++; if (diff(x, y) > 12) bad++; }
      out.push({ label: label, kind: 'control', n: n, bad: bad });
    } else if (own) {
      var c = rgb(cs.color); if (!c) return;
      for (y = y0; y < y1; y++) for (x = x0; x < x1; x++) {
        var p = (y * off.width + x) * 4, dd = 0;
        for (var j = 0; j < 3; j++) dd += Math.abs(off.data[p + j] - c[j]);
        if (dd < 24) { n++; if (diff(x, y) > 12) bad++; }
      }
      if (n >= 6) out.push({ label: label, kind: 'text', n: n, bad: bad });
    }
  });
  return out;
})`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    if (!await fetchIcons()) { console.log('  SKIPPED  no Material Icons font\n\n  0 passed, 0 failed, skipped'); return 0; }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    try {
        for (const theme of THEMES) {
            for (const scene of SCENES) {
                const where = theme + ' ' + scene + ' phone';
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=phone', 'phone', { reducedMotion: true });
                if (r.error) { ok(where + ': draws', false, r.error); continue; }
                const on = 'data:image/png;base64,' + (await c.shot('png')).toString('base64');
                await c.evaluate("(function(){var s=document.createElement('style');s.textContent='#profiles-gate-overlay::before,#profiles-gate-overlay::after{display:none !important}';document.head.appendChild(s);return 1;})()");
                await new Promise(res => setTimeout(res, 150));
                const off = 'data:image/png;base64,' + (await c.shot('png')).toString('base64');
                const list = await c.evaluate(COMPARE + '(' + JSON.stringify({ on: on, off: off }) + ')');
                const bad = list.filter(e => e.bad > 0);
                ok(where + ': something was measured (' + list.length + ' elements)', list.length > 0);
                bad.forEach(e => ok(where + ': ' + e.kind + ' ' + e.label + ' has no ornament over it', false, e.bad + ' of ' + e.n + ' pixels changed'));
                if (!bad.length) ok(where + ': nothing is drawn over text or controls', true);
            }
        }
    } finally { await c.close(); await server.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
