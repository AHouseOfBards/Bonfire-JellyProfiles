/*
 * P4-13: the injected stylesheet must render on the televisions people actually have.
 *
 * webOS 5 (the LG CX in issue #16) is Chromium 68. Tizen 6.0 is Chromium 76. Two
 * different failure modes, and the second is the dangerous one:
 *
 *   - a DECLARATION they cannot parse is dropped, and the rest of the rule survives.
 *     Recoverable, if an older declaration for the same property precedes it.
 *   - a SELECTOR they cannot parse invalidates the WHOLE RULE, and before :is() there
 *     is no forgiveness inside a comma-separated list. One :focus-visible in a list of
 *     four takes all four down.
 *
 * So this does not ask "is the feature used". It asks, per use, "is there something
 * that carries this on Chromium 68", and it names the mitigation it will accept. A
 * check that merely counted modern features would go green the moment someone deleted
 * a fallback, because the feature count would not change.
 *
 * Against 1.5.7 this fails on clamp, aspect-ratio, inset, :focus-visible and every
 * flex gap — which is the point; those were all really unguarded there.
 *
 *   node tests/js/cssbaseline.js [path/to/profiles.js]
 */
'use strict';

const L = require('./_lib');

const BASELINE = 68;          // webOS 5. Tizen 6.0 is 76 and is covered by it.

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const css = L.extractCss(L.readProfiles());

/* Comments blanked, newlines kept, so a feature discussed in prose is never mistaken
 * for a use. Two false findings came from not doing this. */
const bare = css.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));
const lineOf = i => bare.slice(0, i).split('\n').length;

/* ── parse into rules, keeping at-rule context ──────────────────────────────── */

function parse(text) {
    const out = [];
    const stack = [];
    let i = 0, selStart = 0;
    while (i < text.length) {
        if (text[i] === '{') {
            const sel = text.slice(selStart, i).trim();
            if (/^@/.test(sel)) { stack.push(sel); selStart = ++i; continue; }
            let depth = 1, j = i + 1;
            while (j < text.length && depth > 0) {
                if (text[j] === '{') depth++;
                else if (text[j] === '}') depth--;
                j++;
            }
            out.push({ selector: sel, body: text.slice(i + 1, j - 1), context: stack.slice(), index: i });
            selStart = j; i = j; continue;
        }
        if (text[i] === '}') { stack.pop(); selStart = i + 1; }
        i++;
    }
    return out;
}

const rules = parse(bare);
const selectors = new Set();
for (const r of rules) for (const s of r.selector.split(',')) selectors.add(s.trim().replace(/\s+/g, ' '));

console.log();
console.log('── the sheet parses into rules at all ─────────────────────────');
// A floor, not a count. themetest asserted exactly 15 of something and broke when a
// dead rule was deleted, reporting a regression where there was none. This only needs
// to catch "the parser returned nothing useful".
ok('rules found', rules.length > 100, rules.length + ' rules');
// A floor against an empty or partial sheet. It was 60,000 when the CSS lived inside a
// template literal; de-indenting it into Web/styles.css removed ~25,000 characters of
// leading whitespace, so the real size is ~58,700.
ok('stylesheet is the whole thing, not an empty splice', css.length > 40000,
    css.length + ' chars');
// Kept, but it is now a courtesy rather than a guard. A stray backtick used to end the
// stylesheet literal early and silently delete everything after it — that shipped twice,
// and I reproduced it twice more while writing P4. In a .css file a backtick is just a
// character, so this only flags one left behind by a bad merge.
ok('no backtick anywhere in the stylesheet', css.indexOf('`') === -1);

/* ── 1. a selector no old parser understands takes its whole rule with it ───── */

console.log();
console.log('── selector-level: one bad selector kills the entire rule ─────');

const SELECTOR_FEATURES = [
    { token: ':focus-visible', since: 86 },
    { token: ':is(', since: 88 },
    { token: ':where(', since: 88 },
    { token: ':has(', since: 105 },
];

