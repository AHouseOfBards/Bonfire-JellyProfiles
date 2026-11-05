/*
 * Does every Bonfire button, field and dialog draw in the theme's font?
 *
 * They did not, until 1.7.2. A <button>, <input>, <select> or <textarea> takes its font
 * from the browser, not from its parent, and jellyfin-web only fixes that for its own
 * emby-* elements (release-10.11.z: no rule in any of its stylesheets sets a font on a bare
 * control). The gate set the theme font on itself, so its headings and names had it and
 * its buttons were Arial. Dialogs are appended to <body>, outside the gate, so they had no
 * theme font at all. Found by the golden screenshots (tests/screens), not by a user.
 *
 * Asked pairwise: for each surface and each control, the rule that gives that control
 * that surface's font. Then every other rule in the sheet that sets a font on a control
 * is named, so a later rule cannot quietly take the font back.
 *
 *   node tests/js/controlfonts.js [styles.css]
 */
'use strict';

const fs = require('fs');
const lib = require('./_lib');

const arg = process.argv[2];
const css = (arg && /\.css$/.test(arg) ? fs.readFileSync(arg, 'utf8') : lib.stylesOnly())
    .replace(/\/\*[\s\S]*?\*\//g, '');

let passed = 0;
const failures = [];
function ok(cond, label, detail) {
    if (cond) { passed++; return; }
    failures.push(label + (detail ? '\n      ' + detail : ''));
}

// Every rule, flattened out of any @media or @supports block, as { selectors, body }.
const rules = [];
(function walk(text) {
    let i = 0;
    while (i < text.length) {
        const open = text.indexOf('{', i);
        if (open === -1) break;
        const head = text.slice(i, open).trim();
        let depth = 1, j = open + 1;
        while (j < text.length && depth > 0) {
            if (text[j] === '{') depth++;
            else if (text[j] === '}') depth--;
            j++;
        }
        const body = text.slice(open + 1, j - 1);
        if (/^@(media|supports)/.test(head)) walk(body);
        else if (head && head[0] !== '@') {
            rules.push({ selectors: head.split(',').map(s => s.trim().replace(/\s+/g, ' ')), body });
        }
        i = j;
    }
})(css);

function fontOf(body) {
    const m = /(?:^|;|\s)font-family\s*:\s*([^;]+)/.exec(body);
    return m ? m[1].trim() : null;
}

const SURFACES = ['#profiles-gate-overlay', '.jpf-trap-surface', '.create-profile-container'];
const CONTROLS = ['button', 'input', 'select', 'textarea'];

SURFACES.forEach(surface => {
    const own = rules.filter(r => r.selectors.indexOf(surface) !== -1 && fontOf(r.body));
    ok(own.some(r => /var\(--jpf-font\b/.test(fontOf(r.body))),
        surface + ' itself is in the theme font',
        own.length ? 'its font is ' + own.map(r => fontOf(r.body)).join(' / ') : 'no rule sets its font');
    CONTROLS.forEach(control => {
        const sel = surface + ' ' + control;
        const hits = rules.filter(r => r.selectors.indexOf(sel) !== -1 && fontOf(r.body));
        ok(hits.some(r => fontOf(r.body) === 'inherit'),
            sel + ' inherits the surface\'s font',
            hits.length ? 'set to ' + hits.map(r => fontOf(r.body)).join(' / ') : 'no rule; the browser default wins');
    });
});

// Anything else that sets a font on a control, named, so it is a decision and not an
// accident. A selector whose last part is a bare control, or a control with a class.
const ALLOWED = new Set(SURFACES.reduce((all, s) => all.concat(CONTROLS.map(c => s + ' ' + c)), []));
rules.forEach(r => {
    const font = fontOf(r.body);
    if (!font) return;
    r.selectors.forEach(sel => {
        const last = sel.split(/[\s>+~]+/).pop();
        const tag = /^([a-z]+)/.exec(last);
        if (!tag || CONTROLS.indexOf(tag[1]) === -1 || ALLOWED.has(sel)) return;
        ok(font === 'inherit' || /var\(--jpf-font\b/.test(font),
            sel + ' keeps the theme font', 'sets font-family: ' + font);
    });
});

if (failures.length) {
    failures.forEach(f => console.log('  FAIL  ' + f));
    console.log(passed + ' passed, ' + failures.length + ' failed');
    process.exit(1);
}
console.log(passed + ' passed, 0 failed');
