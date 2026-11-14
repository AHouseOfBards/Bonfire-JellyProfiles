/*
 * First-time setup, the theme step (ob-theme): a live preview, and a button row that stays
 * on screen.
 *
 * Decided:
 *   - Choosing a card repaints the setup screen in that theme straight away, through the
 *     fade Bonfire already uses when a theme changes (fadeThemeFrom: a .jpf-theme-veil
 *     holding the old look whose opacity runs 1 to 0). Never a cut. Skip puts back the
 *     theme the account had. Reduced motion gets no veil, as everywhere else.
 *   - The theme grid runs past the bottom of the screen, so Next and Skip pin to the bottom
 *     of it the way the picker's row does (.jpf-footer-stuck), only while the step overflows.
 *
 * Asked of every theme on a phone, a desktop and a television:
 *   Preview:  the click moves data-jpf-theme at once, with the veil at full strength in the
 *             same task; frames after it show the veil between 0 and 1 (a transition, not a
 *             cut) and then gone; the end state is exactly what a page that opened in the
 *             new theme looks like (accent, panel, gate background, title colour and font);
 *             focus is still on the card; Skip puts the first theme back
 *             (setup ends and takes the gate with it, so no veil outlives the click; the
 *             next screen is drawn in the first theme).
 *   Reduced:  no veil in any frame, the new theme on the first frame.
 *   Pinned:   when the step overflows, the row's bottom is within 2px of the viewport's at
 *             the top, middle and end of the scroll, no card shows beneath it or is drawn
 *             over it, every card brought into view the way the remote does is clear of it,
 *             Down from the first card walks to the row without stopping under it, and Up
 *             returns to a card in the last row. When the window is tall enough that it
 *             does not overflow, the row is in the normal flow below the last card and Down
 *             still reaches it; going back to the short window pins it again.
 * Each device must have an overflowing and a fitting case, so the checks cannot pass by
 * asking nothing.
 *
 * Needs Chrome; says it was skipped, exiting 0, without it. The stylesheet is always the
 * checkout's own, so pointing argv[2] at an older profiles.js removes the script half.
 *
 *     node tests/js/themestep.js [/path/to/old/profiles.js]
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

function themes() {
    const dir = path.join(ROOT, 'Web', 'themes');
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id).sort();
}

// What the screen looks like, as one string. Runs in the page.
const SIGNATURE = `(function () {
  var r = document.documentElement, o = document.getElementById('profiles-gate-overlay');
  var rs = getComputedStyle(r);
  var head = [r.getAttribute('data-jpf-theme'), rs.getPropertyValue('--jpf-accent'), rs.getPropertyValue('--jpf-panel')];
  // Skip ends setup, which takes the gate away: then only the page's own variables are left.
  var t = document.querySelector('.profiles-title');
  if (!o || !t) return head.join('|');
  var ov = getComputedStyle(o), ti = getComputedStyle(t);
  return head.concat([ov.backgroundColor, ov.backgroundImage, ti.color, ti.fontFamily]).join('|');
})()`;

// Runs in the page. Reports numbers and names, never asserts.
const MEASURE = `(function () {
  var P = window.__bonfire;
  var root = document.getElementById('profiles-gate-overlay');
  var ct = document.querySelector('.jpf-onboarding');
  var bar = document.querySelector('.jpf-onboarding-actions');
  var cards = [].slice.call(document.querySelectorAll('.jpf-theme-option'));
  if (!P || !root || !ct || !bar || !cards.length) return { missing: true };
  var vh = window.innerHeight;
  var rect = function (e) { var r = e.getBoundingClientRect(); return { top: r.top, bottom: r.bottom, left: r.left, right: r.right }; };
  var hits = function (a, b) { return a.bottom > b.top + 0.5 && a.top < b.bottom - 0.5 && a.right > b.left && a.left < b.right; };
  var alpha = function (c) { var m = /rgba?\\(([^)]*)\\)/.exec(c); if (!m) return 0; var p = m[1].split(','); return p.length > 3 ? parseFloat(p[3]) : 1; };
  root.scrollTop = 0;
  ct.scrollTop = 0;
  // Whether the step is taller than the screen: all of the sheet's content under its top,
  // and the gate's own bottom padding. The same sum before and after the change, whatever
  // does the scrolling.
  var padB = parseFloat(getComputedStyle(root).paddingBottom) || 0;
  var overflow = rect(ct).top + ct.scrollHeight + padB > vh + 1;
  var cs = getComputedStyle(bar);
  var out = {
    overflow: overflow, vh: vh, cards: cards.length,
    stuckClass: bar.classList.contains('jpf-footer-stuck'),
    position: cs.position, alpha: alpha(cs.backgroundColor),
    bar: rect(bar),
    lastCardBottom: Math.max.apply(null, cards.map(function (c) { return c.getBoundingClientRect().bottom; })),
    covered: [], offscreen: [], pinned: []
  };
  cards.forEach(function (t, i) {
    root.scrollTop = 0; ct.scrollTop = 0;
    P._focusVisibly(t);
    var r = rect(t), b = rect(bar);
    if (r.top < -0.5 || r.bottom > vh + 0.5) out.offscreen.push('#' + i);
    if (hits(r, b)) out.covered.push('#' + i);
  });
  var maxScroll = root.scrollHeight - root.clientHeight;
  [0, Math.round(maxScroll / 2), maxScroll].forEach(function (y) {
    root.scrollTop = y; ct.scrollTop = 0;
    var br = rect(bar), below = [], over = [];
    cards.forEach(function (t, i) {
      var r = rect(t);
      if (Math.min(r.bottom, vh) > br.bottom + 0.5 && r.top < vh && r.bottom > 0) below.push('#' + i);
      [].slice.call(t.querySelectorAll('*')).forEach(function (n) {
        var q = rect(n);
        var l = Math.max(q.left, br.left), rr = Math.min(q.right, br.right);
        var tp = Math.max(q.top, br.top), bt = Math.min(q.bottom, br.bottom, vh);
        if (rr - l < 1 || bt - tp < 1) return;
        var top = document.elementFromPoint((l + rr) / 2, (tp + bt) / 2);
        if (top && !bar.contains(top) && t.contains(top)) over.push('#' + i);
      });
    });
    out.pinned.push({ y: y, gap: vh - br.bottom, below: below, over: over });
  });
  root.scrollTop = 0; ct.scrollTop = 0;
  cards[0].focus();
  var steps = 0, reached = false, under = [];
  while (steps < cards.length + 6) {
    P._moveOverlayFocus(root, 'down');
    steps++;
    var a = document.activeElement;
    if (!a || !root.contains(a)) break;
    if (bar.contains(a)) { reached = true; break; }
    if (overflow && hits(rect(a), rect(bar))) under.push(steps);
  }
  out.reached = reached; out.steps = steps; out.under = under;
  if (reached) {
    P._moveOverlayFocus(root, 'up');
    var u = document.activeElement;
    var lastRowY = Math.max.apply(null, cards.map(function (t) { var r = t.getBoundingClientRect(); return r.top + r.height / 2; }));
    var ur = u && u.getBoundingClientRect();
    out.upIsCard = !!u && cards.indexOf(u) !== -1;
    out.upLastRow = out.upIsCard && Math.abs(ur.top + ur.height / 2 - lastRowY) < 12;
    out.upClear = out.upIsCard && !hits(rect(u), rect(bar));
  }
  return out;
})()`;

// Clicks the card for `to`, then samples every frame for 700ms.
function preview(to) {
    return `(function (to) {
  var root = document.documentElement;
  var card = document.querySelector('.jpf-theme-option[data-id="' + to + '"]');
  if (!card) return { missing: 'no card for ' + to };
  var frames = [], t0 = performance.now();
  var sample = function () {
    var v = document.querySelector('.jpf-theme-veil');
    frames.push({ attr: root.getAttribute('data-jpf-theme'), veil: !!v, op: v ? parseFloat(getComputedStyle(v).opacity) : null });
  };
  card.focus(); card.click(); sample();
  var sync = frames[0];
  return new Promise(function (resolve) {
    (function tick() {
      sample();
      if (performance.now() - t0 < 700) return requestAnimationFrame(tick);
      var others = [].slice.call(document.querySelectorAll('.jpf-theme-option')).filter(function (o) { return o !== card; });
      resolve({
        sync: sync, frames: frames,
        focused: document.activeElement === card,
        checked: card.getAttribute('aria-checked') === 'true' && others.every(function (o) { return o.getAttribute('aria-checked') === 'false'; })
      });
    })();
  });
})(${JSON.stringify(to)})`;
}

// Clicks Skip and reports the theme at once, with whether a veil was started, and later.
const SKIP = `(function () {
  var root = document.documentElement, s = document.getElementById('onboarding-skip');
  if (!s) return { missing: true };
  s.click();
  var now = { attr: root.getAttribute('data-jpf-theme'), veil: !!document.querySelector('.jpf-theme-veil') };
  return new Promise(function (resolve) { setTimeout(function () { resolve(now); }, 700); });
})()`;

// `ok` is the scene's own check() from c.checks(), which shadows the harness's.
function checkLayout(where, m, seen, device, ok) {
    if (m.missing) { ok(where + ': the step draws', false, 'no sheet, row, cards or plugin'); return; }
    seen[device + (m.overflow ? ' overflow' : ' fit')] = true;
    if (m.overflow) {
        const w = where + ' (overflows)';
        ok(w + ': the row is inside the viewport', m.bar.top >= -0.5 && m.bar.bottom <= m.vh + 0.5,
            'row ' + Math.round(m.bar.top) + '..' + Math.round(m.bar.bottom) + ' in ' + m.vh);
        ok(w + ': the pinned row reaches the bottom edge, with no card under it or over it',
            m.pinned.every(p => p.gap <= 2 && p.gap >= -2 && p.below.length === 0 && p.over.length === 0),
            m.pinned.map(p => 'scroll ' + p.y + ': ' + Math.round(p.gap) + 'px short, cards under it: ' + (p.below.join(',') || 'none') + ', over it: ' + (p.over.slice(0, 3).join(',') || 'none')).join('; '));
        ok(w + ': the row is sticky and its panel is opaque', m.position === 'sticky' && m.alpha === 1, m.position + ', alpha ' + m.alpha);
        ok(w + ': every card comes into view clear of the row', m.covered.length === 0 && m.offscreen.length === 0,
            'under the row: ' + m.covered.join(', ') + '; off screen: ' + m.offscreen.join(', '));
        ok(w + ': Down reaches the row, and no stop is under it', m.reached && m.under.length === 0,
            'reached ' + m.reached + ' after ' + m.steps + ' steps; under the row at step ' + m.under.join(', '));
        ok(w + ': Up from the row returns to a card in the last row, clear of it',
            !!m.upIsCard && m.upLastRow && m.upClear, 'card ' + m.upIsCard + ', last row ' + m.upLastRow + ', clear ' + m.upClear);
    } else {
        const w = where + ' (fits)';
        ok(w + ': the row is in the normal flow', !m.stuckClass && m.position !== 'sticky', m.position + (m.stuckClass ? ', stuck class set' : ''));
        ok(w + ': the row is below the last card', m.bar.top >= m.lastCardBottom - 0.5, 'row top ' + Math.round(m.bar.top) + ', last card bottom ' + Math.round(m.lastCardBottom));
        ok(w + ': Down still reaches the row', m.reached, 'after ' + m.steps + ' steps');
    }
}

const settle = ms => 'new Promise(function (r) { setTimeout(r, ' + ms + '); })';

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    if (!fs.existsSync(ICONS)) {
        console.log('  SKIPPED  the Material Icons font is not cached (run tests/screens/shoot.js once): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    const all = themes();
    const seen = {};
    const sig = {};
    const urlOf = (theme, device) => 'http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=ob-theme&device=' + device;
    try {
        // Pass one: what each theme looks like on this step, and where the row is.
        // Every theme and size is its own page, independent of the others (what pass two needs
        // from pass one is the sig table, complete before it starts), so each pass is measured
        // several at a time (chrome.js checks()), in the order a serial loop would give. `c`
        // inside a callback is that scene's own tab.
        const one = [];
        for (const device of DEVICES) {
            sig[device] = {};
            for (const theme of all) one.push({ device, theme });
        }
        const passOne = await c.checks(one, async (c, { device, theme }, ok) => {
                const d = chrome.DEVICES[device];
                const where = theme + ' ' + device;
                const r = await c.open(urlOf(theme, device), device);
                if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
                sig[device][theme] = await c.evaluate(SIGNATURE);
                checkLayout(where, await c.evaluate(MEASURE), seen, device, ok);
                // A window tall enough for all of it: the row goes back in the flow. Then
                // the short window again: it pins again.
                await c.page('Emulation.setDeviceMetricsOverride', { width: d.width, height: 3400, deviceScaleFactor: d.scale, mobile: d.mobile });
                await c.evaluate(settle(400));
                checkLayout(where + ', tall window', await c.evaluate(MEASURE), seen, device, ok);
                await c.page('Emulation.setDeviceMetricsOverride', { width: d.width, height: d.height, deviceScaleFactor: d.scale, mobile: d.mobile });
                await c.evaluate(settle(400));
                checkLayout(where + ', back to the short window', await c.evaluate(MEASURE), seen, device, ok);
        });
        passOne.forEach(a => ok(a[0], a[1], a[2]));
        // Pass two: choosing a card, and Skip.
        const two = [];
        for (const device of DEVICES) {
            for (let i = 0; i < all.length; i++) two.push({ device, from: all[i], to: all[(i + 1) % all.length], reduced: false });
            // Less motion: the same click, no veil in any frame.
            two.push({ device, from: all[0], to: all[1], reduced: true });
        }
        const passTwo = await c.checks(two, async (c, { device, from, to, reduced }, ok) => {
            if (reduced) {
                const where = from + ' to ' + to + ' ' + device + ', reduced motion';
                const r = await c.open(urlOf(from, device), device, { reducedMotion: true });
                if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
                const p = await c.evaluate(preview(to));
                ok(where + ': the new theme on the click itself', !p.missing && p.sync.attr === to);
                ok(where + ': no veil in any frame', !p.missing && p.frames.every(f => !f.veil));
                ok(where + ': the end state is the new theme', !p.missing && await c.evaluate(SIGNATURE) === sig[device][to]);
                return;
            }
            {
                const where = from + ' to ' + to + ' ' + device;
                const r = await c.open(urlOf(from, device), device);
                if (r.error) { ok(where + ': the screen draws', false, r.error); return; }
                const p = await c.evaluate(preview(to));
                if (p.missing) { ok(where + ': the card is there', false, p.missing); return; }
                ok(where + ': the screen is in the new theme on the click itself', p.sync.attr === to, 'data-jpf-theme is ' + p.sync.attr);
                ok(where + ': it fades from the old look (the veil is up, at full strength)', p.sync.veil && p.sync.op !== null && p.sync.op > 0.9,
                    'veil ' + p.sync.veil + ', opacity ' + p.sync.op);
                const mid = p.frames.filter(f => f.veil && f.op !== null && f.op > 0.02 && f.op < 0.98);
                ok(where + ': a frame between the two proves it is a fade, not a cut', mid.length > 0,
                    'veil opacities: ' + p.frames.filter(f => f.veil).map(f => f.op).join(', '));
                const ops = p.frames.filter(f => f.veil).map(f => f.op);
                ok(where + ': the veil only gets fainter, and is gone at the end',
                    ops.every((o, k) => k === 0 || o <= ops[k - 1] + 0.001) && !p.frames[p.frames.length - 1].veil,
                    ops.join(', '));
                ok(where + ': no frame is in another theme', p.frames.every(f => f.attr === to), p.frames.map(f => f.attr).filter((a, k, l) => l.indexOf(a) === k).join(', '));
                ok(where + ': the end state is the new theme as drawn on a page that opened in it', await c.evaluate(SIGNATURE) === sig[device][to],
                    await c.evaluate(SIGNATURE) + '  vs  ' + sig[device][to]);
                ok(where + ': focus stays on the chosen card, which is the checked one', p.focused && p.checked, 'focused ' + p.focused + ', checked ' + p.checked);
                const s = await c.evaluate(SKIP);
                ok(where + ': Skip puts the first theme back at once', !s.missing && s.attr === from, JSON.stringify(s));
                const head = x => x.split('|').slice(0, 3).join('|');
                ok(where + ': and it is the first theme as a page that opened in it has it', head(await c.evaluate(SIGNATURE)) === head(sig[device][from]));
            }
        });
        passTwo.forEach(a => ok(a[0], a[1], a[2]));
        DEVICES.forEach(d => {
            ok(d + ': at least one case overflows, so the pinned checks asked something', !!seen[d + ' overflow']);
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
