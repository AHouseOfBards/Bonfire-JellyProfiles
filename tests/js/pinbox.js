/*
 * The PIN box, measured in a real browser. Two defects:
 *
 *   1. The empty box was blank (or carried words). It shows four dim dots until something
 *      is typed, like a phone's lock screen, drawn as background gradients so there is no
 *      text to translate and nothing for a screen reader to say: the field is still named
 *      by its aria-label, never "bullet bullet". The dots are tied to :placeholder-shown
 *      (Chromium 47), because ::placeholder is Chromium 57 and a television would drop it.
 *   2. Focus drew two rings: the accent border and glow of the field's own rule, plus the
 *      2px outline that keyboard focus gets everywhere. Each of the three is measured on
 *      its own and every pair is asserted not to occur together.
 *
 * Needs Chrome and the Material Icons font; skips (exit 0) without them.
 *
 *     node tests/js/pinbox.js [/path/to/old/profiles.js]
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
const SCENES = ['pin', 'mainpin', 'ob-mainpin', 'pinreset'];
// Not #pin-reset-code: the code from the email is six visible digits, not a PIN, and four
// PIN dots over it said the wrong thing. It is asked about on its own below.
const BOXES = '#profile-pin-input, #master-pin-input, #main-pin-new, #pin-reset-new';

// Runs in the page. Reports numbers; every check names its own member.
const MEASURE = `(function () {
  var wait = function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };
  var boxes = [].slice.call(document.querySelectorAll(${JSON.stringify(BOXES)})).filter(function (b) { return b.offsetWidth; });
  var out = [];
  return boxes.reduce(function (chain, b) {
    return chain.then(function () {
      b.blur();
      return wait(450).then(function () {
        var cs = getComputedStyle(b);
        var rec = { id: b.id, label: b.getAttribute('aria-label') || '', placeholder: b.getAttribute('placeholder'),
          dots: (cs.backgroundImage.match(/radial-gradient/g) || []).length, border0: cs.borderTopColor };
        b.value = '1';
        rec.dotsTyped = (getComputedStyle(b).backgroundImage.match(/radial-gradient/g) || []).length;
        b.value = '';
        b.focus();
        return wait(450).then(function () {
          var f = getComputedStyle(b);
          rec.outline = f.outlineStyle !== 'none' && parseFloat(f.outlineWidth) > 0;
          rec.glow = f.boxShadow !== 'none';
          rec.border = f.borderTopColor !== rec.border0;
          rec.detail = f.outlineStyle + ' ' + f.outlineWidth + ' | ' + f.boxShadow + ' | ' + rec.border0 + ' -> ' + f.borderTopColor;
          out.push(rec);
        });
      });
    });
  }, Promise.resolve()).then(function () {
    var code = document.getElementById('pin-reset-code');
    if (code) out.code = { dots: (getComputedStyle(code).backgroundImage.match(/radial-gradient/g) || []).length,
      placeholder: code.getAttribute('placeholder') || '' };
    return { boxes: out, code: out.code || null };
  });
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
            for (const scene of SCENES) {
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=tablet', 'tablet');
                if (r.error) throw new Error(theme + ' ' + scene + ': ' + r.error);
                const got = await c.evaluate(MEASURE);
                const boxes = got.boxes;
                const n = theme + ' ' + scene;
                if (scene === 'pinreset') {
                    const cd = got.code;
                    ok(n + ' #pin-reset-code: the emailed code is not drawn as a PIN (no dots)', !!cd && cd.dots === 0, cd ? cd.dots + ' gradients' : 'no code field');
                    ok(n + ' #pin-reset-code: it says what goes in it', !!cd && cd.placeholder.trim().length > 0, cd ? JSON.stringify(cd.placeholder) : 'no code field');
                }
                ok(n + ': there is a PIN box to measure', boxes.length > 0);
                boxes.forEach(b => {
                    const w = n + ' ' + b.id;
                    ok(w + ': empty, it shows four dots', b.dots === 4, b.dots + ' gradients');
                    ok(w + ': typed into, the dots are gone', b.dotsTyped === 0, b.dotsTyped + ' gradients');
                    ok(w + ': it carries no words as a placeholder', !b.placeholder || !b.placeholder.trim(), JSON.stringify(b.placeholder));
                    ok(w + ': a screen reader still has the field\'s label', b.label.trim().length > 0 && !/[•●]/.test(b.label), JSON.stringify(b.label));
                    ok(w + ': focus is not an outline and a glow', !(b.outline && b.glow), b.detail);
                    ok(w + ': focus is not an outline and a border change', !(b.outline && b.border), b.detail);
                    ok(w + ': focus is not a glow and a border change', !(b.glow && b.border), b.detail);
                    ok(w + ': focus is shown', b.outline || b.glow || b.border, b.detail);
                });
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
