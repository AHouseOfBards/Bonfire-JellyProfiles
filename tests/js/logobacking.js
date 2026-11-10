/*
 * A see-through logo on every theme. One logo serves all twelve, and it sat straight on the
 * gate: a white one vanished on Jellyfin Light and a near-black one on every dark theme.
 *
 * Judged from the pixels on screen, not from the code's own sums: the logo is one of three
 * pictures fixture.js draws (white, near-black and orange bars on a clear ground), so the
 * harness knows which of its pixels are painted and which are clear. From a screenshot it
 * reads the colour of the bars, of the clear gaps between them (whatever shows through:
 * the theme's background, or a backing), and of the background just outside the logo.
 *
 *   - The logo can be seen: the bars contrast 3:1 (WCAG's floor for graphics) with the gaps.
 *   - A backing only where one is needed: there is one exactly when the bars contrast under
 *     3:1 with the background outside the logo (between 2.8 and 3.3:1 either is accepted).
 *   - Changing theme with the gate open puts on or takes off the backing.
 *   - A solid picture (the JPEG scene) never gets one.
 *   - On a TV the picture is the same share of the screen at 720p and 1080p.
 *
 * Needs Chrome and the Material Icons font; skips (exit 0) without them.
 *
 *     node tests/js/logobacking.js [/path/to/old/profiles.js]
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
    return { w, h, at: (x, y) => { const o = Math.round(y) * stride + Math.round(x) * bpp; return [out[o], out[o + 1], out[o + 2]]; } };
}

const lin = x => { x /= 255; return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); };
const lum = k => 0.2126 * lin(k[0]) + 0.7152 * lin(k[1]) + 0.0722 * lin(k[2]);
const ratio = (p, q) => { const a = lum(p), b = lum(q); return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05); };
const median = a => a.slice().sort((x, y) => x - y)[a.length >> 1];
const hex = k => '#' + k.map(v => (v < 16 ? '0' : '') + v.toString(16)).join('');

// Points in fixture.js's 160x60 picture: inside a bar, and in a clear gap between bars.
const BARS = [[12, 30], [58, 30], [90, 18], [136, 40], [100, 44]];
const GAPS = [[46, 30], [24, 30], [70, 30], [114, 30], [140, 4]];

const WAIT = `(function () {
  var img = document.querySelector('#profiles-logo img');
  return new Promise(function (done) {
    var n = 0;
    (function tick() { if ((img && img.complete) || ++n > 50) setTimeout(done, 150); else setTimeout(tick, 50); })();
  });
})()`;
const MEASURE = `(function () {
  var box = document.getElementById('profiles-logo');
  var img = box && box.querySelector('img');
  if (!img || !img.offsetWidth) return null;
  var b = box.getBoundingClientRect(), i = img.getBoundingClientRect();
  return { box: { l: b.left, t: b.top, r: b.right, b: b.bottom }, img: { l: i.left, t: i.top, w: i.width, h: i.height },
    vh: innerHeight, backed: getComputedStyle(box).backgroundColor !== 'rgba(0, 0, 0, 0)' };
})()`;

async function look(c, scale) {
    await c.evaluate(WAIT);
    const m = await c.evaluate(MEASURE);
    if (!m) return null;
    const png = decodePng(await c.shot('png'));
    const at = (x, y) => png.at(x * scale, y * scale);
    const pic = (p) => at(m.img.l + p[0] * m.img.w / 160, m.img.t + p[1] * m.img.h / 60);
    const bars = BARS.map(pic), gaps = GAPS.map(pic);
    const cy = (m.box.t + m.box.b) / 2, cx = (m.box.l + m.box.r) / 2;
    const outside = [at(m.box.l - 6, cy), at(m.box.l - 6, m.box.t + 4), at(m.box.l - 6, m.box.b - 4), at(cx, m.box.b + 5), at(m.box.l + 6, m.box.b + 5)];
    const seen = median(bars.map(k => median(gaps.map(g => ratio(k, g)))));
    const bare = median(bars.map(k => median(outside.map(g => ratio(k, g)))));
    return { m, seen, bare, bar: hex(bars[0]), gap: hex(gaps[0]), out: hex(outside[0]) };
}

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
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
    const url = (theme, scene, device) => 'http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=' + device;
    try {
        const share = {};
        for (const theme of themes()) {
            for (const scene of ['logowhite', 'logodark', 'logoorange']) {
                for (const device of ['tablet', 'tv720', 'tv']) {
                    const where = theme + ' ' + scene + ' ' + device;
                    const r = await c.open(url(theme, scene, device), device);
                    if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                    const s = await look(c, chrome.DEVICES[device].scale || 1);
                    if (!s) { ok(where + ': the logo is drawn', false, 'no picture'); continue; }
                    const d = 'bars ' + s.bar + ', gaps ' + s.gap + ', outside ' + s.out + '; ' + s.seen.toFixed(2) + ':1 seen, ' + s.bare.toFixed(2) + ':1 bare, backed ' + s.m.backed;
                    ok(where + ': the logo can be seen (3:1 against what shows through it)', s.seen >= 3, d);
                    // Within a hair of 3:1 either answer is right: the client decides from the
                    // theme's colours (Glass's top-right glow is #2f4d6e, 2.97:1 to the orange),
                    // and the screen shows that glow already fading (3.14:1). Outside the band
                    // the answer must match the screen; and the first check holds everywhere.
                    if (s.bare < 2.8 || s.bare > 3.3) ok(where + ': ' + (s.bare < 3 ? 'it needs a backing and has one' : 'it reads on the background and has no backing'), s.m.backed === (s.bare < 3), d);
                    if (/^tv/.test(device)) (share[theme + scene] = share[theme + scene] || []).push(s.m.img.h / s.m.vh);
                }
            }
        }
        Object.keys(share).forEach(k => {
            const v = share[k];
            ok(k + ': on a TV the picture is the same share of the screen at 720p and 1080p', v.length === 2 && Math.abs(v[0] - v[1]) < 0.005, v.map(x => (x * 100).toFixed(1) + '%').join(' vs '));
        });

        // With the gate open, a theme change puts the backing on or takes it off.
        for (const pair of [['bonfire', 'jellyfin-light'], ['jellyfin-light', 'bonfire']]) {
            for (const scene of ['logowhite', 'logodark']) {
                const where = scene + ' ' + pair[0] + ' then ' + pair[1];
                const r = await c.open(url(pair[0], scene, 'tablet'), 'tablet');
                if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                await c.evaluate(WAIT);
                await c.evaluate('window.__bonfire.applyTheme(' + JSON.stringify(pair[1]) + ')');
                const s = await look(c, chrome.DEVICES.tablet.scale || 1);
                if (!s) { ok(where + ': the logo is drawn', false, 'no picture'); continue; }
                const d = s.seen.toFixed(2) + ':1 seen, ' + s.bare.toFixed(2) + ':1 bare, backed ' + s.m.backed;
                ok(where + ': the logo can be seen', s.seen >= 3, d);
                ok(where + ': backed exactly when it needs it', s.m.backed === (s.bare < 3), d);
            }
        }

        // A solid picture brings its own background.
        for (const theme of themes()) {
            const r = await c.open(url(theme, 'logo', 'tablet'), 'tablet');
            if (r.error) { ok(theme + ' logo: the screen draws', false, r.error); continue; }
            await c.evaluate(WAIT);
            const m = await c.evaluate(MEASURE);
            ok(theme + ' logo (solid JPEG): no backing', !!m && !m.backed, m ? 'backed ' + m.backed : 'no picture');
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
