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
// How far in from the screen's edge the logo sits, on a phone, tablet or desktop: 2rem (32px at a 16px root), up
// from 1rem. The owner found it too close. A TV stays at 5% of the screen (the overscan safe
// area), which is already further in.
const EDGE_REM = 2;

const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  var box = document.getElementById('profiles-logo');
  if (!root || !box) return { found: false };
  function r(e) { var b = e.getBoundingClientRect(); return { l: b.left, t: b.top, r: b.right, b: b.bottom, w: b.width, h: b.height }; }
  var cs = getComputedStyle(box);
  var img = box.querySelector('img');
  var title = root.querySelector('.profiles-title');
  var out = { found: true, st: root.scrollTop, root: parseFloat(getComputedStyle(document.documentElement).fontSize), text: !img, box: r(box), vw: innerWidth, vh: innerHeight,
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
  [].slice.call(root.querySelectorAll('.jpf-profile-card')).forEach(function (c, i) {
    if (over(out.box, r(c))) hits.push('tile ' + (i + 1));
  });
  [].slice.call(root.querySelectorAll('.profiles-btn')).forEach(function (c, i) {
    if (over(out.box, r(c))) hits.push('button ' + (i + 1));
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
        // Every combination below is its own page and independent of the others, so they are
        // measured several at a time (chrome.js checks()). The checks come back in the order a
        // serial loop would have made them.
        const feed = list => list.forEach(a => ok(a[0], a[1], a[2]));
        const base = 'http://127.0.0.1:' + server.port + '/';

        const grid = [];
        for (const theme of themes()) for (const scene of SCENES) for (const device of DEVICES) grid.push({ theme, scene, device });
        feed(await c.checks(grid, async (t, { theme, scene, device }, ok) => {
            const r = await t.open(base + '?theme=' + theme + '&scene=' + scene + '&device=' + device, device);
            const where = theme + ' ' + scene + ' ' + device;
            if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
            const m = await t.evaluate(MEASURE);
            if (!m.found) { ok(where + ': a logo is drawn', false, 'none'); return; }
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
            const top = m.box.t + m.st, side = m.vw - m.box.r;
            ok(where + ': ' + (tv ? 'unchanged on a TV: 5% in' : 'at least ' + EDGE_REM + 'rem from the top and the side'),
                tv ? Math.abs(side - m.vw * 0.05) < 1 && Math.abs(top - m.vh * 0.05) < 1 : top >= EDGE_REM * m.root - 0.5 && side >= EDGE_REM * m.root - 0.5,
                'top ' + top + ', side ' + side);
        }));

        // Rounded or square (the Logo corners setting, 2.0): square takes the picture's and the
        // backing's corners off, rounded is what it always was. The backing is tried by putting
        // its class on the box, so no particular logo has to fail to read on a theme.
        const CORNERS = `(function () { var b = document.getElementById('profiles-logo'); var i = b.querySelector('img');
          var o = { img: i ? parseFloat(getComputedStyle(i).borderTopLeftRadius) || 0 : -1 };
          ['jpf-logo-back-dark', 'jpf-logo-back-light'].forEach(function (c) { b.classList.add(c);
            o[c] = parseFloat(getComputedStyle(b).borderTopLeftRadius) || 0; b.classList.remove(c); });
          return o; })()`;
        const cornerGrid = [];
        for (const theme of ['bonfire', 'bloom']) for (const device of ['phone', 'desktop']) for (const corners of ['rounded', 'square']) cornerGrid.push({ theme, device, corners });
        feed(await c.checks(cornerGrid, async (t, { theme, device, corners }, ok) => {
            const where = theme + ' ' + device + ' ' + corners + ' corners';
            const r = await t.open(base + '?theme=' + theme + '&scene=logo&device=' + device + '&corners=' + corners, device);
            if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
            const k = await t.evaluate(CORNERS);
            const want = corners === 'square' ? 0 : RADIUS[theme];
            ok(where + ': the picture', k.img === want, k.img + 'px, wanted ' + want);
            ok(where + ': the backing behind a see-through logo', k['jpf-logo-back-dark'] === want && k['jpf-logo-back-light'] === want,
                k['jpf-logo-back-dark'] + ' / ' + k['jpf-logo-back-light'] + 'px, wanted ' + want);
        }));

        // The corner is the theme's (layout.logo, 2.0). In each one, with the other layout
        // choices at their most crowded, and with a household that scrolls: the logo is in
        // its corner and never on the title, a tile or a button. At the bottom of a gate
        // that scrolls, its corner is below the last button.
        const VARIANTS = [{ lay: '', count: 0 }, { lay: ',profiles:list,align:left,size:large', count: 0 },
            { lay: ',size:large', count: 21 }, { lay: ',profiles:list,size:large', count: 21 }];
        const BOX = `(function () { var b = document.getElementById('profiles-logo'); var root = document.getElementById('profiles-gate-overlay');
          var last = 0; [].slice.call(root.querySelectorAll('.profiles-btn, .jpf-profile-card')).forEach(function (e) { last = Math.max(last, e.getBoundingClientRect().bottom); });
          return { last: last, scrolls: root.scrollHeight > root.clientHeight + 1, below: root.scrollHeight - (b.getBoundingClientRect().bottom + root.scrollTop) }; })()`;
        // Scrolled right to the top, the first thing in the content is on the screen. A flex
        // column centred with justify-content spills an overflowing gate above scroll 0, where
        // no scrolling can reach the title or the first rows (the bottom corners, 2.0).
        const REACH = `(function () { var root = document.getElementById('profiles-gate-overlay');
          var c = root.querySelector('.profiles-modal-content'); var f = c && c.firstElementChild;
          if (!f) return { found: false }; root.scrollTop = 0;
          return { found: true, first: f.getBoundingClientRect().top, top: root.getBoundingClientRect().top }; })()`;
        const cornerScenes = [];
        for (const theme of ['bonfire', 'bloom']) for (const corner of ['top-left', 'top-right', 'bottom-left', 'bottom-right'])
            for (const v of VARIANTS) for (const scene of SCENES) for (const device of DEVICES) cornerScenes.push({ theme, corner, v, scene, device });
        feed(await c.checks(cornerScenes, async (t, { theme, corner, v, scene, device }, ok) => {
            const r = await t.open(base + '?theme=' + theme + '&scene=' + scene + '&device=' + device
                + '&layout=logo:' + corner + v.lay + (v.count ? '&count=' + v.count : ''), device);
            const where = theme + ' ' + corner + v.lay + (v.count ? ' ' + v.count + ' profiles' : '') + ' ' + scene + ' ' + device;
            if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
            const m = await t.evaluate(MEASURE);
            if (!m.found) { ok(where + ': a logo is drawn', false, 'none'); return; }
            const e = await t.evaluate(BOX);
            const left = /left$/.test(corner), top = /^top/.test(corner);
            const across = left ? m.box.l < m.vw * 0.1 && m.box.r < m.vw * 0.5 : m.box.r > m.vw * 0.9 && m.box.l > m.vw * 0.5;
            const down = top ? m.box.t < 120
                : e.scrolls ? m.box.t >= e.last - 1 : m.box.b > m.vh - 120 && m.box.b <= m.vh + 1;
            ok(where + ': in its corner', across && down, 'box ' + Math.round(m.box.l) + ',' + Math.round(m.box.t) + '-' + Math.round(m.box.r) + ',' + Math.round(m.box.b)
                + ' in ' + m.vw + 'x' + m.vh + (e.scrolls ? ', scrolls, last control ends ' + Math.round(e.last) : ''));
            ok(where + ': off the title, the tiles and the buttons', m.hits.length === 0, m.hits.join(', '));
            const s = await t.evaluate(REACH);
            ok(where + ': the top of the content can be scrolled to', s.found && s.first >= s.top - 0.5,
                s.found ? 'first child at ' + Math.round(s.first) + ', gate top ' + Math.round(s.top) : 'no content');
            if (!/^tv/.test(device)) {
                // From the screen's edge on the side and at the top or bottom it is in; at the
                // bottom of a gate that scrolls, from the end of the page.
                const sideGap = left ? m.box.l : m.vw - m.box.r;
                const vGap = top ? m.box.t + m.st : e.scrolls ? e.below : m.vh - m.box.b;
                ok(where + ': at least ' + EDGE_REM + 'rem in from its edges', sideGap >= EDGE_REM * m.root - 0.5 && vGap >= EDGE_REM * m.root - 0.5, 'side ' + sideGap + ', ' + (top ? 'top ' : 'bottom ') + vGap);
            }
        }));
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
