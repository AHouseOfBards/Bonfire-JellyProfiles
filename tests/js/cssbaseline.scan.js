/*
 * Survey tool, not a harness: enumerate every CSS feature in the injected stylesheet
 * that is newer than the TV baseline, and say which rule each one is in.
 *
 * The plan asserted "27 flex containers relying on gap". `gap:` appears 68 times in
 * the file, and the distinction matters in both directions: flexbox gap needs
 * Chrome 84, but grid gap has worked since Chrome 66, so adding margin fallbacks to
 * a grid container would double its spacing on every modern browser while fixing
 * nothing on a TV. Enumerate and classify; do not count occurrences.
 *
 * Run:  node tests/js/cssbaseline.scan.js [path/to/profiles.js]
 */
'use strict';

const lib = require('./_lib.js');

const css = lib.extractCss(lib.readProfiles());

/* Blank comments out, keeping every newline so reported line numbers stay true.
 * Without this a rule's "selector" is whatever comment happened to precede it, and
 * three rules came back unclassifiable for that reason alone. */
const bare = css.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));

/* Split into rule blocks. Nested at-rules (@media, @supports) are kept as context so
 * a declaration guarded by @supports can be told apart from an unguarded one. */
function blocks(text) {
    const out = [];
    const stack = [];
    let i = 0;
    let selStart = 0;

    while (i < text.length) {
        const ch = text[i];
        if (ch === '{') {
            const sel = text.slice(selStart, i).trim();
            if (/^@(media|supports|keyframes|font-face)/.test(sel)) {
                stack.push(sel);
                selStart = i + 1;
                i++;
                continue;
            }
            // Find the matching close brace for this declaration block.
            let depth = 1;
            let j = i + 1;
            while (j < text.length && depth > 0) {
                if (text[j] === '{') depth++;
                else if (text[j] === '}') depth--;
                j++;
            }
            out.push({
                selector: sel,
                body: text.slice(i + 1, j - 1),
                context: stack.slice(),
                index: i,
            });
            selStart = j;
            i = j;
            continue;
        }
        if (ch === '}') {
            stack.pop();
            selStart = i + 1;
        }
        i++;
    }
    return out;
}

const rules = blocks(bare);

function lineOf(index) {
    return css.slice(0, index).split('\n').length;
}

/* ── gap, split by the display mode of the rule that declares it ─────────────── */

const gapRules = rules.filter(r => /(^|[;\s])(gap|row-gap|column-gap)\s*:/.test(r.body));

/* A rule may set gap without setting display -- the display can live on the same
 * selector in another rule, or be inherited from the element's role. Resolve by
 * looking at every rule sharing the selector. */
const displayBySelector = new Map();
for (const r of rules) {
    const m = r.body.match(/(^|[;\s])display\s*:\s*(inline-)?(flex|grid)/);
    if (m) {
        for (const sel of r.selector.split(',').map(s => s.trim())) {
            displayBySelector.set(sel, m[3]);
        }
    }
}

const flex = [];
const grid = [];
const unknown = [];
for (const r of gapRules) {
    const own = r.body.match(/(^|[;\s])display\s*:\s*(inline-)?(flex|grid)/);
    let mode = own ? own[3] : null;
    if (!mode) {
        const modes = new Set();
        for (const sel of r.selector.split(',').map(s => s.trim())) {
            if (displayBySelector.has(sel)) modes.add(displayBySelector.get(sel));
        }
        if (modes.size === 1) mode = [...modes][0];
    }
    /* `display:none` in the base rule with a state rule flipping it on --
     * `.picture-sources { display:none; flex-direction:column; gap:... }` plus
     * `.picture-sources.is-open { display:flex }`. Both of these are flex
     * containers that lay out with gap; missing them would leave two panels with
     * no spacing on a TV and nothing to show for the scan. */
    if (!mode && /display\s*:\s*none/.test(r.body)) {
        for (const sel of r.selector.split(',').map(s => s.trim())) {
            for (const [other, m] of displayBySelector) {
                if (m === 'flex' && other !== sel && other.indexOf(sel) !== -1) mode = 'flex';
            }
        }
    }
    const row = { sel: r.selector, line: lineOf(r.index), ctx: r.context.join(' > ') };
    if (mode === 'flex') flex.push(row);
    else if (mode === 'grid') grid.push(row);
    else unknown.push(row);
}

function report(title, rows) {
    console.log('\n' + title + '  (' + rows.length + ')');
    for (const r of rows) {
        console.log('   L' + String(r.line).padEnd(6) + r.sel.replace(/\s+/g, ' ').slice(0, 88)
            + (r.ctx ? '   [' + r.ctx.slice(0, 40) + ']' : ''));
    }
}

console.log('stylesheet: ' + css.length + ' chars, ' + css.split('\n').length + ' lines');
console.log('rules with a gap declaration: ' + gapRules.length);
report('FLEX  -- needs a margin fallback (flex gap is Chrome 84)', flex);
report('GRID  -- no fallback needed (grid gap is Chrome 66)', grid);
report('UNRESOLVED -- display not found; classify by hand', unknown);

/* ── the other features, with their guard status ─────────────────────────────── */

/* Matched as whole tokens, and never inside an at-rule prelude.
 *
 * Substring matching gave two false findings on the first run and both looked real:
 * `max(` matched inside `minmax(` (grid, Chrome 57), and `color-mix(` matched the
 * text of the very `@supports (color: color-mix(...))` line that guards the eight
 * real uses -- reporting the guard as the thing needing a guard. This is the same
 * bug the selector ledger had twice, where `.navMenu` "verified" against
 * `navMenuOption`. A function name must be preceded by a non-identifier character;
 * a property must sit at the start of a declaration.
 */
