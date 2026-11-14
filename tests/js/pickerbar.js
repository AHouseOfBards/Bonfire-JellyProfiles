/*
 * The picker's button row (Manage profiles, Settings, Cancel) when the households do not fit.
 *
 * It sat after every household, so with several of them joined through Bonfires it was
 * pushed far below the fold. Decided: when they do not fit, the row sticks to the bottom
 * of the screen, drawn like the Save bar on the profile forms; when they fit, it stays in
 * the flow, where it always was.
 *
 * Asked of every theme on a phone, a desktop and a television, with two households and
 * with one, at sizes that overflow and sizes that fit. Whether a case overflows is read off
 * the page (the gate's scrollHeight against its clientHeight), never assumed from the
 * count, and each device must have at least one case of each kind so the checks below can
 * not pass by asking nothing.
 *
 * Overflowing:
 *   - the row is inside the viewport, is sticky, and its panel is opaque;
 *   - every tile, brought into view the way the remote does (_focusVisibly), is on screen
 *     and clear of the row;
 *   - walking Down with the remote's own mover (_moveOverlayFocus) from the first tile
 *     reaches the row, never lands on a tile under it, and Up from the row returns to a
 *     tile in the last row.
 * Fitting:
 *   - the row is in the normal flow (not stuck, not sticky) and below the last household,
 *     and Down from the first tile still reaches it.
 *
 * Needs Chrome; says it was skipped, exiting 0, without it. The stylesheet is always the
 * checkout's own, so pointing argv[2] at an older profiles.js removes the script half.
 *
 *     node tests/js/pickerbar.js [/path/to/old/profiles.js]
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
const DEVICES = ['phone', 'desktop', 'tv'];
// count is the size of your own household; bonfire adds a second one beside it.
const CASES = [
    { name: 'two households, crowded', count: 21, bonfire: true },
    { name: 'one household, crowded', count: 21, bonfire: false },
    { name: 'two households, a few', count: 6, bonfire: true },
    { name: 'one household, a few', count: 4, bonfire: false }
];

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id).sort();
}

// Runs in the page. Reports numbers and names, never asserts.
const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  var P = window.__bonfire;
  var bar = root && root.querySelector('.profiles-footer');
  if (!root || !bar || !P) return { missing: true };
  var vh = window.innerHeight;
  var cs = getComputedStyle(bar);
  var tiles = [].slice.call(root.querySelectorAll('.profiles-grid .jpf-profile-card'));
  var overflow = root.scrollHeight > root.clientHeight + 1;
  var rect = function (e) { var r = e.getBoundingClientRect(); return { top: r.top, bottom: r.bottom, left: r.left, right: r.right, h: r.height }; };
  var hits = function (a, b) { return a.bottom > b.top + 0.5 && a.top < b.bottom - 0.5 && a.right > b.left && a.left < b.right; };
  var alpha = function (c) { var m = /rgba?\\(([^)]*)\\)/.exec(c); if (!m) return 0; var p = m[1].split(','); return p.length > 3 ? parseFloat(p[3]) : 1; };
  var out = {
    overflow: overflow, tiles: tiles.length,
    stuckClass: bar.classList.contains('jpf-footer-stuck'),
    position: cs.position, alpha: alpha(cs.backgroundColor),
    bar: rect(bar), vh: vh,
    lastGridBottom: Math.max.apply(null, [].slice.call(root.querySelectorAll('.profiles-grid')).map(function (g) { return g.getBoundingClientRect().bottom; })),
    covered: [], offscreen: []
  };
  // Each tile, brought into view the way the remote does.
  tiles.forEach(function (t, i) {
    root.scrollTop = 0;
    P._focusVisibly(t);
    var r = rect(t), b = rect(bar);
    var name = (t.querySelector('.jpf-profile-name') || t).textContent.trim().split(/\\s+/)[0] + ' #' + i;
    if (r.top < -0.5 || r.bottom > vh + 0.5) out.offscreen.push(name);
    if (hits(r, b)) out.covered.push(name);
  });
  // Pinned: the row's own box must reach the bottom edge of the screen, and no tile may
  // be visible in whatever is left under it. Read at the top, the middle and the end of the
  // page, because a sticky box can fall short at one scroll position and not another.
  out.pinned = [];
  var maxScroll = root.scrollHeight - root.clientHeight;
  [0, Math.round(maxScroll / 2), maxScroll].forEach(function (y) {
    root.scrollTop = y;
    var br = rect(bar), below = [];
    tiles.forEach(function (t, i) {
      var r = rect(t);
      // Only what is on screen counts: a tile running on past the bottom edge is not seen.
      if (Math.min(r.bottom, vh) > br.bottom + 0.5 && r.top < vh && r.bottom > 0) below.push('#' + i);
    });
    // And nothing of a tile may be drawn over the row: its badges and lock sit above the
    // tile in z order, so each part of a tile that overlaps the row is asked what is on top
    // at the middle of the overlap.
    var over = [];
    tiles.forEach(function (t, i) {
      [].slice.call(t.querySelectorAll('*')).forEach(function (n) {
        var r = rect(n);
        var l = Math.max(r.left, br.left), rr = Math.min(r.right, br.right);
        var tp = Math.max(r.top, br.top), bt = Math.min(r.bottom, br.bottom, vh);
        if (rr - l < 1 || bt - tp < 1) return;
        // Badges and the lock are pointer-events: none, which hit testing skips; let this
        // one be hit for the moment, or the one thing drawn over the row is the one thing missed.
        var was = n.style.pointerEvents;
        n.style.pointerEvents = 'auto';
        var top = document.elementFromPoint((l + rr) / 2, (tp + bt) / 2);
        n.style.pointerEvents = was;
        if (top && !bar.contains(top) && t.contains(top)) over.push('#' + i + ' ' + String(n.className && n.className.baseVal === undefined ? n.className : n.tagName).split(' ')[0]);
      });
    });
    out.pinned.push({ y: y, gap: vh - br.bottom, below: below, over: over });
  });
  root.scrollTop = 0;
  // Walk Down from the first tile.
  root.scrollTop = 0;
  tiles[0].focus();
  var steps = 0, reached = false, under = [];
  while (steps < tiles.length + 6) {
    P._moveOverlayFocus(root, 'down');
    steps++;
    var a = document.activeElement;
    if (!a || !root.contains(a)) break;
    if (bar.contains(a)) { reached = true; break; }
    if (out.overflow && hits(rect(a), rect(bar))) under.push(steps);
  }
  out.reached = reached; out.steps = steps; out.under = under;
  // And back.
  if (reached) {
    P._moveOverlayFocus(root, 'up');
    var u = document.activeElement;
    var lastRowY = Math.max.apply(null, tiles.map(function (t) { var r = t.getBoundingClientRect(); return r.top + r.height / 2; }));
    var ur = u && u.getBoundingClientRect();
    out.upIsTile = !!u && tiles.indexOf(u) !== -1;
    out.upLastRow = out.upIsTile && Math.abs(ur.top + ur.height / 2 - lastRowY) < 12;
    out.upClear = out.upIsTile && !hits(rect(u), rect(bar));
  }
  return out;
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, {
        materialIcons: fs.existsSync(ICONS) ? ICONS : undefined,
        source: given ? fs.readFileSync(given, 'utf8') : undefined
    });
    const c = await chrome.launch();
    const seen = {};
    try {
        // Every theme, size and case is its own page, independent of the others, so they are
        // measured several at a time (chrome.js checks()), in the order a serial loop would give.
        // `c` inside the callback is that scene's own tab.
        const grid = [];
        for (const theme of themes()) {
            for (const device of DEVICES) {
                for (const k of CASES) grid.push({ theme, device, k });
            }
        }
        const results = await c.checks(grid, async (c, { theme, device, k }, ok) => {
                    const url = 'http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=picker&device=' + device
                        + '&count=' + k.count + (k.bonfire ? '&bonfire=yes' : '');
                    const where = theme + ' ' + device + ' ' + k.name;
                    const r = await c.open(url, device);
                    if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
                    await c.evaluate('new Promise(function (r) { setTimeout(r, 250); })');
                    const m = await c.evaluate(MEASURE);
                    if (m.missing) { ok(where + ': the screen draws', false, 'no gate, row or plugin'); return; }
                    seen[device + (m.overflow ? ' overflow' : ' fit')] = true;
                    if (m.overflow) {
                        const w = where + ' (overflows)';
                        ok(w + ': the row is inside the viewport',
                            m.bar.top >= -0.5 && m.bar.bottom <= m.vh + 0.5, 'row ' + Math.round(m.bar.top) + '..' + Math.round(m.bar.bottom) + ' in ' + m.vh);
                        ok(w + ': the pinned row reaches the bottom edge, with no tile under it',
                            m.pinned.every(p => p.gap <= 2 && p.gap >= -2 && p.below.length === 0 && p.over.length === 0),
                            m.pinned.map(p => 'scroll ' + p.y + ': ' + Math.round(p.gap) + 'px short, tiles under it: ' + (p.below.join(',') || 'none') + ', drawn over it: ' + (p.over.slice(0, 3).join(',') || 'none')).join('; '));
                        ok(w + ': the row is sticky and its panel is opaque', m.position === 'sticky' && m.alpha === 1, m.position + ', alpha ' + m.alpha);
                        ok(w + ': every tile comes into view clear of the row', m.covered.length === 0 && m.offscreen.length === 0,
                            'under the row: ' + m.covered.join(', ') + '; off screen: ' + m.offscreen.join(', '));
                        ok(w + ': Down reaches the row, and no stop is under it', m.reached && m.under.length === 0,
                            'reached ' + m.reached + ' after ' + m.steps + ' steps; under the row at step ' + m.under.join(', '));
                        ok(w + ': Up from the row returns to a tile in the last row, clear of it',
                            !!m.upIsTile && m.upLastRow && m.upClear, 'tile ' + m.upIsTile + ', last row ' + m.upLastRow + ', clear ' + m.upClear);
                    } else {
                        const w = where + ' (fits)';
                        ok(w + ': the row is in the normal flow', !m.stuckClass && m.position !== 'sticky', m.position + (m.stuckClass ? ', stuck class set' : ''));
                        ok(w + ': the row is below the last household', m.bar.top >= m.lastGridBottom - 0.5, 'row top ' + Math.round(m.bar.top) + ', grid bottom ' + Math.round(m.lastGridBottom));
                        ok(w + ': Down still reaches the row', m.reached, 'after ' + m.steps + ' steps');
                    }
        });
        // The theme headings, printed as the results reach each theme.
        let heading = null;
        results.forEach(a => {
            const theme = a[0].split(' ')[0];
            if (theme !== heading) { heading = theme; console.log('\n── ' + theme + ' ──'); }
            ok(a[0], a[1], a[2]);
        });
        DEVICES.forEach(d => {
            ok(d + ': at least one case overflows, so the sticky checks asked something', !!seen[d + ' overflow']);
            ok(d + ': at least one case fits, so the flow checks asked something', !!seen[d + ' fit']);
        });
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
