/*
 * A custom theme's background picture (2.0), on screen. Every built-in theme is made into a
 * custom one carrying a picture at the dim the editor starts it at, and the picker is drawn
 * in Chrome on a tablet and a TV. Judged from the pixels, not from the CSS:
 *
 *   - The picture is drawn: the gate's background is the picture under a layer of the
 *     theme's fill at the dim, to within rounding.
 *   - The text can still be read on the worst picture for it: a white one under a dark
 *     theme, a black one under a light theme. text1 reaches 7:1 and a name 4.5:1 against
 *     the lightest (or darkest) background pixel found, the same pairs the server's check
 *     holds a theme to.
 *   - A picture that fails to load leaves the screen exactly as it is with no picture:
 *     the same pixels.
 *
 * Needs Chrome and the Material Icons font; skips (exit 0) without them.
 *
 *     node tests/js/themephoto.js [/path/to/old/profiles.js [/path/to/old/profilesDashboard.html]]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');
const zlib = require('zlib');

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

// Chrome's screenshot PNG: 8-bit RGB or RGBA, not interlaced.
function decodePng(buf) {
    let i = 8, w = 0, h = 0, type = 0;
    const idat = [];
    while (i < buf.length) {
        const len = buf.readUInt32BE(i), kind = buf.toString('ascii', i + 4, i + 8);
        const data = buf.slice(i + 8, i + 8 + len);
        if (kind === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); type = data[9]; }
        if (kind === 'IDAT') idat.push(data);
        i += 12 + len;
    }
    const bpp = type === 6 ? 4 : 3;
    const raw = zlib.inflateSync(Buffer.concat(idat));
    const stride = w * bpp;
    const out = Buffer.alloc(stride * h);
    for (let y = 0; y < h; y++) {
        const f = raw[y * (stride + 1)];
        for (let x = 0; x < stride; x++) {
            const v = raw[y * (stride + 1) + 1 + x];
            const a = x >= bpp ? out[y * stride + x - bpp] : 0;
            const b = y ? out[(y - 1) * stride + x] : 0;
            const c = x >= bpp && y ? out[(y - 1) * stride + x - bpp] : 0;
            let p = 0;
            if (f === 1) p = a; else if (f === 2) p = b; else if (f === 3) p = (a + b) >> 1;
            else if (f === 4) { const q = a + b - c, pa = Math.abs(q - a), pb = Math.abs(q - b), pc = Math.abs(q - c); p = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
            out[y * stride + x] = (v + p) & 255;
        }
    }
    const rgb = Buffer.alloc(w * h * 3);
    for (let p = 0; p < w * h; p++) for (let k = 0; k < 3; k++) rgb[p * 3 + k] = out[p * bpp + k];
    return { w, h, rgb, at: (x, y) => { const o = (Math.round(y) * w + Math.round(x)) * 3; return [rgb[o], rgb[o + 1], rgb[o + 2]]; } };
}

const lin = x => { x /= 255; return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); };
const lum = k => 0.2126 * lin(k[0]) + 0.7152 * lin(k[1]) + 0.0722 * lin(k[2]);
const ratio = (p, q) => { const a = lum(p), b = lum(q); return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05); };
const hex = k => '#' + k.map(v => (v < 16 ? '0' : '') + v.toString(16)).join('');
const rgbOfCss = s => (String(s).match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);

// The dim a picture starts at is the editor's: themePhotoDim, run out of the dashboard
// (argv[3] to point at another copy), so this measures what an administrator gets.
const L = require('./_lib');
const startingDim = (() => {
    const script = L.dashboardScript(fs.readFileSync(L.dashboardPath(), 'utf8'));
    const names = ['getThemePath', 'themeRgb', 'themeLuminance', 'themeContrast', 'themePhotoDim'];
    let fn = null;
    try {
        // eslint-disable-next-line no-new-func
        fn = new Function(names.map(n => L.extractFunction(script, n)).join('\n') + '\nreturn themePhotoDim;')();
    } catch (e) { /* an older page: 0.8, what the first version started at */ }
    return theme => (fn ? fn(theme) : 0.8);
})();
const WHITE = 'ff'.repeat(16) + '.png', BLACK = '00'.repeat(16) + '.png';
const BROKEN = '0123456789abcdef'.repeat(2) + '.png';

