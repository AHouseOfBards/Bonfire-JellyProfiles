/*
 * R-0003: the confirm and alert boxes were 420px wide with the width written inline, so
 * on a 1080p television they were a small card in the middle of a big screen and the
 * stylesheet could not make them larger (an inline style outranks everything but
 * !important).
 *
 * Two questions, asked of each of the four boxes (confirm, alert, prompt, select) separately:
 *   1. does the markup still carry a max-width or width of its own?
 *   2. on a .jpf-tv page, which max-width does the cascade settle on, and is it larger
 *      than the one everyone else gets?
 *
 *   node tests/js/dialogwidth.js                 # the working tree
 *   node tests/js/dialogwidth.js path/to/old.js  # expected to fail
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const src = L.readProfiles();
const css = L.stylesOnly().replace(/\/\*[\s\S]*?\*\//g, '');

/* Every rule as { selectors: [...], body, index }, in sheet order. @media blocks are
 * flattened; none of the rules asked about here sit inside one. */
const rules = [];
{
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(css))) {
        const sels = m[1].split(',').map(s => s.trim()).filter(Boolean);
        rules.push({ sels, body: m[2], index: m.index });
    }
}

function px(body, prop) {
    const m = new RegExp('(^|[;\\s])' + prop + '\\s*:\\s*(\\d+)px').exec(body);
    return m ? parseInt(m[2], 10) : null;
}

/* The winning max-width for an element with `cls`, with or without .jpf-tv above it.
 * Specificity is (classes), compared first, then sheet order. Only the selector shapes
 * this sheet uses for the boxes are understood: ".cls" and ".jpf-tv .cls". */
function maxWidth(cls, tv) {
    let best = null;
    for (const r of rules) {
        for (const s of r.sels) {
            let weight = 0;
            if (s === '.' + cls) weight = 1;
            else if (tv && s === '.jpf-tv .' + cls) weight = 2;
            else continue;
            const v = px(r.body, 'max-width');
            if (v === null) continue;
            if (!best || weight > best.weight || (weight === best.weight && r.index > best.index)) {
                best = { weight, index: r.index, value: v };
            }
        }
    }
    return best ? best.value : null;
}

/* The opening tag of each box in the script: `<div class="alert-dialog-content" style="...">`. */
function tags(cls) {
    const out = [];
    const re = new RegExp('<div class="' + cls + '"[^>]*>', 'g');
    let m;
    while ((m = re.exec(src))) out.push(m[0]);
    return out;
}

for (const cls of ['confirm-dialog-content', 'alert-dialog-content', 'prompt-dialog-content', 'select-dialog-content']) {
    console.log('── ' + cls + ' ──');
    const found = tags(cls);
    ok(cls + ' is built somewhere', found.length > 0);
    found.forEach((t, i) => {
        ok(cls + ' #' + (i + 1) + ' carries no inline max-width', !/max-width/.test(t));
        ok(cls + ' #' + (i + 1) + ' carries no inline width', !/(^|[;\s"])width\s*:/.test(t));
    });
    const base = maxWidth(cls, false);
    const tv = maxWidth(cls, true);
    ok(cls + ' has a stylesheet max-width for everyone', base !== null);
    ok(cls + ' is wider on a television than elsewhere (' + tv + ' vs ' + base + ')',
        tv !== null && base !== null && tv > base);
    ok(cls + ' fits a 1080p screen (<= 1200px)', tv !== null && tv <= 1200);
}

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
if (fails.length) process.exit(1);
