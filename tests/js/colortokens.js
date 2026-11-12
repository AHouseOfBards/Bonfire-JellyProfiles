/*
 * R-1270: a colour written as a literal in styles.css does not follow the theme. The Remove
 * picture icon was a fixed light red (2.8:1 on Jellyfin Light's white), the "allowed tags"
 * warning a fixed amber (about 2:1), and the red tints behind an error and a delete button were
 * the dark themes' coral on every theme.
 *
 * So every declaration in styles.css that writes a colour as a hex, rgb(), rgba() or hsl() is
 * listed one at a time, and must either be gone or be in ALLOWED below with the reason a theme
 * token would be wrong there. An entry that no longer matches fails too, so the list cannot keep
 * cover for something that has gone. Custom properties (--jpf-x: ...) are skipped: they are the
 * tokens. Drawn with the arrow and the tints as theme tokens, themes.js holds that they read.
 *
 * Also: no select arrow is a picture with a fixed fill (R-1269).
 *
 *     node tests/js/colortokens.js [path/to/styles.css]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name);
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const CSS = fs.readFileSync(process.argv[2] || path.join(L.ROOT, 'Web', 'styles.css'), 'utf8');

// "selector { property }" -> why no theme token is right there.
const ALLOWED = {
    '.jpf-theme-preview-tile:nth-child(1) { background }': 'a swatch of what a theme looks like, not a theme',
    '.jpf-theme-preview-tile:nth-child(2) { background }': 'a swatch of what a theme looks like, not a theme',
    '.jpf-theme-preview-tile:nth-child(3) { background }': 'a swatch of what a theme looks like, not a theme',
    '#profiles-logo.jpf-logo-back-dark { background }': 'set only behind a light logo the theme would hide (logobacking.js)',
    '#profiles-logo.jpf-logo-back-light { background }': 'set only behind a dark logo the theme would hide (logobacking.js)',
    '.profiles-maintenance { background }': 'the fallback if the theme has not set its background yet',
    '.jpf-profile-card.is-switching .jpf-profile-avatar-container::after { border-top-color }':
        'the spinner over a picture darkened to 40%, whatever the theme',
    '.profiles-home-icon { text-shadow }': "the flame's own orange glow: a brand colour, kept off the warning token on purpose",
    '.profiles-home-icon.bonfire-icon-color { text-shadow }': "the flame's own orange glow: a brand colour",
    '.jpf-profile-avatar.is-transparent img { filter }': 'a black shadow under a cut-out picture, which is dark on any theme',
    '.jpf-profile-card.is-current .jpf-profile-avatar.is-transparent img { filter }': 'the same black shadow, current tile',
    '.jpf-profile-avatar-overlay-svg { filter }': 'a black shadow behind the pencil over a darkened picture',
    '.jpf-profile-lock-indicator { background }': "a dark disc over a profile's own picture, whatever the theme",
    '.jpf-btn-danger.is-busy::after { border-top-color }': 'white on the red fill of a delete button',
    '.jpf-btn-danger { background }': 'a fixed red under white text; --jpf-danger is a light coral on dark themes and white on it is under 3:1',
    '.jpf-btn-danger { color }': 'white on the red fill of a delete button',
    '.jpf-btn-danger:hover,\n.jpf-btn-danger:focus { background }': 'the hover of that same fixed red',
    '.jpf-btn-primary:hover,\n.jpf-btn-primary:focus { border-color }': 'a light edge on the accent fill',
    '.jpf-image-upload-preview { color }': "a profile's initial on that profile's own colour",
    '#profiles-floating-bubble.profiles-floating-fallback { color }': "the fallback pill sits on jellyfin-web's page, not on Bonfire's"
};

const bare = CSS.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' ')).replace(/\r\n/g, '\n');
const LITERAL = /#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(/;

const hits = [];
{
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(bare)) !== null) {
        const selector = m[1].trim().replace(/^@media[^{]*$/, '').replace(/^.*\}\s*/, '');
        m[2].split(';').forEach(d => {
            const i = d.indexOf(':');
            if (i === -1) return;
            const prop = d.slice(0, i).trim();
            const value = d.slice(i + 1);
            if (/^--/.test(prop) || !LITERAL.test(value)) return;
            hits.push(selector + ' { ' + prop + ' }');
        });
    }
}

console.log('\n── styles.css names no colour a theme could own ────────────────');
ok('the scan finds declarations to judge (' + hits.length + ')', hits.length > 0);
hits.forEach(h => {
    const why = ALLOWED[h];
    ok(h.replace(/\n/g, ' ') + (why ? ' — ' + why : ''), !!why,
       'use a theme token (--jpf-danger, --jpf-danger-a15, --jpf-text-1 ...) or add it to ALLOWED with the reason');
});
Object.keys(ALLOWED).forEach(k =>
    ok('allowed declaration still exists: ' + k.replace(/\n/g, ' '), hits.indexOf(k) !== -1));

console.log('\n── No select arrow is a picture with a fixed fill ──────────────');
const pictures = (bare.match(/url\(\s*["']?data:image\/svg[^)]*\)/g) || []);
ok('no stylesheet rule carries an inline picture (' + pictures.length + ')', pictures.length === 0,
   'the arrow is --jpf-chevron, in the theme\'s text colour; a fixed fill vanishes on a light theme');
ok('the select and the device dropdown take the arrow from the theme',
   (bare.match(/var\(--jpf-input-bg\)\s+var\(--jpf-chevron\)/g) || []).length === 2);

console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