for (const f of SELECTOR_FEATURES) {
    const using = rules.filter(r => r.selector.indexOf(f.token) !== -1);
    if (!using.length) { ok('no ' + f.token + ' to worry about', true); continue; }

    for (const r of using) {
        const parts = r.selector.split(',').map(s => s.trim());
        const line = lineOf(r.index);
        // The rule must be alone in its list, or the good selectors die with the bad.
        ok(f.token + ' at L' + line + ' is alone in its selector list',
            parts.length === 1,
            parts.length > 1 ? parts.length + ' selectors share the rule: ' + r.selector.replace(/\s+/g, ' ') : '');

        /* A negated use -- :focus:not(:focus-visible) -- is the modern half of the
         * pair. It only ever REMOVES a ring, on browsers new enough to understand it,
         * so it needs no counterpart; requiring one asks for :focus:not(:focus), which
         * matches nothing. Only a positive use has to be paired. */
        const negated = /:not\([^)]*:focus-visible[^)]*\)/.test(parts[0]);
        if (f.token === ':focus-visible' && parts.length === 1 && !negated) {
            // Something has to paint the ring on the browsers that drop this rule.
            const plain = parts[0].replace(':focus-visible', ':focus').replace(/\s+/g, ' ');
            ok(f.token + ' at L' + line + ' has a plain :focus counterpart',
                selectors.has(plain), 'wanted a rule for ' + plain);
        }
    }
}

/* ── 2. a declaration is dropped alone, so an older one before it survives ──── */

console.log();
console.log('── declaration-level: the old way has to come first ───────────');

