// Parses the inline <script> block of the dashboard page. The file is an embedded
// resource, so a syntax error there is invisible until the settings page is opened.
const fs = require('fs');
const L = require('./_lib');
const html = fs.readFileSync(L.dashboardPath(), 'utf8');
const m = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
if (!m) { console.error('no script block found'); process.exit(1); }
let failed = false;
try {
    new Function(m[1]);
    console.log('dashboard script parses OK (' + m[1].split('\n').length + ' lines)');
} catch (e) {
    console.error('SYNTAX ERROR:', e.message);
    process.exit(1);
}

// ── No inline style may be written twice ────────────────────────────────────
//
// This page carried 132 inline style attributes across 55 distinct values, five of
// them amber notice boxes and no two the same amber. P6 moved the repeated ones onto
// classes; what is left is single-use, where a style sitting next to the element it
// describes is clearer than a name used nowhere else.
//
// So the rule is not "no inline styles" — that would be a rule nobody keeps, and the
// next person would add the exception back. It is "no inline style value appears
// twice", which is exactly the condition that says a class was wanted. It also catches
// the real failure: someone copying a notice box and tweaking one colour, which is how
// there came to be five ambers.
// ── No element may carry the same attribute twice ───────────────────────────
//
// An element with two class attributes keeps the first and discards the second, with
// no error in the console and nothing to see in the source unless you are looking for
// it. The P6-10 conversion produced four of them: it swapped a style attribute for a
// class attribute without checking whether the element already had a class, so four
// rows silently lost their layout. Caught by a scripted audit, not by a person reading
// the diff, which is the argument for this check existing.
const dupeAttrs = [];
const tagRe = /<[a-zA-Z][\w-]*((?:\s+[\w-]+\s*=\s*"[^"]*")+)\s*\/?>/g;
let tag;
while ((tag = tagRe.exec(html)) !== null) {
    const names = (tag[1].match(/\s([\w-]+)\s*=/g) || []).map(a => a.trim().replace(/=$/, ''));
    const seen = new Set();
    const twice = names.filter(n => (seen.has(n) ? true : (seen.add(n), false)));
    if (twice.length) {
        dupeAttrs.push({
            line: html.slice(0, tag.index).split('\n').length,
            names: [...new Set(twice)].join(', '),
            tag: tag[0].slice(0, 120)
        });
    }
}

if (dupeAttrs.length) {
    failed = true;
    console.error('DUPLICATE attributes (the second is discarded by the browser):');
    dupeAttrs.forEach(d => console.error('  line ' + d.line + '  ' + d.names + '  ' + d.tag));
} else {
    console.log('no element carries the same attribute twice');
}

const values = new Map();
const re = /style="([^"]*)"/g;
let hit;
while ((hit = re.exec(html)) !== null) {
    const value = hit[1].trim().replace(/\s+/g, ' ');
    if (!value) continue;
    values.set(value, (values.get(value) || 0) + 1);
}

const repeated = [...values.entries()].filter(([, n]) => n > 1);
const total = [...values.values()].reduce((a, b) => a + b, 0);

if (repeated.length) {
    failed = true;
    console.error('REPEATED inline styles (' + repeated.length + ' value(s)) — these want a class:');
    repeated
        .sort((a, b) => b[1] - a[1])
        .forEach(([value, n]) => console.error('  ' + n + '×  ' + value.slice(0, 100)));
} else {
    console.log('no inline style value is used twice (' + total + ' single-use)');
}

process.exit(failed ? 1 : 0);
