/*
 * The Add and Edit profile forms on a wide window (two columns, from 900px).
 *
 * The left column (Profile, Security) was much shorter than the right (Libraries, then Viewing
 * limits with its seven day rows), so a large empty area sat under Security, beside Viewing
 * limits. Viewing limits is now a card across both columns with its two halves side by side,
 * and a column that is shorter than its neighbour stretches its cards to match.
 *
 * Asked card by card, never as a total: for each card, the part of its height that has no card
 * beside it, as the longest unbroken run. The limit is the height of that theme's own card
 * header (title and subtitle, the thing a card cannot be without), measured on the page; the
 * ordinary space between two cards is shorter than that in every theme, so it passes.
 * Measured with More closed and open, Add and Edit, in every theme, at desktop, tablet and
 * television sizes. Below 900px nothing changes: one column, the halves stacked.
 *
 * Needs Chrome, like statusline.js, and says it was skipped, exiting 0, without it.
 *
 *     node tests/js/viewwide.js [/path/to/old/profiles.js]
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
    if (cond) { pass++; if (process.env.VERBOSE) console.log('  PASS  ' + name); }
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

const SCENES = ['add', 'add-more', 'edit', 'edit-more'];
const WIDE = ['desktop', 'tablet-land', 'tv720', 'tv'];
const NARROW = ['phone', 'tablet'];

// Runs in the page. Reports numbers, never asserts.
const MEASURE = `(function () {
  var form = document.querySelector('.jpf-create-profile-container');
  if (!form) return { found: false };
  function box(e) { var b = e.getBoundingClientRect(); return { l: b.left, t: b.top, r: b.right, b: b.bottom, w: b.width, h: b.height }; }
  var cards = [].slice.call(form.querySelectorAll('.jpf-profile-section')).filter(function (e) { return e.getBoundingClientRect().height > 0; });
  var rects = cards.map(box);
  var header = cards[0].querySelector('.jpf-profile-section-header').getBoundingClientRect().height;
  var f = box(form);
  var cs = getComputedStyle(form);
  var inner = { l: f.l + parseFloat(cs.paddingLeft), r: f.r - parseFloat(cs.paddingRight) };
  // For each card, the longest run of its height with no card beside it.
  var gaps = [];
  rects.forEach(function (a, i) {
    var side = rects.filter(function (b, j) { return j !== i && (b.r <= a.l + 2 || b.l >= a.r - 2); });
    if (!side.length) return;
    var spans = side.map(function (b) { return [Math.max(b.t, a.t), Math.min(b.b, a.b)]; })
      .filter(function (s) { return s[1] > s[0]; }).sort(function (x, y) { return x[0] - y[0]; });
    var at = a.t, worst = 0;
    spans.forEach(function (s) { if (s[0] > at) worst = Math.max(worst, s[0] - at); at = Math.max(at, s[1]); });
    worst = Math.max(worst, a.b - at);
    gaps.push({ title: cards[i].querySelector('.jpf-profile-section-title').textContent, gap: Math.round(worst) });
  });
  var slot = form.querySelector('[id$="-viewing-slot"]');
  var viewing = slot && slot.querySelector('.jpf-profile-section');
  var sides = viewing ? [].slice.call(viewing.querySelectorAll('.jpf-viewing-side')).map(box) : [];
  var vb = viewing ? box(viewing) : null;
  return {
    found: true, width: window.innerWidth, twoCol: form.classList.contains('is-two-col'),
    header: header, gaps: gaps, cards: cards.length,
    viewing: vb, sides: sides, inner: inner,
    sideways: form.scrollWidth - form.clientWidth,
    pageSideways: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    outside: rects.filter(function (r) { return r.l < inner.l - 1 || r.r > inner.r + 1; }).length
  };
})()`;

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
    chrome.DEVICES['tablet-land'] = { width: 1180, height: 820, mobile: true, scale: 1 };
    let measured = 0;
    try {
        for (const theme of themes()) {
            for (const device of WIDE.concat(NARROW)) {
                const wide = WIDE.indexOf(device) !== -1;
                for (const scene of SCENES) {
                    // The narrow sizes only need one pass with the fold open: it holds every card.
                    if (!wide && scene !== 'add-more' && scene !== 'edit-more') continue;
                    const where = theme + ' ' + scene + ' ' + device;
                    const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=' + device, device);
                    if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                    const m = await c.evaluate(MEASURE);
                    if (!m.found) { ok(where + ': the form is drawn', false, 'no form'); continue; }
                    measured++;
                    const more = /more/.test(scene);
                    ok(where + ': cards are drawn', m.cards >= (more ? 6 : 3), m.cards + ' cards');
                    ok(where + ': nothing sideways', m.sideways <= 1 && m.pageSideways <= 1 && m.outside === 0,
                        'form ' + m.sideways + 'px, page ' + m.pageSideways + 'px, ' + m.outside + ' cards outside');
                    ok(where + ': viewing limits is drawn', !!m.viewing && m.sides.length === 2, JSON.stringify(m.sides.length));
                    if (wide) {
                        // Asked before the halves are looked for, so it fails on its own.
                        const worst = m.gaps.reduce((x, g) => g.gap > x.gap ? g : x, { gap: 0, title: '' });
                        ok(where + ': no card has an empty area beside it over a header (' + Math.round(m.header) + 'px)', worst.gap <= m.header,
                            worst.title + ' has ' + worst.gap + 'px');
                    }
                    if (!m.viewing || m.sides.length !== 2) continue;
                    if (wide) {
                        const spans = m.viewing.w >= (m.inner.r - m.inner.l) - 2;
                        ok(where + ': viewing limits spans the form', spans, 'card ' + Math.round(m.viewing.w) + 'px in ' + Math.round(m.inner.r - m.inner.l) + 'px');
                        const [a, b] = m.sides;
                        ok(where + ': its two halves sit side by side', b.l >= a.r - 1 && Math.abs(a.t - b.t) <= 2,
                            'halves at ' + Math.round(a.l) + '-' + Math.round(a.r) + ' and ' + Math.round(b.l) + '-' + Math.round(b.r) + ', tops ' + Math.round(a.t) + '/' + Math.round(b.t));
                    } else {
                        const [a, b] = m.sides;
                        ok(where + ': one column, the halves stacked', b.t >= a.b - 1 && m.gaps.length === 0,
                            'second half at ' + Math.round(b.t) + ' after ' + Math.round(a.b) + ', ' + m.gaps.length + ' cards with a neighbour');
                    }
                }
            }
        }
    } finally {
        await c.close();
        await server.close();
    }
    ok('something was measured', measured > 100, measured + ' screens');
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
