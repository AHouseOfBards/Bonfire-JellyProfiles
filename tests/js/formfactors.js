/*
 * Form factors, measured in a real browser: the shapes the review's phone and television
 * screenshots found wrong (P2-508 to P2-515, and the 8.6px text). Every other harness here
 * reads the stylesheet; these are questions about what the cascade and the font size settle
 * on, and the only honest way to ask them is to lay the screen out and measure it.
 *
 * It reuses tests/screens (the fixture and the Chrome driver), so like shortscreens.js it
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
  out.form = box(q('.create-profile-container'));
  out.bar = box(q('.create-profile-container > .profile-dialog-actions'));
  out.pick = qa('.image-upload-btn').filter(function (b) { return b.offsetWidth; }).map(function (b) {
    return { h: b.getBoundingClientRect().height, clipped: b.scrollHeight > b.clientHeight + 1 || b.scrollWidth > b.clientWidth + 1,
      lines: Math.round(b.getBoundingClientRect().height / parseFloat(getComputedStyle(b).fontSize)) };
  });
  // Everything a thumb has to hit, smaller than 44px in either direction (P2-510/511). A
  // checkbox is judged by the label round it, which is what takes the tap; a label that only
  // names a field is not a control.
  out.small = [];
  qa('button, input, select, textarea, summary, a[href], [role=button], .color-dot, .devices-dropdown-trigger').forEach(function (e) {
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
  var dlg = document.querySelector('.confirm-dialog-content, .alert-dialog-content');
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
        // 44px target (buttons and fields 41px, checkboxes 18px, colour dots 36px).
        const ph = 'phone-360x640';
        console.log('\n── ' + ph + ' ──');
        const d = await look('dialog', ph);
        ok(ph + ': the confirm dialog keeps a gutter (' + Math.round(d.dialog.l) + '..' + Math.round(d.dialog.r) + ' of ' + d.vw + ')',
            d.dialog.l >= 12 && d.dialog.r <= d.vw - 12);
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
