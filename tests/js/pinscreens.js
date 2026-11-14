/*
 * The maintenance screen, the Emergency disable dialog and the PIN reset screen on a phone
 * and a tablet, each element asked on its own:
 *   - "Check again" and "Sign out" do not touch;
 *   - the Emergency disable dialog keeps a side margin and its code field is dressed like
 *     the other fields;
 *   - the PIN reset screen keeps the gutter the other screens do, and "New PIN" is as wide
 *     as "Code".
 *
 * Needs Chrome and the Material Icons font, like formfactors.js; skips without them.
 *
 *     node tests/js/pinscreens.js [/path/to/old/profiles.js]
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

const MEASURE = `(function () {
  function r(el) { if (!el) return null; var b = el.getBoundingClientRect(); return { l: b.left, r: b.right, t: b.top, b: b.bottom, w: b.width, h: b.height }; }
  var out = { vw: document.documentElement.clientWidth };
  var m = document.getElementById('profiles-maintenance');
  if (m) { out.btns = [].slice.call(m.querySelectorAll('button')).map(r); }
  var p = document.getElementById('profiles-panic-dialog');
  if (p) {
    var inp = p.querySelector('input');
    out.panicBox = r(p.firstElementChild);
    var cs = getComputedStyle(inp);
    out.panicInputBg = cs.backgroundColor;
    var ref = document.createElement('input'); ref.id = 'jpf-pin-reset-new'; document.body.appendChild(ref);
    out.refBg = getComputedStyle(ref).backgroundColor; ref.remove();
  }
  var code = document.getElementById('jpf-pin-reset-code'), nw = document.getElementById('jpf-pin-reset-new');
  if (code && nw) {
    out.code = r(code); out.nw = r(nw);
    out.content = r(document.querySelector('.profiles-modal-content'));
    var csn = getComputedStyle(nw);
    // Chrome puts an empty field's placeholder on the baseline of the field's own text size,
    // so a 1rem placeholder in a 2.5rem field sits low. Equal sizes sit centred.
    out.ph = [code, nw].map(function (el) {
      return { id: el.id, field: getComputedStyle(el).fontSize, placeholder: getComputedStyle(el, '::placeholder').fontSize };
    });
  }
  return out;
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped');
        return 0;
    }
    if (!await fetchIcons()) {
        console.log('  SKIPPED  the Material Icons font could not be fetched\n\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    try {
        for (const theme of ['bonfire', 'harbor']) {
            for (const device of ['phone', 'tablet']) {
                const at = (scene) => c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=' + device, device);
                const where = theme + ' ' + device;
                await at('maintenance');
                let m = await c.evaluate(MEASURE);
                if (m.btns && m.btns.length >= 2) {
                    const a = m.btns[0], b = m.btns[1];
                    const sameRow = Math.abs(a.t - b.t) < a.h / 2;
                    const gap = sameRow ? b.l - a.r : 99;
                    ok(where + ' maintenance: Check again and Sign out do not touch', gap >= 6, 'gap ' + Math.round(gap) + 'px');
                } else ok(where + ' maintenance: two buttons', false, JSON.stringify(m));

                await at('panic');
                m = await c.evaluate(MEASURE);
                if (m.panicBox) {
                    const side = Math.min(m.panicBox.l, m.vw - m.panicBox.r);
                    ok(where + ' panic: the dialog keeps a side margin', side >= 12, 'margin ' + Math.round(side) + 'px of ' + m.vw);
                    ok(where + ' panic: the code field is dressed like the other fields', m.panicInputBg === m.refBg, m.panicInputBg + ' vs ' + m.refBg);
                } else ok(where + ' panic: draws', false, JSON.stringify(m));

                await at('pinreset');
                m = await c.evaluate(MEASURE);
                if (m.code) {
                    ok(where + ' pinreset: New PIN is as wide as Code', Math.abs(m.nw.w - m.code.w) < 1, 'Code ' + Math.round(m.code.w) + ' New PIN ' + Math.round(m.nw.w));
                    m.ph.forEach(p => ok(where + ' pinreset: the ' + p.id + ' placeholder is centred (same size as the field)',
                        p.field === p.placeholder, 'field ' + p.field + ', placeholder ' + p.placeholder));
                } else ok(where + ' pinreset: draws', false, JSON.stringify(m));
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
