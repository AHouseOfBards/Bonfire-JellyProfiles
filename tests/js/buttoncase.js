/*
 * One button case per theme, measured in a real browser. Five themes drew their secondary
 * buttons (BACK, CANCEL, CHANGE) in spaced capitals beside a sentence-case main button
 * (Save PIN, Unlock), so one pair of buttons used two styles. The rule: inside a theme,
 * every button has the same text-transform and the same letter-spacing.
 *
 * Every visible .profiles-btn on a set of screens is read for its computed case and its
 * letter-spacing in em, and each PAIR of distinct styles in a theme is asserted on its own,
 * so the failure names the two buttons and the theme.
 *
 * Needs Chrome and the Material Icons font, like formfactors.js; skips (exit 0) without them.
 *
 *     node tests/js/buttoncase.js [/path/to/old/profiles.js]
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

const THEMES = fs.readdirSync(path.join(ROOT, 'Web', 'themes'))
    .filter(f => /\.json$/.test(f) && f !== 'fonts.json').map(f => f.replace(/\.json$/, '')).sort();
const SCENES = ['pin', 'mainpin', 'pinreset', 'edit', 'settings', 'dialog', 'manage'];

const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay') || document;
  return [].slice.call(root.querySelectorAll('.profiles-btn')).filter(function (b) { return b.offsetWidth; }).map(function (b) {
    var cs = getComputedStyle(b), px = parseFloat(cs.letterSpacing);
    return { label: (b.textContent || '').trim().slice(0, 24), cls: b.className.replace('profiles-btn', '').trim(),
      transform: cs.textTransform, spacing: isNaN(px) ? 0 : Math.round(px / parseFloat(cs.fontSize) * 100) / 100 };
  });
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
    try {
        for (const theme of THEMES) {
            const seen = {};
            for (const scene of SCENES) {
                const r = await c.open('http://127.0.0.1:' + server.port + '/?theme=' + theme + '&scene=' + scene + '&device=tablet', 'tablet');
                if (r.error) throw new Error(theme + ' ' + scene + ': ' + r.error);
                (await c.evaluate(MEASURE)).forEach(b => {
                    const k = b.transform + '/' + b.spacing;
                    (seen[k] = seen[k] || { transform: b.transform, spacing: b.spacing, who: [] }).who.push(scene + ':' + b.label);
                });
            }
            const styles = Object.keys(seen).map(k => seen[k]);
            ok(theme + ': buttons were found to measure', styles.length > 0);
            // Each pair on its own: the case, then the spacing.
            for (let i = 0; i < styles.length; i++) for (let j = i + 1; j < styles.length; j++) {
                const a = styles[i], b = styles[j];
                ok(theme + ': "' + a.who[0] + '" and "' + b.who[0] + '" share a case',
                    a.transform === b.transform, a.transform + ' vs ' + b.transform);
                ok(theme + ': "' + a.who[0] + '" and "' + b.who[0] + '" share a letter-spacing',
                    a.spacing === b.spacing, a.spacing + 'em vs ' + b.spacing + 'em');
            }
            ok(theme + ': one button style across ' + styles.length + ' measured', styles.length === 1,
                styles.map(s => s.transform + '/' + s.spacing + 'em e.g. ' + s.who[0]).join(' | '));
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
