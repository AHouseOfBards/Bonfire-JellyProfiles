/*
 * A profile whose stored avatar color is not one of the palette's 18 (made by another app
 * or an older palette). The edit form had no swatch selected and drew the preview in the
 * default blue instead of the profile's own color. The fixture's Rosalind is #81b29a, off
 * the palette.
 *
 * Asked of the edit screen on its own: is exactly one swatch selected and is it the stored
 * color, and is the preview painted in it.
 *
 * Needs Chrome and the Material Icons font, like formfactors.js, and says it was skipped,
 * exiting 0, without them.
 *
 *     node tests/js/offpalette.js [/path/to/old/profiles.js]
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

const STORED = '#81b29a';
const STORED_RGB = 'rgb(129, 178, 154)';

const MEASURE = `(function () {
  var dots = [].slice.call(document.querySelectorAll('.jpf-avatar-color-picker .jpf-color-dot'));
  var active = dots.filter(function (d) { return d.classList.contains('active'); });
  var preview = document.getElementById('edit-image-upload-preview');
  return {
    dots: dots.length,
    active: active.map(function (d) { return d.getAttribute('data-color'); }),
    checked: dots.filter(function (d) { return d.getAttribute('aria-checked') === 'true'; }).length,
    preview: preview ? getComputedStyle(preview).backgroundColor : null
  };
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
        const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=edit&device=tablet', 'tablet');
        if (r.error) { ok('the edit screen draws', false, r.error); return finish(); }
        const m = await c.evaluate(MEASURE);
        ok('the edit screen has swatches', m.dots >= 18, m.dots + ' swatches');
        ok('exactly one swatch is selected', m.active.length === 1, m.active.length + ' selected: ' + m.active.join(','));
        ok('the selected swatch is the stored colour', m.active.length === 1 && m.active[0].toLowerCase() === STORED, m.active.join(','));
        ok('the selected swatch is announced as checked', m.checked === 1, m.checked + ' checked');
        ok('the preview is the stored colour, not the default blue', m.preview === STORED_RGB, String(m.preview));
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