const FEATURES = [
    { name: 'clamp()', since: 79, re: /(^|[^\w-])clamp\s*\(/g },
    { name: 'min()', since: 79, re: /(^|[^\w-])min\s*\(/g },
    { name: 'max()', since: 79, re: /(^|[^\w-])max\s*\(/g },
    { name: 'color-mix()', since: 111, re: /(^|[^\w-])color-mix\s*\(/g },
    { name: 'aspect-ratio', since: 88, re: /(^|[;{]\s*)aspect-ratio\s*:/g },
    { name: 'inset', since: 87, re: /(^|[;{]\s*)inset\s*:/g },
    { name: 'gap (flex only)', since: 84, re: /(^|[;{]\s*)(row-|column-)?gap\s*:/g },
    { name: ':focus-visible', since: 86, re: /:focus-visible/g },
    { name: 'position:sticky', since: 56, re: /position\s*:\s*sticky/g },
];

/* Offsets covered by an at-rule prelude -- the text between `@` and its `{`. A
 * feature named there is being tested for, not used. */
const preludes = [];
for (const m of bare.matchAll(/@(?:media|supports)[^{]*\{/g)) {
    preludes.push([m.index, m.index + m[0].length]);
}
const inPrelude = at => preludes.some(([a, b]) => at >= a && at < b);

console.log('\n\nfeature uses, with @supports context');
for (const f of FEATURES) {
    const hits = [];
    for (const m of bare.matchAll(f.re)) {
        const at = m.index + (m[1] ? m[1].length : 0);
        if (inPrelude(at)) continue;
        const r = rules.find(x => at > x.index && at < x.index + x.body.length);
        hits.push({
            line: lineOf(at),
            sel: r ? r.selector.replace(/\s+/g, ' ').slice(0, 60) : '(top level)',
            guarded: r ? r.context.some(c => c.startsWith('@supports')) : false,
        });
    }
    const unguarded = hits.filter(h => !h.guarded);
    const flag = unguarded.length && f.since > 68 ? '  <-- breaks on webOS 5' : '';
    console.log('\n  ' + f.name.padEnd(18) + 'Chrome ' + String(f.since).padEnd(5)
        + 'uses ' + hits.length + ', unguarded ' + unguarded.length + flag);
    for (const h of hits.slice(0, 8)) {
        console.log('     L' + String(h.line).padEnd(6) + (h.guarded ? '[guarded] ' : '          ') + h.sel);
    }
    if (hits.length > 8) console.log('     ... and ' + (hits.length - 8) + ' more');
}

/* ── detail for the flex-gap fallback work (P4-8) ─────────────────────────── */
console.log('\n\nflex containers, with what a margin fallback would need');
const detail = [];
for (const r of rules) {
    // Allow leading whitespace before the property. Requiring `;` or `{` skipped any
    // declaration sitting first in a body, which silently lost both media-query gap
    // overrides -- and one of those also flips flex-direction, so the fallback margin
    // has to change axis there. The classification pass above already allowed \s;
    // the two matchers disagreeing is what showed 34 in one place and 32 in the other.
    if (!/(^|[;{\s])(row-|column-)?gap\s*:/.test(r.body)) continue;
    const own = r.body.match(/display\s*:\s*(inline-)?(flex|grid)/);
    const sel = r.selector.trim();
    let isFlex = own && own[2] === 'flex';
    if (!isFlex && displayBySelector.get(sel) === 'flex') isFlex = true;
    if (!isFlex && /display\s*:\s*none/.test(r.body)) {
        for (const [o, m] of displayBySelector) {
            if (m === 'flex' && o !== sel && o.indexOf(sel) !== -1) isFlex = true;
        }
    }
    if (!isFlex) continue;
    const gap = /(?:^|[;{\s])gap\s*:\s*([^;]+)/.exec(r.body);
    const rowGap = /(?:^|[;{]\s*)row-gap\s*:\s*([^;]+)/.exec(r.body);
    const colGap = /(?:^|[;{]\s*)column-gap\s*:\s*([^;]+)/.exec(r.body);
    const dir = /flex-direction\s*:\s*(column|row)/.exec(r.body);
    const wrap = /flex-wrap\s*:\s*(wrap)/.test(r.body);
    detail.push({
        sel,
        line: lineOf(r.index),
        gap: (gap ? gap[1] : (rowGap ? 'row ' + rowGap[1] : '') + (colGap ? ' col ' + colGap[1] : '')).trim(),
        dir: dir ? dir[1] : 'row (default)',
        wrap,
        ctx: r.context.join(' '),
    });
}
const byShape = {};
for (const d of detail) {
    const key = d.dir.split(' ')[0] + (d.wrap ? ' + wrap' : '');
    (byShape[key] = byShape[key] || []).push(d);
}
for (const k of Object.keys(byShape).sort()) {
    console.log('\n  ' + k + '   (' + byShape[k].length + ')');
    for (const d of byShape[k]) {
        console.log('     L' + String(d.line).padEnd(6) + d.sel.padEnd(46).slice(0, 46)
            + ' gap: ' + d.gap + (d.ctx ? '   ' + d.ctx.slice(0, 28) : ''));
    }
}
console.log('\n  total flex containers with a gap: ' + detail.length);
