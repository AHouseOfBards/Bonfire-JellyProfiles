/*
 * Placeholder text, measured in a real browser. A field's ::placeholder had no rule of its own,
 * so every theme showed the browser's default grey, which on a dark or tinted field fell under
 * 4.5:1 (the PIN field in the profile form, the code field on Sharing).
 *
 * Asked of each empty field on its own, in every theme: the computed ::placeholder colour,
 * composited over the field's own background and the layers behind it, against 4.5:1. Fields
 * whose placeholder is a single space are the PIN boxes, which draw their own dots, and are
 * not text anyone reads.
 *
 *     node tests/js/placeholders.js [/path/to/old/profiles.js]
 */
'use strict';
const fs = require('fs');
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

let pass = 0; const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}
const ICONS = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');
const SCENES = ['add', 'edit', 'sharing', 'ob-profile', 'account'];
const DEVICES = ['phone'];

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id).sort();
}

// Runs in the page: the colours only. The arithmetic is below, where it can be read.
const MEASURE = `(function () {
  var out = [];
  [].slice.call(document.querySelectorAll('input, textarea')).forEach(function (e) {
    var ph = e.getAttribute('placeholder');
    if (!ph || !ph.trim() || e.type === 'hidden' || !e.offsetWidth) return;
    if (e.value) return;
    var layers = [];
    for (var n = e; n && n.nodeType === 1; n = n.parentElement) layers.push(getComputedStyle(n).backgroundColor);
    out.push({ id: e.id || e.className || e.tagName, text: ph, color: getComputedStyle(e, '::placeholder').color, layers: layers });
  });
  return out;
})()`;

function parse(c) {
    const m = /rgba?\(([^)]+)\)/.exec(c);
    if (!m) return null;
    const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(Number);
    return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
}
function over(top, under) {
    const a = top.a + under.a * (1 - top.a);
    if (!a) return { r: 0, g: 0, b: 0, a: 0 };
    const f = k => (top[k] * top.a + under[k] * under.a * (1 - top.a)) / a;
    return { r: f('r'), g: f('g'), b: f('b'), a: a };
}
function lum(c) {
    const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
}
function ratio(a, b) { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); }

async function main() {
    try { chrome.findChrome(); } catch (e) { console.log('  SKIPPED  no Chrome'); console.log('\n  0 passed, 0 failed, skipped'); return 0; }
    if (!fs.existsSync(ICONS)) { console.log('  SKIPPED  no icons font cached'); console.log('\n  0 passed, 0 failed, skipped'); return 0; }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    try {
        for (const theme of themes()) for (const scene of SCENES) for (const device of DEVICES) {
            const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=' + device, device);
            const where = theme + ' ' + scene;
            if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
            const fields = await c.evaluate(MEASURE);
            if (process.env.PROBE) console.log(where, JSON.stringify(fields));
            fields.forEach(f => {
                // Behind everything is the page, which a gate paints over; white is the worst
                // case for a light placeholder and the browser's own fallback.
                let bg = { r: 255, g: 255, b: 255, a: 1 };
                for (let i = f.layers.length - 1; i >= 0; i--) { const l = parse(f.layers[i]); if (l && l.a > 0) bg = over(l, bg); }
                const fg = over(parse(f.color), bg);
                const cr = ratio(fg, bg);
                ok(where + ' #' + f.id + ' "' + f.text + '": placeholder reads at 4.5:1', cr >= 4.5, cr.toFixed(2) + ':1 (' + f.color + ' on rgb(' + Math.round(bg.r) + ',' + Math.round(bg.g) + ',' + Math.round(bg.b) + '))');
            });
        }
    } finally { await c.close(); await server.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
