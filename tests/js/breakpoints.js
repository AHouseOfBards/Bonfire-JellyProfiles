/*
 * One breakpoint scale: 480 (phone), 900 (tablet), 1280 (desktop). A television is found by
 * detection (.jpf-tv), never by its width.
 *
 * The sheet had grown seven widths — 360, 480, 600, 899, 900 and 50em — each picked for the
 * one rule beside it, so no two screens agreed on what "a phone" was: the Save bar went
 * single-column at 480, the section padding at 600, the bubble moved at 600, and a 540px
 * window got half of each. Every width an @media or @container rule names, in the stylesheet,
 * in the CSS the theme generator writes, and in the admin page, must be on the scale.
 *
 * The >= / <= pairs may sit one pixel off the scale (479/480, 899/900, 1279/1280), because
 * `max-width: 479px` beside `min-width: 480px` is how the two halves of a pair avoid
 * overlapping. em/rem widths are converted at 16px so `50em` cannot slip through as "not a
 * pixel value".
 *
 *     node tests/js/breakpoints.js /path/to/old/profiles.js
 */
'use strict';

const L = require('./_lib');
const fs = require('fs');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SCALE = [480, 900, 1280];
const ALLOWED = SCALE.concat([479, 899, 1279]);

const raw = L.readProfiles();
const sources = [
    ['Web/styles.css', L.extractCss(raw)],
    ['generated theme CSS', L.themesCss(raw)],
    ['Web/profilesDashboard.html', fs.readFileSync(L.dashboardPath(), 'utf8')]
];

// Every width feature in every media/container query header, comments blanked first so a
// comment that mentions a width is not a width. A width written in a comment that quotes
// "@media (min-width: 50em)" would otherwise read as a rule.
function widths(css) {
    const bare = css.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));
    const out = [];
    const re = /@(?:media|container)\b([^{;]*)\{/g;
    let m;
    while ((m = re.exec(bare)) !== null) {
        const q = m[1];
        const w = /(?:min-|max-)?(?:width|device-width)\s*:\s*([0-9.]+)\s*(px|em|rem)?/g;
        let x;
        while ((x = w.exec(q)) !== null) {
            const n = parseFloat(x[1]);
            const px = x[2] === 'em' || x[2] === 'rem' ? n * 16 : n;
            out.push({ px, text: x[0].trim(), query: q.trim() });
        }
        // Range syntax: (width <= 600px), (400px < width < 700px).
        const r = /([0-9.]+)\s*(px|em|rem)\s*[<>]=?|[<>]=?\s*([0-9.]+)\s*(px|em|rem)/g;
        while ((x = r.exec(q)) !== null) {
            const n = parseFloat(x[1] || x[3]);
            const u = x[2] || x[4];
            out.push({ px: u === 'px' ? n : n * 16, text: x[0].trim(), query: q.trim() });
        }
    }
    return out;
}

console.log();
console.log('── every width in a media query is on the scale ─────────────');
let total = 0;
sources.forEach(([name, css]) => {
    const ws = widths(css);
    total += ws.length;
    const off = ws.filter(w => ALLOWED.indexOf(w.px) === -1);
    ok(name + ': ' + ws.length + ' widths, all on ' + SCALE.join('/') + (off.length ? '  (off: ' + off.map(w => w.text).join('; ') + ')' : ''),
        off.length === 0);
});

// The extractor has to be able to see what it is policing, or this passes by finding nothing.
ok('the detector finds widths at all (' + total + ')', total >= 6);
const probe = widths('@media (max-width: 600px) { a{} } @media all and (min-width: 50em) { b{} } @media (max-width: 360px) and (min-width: 20em) { c{} }');
ok('the detector reads px, em and a pair in one query', probe.length === 4 && probe[1].px === 800 && probe[3].px === 320);
ok('the detector ignores a width named in a comment', widths('/* @media (max-width: 777px) { */ a{}').length === 0);

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