// Waits for the picture to be marked (or a second to pass), then reads what the page drew.
const LOOK = `(function () {
  // A wrapper that draws something of its own: a panel behind the tiles. Bonfire's TV panel
  // ends a few pixels apart between two loads of the same page (a strip at y 817-836).
  function paints(el) {
    var cs = getComputedStyle(el);
    return (cs.backgroundColor && !/rgba\\([^)]*,\\s*0\\)|transparent/.test(cs.backgroundColor))
      || cs.backgroundImage !== 'none' || cs.boxShadow !== 'none'
      || parseFloat(cs.borderTopWidth) > 0 || parseFloat(cs.borderBottomWidth) > 0
      || (cs.backdropFilter && cs.backdropFilter !== 'none') || (cs.webkitBackdropFilter && cs.webkitBackdropFilter !== 'none');
  }
  // Each drawn piece of the content and the logo, one box each: the elements with no element
  // inside (text, avatars, icons), every focusable one (a tile and its focus ring) and every
  // wrapper that paints. Not one box around them all: the logo in one corner and the tiles
  // across the middle would join into most of a TV screen, leaving a fifth of it compared.
  // The backslashes are doubled because this is a template literal: a single \\b reached
  // the page as a backspace and matched nothing.
  function contentBox(gate) {
    var els = [].slice.call(gate ? gate.querySelectorAll('.profiles-modal-content *, #profiles-logo, #profiles-logo *') : []);
    var r = devicePixelRatio || 1, out = [];
    els.forEach(function (el) {
      if (el.firstElementChild && !/\\bfocusable\\b/.test(el.getAttribute('class') || '') && el.id !== 'profiles-logo' && !paints(el)) return;
      var k = el.getBoundingClientRect();
      if (!k.width || !k.height) return;
      out.push([k.left * r, k.top * r, k.right * r, k.bottom * r]);
    });
    return out.length ? out : null;
  }
  return new Promise(function (done) {
    var n = 0;
    (function tick() {
      if (document.documentElement.getAttribute('data-jpf-photos') || ++n > 20) {
        requestAnimationFrame(function () { requestAnimationFrame(function () {
          var gate = document.getElementById('profiles-gate-overlay');
          var title = gate && gate.querySelector('h1, h2, .profiles-title');
          var name = gate && gate.querySelector('.jpf-profile-name, .profiles-name');
          var cs = getComputedStyle(document.body);
          done({
            marked: document.documentElement.getAttribute('data-jpf-photos'),
            image: gate ? getComputedStyle(gate).backgroundImage : '',
            text1: cs.getPropertyValue('--jpf-text-1').trim(),
            titleColor: title ? getComputedStyle(title).color : null,
            nameColor: name ? getComputedStyle(name).color : null,
            content: contentBox(gate)
          });
        }); });
      } else setTimeout(tick, 50);
    })();
  });
})()`;

// As shoot.js does: anything that loops is held at its first frame, any entrance run to its end.
const SETTLE = 'document.getAnimations().forEach(function(a){var t=a.effect&&a.effect.getComputedTiming();if(t&&t.iterations===Infinity){a.pause();a.currentTime=0;}else{try{a.finish();}catch(e){}}})';
// Settled twice around the fonts and a frame: one run under load caught crimson's TV shot
// mid-entrance once, and the same two pages compared equal on every run after.
async function shot(c) {
    await c.evaluate(SETTLE);
    await c.evaluate('(document.fonts ? document.fonts.ready : Promise.resolve()).then(function(){return new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})})');
    await c.evaluate(SETTLE);
    await c.evaluate('new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})');
    const buf = await c.shot('png');
    if (process.env.SAVE) fs.writeFileSync(path.join(process.env.SAVE, 'shot-' + (++shotCount) + '.png'), buf);
    return decodePng(buf);
}
let shotCount = 0;

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    const only = process.env.THEMES ? process.env.THEMES.split(',') : null;
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'))).filter(t => !only || only.indexOf(t.id) !== -1).sort((a, b) => (a.id < b.id ? -1 : 1));
}

