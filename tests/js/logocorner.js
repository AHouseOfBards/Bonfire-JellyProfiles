/*
 * The custom logo, top right of the picker (scenes "logo" and "logotext"): it was a bare
 * picture 48px tall with square corners, and the text form was small grey type. It now has
 * rounded corners and a little padding, is half as big again, and the text is in the
 * theme's title colour. Still top right, and it must not sit on the title or a tile.
 *
 * Measured in a real browser, each element on its own, in every theme and device.
 *
 *     node tests/js/logocorner.js [/path/to/old/profiles.js]
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

const DEVICES = ['phone', 'phone-land', 'tablet', 'desktop', 'tv720', 'tv'];
const SCENES = ['logo', 'logotext'];

const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  var box = document.getElementById('profiles-logo');
  if (!root || !box) return { found: false };
  function r(e) { var b = e.getBoundingClientRect(); return { l: b.left, t: b.top, r: b.right, b: b.bottom, w: b.width, h: b.height }; }
  var cs = getComputedStyle(box);
  var img = box.querySelector('img');
  var title = root.querySelector('.profiles-title');
  var out = { found: true, root: parseFloat(getComputedStyle(document.documentElement).fontSize), text: !img, box: r(box), vw: innerWidth, vh: innerHeight,
    pad: parseFloat(cs.paddingTop) + parseFloat(cs.paddingLeft), radius: parseFloat(cs.borderTopLeftRadius) || 0,
    plate: cs.backgroundColor + (cs.backgroundImage !== 'none' ? ' ' + cs.backgroundImage : ''), size: parseFloat(cs.fontSize), color: cs.color, titleColor: title ? getComputedStyle(title).color : '' };
  if (img) { var ic = getComputedStyle(img); out.imgMax = parseFloat(ic.maxHeight); out.imgRadius = parseFloat(ic.borderTopLeftRadius) || 0; out.img = r(img); }
  // Whatever it must stay off: the title's own text, and each tile.
  var hits = [];
  function over(a, b) { return a.l < b.r - 1 && a.r > b.l + 1 && a.t < b.b - 1 && a.b > b.t + 1; }
  if (title) {
    var rg = document.createRange(); rg.selectNodeContents(title);
    var tb = rg.getBoundingClientRect();
    if (over(out.box, { l: tb.left, r: tb.right, t: tb.top, b: tb.bottom })) hits.push('the title');
  }
  [].slice.call(root.querySelectorAll('.profile-card')).forEach(function (c, i) {
    if (over(out.box, r(c))) hits.push('tile ' + (i + 1));
  });
  out.hits = hits;
  return out;
})()`;

const RADIUS = {};
function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json').forEach(f => {
        const t = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
        RADIUS[t.id] = ((t.shape || {}).radius || {}).medium;
    });
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id).sort();
}

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
        for (const theme of themes()) {
            for (const scene of SCENES) {
                for (const device of DEVICES) {
                    const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=' + device, device);
                    const where = theme + ' ' + scene + ' ' + device;
                    if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                    const m = await c.evaluate(MEASURE);
                    if (!m.found) { ok(where + ': a logo is drawn', false, 'none'); continue; }
                    const tv = /^tv/.test(device);
                    ok(where + ': top right', m.box.r > m.vw * 0.5 && m.box.t < 120, 'box ' + Math.round(m.box.l) + ',' + Math.round(m.box.t) + ' in ' + m.vw);
                    ok(where + ': has some padding', m.pad >= 8, 'padding ' + m.pad);
                    // The picture's own corners only: a transparent logo shows no corner, and
                    // nothing round the picture may clip it or sit behind it as a plate.
                    // In the theme's own medium corner (Crimson's are 3px, Bloom's 16px), so the
                    // logo matches the tiles and buttons beside it; never square.
                    if (!m.text) ok(where + ': the picture has the theme\'s rounded corners', m.imgRadius > 0 && m.imgRadius === RADIUS[theme], 'radius ' + m.imgRadius + ', theme medium ' + RADIUS[theme]);
                    ok(where + ': no plate behind it and nothing clips it', m.radius === 0 && m.plate === 'rgba(0, 0, 0, 0)', 'radius ' + m.radius + ', background ' + m.plate);
                    if (m.text) {
                        ok(where + ': the text is in the title colour', m.color === m.titleColor, m.color + ' vs ' + m.titleColor);
                        ok(where + ': the text is half as big again', m.size / m.root >= (tv ? 2.0 : 1.49), m.size + 'px on a ' + m.root + 'px root');
                    } else {
                        // On a TV a tenth of the screen's height: 108px at 1080p, the same share
                        // at 720p (logobacking.js holds the two to the same share).
                        ok(where + ': the picture may be half as big again', m.imgMax >= (tv ? m.vh * 0.1 - 0.5 : 72), 'max-height ' + m.imgMax + 'px of ' + m.vh);
                    }
                    ok(where + ': off the title and the tiles', m.hits.length === 0, m.hits.join(', '));
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
