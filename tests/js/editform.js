/*
 * The profile edit form on a phone, measured in a real browser, every theme (P2-942, P2-944,
 * P2-1010). Each is a question asked of one element:
 *
 *   - each "Only at these times" day row keeps its day, both times and the dash on one line
 *     (the end time dropped to a line of its own with no day beside it);
 *   - "Delete Profile" is a single line, as tall as Cancel beside it (it wrapped in four themes);
 *   - the artwork switch sits inside a titled card, like everything else on the form.
 *
 * Needs Chrome and the Material Icons font, like formfactors.js, and says it was skipped,
 * exiting 0, without them.
 *
 *     node tests/js/editform.js [/path/to/old/profiles.js]
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

const SIZES = {
    'phone-360': { width: 360, height: 740, mobile: true, scale: 2 },
    'phone-390': { width: 390, height: 844, mobile: true, scale: 2 }
};

// Runs in the page. Reports numbers, never asserts.
const MEASURE = `(function () {
  var out = { rows: [], del: null, cancel: null, art: null };
  var hours = document.getElementById('viewing-hours');
  if (hours && !hours.checked) { hours.checked = true; hours.dispatchEvent(new Event('change', { bubbles: true })); }
  [].slice.call(document.querySelectorAll('.jpf-viewing-day')).forEach(function (row, i) {
    var parts = [row.querySelector('.jpf-viewing-dayname'), row.querySelector('select[id^=viewing-from]'),
                 row.querySelector('.jpf-viewing-dash'), row.querySelector('select[id^=viewing-to]')];
    var tops = parts.map(function (p) { var r = p.getBoundingClientRect(); return { top: (r.top + r.bottom) / 2, bottom: r.bottom, w: r.width }; });
    // The two times and the dash share a line; the day sits on that line or above it.
    var t3 = tops.slice(1);
    var lo = Math.min.apply(null, t3.map(function (r) { return r.top; }));
    var hi = Math.max.apply(null, t3.map(function (r) { return r.top; }));
    if (tops[0].bottom > hi + 4) hi += 999;
    out.rows.push({ i: i, spread: Math.round(hi - lo), zero: tops.some(function (r) { return r.w < 1; }) });
  });
  var d = document.getElementById('jpf-edit-delete-btn'), c = document.getElementById('edit-cancel-btn');
  if (d && c) {
    var rg = document.createRange(); rg.selectNodeContents(d);
    var tops = {}; [].slice.call(rg.getClientRects()).forEach(function (r) { tops[Math.round(r.top / 4)] = 1; });
    out.del = { h: Math.round(d.getBoundingClientRect().height), lines: Object.keys(tops).length };
    out.cancel = { h: Math.round(c.getBoundingClientRect().height) };
  }
  var t = document.getElementById('edit-libart-toggle');
  if (t) {
    var card = t.closest('.jpf-profile-section');
    out.art = { card: !!card, title: card && card.querySelector('.jpf-profile-section-title') ? card.querySelector('.jpf-profile-section-title').textContent : '' };
  }
  return out;
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
    Object.keys(SIZES).forEach(k => { chrome.DEVICES[k] = SIZES[k]; });
    try {
        for (const theme of themes()) {
            for (const device of Object.keys(SIZES)) {
                const where = theme + ' ' + device;
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=edit-more&device=' + device, device);
                if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                // The limits load after the form does.
                let m;
                for (let n = 0; n < 20; n++) {
                    m = await c.evaluate(MEASURE);
                    if (m.rows.length) break;
                    await new Promise(res => setTimeout(res, 250));
                }
                ok(where + ': the seven day rows are there', m.rows.length === 7, m.rows.length + ' rows');
                const split = m.rows.filter(x => x.spread > 4 || x.zero).map(x => 'row ' + x.i + ' spans ' + x.spread + 'px');
                ok(where + ': every day row keeps its day above or beside both times, on one line', m.rows.length === 7 && split.length === 0, split.join('; '));
                ok(where + ': Delete Profile is there', !!m.del, 'no button');
                if (m.del) {
                    ok(where + ': Delete Profile is one line', m.del.lines === 1, m.del.lines + ' lines, ' + m.del.h + 'px tall');
                    ok(where + ': Delete Profile is as tall as Cancel', m.del.h === m.cancel.h, m.del.h + 'px against ' + m.cancel.h);
                }
                ok(where + ': the artwork switch is in a titled card', !!(m.art && m.art.card && m.art.title.trim()),
                    m.art ? (m.art.card ? 'card has no title' : 'in no card') : 'no switch');
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