// Points on the gate clear of the content: the four edges, a little in.
function edgePoints(png) {
    const w = png.w, h = png.h;
    return [[8, h / 2], [w - 8, h / 2], [w / 2, h - 8], [8, h - 8], [w - 8, h - 8], [w / 4, h - 8], [3 * w / 4, h - 8]];
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
        // Every theme and size is its own set of pages, independent of the others, so they are
        // measured several at a time (chrome.js checks()), in the order a serial loop would give.
        // `c` inside the callback is that scene's own tab.
        const grid = [];
        for (const theme of themes()) for (const device of ['tablet', 'tv']) grid.push({ theme, device });
        const results = await c.checks(grid, async (c, { theme, device }, ok) => {
            const DIM = startingDim(theme);
            const url = (th, dv, photo) => 'http://127.0.0.1:' + server.port + '/?theme=' + th + '&scene=picker&device=' + dv
                + (photo ? '&photo=' + photo + '&dim=' + DIM : '');
            const light = theme.scheme === 'light';
            const fill = [1, 3, 5].map(i => parseInt(theme.background.fill.slice(i, i + 2), 16));
            const worst = light ? BLACK : WHITE;
            const end = light ? 0 : 255;
            const expect = fill.map(x => Math.round(x * DIM + end * (1 - DIM)));
            {
                const at = theme.id + ' ' + device;
                await c.open(url(theme.id, device, worst), device);
                const seen = await c.evaluate(LOOK);
                const png = await shot(c);
                ok(at + ': the picture is loaded and drawn on the gate', seen.marked === worst && /backgrounds\/[0f]{32}\.png/.test(seen.image),
                    JSON.stringify(seen).slice(0, 300));
                const backs = edgePoints(png).map(p => png.at(p[0], p[1]));
                // The background is the picture under the fill at the dim. A pattern ornament
                // may sit on one point, so the closest point is the one compared.
                const near = Math.min.apply(null, backs.map(k => Math.max.apply(null, k.map((v, i) => Math.abs(v - expect[i])))));
                ok(at + ': under the fill at ' + DIM + ' (' + hex(expect) + ')', near <= 3, backs.map(hex).join(' '));
                // The text is judged on that measured point: the picture under its layer. Not
                // the lightest point, which on bloom's TV shot was a blossom of the pattern
                // decoration, drawn over the picture as it is over a theme with none; the
                // server's check leaves decorations out on purpose (docs/themes.md).
                const dist = k => Math.max.apply(null, k.map((v, i) => Math.abs(v - expect[i])));
                const pick = backs.slice().sort((a, b) => dist(a) - dist(b))[0];
                const t1 = rgbOfCss(seen.titleColor || '') .length === 3 ? rgbOfCss(seen.titleColor) : null;
                const text1 = /^#[0-9a-f]{6}/i.test(seen.text1) ? [1, 3, 5].map(i => parseInt(seen.text1.slice(i, i + 2), 16)) : t1;
                const r1 = text1 ? ratio(text1, pick) : 0;
                ok(at + ': text1 reaches 7:1 on a ' + (light ? 'black' : 'white') + ' picture (' + r1.toFixed(2) + ':1)', r1 >= 7,
                    'text ' + (text1 && hex(text1)) + ' on ' + hex(pick));
                const nm = seen.nameColor ? rgbOfCss(seen.nameColor) : null;
                const rn = nm && nm.length === 3 ? ratio(nm, pick) : 0;
                ok(at + ': a name reaches 4.5:1 there (' + rn.toFixed(2) + ':1)', rn >= 4.5, 'name ' + seen.nameColor + ' on ' + hex(pick));

                // A picture that fails: exactly the theme with none.
                // Both shot after the same wait: crimson's TV screen changes in the first
                // second (not an animation SETTLE can hold), so a plain shot taken at once
                // differed from a broken one taken after LOOK's wait by 50,000 values.
                await c.open(url(theme.id, device, null), device);
                const plainSeen = await c.evaluate(LOOK);
                const plain = await shot(c);
                const plainAfter = await c.evaluate(LOOK);
                await c.open(url(theme.id, device, BROKEN), device);
                const brokenSeen = await c.evaluate(LOOK);
                const broken = await shot(c);
                const brokenAfter = await c.evaluate(LOOK);
                // Compared exactly, over the background: every pixel outside the content's
                // measured boxes, where the picture would be and no content is. Not the content:
                // two loads of the same plain page differ there by thousands of values on a
                // TV, where focus lands on the first tile a moment after drawing. A fixed outer
                // fifth was used until 2.0's layout choices; the TV row of tiles now reaches
                // into it (x 271 of 1920), and two plain loads failed it on 8 of 9 themes.
                // The boxes of both loads, measured before and after each shot: focus lands on a
                // tile a moment after drawing and grows it, and bloom's and snowfall's TV runs
                // differed by a few hundred values just left of a tile measured before it.
                const all = [plainSeen, plainAfter, brokenSeen, brokenAfter].map(v => v.content);
                const boxes = all.every(Boolean) ? [].concat.apply([], all) : null, PAD = 40;
                // A mask of the content, each box grown by PAD for shadows and focus glows.
                const mask = new Uint8Array(plain.w * plain.h);
                (boxes || []).forEach(b => {
                    const x0 = Math.max(0, Math.floor(b[0] - PAD)), x1 = Math.min(plain.w, Math.ceil(b[2] + PAD));
                    const y0 = Math.max(0, Math.floor(b[1] - PAD)), y1 = Math.min(plain.h, Math.ceil(b[3] + PAD));
                    for (let y = y0; y < y1; y++) mask.fill(1, y * plain.w + x0, y * plain.w + x1);
                });
                let diff = 0, masked = 0;
                for (let i = 0; i < mask.length; i++) masked += mask[i];
                if (plain.w !== broken.w || plain.h !== broken.h || !boxes) diff = -1;
                else {
                    for (let y = 0; y < plain.h; y++) {
                        for (let x = 0; x < plain.w; x++) {
                            if (mask[y * plain.w + x]) continue;
                            const o = (y * plain.w + x) * 3;
                            // Within 2 of 255: crimson's fine-line pattern rasterises a level
                            // apart between two loads of the very same page. The layer this
                            // guards against (the fill at 0.75 or more over the gradient)
                            // moves every value by tens.
                            for (let k = 0; k < 3; k++) if (Math.abs(plain.rgb[o + k] - broken.rgb[o + k]) > 2) diff++;
                        }
                    }
                }
                // The boxes must leave real background to compare, or the check above is empty.
                // A quarter: bonfire's TV panel covers most of the middle and leaves 32%; the
                // fixed outer fifths this replaced compared 40%.
                const outside = boxes ? 1 - masked / mask.length : 0;
                ok(at + ': at least a quarter of the screen is compared (' + Math.round(outside * 100) + '%)', outside >= 1 / 4,
                    (boxes ? boxes.length : 0) + ' content boxes');
                ok(at + ': a picture that fails leaves the background exactly as with none', !brokenSeen.marked && diff === 0,
                    'marked ' + brokenSeen.marked + ', ' + diff + ' channel values differ');
            }
        });
        results.forEach(a => ok(a[0], a[1], a[2]));
    } finally {
        await c.close();
        await server.close();
    }
    console.log('');
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        return 1;
    }
    console.log(pass + ' passed, 0 failed');
    return 0;
}

main().then(code => { process.exitCode = code; }, e => { console.error(e.stack || e); process.exitCode = 1; });