/* value functions: the same property must be declared just before, without it */
const VALUE_FEATURES = [
    { name: 'clamp()', since: 79, re: /(^|[^\w-])clamp\s*\(/ },
    { name: 'min()', since: 79, re: /(^|[^\w-])min\s*\(/ },
    { name: 'max()', since: 79, re: /(^|[^\w-])max\s*\(/ },
];

for (const f of VALUE_FEATURES) {
    let seen = 0;
    for (const r of rules) {
        const decls = r.body.split(';').map(d => d.trim()).filter(Boolean);
        decls.forEach((d, idx) => {
            const colon = d.indexOf(':');
            if (colon === -1) return;
            const prop = d.slice(0, colon).trim();
            const val = d.slice(colon + 1);
            if (!f.re.test(val)) return;
            seen++;
            // Look back for the same property with a value this baseline can parse.
            const earlier = decls.slice(0, idx).filter(p => p.slice(0, p.indexOf(':')).trim() === prop);
            const fallback = earlier.some(p => !f.re.test(p.slice(p.indexOf(':') + 1)));
            ok(f.name + ' on ' + prop + ' at L' + lineOf(r.index) + ' has a plain fallback first',
                fallback,
                fallback ? '' : r.selector.replace(/\s+/g, ' ').slice(0, 50));
        });
    }
    if (!seen) ok('no ' + f.name + ' to worry about', true);
}

/* properties that must be guarded by @supports, or simply not used */
const PROP_FEATURES = [
    { prop: 'aspect-ratio', since: 88, guard: 'supports' },
    { prop: 'inset', since: 87, guard: 'banned', why: 'use top/right/bottom/left longhand' },
    { prop: 'gap', since: 84, guard: 'flexclass' },
    { prop: 'row-gap', since: 84, guard: 'flexclass' },
    { prop: 'column-gap', since: 84, guard: 'flexclass' },
];

/* which selectors are flex containers, and which are grid (grid gap is Chrome 66) */
const displayOf = new Map();
for (const r of rules) {
    // A flex fallback under @supports not (display: grid) is for a browser older than
    // Chromium 57, below this 68 baseline. Read as the container's display here it made
    // a grid container look like a flex one and demanded a margin on top of its grid gap.
    // Section 3 checks those blocks on their own terms.
    if (r.context.some(c => /^@supports\s+not\s*\(\s*display\s*:\s*(inline-)?grid/.test(c))) continue;
    const m = r.body.match(/display\s*:\s*(inline-)?(flex|grid)/);
    if (m) for (const s of r.selector.split(',')) displayOf.set(s.trim(), m[2]);
}
function isFlex(sel) {
    const s = sel.trim();
    if (displayOf.get(s) === 'flex') return true;
    if (displayOf.get(s) === 'grid') return false;
    // display:none base with a state rule flipping it to flex
    for (const [other, mode] of displayOf) {
        if (mode === 'flex' && other !== s && other.indexOf(s) !== -1) return true;
    }
    return false;
}

for (const f of PROP_FEATURES) {
    const propRe = new RegExp('(^|[;{\\s])' + f.prop + '\\s*:');
    let seen = 0;
    for (const r of rules) {
        if (!propRe.test(r.body)) continue;
        if (r.selector.indexOf('.jpf-no-flex-gap') !== -1) continue;   // the fallback itself
        const line = lineOf(r.index);
        const guardedBySupports = r.context.some(c => c.startsWith('@supports'));

        if (f.guard === 'banned') {
            seen++;
            ok('no ' + f.prop + ' shorthand at L' + line, false, f.why);
            continue;
        }
        if (f.guard === 'supports') {
            seen++;
            // Either the use itself is guarded, or an @supports-not block supplies the
            // old way for the same selector.
            const negated = rules.some(o =>
                o.context.some(c => /@supports\s+not\s*\(\s*aspect-ratio/.test(c))
                && o.selector.split(',').some(s => r.selector.split(',').some(t => s.trim() === t.trim())));
            ok(f.prop + ' at L' + line + ' is guarded or has a fallback block',
                guardedBySupports || negated, r.selector.replace(/\s+/g, ' ').slice(0, 50));
            continue;
        }
        if (f.guard === 'flexclass') {
            for (const sel of r.selector.split(',').map(s => s.trim())) {
                if (!isFlex(sel)) continue;      // grid gap is fine at this baseline
                seen++;
                // A margin fallback must exist for this container under the measured
                // class. Matched on the container selector, not merely on the class
                // existing somewhere -- "some fallbacks exist" is an aggregate, and
                // aggregates hide which member is missing.
                const wanted = '.jpf-no-flex-gap ' + sel;
                const has = [...selectors].some(s =>
                    s.indexOf('.jpf-no-flex-gap') !== -1 && s.indexOf(sel) !== -1);
                ok('flex gap on ' + sel + ' at L' + line + ' has a margin fallback',
                    has, has ? '' : 'wanted a rule matching ' + wanted);
            }
            continue;
        }
    }
    if (!seen) ok('no ' + f.prop + ' to worry about', true);
}

/* ── 3. Chromium 53, the floor (webOS 4.5) ───────────────────────────────────
 *
 * Everything above is written against 68. The floor the client promises is 53, and three
 * things the 68 checks cannot see were broken there (P2-504..507):
 *
 *   - :focus-within is Chrome 60. In a list with :hover and :focus it took both with it.
 *   - display: grid is Chrome 57. Declared with nothing to fall back to, it is a block.
 *   - position: sticky is Chrome 56.
 *
 * Each detector below is a function of a parsed sheet, and runs first on small sheets that
 * must trip it. A detector that cannot go red is a restatement, the way jsbaseline.js
 * proves its own first.
 */
console.log();
console.log('── Chromium 53: selectors, grid and sticky ────────────────────');

// A selector this old a parser cannot read. Matched on the text of one selector, never on
// the list: the list is what a mixed use is measured against.
const OLD_UNPARSEABLE = [
    { name: ':focus-within', since: 60, re: /:focus-within/ },
    { name: ':focus-visible', since: 86, re: /:focus-visible/ },
    { name: ':is()', since: 88, re: /:is\(/ },
    { name: ':where()', since: 88, re: /:where\(/ },
    { name: ':has()', since: 105, re: /:has\(/ },
    { name: '::placeholder', since: 57, re: /::placeholder/ },
    { name: '::marker', since: 86, re: /::marker/ },
    { name: ':not() with a list', since: 88, re: /:not\([^)]*,/ }
];

/** A selector list's members, split on the commas outside any parentheses. */
function splitList(sel) {
    const out = [];
    let depth = 0, from = 0;
    for (let i = 0; i < sel.length; i++) {
        if (sel[i] === '(') depth++;
        else if (sel[i] === ')') depth--;
        else if (sel[i] === ',' && depth === 0) { out.push(sel.slice(from, i)); from = i + 1; }
    }
    out.push(sel.slice(from));
    return out;
}

/** Every selector that shares a comma list with one Chromium 53 cannot parse. */
function mixedLists(rs) {
    const out = [];
    for (const r of rs) {
        const parts = splitList(r.selector).map(s => s.trim().replace(/\s+/g, ' ')).filter(Boolean);
        if (parts.length < 2) continue;
        const bad = parts.map(p => OLD_UNPARSEABLE.find(f => f.re.test(p)) || null);
        parts.forEach((p, i) => {
            if (bad[i]) return;
            const partner = bad.findIndex(b => b);
            if (partner !== -1) out.push({ line: r.index, victim: p, culprit: parts[partner], feature: bad[partner].name });
        });
    }
    return out;
}

const displayDecl = body => {
    const all = [];
    body.replace(/(^|[;{\s])display\s*:\s*([^;}]+)/g, (m, pre, v) => { all.push(v.trim()); return m; });
    return all;
};
const isGridValue = v => /^(inline-)?grid\b/.test(v);
const supportsNotGrid = ctx => ctx.some(c => /^@supports\s+not\s*\(\s*display\s*:\s*(inline-)?grid\s*\)/.test(c));
const hasGap = body => /(^|[;{\s])(row-|column-)?gap\s*:/.test(body);
const norm = s => s.trim().replace(/\s+/g, ' ');

/** The simple selectors of the last compound: ".a.b:hover" -> [".a", ".b", ":hover"]. */
function lastCompound(sel) {
    const m = /([^\s>+~]+)$/.exec(norm(sel));
    return m ? m[1] : '';
}
const simpleParts = c => c.match(/[.#:][^.#:\s]+|^[a-z][a-z0-9-]*/gi) || [];
/** `base` styles every element `sel` does: same ancestry, and a subset of its last compound. */
function covers(base, sel) {
    const b = norm(base), s = norm(sel);
    const bp = b.slice(0, b.length - lastCompound(b).length), sp = s.slice(0, s.length - lastCompound(s).length);
    if (bp !== sp) return false;
    const have = simpleParts(lastCompound(s));
    return simpleParts(lastCompound(b)).every(x => have.indexOf(x) !== -1);
}

/**
 * Every grid container, and what a browser with no grid gets. Two nameable fallbacks:
 *   - an EARLIER display other than grid for the same elements, which the unparseable
 *     grid declaration leaves standing, or
 *   - a rule under @supports not (display: grid) that sets one.
 * And when that fallback is a flex row with a gap, the children's spacing must exist for
 * a browser with no grid AND no flex gap: a .jpf-no-flex-gap margin rule that is not
 * boxed into a media range, or one inside the @supports-not block. A margin rule that
 * only runs below 900px did not cover a grid that starts at 900px; "some fallback
 * exists" is an aggregate, so each container is matched on its own.
 */
function gridProblems(rs) {
    const out = [];
    rs.forEach((r, idx) => {
        for (const sel of splitList(r.selector).map(norm)) {
            if (/jpf-no-flex-gap/.test(sel) || r.context.some(c => /^@supports\s+not/.test(c))) continue;
            if (!displayDecl(r.body).some(isGridValue)) continue;
            // "Underneath" the grid rule in the cascade: earlier in the sheet, or a plainer
            // selector (fewer classes in its last compound) wherever it sits. The second
            // is how .create-profile-container sits after .create-profile-container.is-two-col.
            const under = (o, s) => rs.indexOf(o) < idx
                || simpleParts(lastCompound(s)).length < simpleParts(lastCompound(sel)).length;
            const earlier = rs.filter(o =>
                !o.context.some(c => /^@supports\s+not/.test(c)) &&
                splitList(o.selector).some(s => covers(s, sel) && under(o, s)) &&
                displayDecl(o.body).some(v => !isGridValue(v) && v !== 'none'));
            const guarded = rs.filter(o => supportsNotGrid(o.context) &&
                splitList(o.selector).some(s => norm(s) === sel) &&
                displayDecl(o.body).some(v => !isGridValue(v) && v !== 'none'));
            const declFallback = displayDecl(r.body).some(v => !isGridValue(v));
            if (!earlier.length && !guarded.length && !declFallback) {
                out.push({ line: r.index, sel, why: 'no display other than grid for a browser without it' });
                continue;
            }
            // The effective gap: this rule's, or the earlier fallback rule's.
            const gap = hasGap(r.body) || earlier.some(o => hasGap(o.body));
            if (!gap) continue;
            const containers = earlier.map(o => splitList(o.selector).find(s => covers(s, sel))).concat([sel]).map(norm);
            const spaced = rs.some(o => {
                if (!splitList(o.selector).some(s => /jpf-no-flex-gap/.test(s) && containers.some(c => s.indexOf(c) !== -1))) return false;
                if (!/margin/.test(o.body)) return false;
                // Valid with no grid and no flex gap: no media range at all, or the no-grid block.
                return !o.context.some(c => /^@media/.test(c)) || supportsNotGrid(o.context);
            }) || rs.some(o => supportsNotGrid(o.context) && guarded.length && /margin/.test(o.body));
            if (!spaced) out.push({ line: r.index, sel, why: 'its children touch with no grid and no flex gap: no margin rule that applies outside a media range' });
        }
    });
    return out;
}

/** Every position: sticky, and whether anything is named for the browsers without it. */
function stickyProblems(rs, accepted) {
    const out = [];
    for (const r of rs) {
        if (!/(^|[;{\s])position\s*:\s*sticky/.test(r.body)) continue;
        for (const sel of splitList(r.selector).map(norm)) {
            const a = accepted.find(x => x.selector === sel);
            if (!a) out.push({ line: r.index, sel, why: 'sticky with no fallback the check can name' });
            else if (!a.holds()) out.push({ line: r.index, sel, why: 'its named fallback no longer holds: ' + a.fallback });
        }
    }
    return out;
}

const parseSheet = text => parse(text.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' ')));

// ── the detectors must be able to go red ───────────────────────────────────────
{
    const m = mixedLists(parseSheet('.a:hover, .a:focus, .a:focus-within { color: red; }'));
    ok('detector: a :focus-within list sharing a rule with :hover and :focus is flagged, both victims named',
        m.length === 2 && m.every(x => x.feature === ':focus-within') && m.some(x => x.victim === '.a:hover') && m.some(x => x.victim === '.a:focus'));
    ok('detector: :focus-within in a rule of its own is not flagged',
        mixedLists(parseSheet('.a:hover, .a:focus { color: red; } .a:focus-within { color: red; }')).length === 0);
    ok('detector: :is() beside a plain selector is flagged',
        mixedLists(parseSheet('.a, :is(.b, .c) { x: y; }')).length === 1);

    ok('detector: grid with nothing before it is flagged',
        gridProblems(parseSheet('.g { display: grid; }')).length === 1);
    ok('detector: grid after a flex rule for the same element passes, with no gap',
        gridProblems(parseSheet('.g { display: flex; } @media (min-width: 900px) { .g { display: grid; } }')).length === 0);
    ok('detector: that grid with a gap and a margin fallback boxed below 900px is flagged',
        gridProblems(parseSheet('.g { display: flex; gap: 1rem; } @media (min-width: 900px) { .g { display: grid; } }'
            + '@media (max-width: 899px) { .jpf-no-flex-gap .g > * + * { margin-top: 1rem; } }')).length === 1);
    ok('detector: and passes once the margin also applies under @supports not (display: grid)',
        gridProblems(parseSheet('.g { display: flex; gap: 1rem; } @media (min-width: 900px) { .g { display: grid; } }'
            + '@supports not (display: grid) { .jpf-no-flex-gap .g > * + * { margin-top: 1rem; } }')).length === 0);
    ok('detector: grid whose only fallback is an @supports-not flex block with a margin passes',
        gridProblems(parseSheet('.g { display: grid; gap: 4px; } @supports not (display: grid) { .g { display: flex; } .g > * { margin: 0 4px 4px 0; } }')).length === 0);
    ok('detector: sticky with no named fallback is flagged',
        stickyProblems(parseSheet('.bar { position: sticky; }'), []).length === 1);
    ok('detector: sticky whose named fallback has stopped holding is flagged',
        stickyProblems(parseSheet('.bar { position: sticky; }'), [{ selector: '.bar', fallback: 'x', holds: () => false }]).length === 1);
}

// ── the sheet, one finding at a time ───────────────────────────────────────────
const mixed = mixedLists(rules);
mixed.forEach(x => ok(x.victim + ' at L' + lineOf(x.line)
    + ' does not share a rule with ' + x.feature + ' (' + x.culprit + ')', false,
    x.culprit + ' is unparseable on Chromium 53 and takes this selector with it'));
ok('no comma list mixes a Chromium 53 selector with one it can parse (' + rules.length + ' rules read)', mixed.length === 0);

/* Enumerated, not counted: each :focus-within selector is one rule's whole list. */
for (const r of rules) {
    for (const f of OLD_UNPARSEABLE) {
        const parts = splitList(r.selector).map(norm).filter(p => f.re.test(p));
        for (const p of parts) {
            if (splitList(r.selector).length === 1) ok(f.name + ' at L' + lineOf(r.index) + ' is a rule of its own: ' + p.slice(0, 60), true);
        }
    }
}

const grids = gridProblems(rules);
grids.forEach(g => ok('grid on ' + g.sel + ' at L' + lineOf(g.line) + ' has a fallback', false, g.why));
ok('every display: grid has a fallback for a browser without grid', grids.length === 0);

const SRC_JS = L.readProfiles();
const STICKY_OK = [{
    selector: '.create-profile-container > .profile-dialog-actions',
    fallback: 'the row is the last child of the form, so it is the end of the form unpinned, and the remote\'s focus move scrolls it into view',
    holds: () => {
        if (!/_focusVisibly:\s*function[\s\S]{0,2500}scrollIntoView/.test(SRC_JS)) return false;
        // Last child, for each form that draws the row: nothing follows it but the form's
        // own closing tag and the end of the template.
        const open = '<div class="profile-dialog-actions jpf-actions">';
        let at = SRC_JS.indexOf(open), seen = 0;
        while (at !== -1) {
            seen++;
            const re = /<div\b[^>]*>|<\/div>/g;
            re.lastIndex = at + open.length;
            let depth = 1, m, end = -1;
            while ((m = re.exec(SRC_JS)) !== null) {
                depth += m[0] === '</div>' ? -1 : 1;
                if (depth === 0) { end = re.lastIndex; break; }
            }
            if (end === -1 || !/^\s*<\/div>\s*`/.test(SRC_JS.slice(end, end + 80))) return false;
            at = SRC_JS.indexOf(open, at + open.length);
        }
        return seen > 0;
    }
}];
const stickies = stickyProblems(rules, STICKY_OK);
stickies.forEach(s => ok('position: sticky on ' + s.sel + ' at L' + lineOf(s.line) + ' has a fallback', false, s.why));
ok('every position: sticky has a fallback that can be named and holds', stickies.length === 0);
// The comment said it "holds on both television baselines". It does not on webOS 4.5.
ok('no comment claims sticky holds on both television baselines',
    !/sticky[\s\S]{0,200}holds\s+on\s+both\s+television/i.test(css));

console.log();
console.log('  baseline Chromium ' + BASELINE + ': ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
