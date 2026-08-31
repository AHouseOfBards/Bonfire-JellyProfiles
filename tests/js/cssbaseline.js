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
ok('stylesheet is the whole thing, not a truncated literal', css.length > 60000,
    css.length + ' chars');
// A stray backtick ends the literal early and everything after it silently vanishes.
// That defect shipped twice, and I reproduced it twice more while writing this phase.
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

console.log();
console.log('  baseline Chromium ' + BASELINE + ': ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
