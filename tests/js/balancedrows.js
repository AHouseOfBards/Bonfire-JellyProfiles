/*
 * Balanced rows on the picker and on Manage. With four profiles the tablet drew three tiles
 * and then one alone; the rows should even out when the width allows: 4 = 2+2, 5 = 3+2,
 * 7 = 4+3. Asked in a real browser of each row on its own, never as a count of rows:
 *
 *   - no row holds more tiles than fit across the grid (so no extra rows are made);
 *   - the number of rows is the fewest that holds everyone;
 *   - rows differ by at most one tile, the longer rows first.
 *
 * Needs Chrome and the Material Icons font, and says it was skipped, exiting 0, without them.
 *
 *     node tests/js/balancedrows.js [/path/to/old/profiles.js]
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

// Tile counts include the main account (and, on the picker, nothing else); the Add tile
// is a tile too, so what is drawn is whatever the scene reports.
const COUNTS = [3, 4, 5, 6, 7, 8, 10, 11, 13, 17];
const DEVICES = ['phone', 'tablet', 'desktop', 'tv'];
const SCENES = ['picker', 'manage'];

const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  var grid = root && root.querySelector('.profiles-grid');
  if (!grid) return { n: 0 };
  var kids = [].slice.call(grid.children).filter(function (e) { return e.classList.contains('jpf-profile-card'); });
  var rows = [];
  kids.forEach(function (k) {
    var t = Math.round(k.getBoundingClientRect().top);
    var last = rows[rows.length - 1];
    var bb = k.getBoundingClientRect().bottom;
    if (last && Math.abs(last.top - t) <= 2) last.n++; else rows.push({ top: t, n: 1, bottom: bb });
  });
  // The space between one row and the next, against the grid's own row gap plus the tile's
  // bottom margin: a break that cost an extra gap would show here, row by row.
  var rg = parseFloat(getComputedStyle(grid).rowGap) || 0;
  var mb = parseFloat(getComputedStyle(kids[0]).marginBottom) || 0;
  var spaces = [];
  for (var i = 1; i < rows.length; i++) spaces.push(Math.round((rows[i].top - rows[i - 1].bottom) * 10) / 10);
  var want = rg + mb;
  var gb = grid.getBoundingClientRect();
  var cap = 0;
  if (kids.length > 1) {
    // Pitch from two tiles side by side in the first row; the gap is what is between them
    // beyond the tile's own margin box, so this holds with a flex gap and with margins.
    var a = kids[0].getBoundingClientRect(), b = kids[1].getBoundingClientRect();
    var cs = getComputedStyle(kids[0]);
    var outer = a.width + parseFloat(cs.marginLeft) + parseFloat(cs.marginRight);
    var pitch = b.left - a.left;
    if (b.top - a.top > 2 || pitch <= 0) { pitch = outer; }
    var gap = Math.max(0, pitch - outer);
    cap = Math.floor((gb.width + gap + 1) / pitch);
  }
  return { n: kids.length, rows: rows.map(function (r) { return r.n; }), cap: cap, spaces: spaces, want: want };
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
        for (const scene of SCENES) {
            for (const device of DEVICES) {
                for (const count of COUNTS) {
                    const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=' + scene
                        + '&device=' + device + '&count=' + count, device);
                    const where = scene + ' ' + device + ' ' + count;
                    if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                    const m = await c.evaluate(MEASURE);
                    if (m.n < 2) { ok(where + ': tiles are shown', false, m.n + ' tiles'); continue; }
                    const label = m.rows.join('+') + ' (fits ' + m.cap + ' across)';
                    const most = Math.max.apply(null, m.rows), least = Math.min.apply(null, m.rows);
                    const fewest = Math.ceil(m.n / m.cap);
                    let ordered = true;
                    for (let i = 1; i < m.rows.length; i++) if (m.rows[i] > m.rows[i - 1]) ordered = false;
                    ok(where + ': no row is longer than fits, and no extra rows: ' + label,
                        most <= m.cap && m.rows.length === fewest, label);
                    ok(where + ': rows differ by at most one: ' + label, most - least <= 1, label);
                    ok(where + ': every row sits one gap below the last (' + m.want + 'px): ' + m.spaces.join(', '),
                        m.spaces.every(x => Math.abs(x - m.want) <= 1.5), m.spaces.join(', '));
                    ok(where + ': the longer rows come first: ' + label, ordered, label);
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
