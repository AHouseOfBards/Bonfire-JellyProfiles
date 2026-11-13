/*
 * Bonfire's mark, wherever the app shows it. A flame with a cut-out center over two crossed
 * logs, one svg drawn in currentColor, in the places the Material "local_fire_department"
 * glyph stood for Bonfire: the heading of each household on Who's watching, the badge on a
 * member's card, the Settings entry for sharing, and the dashboard's header.
 *
 * Judged from computed style and layout in Chrome, for every built-in theme, with two
 * households on screen (your own and a linked one):
 *
 *   - Each heading's mark is an svg with a size, and its fill resolves to that theme's
 *     --jpf-warn (your household) or --jpf-flame-deep (a linked one).
 *   - It keeps its glow (a drop-shadow, since text-shadow does nothing on an svg), and the
 *     glow is gone under .jpf-flat.
 *   - The card badge and the Settings entry draw the svg, and the dashboard's header does too.
 *   - No element left in the picker, Settings or the dashboard header draws the Material flame.
 *   - The viewBox crops nothing: drawn large, the flame and both log ends sit inside it.
 *
 * Needs Chrome; skips (exit 0) without it.
 *
 *     node tests/js/bonfiremark.js [/path/to/old/profiles.js [/path/to/old/profilesDashboard.html]]
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

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id).sort();
}

// A colour token resolved the way the page resolves it, to rgb().
const RESOLVE = `function resolve(el, token) {
  var p = document.createElement('span');
  p.style.color = 'var(' + token + ')';
  el.parentNode.appendChild(p);
  var c = getComputedStyle(p).color;
  p.parentNode.removeChild(p);
  return c;
}`;

// The Material flame, wherever it is drawn: a .material-icons element whose text is the
// ligature, or one carrying the glyph's name as a class.
const FLAME_LEFT = `function flameLeft(root) {
  var out = [];
  root.querySelectorAll('.material-icons').forEach(function (e) {
    if (e.textContent.trim() === 'local_fire_department') out.push(e.className);
  });
  return out;
}`;

const HEADINGS = `(function () {
  ${RESOLVE}
  ${FLAME_LEFT}
  var sections = Array.prototype.slice.call(document.querySelectorAll('.profiles-home-section'));
  var rows = sections.map(function (s) {
    var m = s.querySelector('.profiles-home-header > .profiles-home-icon');
    if (!m) return { missing: true };
    var r = m.getBoundingClientRect(), cs = getComputedStyle(m);
    var linked = m.classList.contains('bonfire-icon-color');
    return { tag: m.tagName.toLowerCase(), linked: linked, w: r.width, h: r.height,
      fill: cs.fill, want: resolve(m, linked ? '--jpf-flame-deep' : '--jpf-warn'),
      filter: cs.filter, display: cs.display, visibility: cs.visibility };
  });
  var root = document.documentElement;
  root.classList.add('jpf-flat');
  var flat = sections.map(function (s) {
    var m = s.querySelector('.profiles-home-header > .profiles-home-icon');
    return m ? getComputedStyle(m).filter : null;
  });
  root.classList.remove('jpf-flat');
  var badge = document.querySelector('.jpf-profile-bonfire-indicator > *');
  var br = badge ? badge.getBoundingClientRect() : null;
  return { rows: rows, flat: flat,
    badge: badge ? { tag: badge.tagName.toLowerCase(), w: br.width, h: br.height, fill: getComputedStyle(badge).fill } : null,
    left: flameLeft(document.body) };
})()`;

const SETTINGS = `(function () {
  ${FLAME_LEFT}
  var e = document.querySelector('#settings-your-bonfire > :first-child');
  if (!e) return { missing: true };
  var r = e.getBoundingClientRect();
  return { tag: e.tagName.toLowerCase(), w: r.width, h: r.height, fill: getComputedStyle(e).fill,
    color: getComputedStyle(e).color, left: flameLeft(document.body) };
})()`;

const DASH = `(function () {
  ${FLAME_LEFT}
  var e = document.querySelector('.jpf-brand-mark');
  if (!e) return { missing: true };
  var r = e.getBoundingClientRect(), cs = getComputedStyle(e);
  return { tag: e.tagName.toLowerCase(), w: r.width, h: r.height, fill: cs.fill, color: cs.color,
    label: e.getAttribute('aria-label'), key: e.getAttribute('data-i18n-aria-label'),
    left: flameLeft(document.querySelector('.jpf-brand') || document.body) };
})()`;

// Draw the heading's mark large and measure what the drawing covers against the box it has.
const CROP = `(function () {
  var src = document.querySelector('.profiles-home-header > svg.profiles-home-icon');
  if (!src) return null;
  var big = src.cloneNode(true);
  big.removeAttribute('class');
  big.setAttribute('style', 'position:fixed;left:0;top:0;width:920px;height:1180px;');
  document.body.appendChild(big);
  var box = big.getBoundingClientRect();
  // The viewBox, fitted into the box the way the browser fits it (centered, aspect kept).
  var vb = big.getAttribute('viewBox').split(' ').map(Number);
  var s = Math.min(box.width / vb[2], box.height / vb[3]);
  var vl = box.left + (box.width - vb[2] * s) / 2, vt = box.top + (box.height - vb[3] * s) / 2;
  var l = 1e9, t = 1e9, r = -1e9, b = -1e9;
  Array.prototype.forEach.call(big.children, function (c) {
    var q = c.getBoundingClientRect();
    l = Math.min(l, q.left); t = Math.min(t, q.top); r = Math.max(r, q.right); b = Math.max(b, q.bottom);
  });
  big.parentNode.removeChild(big);
  return { l: l - vl, t: t - vt, r: vl + vb[2] * s - r, b: vt + vb[3] * s - b };
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2], oldDash = process.argv[3];
    const server = await fixture.start(0, {
        materialIcons: fs.existsSync(ICONS) ? ICONS : undefined,
        source: given ? fs.readFileSync(given, 'utf8') : undefined,
        dashboard: oldDash
    });
    const c = await chrome.launch();
    const url = (theme, scene) => 'http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=tablet';
    const flameLike = f => /drop-shadow/.test(f || '');
    try {
        let cropDone = false;
        for (const theme of themes()) {
            let r = await c.open(url(theme, 'households'), 'tablet');
            if (r.error) { ok(theme + ': the households screen draws', false, r.error); continue; }
            await c.evaluate('new Promise(function (d) { setTimeout(d, 200); })');
            const h = await c.evaluate(HEADINGS);
            ok(theme + ': two households are on screen', h.rows.length === 2 && !h.rows.some(x => x.missing), JSON.stringify(h.rows));
            h.rows.forEach((x, i) => {
                const who = theme + ' ' + (i === 0 ? 'your household' : 'linked household');
                if (x.missing) return;
                ok(who + ': the heading mark is an svg', x.tag === 'svg', x.tag);
                ok(who + ': it is visible and has a size', x.w > 4 && x.h > 4 && x.display !== 'none' && x.visibility !== 'hidden', x.w + 'x' + x.h);
                ok(who + ': its fill is ' + (x.linked ? '--jpf-flame-deep' : '--jpf-warn'), x.fill === x.want, x.fill + ' vs ' + x.want);
                ok(who + ': it keeps a drop-shadow glow', flameLike(x.filter), x.filter);
            });
            ok(theme + ': the linked heading is the linked one', h.rows.length === 2 && h.rows[0].linked === false && h.rows[1].linked === true);
            ok(theme + ': .jpf-flat removes the glow from both', h.flat.length === 2 && h.flat.every(f => f === 'none'), JSON.stringify(h.flat));
            ok(theme + ': the card badge is an svg with a size', !!h.badge && h.badge.tag === 'svg' && h.badge.w > 4 && h.badge.h > 4, JSON.stringify(h.badge));
            ok(theme + ': the badge is white', !!h.badge && h.badge.fill === 'rgb(255, 255, 255)', h.badge && h.badge.fill);
            ok(theme + ': the Material flame is gone from the picker', h.left.length === 0, h.left.join(' | '));
            if (!cropDone) {
                cropDone = true;
                const k = await c.evaluate(CROP);
                // 920x1180 is 10x the viewBox, so a log end that crosses an edge reads as a
                // negative margin here; the 0.5 is for antialiasing.
                ok('the viewBox crops nothing (margins ' + (k ? [k.l, k.t, k.r, k.b].map(v => v.toFixed(1)).join(', ') : 'none') + ')',
                    !!k && k.l >= -0.5 && k.t >= -0.5 && k.r >= -0.5 && k.b >= -0.5);
            }

            r = await c.open(url(theme, 'settings'), 'tablet');
            if (r.error) { ok(theme + ': the Settings screen draws', false, r.error); continue; }
            await c.evaluate('new Promise(function (d) { setTimeout(d, 200); })');
            const s = await c.evaluate(SETTINGS);
            ok(theme + ': the Settings entry for sharing is an svg with a size', !s.missing && s.tag === 'svg' && s.w > 4 && s.h > 4, JSON.stringify(s));
            ok(theme + ': its fill follows its color', !s.missing && s.fill === s.color, s.fill + ' vs ' + s.color);
            ok(theme + ': the Material flame is gone from Settings', !s.missing && s.left.length === 0, s.left && s.left.join(' | '));
        }

        const r = await c.open('http://127.0.0.1:' + server.port + '/dashboard?tab=status', 'desktop');
        if (r.error) ok('the dashboard draws', false, r.error);
        else {
            await c.evaluate('new Promise(function (d) { setTimeout(d, 200); })');
            const d = await c.evaluate(DASH);
            ok('the dashboard header mark is an svg with a size', !d.missing && d.tag === 'svg' && d.w > 4 && d.h > 4, JSON.stringify(d));
            ok('its fill follows its color', !d.missing && d.fill === d.color, d.fill + ' vs ' + d.color);
            ok('it keeps its aria-label and the key that translates it', !d.missing && d.label === 'Bonfire logo' && d.key === 'admin.brand.logo', JSON.stringify(d));
            ok('the Material flame is gone from the dashboard header', !d.missing && d.left.length === 0, d.left && d.left.join(' | '));
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
