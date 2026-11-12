/*
 * A household at the limit: the main account and 20 profiles, the most one account can hold, in every theme on a
 * phone, a tablet and a television at 720p and 1080p, on the picker and on Manage.
 *
 * With that many profiles the gate shrinks the tiles to fit a television screen (see
 * fitGateToScreen), down to 120px, but the names stayed at television size, so a long name
 * ran across the tile beside it: "JordaMaximilianPriya", "WeBartholomewIsla". The tests and
 * the screenshots only ever had four profiles, so nobody had seen it.
 *
 * Asked of each name on its own, never as a count: does it stay inside its own tile, and
 * does its text fit its own box. A name may wrap; it may not spill. On a television whose
 * tiles were shrunk, a name is one line, cut with an ellipsis rather than wrapped or broken,
 * and never smaller than the status lines under it (a first fix shrank names to 20px,
 * under a 30px "Time's up").
 *
 * Needs Chrome and the Material Icons font, like formfactors.js, and says it was skipped,
 * exiting 0, without them.
 *
 *     node tests/js/crowdedgrid.js [/path/to/old/profiles.js]
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

// The main account and 20 profiles: the most one account can show.
const COUNT = 21;
const SIZES = {
    'tv-1280x720': { width: 1280, height: 720, mobile: false, scale: 1 },
    'tv-1920x1080': { width: 1920, height: 1080, mobile: false, scale: 1 }
};
const DEVICES = ['phone', 'tablet', 'tv-1280x720', 'tv-1920x1080'];
const SCENES = ['picker', 'manage'];

// Runs in the page. Reports numbers, never asserts.
const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  if (!root) return { cards: 0, bad: [] };
  var cards = [].slice.call(root.querySelectorAll('.profiles-grid .jpf-profile-card'));
  var bad = [], wrap = [], small = [];
  var tv = document.documentElement.classList.contains('jpf-tv');
  var fitted = !!root.querySelector('.profiles-modal-content.jpf-fitted');
  cards.forEach(function (card) {
    var name = card.querySelector('.jpf-profile-name');
    if (!name) { bad.push('a tile with no name'); return; }
    var c = card.getBoundingClientRect(), n = name.getBoundingClientRect();
    var text = (name.textContent || '').trim();
    // On a fitted television: the name is one line (cut with an ellipsis, never wrapped
    // or broken), and never smaller than the lines under it.
    var first = name.firstElementChild;
    if (tv && fitted && first) {
      var fs = parseFloat(getComputedStyle(first).fontSize);
      var lh = parseFloat(getComputedStyle(first).lineHeight) || fs * 1.2;
      if (first.getBoundingClientRect().height > lh * 1.5) wrap.push(text);
      [].slice.call(name.querySelectorAll('.jpf-profile-viewing, .jpf-profile-current-badge')).forEach(function (s) {
        var sf = parseFloat(getComputedStyle(s).fontSize);
        if (sf > fs + 0.5) small.push(text + ' ' + Math.round(fs) + 'px under a ' + Math.round(sf) + 'px line');
      });
    }
    if (n.left < c.left - 1 || n.right > c.right + 1) {
      bad.push(text + ' is ' + Math.round(n.width) + 'px wide in a ' + Math.round(c.width) + 'px tile');
    } else if (name.scrollWidth > name.clientWidth + 1) {
      bad.push(text + ' needs ' + name.scrollWidth + 'px in a ' + name.clientWidth + 'px box');
    }
  });
  // The Add tile is checked for its name like the rest, but it is not a profile.
  var people = cards.filter(function (c) { return !c.classList.contains('action-add-profile'); });
  return { cards: people.length, bad: bad, wrap: wrap, small: small, tv: tv, fitted: fitted };
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
            for (const scene of SCENES) {
                for (const device of DEVICES) {
                    const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene
                        + '&device=' + device + '&count=' + COUNT, device);
                    const where = theme + ' ' + scene + ' ' + device;
                    if (r.error) { ok(where + ': the screen draws', false, r.error); continue; }
                    const m = await c.evaluate(MEASURE);
                    ok(where + ': all ' + COUNT + ' profiles are shown', m.cards === COUNT, m.cards + ' tiles');
                    ok(where + ': every name stays inside its own tile', m.bad.length === 0, m.bad.join('; '));
                    if (/^tv/.test(device)) {
                        // 21 tiles never fit a television without shrinking; if they stop
                        // being fitted, the two checks below would pass by asking nothing.
                        ok(where + ': the tiles were shrunk to fit', m.fitted, 'not fitted');
                        ok(where + ': every name is one line', m.wrap.length === 0, m.wrap.join('; '));
                        ok(where + ': no name is smaller than the lines under it', m.small.length === 0, m.small.join('; '));
                    }
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
