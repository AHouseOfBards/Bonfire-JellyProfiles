/*
 * The line under a name ("Signed in", "45 min left", "Time's up") on the picker, with a second
 * household joined through a Bonfire (?bonfire=yes), in every theme.
 *
 * Two households do not fit a television screen at full size, so the tiles shrink to the
 * 120px floor (fitGateToScreen) while the lines under the names stayed at 1.1rem, 29.7px at
 * 1080p. "Signed in" is 156px wide and the column is 140, so it broke into "SIGNED" / "IN",
 * "45 min" / "left" and "Time's" / "up". crowdedgrid.js looks at names only, and only with
 * one household, so nothing saw it.
 *
 * Asked of every line on its own, never as a count: on a television it is one line, and
 * it stays inside its own tile. On a desktop window it is not under 12px (it was 10.4 and
 * 11.2px, 0.75rem of a 13.9px root); phones have had that floor since 2.0.
 *
 * Needs Chrome, like crowdedgrid.js, and says it was skipped, exiting 0, without it.
 *
 *     node tests/js/statusline.js [/path/to/old/profiles.js]
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
    'tv-1280x720': { width: 1280, height: 720, mobile: false, scale: 1 },
    'tv-1920x1080': { width: 1920, height: 1080, mobile: false, scale: 1 }
};
const DEVICES = ['desktop', 'tv-1280x720', 'tv-1920x1080'];

// Runs in the page. Reports numbers, never asserts.
const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  if (!root) return { lines: 0, wrapped: [], outside: [], small: [] };
  var lines = [].slice.call(root.querySelectorAll('.profiles-grid .jpf-profile-viewing, .profiles-grid .jpf-profile-current-badge'));
  var wrapped = [], outside = [], small = [];
  lines.forEach(function (s) {
    var text = (s.textContent || '').trim();
    var cs = getComputedStyle(s);
    var fs = parseFloat(cs.fontSize);
    var lh = parseFloat(cs.lineHeight) || fs * 1.2;
    var r = s.getBoundingClientRect();
    var card = s.closest('.jpf-profile-card').getBoundingClientRect();
    if (r.height > lh * 1.5) wrapped.push(text + ' is ' + Math.round(r.height / lh) + ' lines (' + Math.round(r.width) + 'px wide, ' + Math.round(fs) + 'px)');
    if (r.left < card.left - 1 || r.right > card.right + 1 || s.scrollWidth > s.clientWidth + 1) {
      outside.push(text + ' needs ' + s.scrollWidth + 'px in ' + Math.round(card.width) + 'px');
    }
    if (fs < 11.95) small.push(text + ' is ' + fs + 'px');
  });
  return { lines: lines.length, wrapped: wrapped, outside: outside, small: small,
    tv: document.documentElement.classList.contains('jpf-tv'),
    fitted: !!root.querySelector('.profiles-modal-content.jpf-fitted') };
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
            console.log('\n── ' + theme + ' ──');
            for (const device of DEVICES) {
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=picker&device=' + device + '&bonfire=yes', device);
                const where = theme + ' two households ' + device;
                if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                const m = await c.evaluate(MEASURE);
                // Three lines ("Signed in", "45 min left", "Time's up"): if they stop being
                // drawn, the checks below would pass by asking nothing.
                ok(where + ': the status lines are drawn', m.lines >= 3, m.lines + ' lines');
                if (/^tv/.test(device)) {
                    ok(where + ': the tiles were shrunk to fit', m.fitted, 'not fitted');
                    ok(where + ': every status line is one line', m.wrapped.length === 0, m.wrapped.join('; '));
                    ok(where + ': every status line stays inside its tile', m.outside.length === 0, m.outside.join('; '));
                } else {
                    ok(where + ': no status line is under 12px', m.small.length === 0, m.small.join('; '));
                }
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
