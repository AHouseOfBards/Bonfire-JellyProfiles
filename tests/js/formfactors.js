/*
 * Form factors, measured in a real browser: the shapes the review's phone and television
 * screenshots found wrong (P2-508 to P2-515, and the 8.6px text). Other harnesses here read
 * the stylesheet; these ask what the cascade and the font size settle on, which only laying
 * the screen out and measuring can answer.
 *
 * Reuses tests/screens (the fixture and the Chrome driver), so like shortscreens.js it
 * needs Chrome and the Material Icons font, and says it was skipped, exiting 0, without them.
 *
 *     node tests/js/formfactors.js [/path/to/old/profiles.js]
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

// "tv…" is a television (layout-tv, so jellyfin-web's 20px / 27px root); anything else a phone.
const SIZES = {
    'tv-1280x720': { width: 1280, height: 720, mobile: false, scale: 1 },
    'tv-1920x1080': { width: 1920, height: 1080, mobile: false, scale: 1 },
    'phone-360x640': { width: 360, height: 640, mobile: true, scale: 1 }
};

// Runs in the page. Reports numbers, never asserts, so each check names its own member.
const MEASURE = `(function () {
  function box(e) { if (!e) return null; var b = e.getBoundingClientRect(); return { l: b.left, t: b.top, w: b.width, h: b.height, r: b.right, b: b.bottom }; }
  var root = document.getElementById('profiles-gate-overlay');
  var out = { vw: innerWidth, vh: innerHeight };
  function q(s) { return root ? root.querySelector(s) : null; }
  function qa(s) { return root ? [].slice.call(root.querySelectorAll(s)) : []; }
  out.form = box(q('.jpf-create-profile-container'));
  out.bar = box(q('.jpf-create-profile-container > .jpf-profile-dialog-actions'));
  out.pick = qa('.jpf-image-upload-btn').filter(function (b) { return b.offsetWidth; }).map(function (b) {
    return { h: b.getBoundingClientRect().height, clipped: b.scrollHeight > b.clientHeight + 1 || b.scrollWidth > b.clientWidth + 1,
      lines: Math.round(b.getBoundingClientRect().height / parseFloat(getComputedStyle(b).fontSize)) };
  });
  // Everything a thumb has to hit, smaller than 44px in either direction (P2-510/511). A
  // checkbox is judged by the label round it, which is what takes the tap; a label that only
  // names a field is not a control.
  out.small = [];
  qa('button, input, select, textarea, summary, a[href], [role=button], .jpf-color-dot, .jpf-devices-dropdown-trigger').forEach(function (e) {
    if (e.type === 'hidden' || e.type === 'file') return;
    var cs = getComputedStyle(e);
    if (cs.display === 'none' || cs.visibility === 'hidden') return;
    var t = e, l = e.closest ? e.closest('label') : null;
    if ((e.type === 'checkbox' || e.type === 'radio') && l && l.offsetWidth) t = l;
    var b = t.getBoundingClientRect();
    if (!b.width || !b.height) return;
    if (b.height < 43.5 || b.width < 43.5) {
      out.small.push(Math.round(b.width) + 'x' + Math.round(b.height) + ' ' + e.tagName.toLowerCase() + (e.id ? '#' + e.id : typeof e.className === 'string' && e.className ? '.' + e.className.split(' ')[0] : ''));
    }
  });
  // Where the opened More section's heading ended up, against the form's visible window
  // (its top, and the top of the sticky Save bar): P2-513, a shot of it must show it.
  var more = q('details.jpf-more');
  if (more) {
    var sum = more.querySelector('summary'), f = q('.jpf-create-profile-container'), bar = q('.jpf-create-profile-container > .jpf-profile-dialog-actions');
    var sb = sum.getBoundingClientRect(), fb = f.getBoundingClientRect();
    out.more = { open: more.open, top: sb.top, bottom: sb.bottom, winTop: fb.top, winBottom: bar ? bar.getBoundingClientRect().top : fb.bottom };
  }
  // The lock badge against the first letter of its own tile (P2-514): the badge was 32px on a
  // tile the gate had fitted down to 88px, and covered the S.
  out.locks = qa('.jpf-profile-lock-indicator').map(function (lk) {
    var card = lk.closest('.jpf-profile-card'), av = card && card.querySelector('.jpf-profile-avatar');
    var lb = lk.getBoundingClientRect(), tb = lk.parentElement.getBoundingClientRect();
    var glyph = null;
    if (av) {
      for (var n = av.firstChild; n && !glyph; n = n.nextSibling) {
        if (n.nodeType === 3 && n.textContent.trim()) { var rg = document.createRange(); rg.selectNodeContents(n); var gb = rg.getBoundingClientRect(); glyph = { l: gb.left, r: gb.right, t: gb.top, b: gb.bottom }; }
      }
    }
    var hit = glyph && lb.left < glyph.r && lb.right > glyph.l && lb.top < glyph.b && lb.bottom > glyph.t;
    return { badge: lb.width, tile: tb.width, hit: !!hit };
  });
  // The smallest visible text. The theme picker's miniature of the switcher is a picture of
  // a screen, not text anyone reads at that size, so it is left out.
  out.tiny = [];
  [].slice.call(document.querySelectorAll('#profiles-gate-overlay *, .jpf-confirm-dialog-content *, .jpf-alert-dialog-content *')).forEach(function (e) {
    var t = '';
    for (var n = e.firstChild; n; n = n.nextSibling) if (n.nodeType === 3) t += n.textContent;
    t = t.trim();
    if (!t || !e.offsetWidth || e.closest('.jpf-theme-preview-button') || e.classList.contains('material-icons')) return;
    var fs = parseFloat(getComputedStyle(e).fontSize);
    if (fs < 11.95) out.tiny.push(Math.round(fs * 10) / 10 + 'px ' + (typeof e.className === 'string' && e.className ? '.' + e.className.split(' ')[0] : e.tagName.toLowerCase()) + ' "' + t.slice(0, 14) + '"');
  });
  var dlg = document.querySelector('.jpf-confirm-dialog-content, .jpf-alert-dialog-content');
  out.dialog = box(dlg);
  return out;
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
    Object.keys(SIZES).forEach(k => { chrome.DEVICES[k] = SIZES[k]; });
    async function look(scene, size) {
        const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=' + scene + '&device=' + size, size);
        if (r.error) throw new Error(scene + ' at ' + size + ': ' + r.error);
        return c.evaluate(MEASURE);
    }
    try {
        for (const size of ['tv-1280x720', 'tv-1920x1080']) {
            console.log('\n── ' + size + ' ──');
            const e = await look('edit', size);
            // P2-508: the picture button was two lines, clipped, on a 720p screen.
            ok(size + ': the picture button is one line and whole', e.pick.length > 0 && e.pick.every(p => !p.clipped && p.h < e.vh * 0.1),
                JSON.stringify(e.pick));
            // P2-509: the Save bar was 16% of a 720p screen.
            ok(size + ': the Save bar is no more than 12% of the screen (' + Math.round(e.bar.h) + 'px of ' + e.vh + ')', e.bar.h <= e.vh * 0.12);
            // P2-508/512: the forms were 960px and 560px whatever the screen.
            ok(size + ': the two-column form takes 70% of the width (' + Math.round(e.form.w) + 'px of ' + e.vw + ')', e.form.w >= e.vw * 0.7);
            const s = await look('settings', size);
            ok(size + ': Settings takes half the width (' + Math.round(s.form.w) + 'px of ' + s.vw + ')', s.form.w >= s.vw * 0.5);
        }
        // P2-510/511: on a phone the dialogs ran edge to edge and almost nothing was a
        // 44px target (buttons and fields 41px, checkboxes 18px, color dots 36px).
        const ph = 'phone-360x640';
        console.log('\n── ' + ph + ' ──');
        // P2-503: the pinned Save bar. With bottom: 0 it stopped at the form's padding edge and
        // left a strip of form showing under it. The side edges are held too: the bar bleeds
        // through the form's padding on purpose, and without that it stops short of both sides.
        // Each edge is named on its own, at each size; 2px allows the form's border.
        for (const size of [ph, 'tv-1280x720', 'tv-1920x1080']) {
            const e = await look('edit', size);
            const got = e.bar && e.form ? 'bar ' + Math.round(e.bar.l) + '..' + Math.round(e.bar.r) + ' bottom ' + Math.round(e.bar.b)
                + ', form ' + Math.round(e.form.l) + '..' + Math.round(e.form.r) + ' bottom ' + Math.round(e.form.b) : 'not found';
            ok(size + ': the Save bar starts at the form\'s left edge', !!e.bar && Math.abs(e.bar.l - e.form.l) <= 2, got);
            ok(size + ': the Save bar ends at the form\'s right edge', !!e.bar && Math.abs(e.bar.r - e.form.r) <= 2, got);
            ok(size + ': nothing of the form shows under the Save bar', !!e.bar && Math.abs(e.bar.b - e.form.b) <= 2, got);
        }
        const d = await look('dialog', ph);
        ok(ph + ': the confirm dialog keeps a gutter (' + Math.round(d.dialog.l) + '..' + Math.round(d.dialog.r) + ' of ' + d.vw + ')',
            d.dialog.l >= 12 && d.dialog.r <= d.vw - 12);
        for (const [scene, size] of [['manage', ph], ['picker', ph], ['manage', 'tv-1280x720'], ['picker', 'tv-1280x720'], ['manage', 'tv-1920x1080']]) {
            const m = await look(scene, size);
            ok(size + ' ' + scene + ': there is a lock badge to measure', m.locks.length > 0);
            ok(size + ' ' + scene + ': no lock badge covers its tile\'s letter, and none is over 35% of the tile',
                m.locks.every(k => !k.hit && k.badge <= k.tile * 0.35), JSON.stringify(m.locks));
        }
        // Text under 12px on a phone: hints and section subtitles at 10.8px and 11.2px, the
        // signed-in and PIN badges at 10.1px and 10.8px.
        for (const scene of ['add', 'edit-more', 'themes', 'timeup', 'manage', 'settings', 'sharing', 'account', 'dialog', 'pin']) {
            const m = await look(scene, ph);
            ok(ph + ' ' + scene + ': no text under 12px (' + m.tiny.length + ' are)', m.tiny.length === 0, m.tiny.slice(0, 5).join(', '));
        }
        // P2-513: the edit-more shot was the edit shot, because the section opened below the
        // fold of a form that was left scrolled to the top.
        for (const scene of ['edit-more', 'add-more']) {
            const m = await look(scene, ph);
            ok(ph + ' ' + scene + ': the opened section is in view', !!m.more && m.more.open && m.more.top >= m.more.winTop - 1 && m.more.bottom <= m.more.winBottom + 1,
                m.more ? JSON.stringify(m.more) : 'no More section');
        }
        for (const scene of ['add', 'edit-more', 'sharing', 'settings', 'manage', 'dialog']) {
            const m = await look(scene, ph);
            ok(ph + ' ' + scene + ': every control is at least 44px each way (' + m.small.length + ' are not)', m.small.length === 0,
                m.small.slice(0, 6).join(', '));
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
