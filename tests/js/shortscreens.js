/*
 * Short screens and notches: the gate, the PIN prompts and the profile form, measured in a
 * real browser at 360x640 (phone), 640x360 and 740x360 (phone on its side, the second with a
 * notch's safe-area insets), 1280x720 and 1920x1080 (television).
 *
 * Every other harness here reads the stylesheet or models the layout. This one has to
 * MEASURE, because the question is "is the Save button on the screen" and the answer lives
 * in the cascade, the font size jellyfin-web sets on <html>, and a flex column that is
 * sometimes taller than its parent. gatefit.js is the model of the gate; this is the check
 * that the model and the browser agree about the screens gatefit does not cover.
 *
 * It reuses tests/screens (the fixture and the Chrome driver) and so needs Chrome and the
 * Material Icons font, which jellyfin-web ships and this repository does not (without it an
 * icon is drawn as its own name and every button is the wrong width). With no Chrome, or no
 * way to get the font, it SAYS it was skipped and exits 0: a gate that cannot run here is
 * not one that can fail here. CI's runners have Chrome.
 *
 * What it asserts, per screen and size:
 *   - nothing scrolls sideways;
 *   - every control can be reached: it is on the screen, or inside something that scrolls to
 *     it, and nothing with overflow:hidden is cutting it off;
 *   - the PIN prompt is whole on the screen, with no scrolling needed to find Unlock;
 *   - the profile form's Cancel / Delete / Save row is on the screen on first paint, and the
 *     form keeps at least 120px to show its fields in, which is one field and its label;
 *   - with a notch (safe-area insets), no control sits under it.
 *
 *     node tests/js/shortscreens.js [/path/to/old/profiles.js]
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

// The sizes, as the fixture names devices: "tv…" is a television (layout-tv on <html>, so
// jellyfin-web's 20px / 27px root), anything else a phone (layout-mobile).
const SIZES = [
    { name: 'phone-360x640', w: 360, h: 640, mobile: true },
    { name: 'phone-640x360', w: 640, h: 360, mobile: true },
    { name: 'phone-740x360-notch', w: 740, h: 360, mobile: true, insets: { top: 0, bottom: 21, left: 47, right: 47 } },
    { name: 'tv-1280x720', w: 1280, h: 720, mobile: false },
    { name: 'tv-1920x1080', w: 1920, h: 1080, mobile: false }
];

// Runs in the page. Reports, never asserts, so one screen's problems are all listed.
const MEASURE = `(function (insets) {
  var vw = window.innerWidth, vh = window.innerHeight;
  var root = document.getElementById('profiles-gate-overlay');
  var out = { vw: vw, vh: vh, problems: [], under: [] };
  if (!root) { out.problems.push('no gate overlay on the page'); return out; }
  function name(e) {
    return (e.id ? '#' + e.id : e.tagName.toLowerCase() + (typeof e.className === 'string' && e.className ? '.' + e.className.split(' ')[0] : ''))
      + ' "' + String(e.textContent || e.value || e.placeholder || '').replace(/\\s+/g, ' ').trim().slice(0, 16) + '"';
  }
  var sel = 'button, input, select, textarea, a[href], .profile-card';
  var els = [].slice.call(root.querySelectorAll(sel)).filter(function (e) {
    var r = e.getBoundingClientRect(), cs = getComputedStyle(e);
    return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'
      && e.type !== 'hidden' && e.type !== 'file' && e.type !== 'checkbox' && e.type !== 'radio';
  });
  out.controls = els.length;
  els.forEach(function (e) {
    var r = e.getBoundingClientRect(), why = '', p = e;
    while (!why && (p = p.parentElement)) {
      var cs = getComputedStyle(p), pr = p.getBoundingClientRect();
      if (cs.overflowX === 'hidden' && (r.left < pr.left - 1 || r.right > pr.right + 1)) why = 'cut off at the side by ' + name(p);
      else if (cs.overflowY === 'hidden' && p !== document.body && p !== document.documentElement
        && (r.top < pr.top - 1 || r.bottom > pr.bottom + 1)) why = 'cut off by ' + name(p);
    }
    if (!why && (r.left < -1 || r.right > vw + 1)) why = 'off the side of the screen';
    if (!why && (r.top < -1 || r.bottom > vh + 1)) {
      var scrolls = false; p = e;
      while (!scrolls && (p = p.parentElement)) {
        var c2 = getComputedStyle(p);
        scrolls = (c2.overflowY === 'auto' || c2.overflowY === 'scroll') && p.scrollHeight > p.clientHeight + 1;
      }
      if (!scrolls) why = 'below the screen with nothing to scroll';
    }
    if (why) out.problems.push(name(e) + ': ' + why);
    if (insets && (r.left < insets.left - 1 || r.right > vw - insets.right + 1)) {
      out.under.push(name(e) + ' spans ' + Math.round(r.left) + '..' + Math.round(r.right));
    }
  });
  // Whether the emulated notch reached the page at all: without this the notch checks pass
  // against a browser that was never given one.
  var probe = document.createElement('div');
  probe.style.cssText = 'position:fixed;visibility:hidden;padding-left:env(safe-area-inset-left,0px);padding-right:env(safe-area-inset-right,0px)';
  document.body.appendChild(probe);
  out.envLeft = parseFloat(getComputedStyle(probe).paddingLeft) || 0;
  out.envRight = parseFloat(getComputedStyle(probe).paddingRight) || 0;
  document.body.removeChild(probe);
  // The content's own edges, not just the controls inside it: a card whose border sits under
  // the notch is cut off even if its buttons are not.
  if (insets) {
    [].slice.call(root.querySelectorAll('.profiles-modal-content, .create-profile-container, .pin-entry-container')).forEach(function (e) {
      var r = e.getBoundingClientRect();
      if (r.width && (r.left < insets.left - 1 || r.right > vw - insets.right + 1)) out.under.push(name(e) + ' spans ' + Math.round(r.left) + '..' + Math.round(r.right));
    });
  }
  out.overflowX = root.scrollWidth - root.clientWidth;
  out.overflowY = root.scrollHeight - root.clientHeight;
  var bar = root.querySelector('.create-profile-container > .profile-dialog-actions');
  if (bar) {
    var b = bar.getBoundingClientRect();
    var box = bar.parentElement.getBoundingClientRect();
    out.bar = { top: b.top, bottom: b.bottom };
    out.formTop = box.top;
    out.fieldRoom = b.top - box.top;
  }
  // Unlock / Back: the PIN prompt's own controls, all of them, whole.
  var pin = root.querySelector('#profile-pin-input, #master-pin-input, #main-pin-new');
  if (pin) {
    var lo = Infinity, hi = -Infinity;
    [].slice.call(pin.closest('.pin-entry-container, .profiles-modal-content').querySelectorAll('input, button')).forEach(function (e) {
      var r = e.getBoundingClientRect(); if (r.height) { lo = Math.min(lo, r.top); hi = Math.max(hi, r.bottom); }
    });
    out.pinSpan = { top: lo, bottom: hi };
  }
  return out;
})`;

async function main() {
    let launched;
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): the screens were not measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    if (!await fetchIcons()) {
        console.log('  SKIPPED  the Material Icons font could not be fetched: the screens were not measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    launched = true;
    try {
        for (const s of SIZES) {
            chrome.DEVICES[s.name] = { width: s.w, height: s.h, mobile: s.mobile, scale: 1 };
        }
        for (const s of SIZES) {
            console.log();
            console.log('── ' + s.name + ' ' + '─'.repeat(Math.max(2, 50 - s.name.length)));
            for (const [scene, label] of [['picker', 'the gate'], ['pin', 'the PIN prompt'], ['mainpin', 'the main-account PIN prompt'], ['edit', 'the profile form']]) {
                const url = 'http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=' + scene + '&device=' + s.name;
                if (s.insets) {
                    await c.page('Emulation.setSafeAreaInsetsOverride', { insets: s.insets });
                } else {
                    await c.page('Emulation.setSafeAreaInsetsOverride', { insets: { top: 0, bottom: 0, left: 0, right: 0 } });
                }
                const r = await c.open(url, s.name);
                if (r.error) { ok(label + ' drew', false, r.error); continue; }
                const m = await c.evaluate(MEASURE + '(' + JSON.stringify(s.insets || null) + ')');
                const tag = label + ' (' + m.controls + ' controls)';
                ok(tag + ': nothing scrolls sideways', m.overflowX <= 0, 'overlay is ' + m.overflowX + 'px wider than the screen');
                ok(tag + ': every control can be reached', m.problems.length === 0, m.problems.join('\n          '));
                if (scene === 'pin' || scene === 'mainpin') {
                    ok(tag + ': whole on the screen, nothing to scroll to', m.pinSpan && m.pinSpan.top >= 0 && m.pinSpan.bottom <= m.vh && m.overflowY <= 0,
                        m.pinSpan ? 'controls span ' + Math.round(m.pinSpan.top) + '..' + Math.round(m.pinSpan.bottom) + ' of ' + m.vh + ', scroll ' + m.overflowY : 'no PIN field');
                }
                if (scene === 'edit') {
                    ok(tag + ': the Cancel / Delete / Save row is on the screen', !!m.bar && m.bar.top >= 0 && m.bar.bottom <= m.vh + 1,
                        m.bar ? 'row is at ' + Math.round(m.bar.top) + '..' + Math.round(m.bar.bottom) + ' of ' + m.vh : 'no row');
                    ok(tag + ': and leaves 120px for the fields', !!m.bar && m.fieldRoom >= 120,
                        m.bar ? 'form top ' + Math.round(m.formTop) + ' to row ' + Math.round(m.bar.top) + ' = ' + Math.round(m.fieldRoom) + 'px' : 'no row');
                }
                if (s.insets) {
                    ok(tag + ': the browser really was given the notch (' + m.envLeft + ' / ' + m.envRight + ')',
                        m.envLeft === s.insets.left && m.envRight === s.insets.right);
                    ok(tag + ': nothing under the notch', m.under.length === 0, m.under.join('\n          '));
                }
            }
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log();
    console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e.stack || e); process.exit(1); });
