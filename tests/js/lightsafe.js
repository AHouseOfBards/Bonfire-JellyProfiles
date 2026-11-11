/*
 * Nothing on Bonfire's screens is painted white except on a fill that is not the theme's.
 *
 * Until Jellyfin Light every theme was dark, so a title written as color:#fff, a border as
 * rgba(255,255,255,.1) or a dialog as background:#181818 looked right everywhere, and a
 * dozen of each built up in styles.css and in profiles.js's inline styles. On a light theme
 * each is white text on a white page, or a black box on one. Nothing errors; it is only
 * seen by somebody who picks that theme.
 *
 * So every declaration that paints with a literal white, or with the old #181818 dialog
 * card, is listed one at a time, and each must either be gone or be in ALLOWED below with
 * the reason the fill under it is not the theme's (a profile's own colour, a darkened
 * picture, the red of a delete button). An allowed entry that no longer exists fails too,
 * so the list cannot keep cover for something that has gone.
 *
 *   node tests/js/lightsafe.js [path/to/styles.css] [path/to/profiles.js]
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
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const CSS_PATH = process.argv[2] || path.join(L.ROOT, 'Web', 'styles.css');
const JS_PATH = process.argv[3] || path.join(L.ROOT, 'Web', 'profiles.js');
const CSS = fs.readFileSync(CSS_PATH, 'utf8');
const JS = fs.readFileSync(JS_PATH, 'utf8');

// A property that paints, set to a white (or the old dark card) anywhere in its value.
const PAINT = /(^|[\s;{"'`])((?:background(?:-color)?|color|border(?:-[a-z]+)*|outline(?:-color)?|scrollbar-color|fill|stroke|text-shadow|box-shadow|filter))\s*:\s*([^;"`{}]*?(?:#fff\b|#ffffff\b|rgba?\(\s*255\s*,\s*255\s*,\s*255\b|\bwhite\b|#181818\b)[^;"`{}]*)/gi;

// where -> why the fill under it is not the theme's. For styles.css "where" is the rule's
// selector, exactly; for profiles.js it is text that must be on the same line.
const ALLOWED = {
    css: {
        '.profile-card.is-switching .profile-avatar-container::after':
            'the spinner over a picture darkened to 40%, whatever the theme',
        '.btn-danger': 'white on the red fill of a delete button',
        '.btn-danger.is-busy::after': "the busy arc on that same red fill",
        '.btn-primary:hover,\n.btn-primary:focus': 'a light edge on the accent fill',
        '.image-upload-preview': "a profile's initial on that profile's own colour",
        '#profiles-floating-bubble.profiles-floating-fallback':
            "the fallback pill sits on jellyfin-web's page, not on Bonfire's",
        '#profiles-logo.jpf-logo-back-light':
            'set only behind a dark logo the theme would hide; it contrasts with the logo, not the theme (logobacking.js)'
    },
    js: {
        'class="profile-avatar-overlay-svg"': 'the edit pencil over a darkened picture',
        'stroke-width="3" stroke-linecap="round" stroke-linejoin="round" style="color: #fff;"':
            "the tick on a profile's own colour",
        'style="font-size: 1.15rem; color: #fff;">${p.isLinked': "the link badge on a profile's own colour",
        'class="bonfire-kick-btn"': 'white on the red fill of a remove button',
        // The initial's own colour is computed now (onAvatarColor); what is left white here
        // is the hairline ring round the header avatar.
        'border: 1.5px solid rgba(255,255,255,0.25)': "a hairline ring on a member's own colour"
    }
};

function stripComments(s) {
    return s.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));
}

// ── styles.css, one declaration at a time, with the selector it is under ──────────
console.log('\n── styles.css paints nothing white on a theme\'s own fill ──────');
const css = stripComments(CSS).replace(/\r\n/g, '\n');
const cssHits = [];
{
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(css)) !== null) {
        const selector = m[1].trim().replace(/^@media[^{]*$/, '');
        let d;
        PAINT.lastIndex = 0;
        while ((d = PAINT.exec(m[2])) !== null) cssHits.push({ selector, decl: d[2] + ': ' + d[3].trim() });
    }
}
ok('the scan finds declarations to judge (' + cssHits.length + ')', cssHits.length > 0);
cssHits.forEach(h => {
    const why = ALLOWED.css[h.selector];
    ok(h.selector.replace(/\n/g, ' ') + ' { ' + h.decl + ' }' + (why ? ' — ' + why : ''), !!why,
       'use a theme token (--jpf-text-1, --jpf-border-1, --jpf-panel...) or add it to ALLOWED with the reason');
});
Object.keys(ALLOWED.css).forEach(sel =>
    ok('allowed rule still paints white: ' + sel.replace(/\n/g, ' '), cssHits.some(h => h.selector === sel)));

// ── profiles.js inline styles, one line at a time ────────────────────────────────
console.log('\n── profiles.js inline styles do the same ──────────────────────');
const lines = stripComments(JS).replace(/\r\n/g, '\n').split('\n');
const jsHits = [];
lines.forEach((line, i) => {
    if (/^\s*\/\//.test(line)) return;
    let d;
    PAINT.lastIndex = 0;
    while ((d = PAINT.exec(line)) !== null) jsHits.push({ n: i + 1, line, decl: d[2] + ': ' + d[3].trim() });
});
ok('the scan finds declarations to judge (' + jsHits.length + ')', jsHits.length > 0);
jsHits.forEach(h => {
    const where = Object.keys(ALLOWED.js).filter(k => h.line.indexOf(k) !== -1)[0];
    ok('profiles.js:' + h.n + ' ' + h.decl.slice(0, 60) + (where ? ' — ' + ALLOWED.js[where] : ''), !!where,
       'use a theme token or add it to ALLOWED with the reason');
});
Object.keys(ALLOWED.js).forEach(k =>
    ok('allowed line still paints white: ' + k.slice(0, 50), jsHits.some(h => h.line.indexOf(k) !== -1)));

console.log('');
if (fails.length) {
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
