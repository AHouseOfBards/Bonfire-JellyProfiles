/*
 * First-time setup looks the same on every step: one centered column, the buttons together
 * with Skip as text beside the main one, progress dots instead of a "STEP 1 OF 5" line, and
 * Bonfire's mark above the welcome.
 *
 * It was Welcome and Main PIN centered and the other four left-aligned, so the content
 * jumped sideways between steps; Skip sat at the far edge of the row; the step count was a
 * small line of capitals.
 *
 * Asked of every step, theme and device on its own, never as a count: the heading, every
 * line of text, the field and the button row each sit on one center line (a line of text is
 * measured line by line, so left-aligned text in a full-width box cannot pass); a field
 * that is not a PIN spans the column; Skip is beside the main button, is text (no fill, no
 * border), and comes after it; the dots are drawn, the current one is the wide one, done and
 * to-come differ from it and from each other, the current one is the theme's accent, and
 * the step is in words for a screen reader; Welcome has the mark, above the heading, in the
 * accent, and no dots.
 *
 * Not checked: the administrator's welcome note. It was cut on purpose (the server stores
 * and sends no such text; tests/cs/onboarding asserts it), so scenes.js's `welcome:` field
 * is read by nothing.
 *
 * Needs Chrome and the Material Icons font, and says it was skipped, exiting 0, without them.
 *
 *     node tests/js/setuplook.js [/path/to/old/profiles.js]
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

// Runs in the page. Reports numbers and names, never asserts.
const MEASURE = `(function () {
  var content = document.querySelector('.profiles-modal-content');
  if (!content) return { missing: 'no content' };
  var c = content.getBoundingClientRect();
  var middle = c.left + c.width / 2;
  function visible(el) { var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; }
  function lineCenters(el) {
    var range = document.createRange();
    range.selectNodeContents(el);
    return [].slice.call(range.getClientRects()).filter(function (r) { return r.width > 0 && r.height > 0; })
      .map(function (r) { return r.left + r.width / 2; });
  }
  function boxCenter(el) { var r = el.getBoundingClientRect(); return r.left + r.width / 2; }
  var out = { middle: middle, items: [] };
  function item(name, el, lines) {
    if (!el || !visible(el)) return;
    (lines ? lineCenters(el) : [boxCenter(el)]).forEach(function (x) { out.items.push({ name: name, off: x - middle }); });
  }
  item('heading', content.querySelector('h1.profiles-title'), true);
  [].slice.call(content.querySelectorAll('.jpf-onboarding-text, .profiles-pin-note, .jpf-form-hint, .jpf-onboarding-label')).forEach(function (e, i) {
    item('text ' + (e.className || e.tagName), e, true);
  });
  var fields = '#jpf-main-pin-new, #onboarding-email, #onboarding-email-code, #onboarding-profile-name, .jpf-onboarding-check, .jpf-onboarding-choices, .jpf-theme-grid';
  [].slice.call(content.querySelectorAll(fields)).forEach(function (e) { item('field ' + (e.id || e.className), e, false); });

  var main = document.getElementById('onboarding-next') || document.getElementById('main-pin-save');
  var skip = document.getElementById('onboarding-skip') || document.getElementById('main-pin-skip');
  var col = content.querySelector('.jpf-onboarding, .jpf-pin-entry-container');
  out.hasMain = !!main; out.hasSkip = !!skip;
  if (main) {
    var mr = main.getBoundingClientRect(), left = mr.left, right = mr.right;
    if (skip) { var sr = skip.getBoundingClientRect(); left = Math.min(left, sr.left); right = Math.max(right, sr.right); }
    out.items.push({ name: 'button row', off: (left + right) / 2 - middle });
    var ms = getComputedStyle(main);
    out.mainFilled = ms.backgroundColor !== 'rgba(0, 0, 0, 0)' && ms.backgroundColor !== 'transparent';
  }
  if (skip && main) {
    var a = main.getBoundingClientRect(), b = skip.getBoundingClientRect();
    out.gap = b.left - a.right;
    out.sameRow = Math.abs((a.top + a.height / 2) - (b.top + b.height / 2)) < 6;
    out.after = b.left >= a.right - 1;
    var ss = getComputedStyle(skip);
    out.skipBg = ss.backgroundColor;
    out.skipBorder = ss.borderTopWidth + ' ' + ss.borderTopStyle + ' ' + ss.borderTopColor;
    out.skipFilled = ss.backgroundColor !== 'rgba(0, 0, 0, 0)' && ss.backgroundColor !== 'transparent';
    out.skipBordered = parseFloat(ss.borderTopWidth) > 0 && ss.borderTopStyle !== 'none' && !/rgba\\(\\s*\\d+,\\s*\\d+,\\s*\\d+,\\s*0\\)/.test(ss.borderTopColor) && ss.borderTopColor !== 'transparent';
    out.skipText = (skip.textContent || '').trim();
  }
  // The column is the container's inside: its own padding is not the field's to fill.
  if (col) {
    var cr = col.getBoundingClientRect(), cs = getComputedStyle(col);
    out.colWidth = cr.width - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight) - parseFloat(cs.borderLeftWidth) - parseFloat(cs.borderRightWidth);
  }
  var wide = [];
  [].slice.call(content.querySelectorAll('#onboarding-email, #onboarding-email-code, #onboarding-profile-name, .jpf-onboarding-choices, .jpf-theme-grid')).forEach(function (e) {
    if (!visible(e) || !col) return;
    var r = e.getBoundingClientRect();
    if (r.width < out.colWidth - 4) wide.push((e.id || e.className) + ' is ' + Math.round(r.width) + 'px in a ' + Math.round(out.colWidth) + 'px column');
  });
  out.narrow = wide;

  // The dots.
  var dots = content.querySelector('.jpf-onboarding-dots');
  out.oldLine = !!content.querySelector('.jpf-onboarding-step');
  var probe = document.createElement('span');
  probe.style.color = 'var(--jpf-accent)';
  content.appendChild(probe);
  var accent = getComputedStyle(probe).color;
  content.removeChild(probe);
  out.accent = accent;
  if (dots) {
    out.dots = { label: dots.getAttribute('aria-label') || '', text: (dots.textContent || '').trim(), role: dots.getAttribute('role'),
      list: [].slice.call(dots.children).map(function (d) {
        var s = getComputedStyle(d), r = d.getBoundingClientRect();
        return { state: d.getAttribute('data-state') || '', w: r.width, h: r.height, bg: s.backgroundColor, border: s.borderTopWidth + ' ' + s.borderTopColor, op: s.opacity,
                 current: d.getAttribute('aria-current') };
      }) };
    var hr = content.querySelector('h1.profiles-title').getBoundingClientRect();
    out.dotsBelowHeading = dots.getBoundingClientRect().top >= hr.bottom - 1;
  }
  // The mark.
  var mark = content.querySelector('svg.jpf-mark');
  if (mark) {
    var m = mark.getBoundingClientRect(), h1 = content.querySelector('h1.profiles-title').getBoundingClientRect();
    out.mark = { above: m.bottom <= h1.top + 1, off: m.left + m.width / 2 - middle, w: m.width, color: getComputedStyle(mark).color };
  }
  return out;
})()`;

const SCENES = ['ob-welcome', 'ob-mainpin', 'ob-email', 'ob-profile', 'ob-switcher', 'ob-theme'];
const DEVICES = ['desktop', 'phone', 'tv'];
const TOL = 2;

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id).sort();
}

// `ok` is the scene's own check() from c.checks(), which shadows the harness's.
function check(where, scene, m, ok) {
    const welcome = scene === 'ob-welcome';
    if (m.missing) { ok(where + ': the step draws', false, m.missing); return; }
    ok(where + ': the step draws a heading and a main button', m.hasMain && m.items.some(i => i.name === 'heading'), 'no heading or no main button');

    // One center line, member by member.
    const off = m.items.filter(i => Math.abs(i.off) > TOL).map(i => i.name + ' ' + i.off.toFixed(1) + 'px');
    ok(where + ': heading, text, field and buttons share one center line', off.length === 0, off.join('; '));
    ok(where + ': the main button is a full button', !!m.mainFilled);
    if (m.narrow) ok(where + ': a field spans the column', m.narrow.length === 0, (m.narrow || []).join('; '));

    if (welcome) {
        ok(where + ': Welcome has no Skip', !m.hasSkip);
        ok(where + ': Welcome has no dots', !m.dots);
        ok(where + ': Welcome shows the mark', !!m.mark, 'no svg.jpf-mark');
        if (m.mark) {
            ok(where + ': the mark is above the heading', m.mark.above);
            ok(where + ': the mark is on the center line', Math.abs(m.mark.off) <= TOL, m.mark.off.toFixed(1) + 'px');
            ok(where + ': the mark is the theme accent', m.mark.color === m.accent, m.mark.color + ' vs ' + m.accent);
        }
        return;
    }

    ok(where + ': there is a Skip', m.hasSkip);
    if (m.hasSkip) {
        ok(where + ': Skip says Skip', m.skipText === 'Skip', JSON.stringify(m.skipText));
        ok(where + ': Skip is on the main button\'s row, after it', m.sameRow && m.after, 'sameRow ' + m.sameRow + ' after ' + m.after);
        ok(where + ': Skip is right beside the main button', m.gap >= 0 && m.gap <= 56, m.gap.toFixed(1) + 'px apart');
        ok(where + ': Skip is text: no fill, no border', !m.skipFilled && !m.skipBordered, 'bg ' + m.skipBg + ', border ' + m.skipBorder);
    }

    const d = m.dots;
    ok(where + ': the progress dots are drawn', !!d && d.list.length === 5, d ? d.list.length + ' dots' : 'none');
    ok(where + ': the old step line is gone', !m.oldLine);
    if (!d || d.list.length !== 5) return;
    ok(where + ': the dots sit under the heading', m.dotsBelowHeading);
    const index = SCENES.indexOf(scene) - 1;   // 0 for the first step after Welcome
    const cur = d.list.map((x, i) => x.current === 'step' ? i : -1).filter(i => i >= 0);
    ok(where + ': exactly the right dot is current', cur.length === 1 && cur[0] === index, 'current ' + JSON.stringify(cur) + ', want ' + index);
    const state = (x, i) => i < index ? 'done' : i === index ? 'current' : 'todo';
    const wide = d.list.every((x, i) => i === index ? x.w > d.list[(i + 1) % 5].w + 4 : true);
    ok(where + ': the current dot is the wide one', wide, d.list.map(x => Math.round(x.w)).join(','));
    const key = x => x.bg + '|' + x.border + '|' + x.op;
    const cd = d.list[index], done = d.list.filter((x, i) => i < index)[0], todo = d.list.filter((x, i) => i > index)[0];
    ok(where + ': the current dot is the theme accent', cd.bg === m.accent, cd.bg + ' vs ' + m.accent);
    if (done) ok(where + ': a done dot differs from a to-come dot', key(done) !== key(todo || {}), key(done));
    if (done) ok(where + ': a done dot differs from the current dot in color or size', key(done) !== key(cd) || done.w !== cd.w);
    if (todo) ok(where + ': a to-come dot differs from the current dot', key(todo) !== key(cd));
    ok(where + ': the step is in words for a screen reader', /^Step \d of 5$/.test(d.label) && d.label === 'Step ' + (index + 1) + ' of 5', JSON.stringify(d.label));
    ok(where + ': the dots show no text', d.text === '', JSON.stringify(d.text));
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
        // Every theme, size and step is its own page, independent of the others, so they are
        // measured several at a time (chrome.js checks()), in the order a serial loop would give.
        // `c` inside the callback is that scene's own tab.
        const grid = [];
        for (const theme of themes()) {
            for (const device of DEVICES) {
                for (const scene of SCENES) grid.push({ theme, device, scene });
            }
        }
        const results = await c.checks(grid, async (c, { theme, device, scene }, ok) => {
            const where = theme + ' ' + scene + ' ' + device;
            const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=' + device, device);
            if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
            check(where, scene, await c.evaluate(MEASURE), ok);
        });
        results.forEach(a => ok(a[0], a[1], a[2]));
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
