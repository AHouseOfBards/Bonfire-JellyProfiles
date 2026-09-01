/*
 * The JavaScript baseline, which nothing checked until now.
 *
 * `tests/js/cssbaseline.js` covers the stylesheet against Chromium 68, because a
 * declaration those browsers cannot parse is dropped in silence. The script has the
 * harsher failure mode and had no check at all: one token of syntax newer than the
 * engine and the *entire file* fails to parse, so Bonfire does not run — no gate, no
 * switcher, no message. That is the shape of the defect that made 1.5.2 and 1.5.3
 * dead on arrival, arriving by a different door.
 *
 * The floor is Chromium 68 (webOS 5, the LG set in issue #16). Tizen 6.0 is 76.
 * `node --check` cannot answer this: it runs today's V8, where every one of these
 * parses perfectly.
 *
 *     node tests/js/jsbaseline.js                     # the working tree
 *     node tests/js/jsbaseline.js path/to/other.js    # any other client build
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

const FLOOR = 68;

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

/// Comments and string bodies come out first. Prose says "you can use ?. here" and a
/// template literal carries a stylesheet; both have produced false positives in every
/// hand-rolled scan of this file. `${...}` is kept, because that is real code.
function stripCommentsAndStrings(s) {
    let out = '';
    let i = 0;
    const n = s.length;
    while (i < n) {
        const c = s[i];
        if (c === '/' && s[i + 1] === '/') { while (i < n && s[i] !== '\n') i++; continue; }
        if (c === '/' && s[i + 1] === '*') {
            i += 2;
            while (i < n && !(s[i] === '*' && s[i + 1] === '/')) i++;
            i += 2;
            continue;
        }
        if (c === '"' || c === "'" || c === '`') {
            const quote = c;
            i++;
            while (i < n && s[i] !== quote) {
                if (s[i] === '\\') { i += 2; continue; }
                if (quote === '`' && s[i] === '$' && s[i + 1] === '{') {
                    let depth = 0;
                    const start = i + 2;
                    i += 2;
                    while (i < n && (depth > 0 || s[i] !== '}')) {
                        if (s[i] === '{') depth++;
                        if (s[i] === '}') depth--;
                        i++;
                    }
                    out += ' ' + s.slice(start, i) + ' ';
                }
                i++;
            }
            i++;
            out += ' "" ';
            continue;
        }
        out += c;
        i++;
    }
    return out;
}

/// [name, pattern, first Chromium that parses or provides it].
/// Syntax first — those take the whole file down. Then the library and DOM calls,
/// which only throw where they are reached.
const FEATURES = [
    // Syntax. One of these anywhere and nothing in the file runs.
    ['optional chaining  ?.',        /[\w)\]]\?\./g,                 80],
    ['nullish coalescing  ??',       /\?\?[^=]/g,                    80],
    ['logical assignment  ||= &&=',  /(\|\|=|&&=|\?\?=)/g,           85],
    ['numeric separators  1_000',    /\b\d+_\d/g,                    75],
    // Class fields are checked separately, below. The obvious pattern for them matches
    // every ordinary assignment at the start of a line, and this file is one IIFE full of
    // object literals: 74 false positives, which is how a check gets ignored.
    ['private class members  #x',    /this\.#\w/g,                   74],
    ['exponent assignment  **=',     /\*\*=/g,                       52],

    // Library and DOM. These throw where they are called.
    ['String.replaceAll',            /\.replaceAll\s*\(/g,           85],
    ['Array/String .at()',           /\.at\s*\(\s*-?\d/g,            92],
    ['Array.flat / flatMap',         /\.(flat|flatMap)\s*\(/g,       69],
    ['Object.fromEntries',           /Object\.fromEntries/g,         73],
    ['String.matchAll',              /\.matchAll\s*\(/g,             73],
    ['globalThis',                   /\bglobalThis\b/g,              71],
    ['Promise.allSettled',           /Promise\.allSettled/g,         76],
    ['Promise.any',                  /Promise\.any\b/g,              85],
    ['Object.hasOwn',                /Object\.hasOwn/g,              93],
    ['structuredClone',              /structuredClone/g,             98],
    ['queueMicrotask',               /queueMicrotask/g,              71],
    ['element.replaceChildren',      /\.replaceChildren\s*\(/g,      86],
    ['element.toggleAttribute',      /\.toggleAttribute\s*\(/g,      69],
    ['element.checkVisibility',      /\.checkVisibility\s*\(/g,     105],
    ['AbortSignal.timeout',          /AbortSignal\.timeout/g,       103],
    ['ResizeObserver',               /ResizeObserver/g,              64],
    ['node.isConnected',             /\.isConnected\b/g,             51],
    ['scrollIntoView(options)',      /\.scrollIntoView\s*\(\s*\{/g,  61],
];

// ── the detector has to be able to go red ──────────────────────────────────
// A baseline check that only ever runs over clean source is indistinguishable from one
// that matches nothing at all. Three of the patterns are put to a file that really
// carries the feature, with the same stripper in front of them.
console.log();
console.log('── The detector itself ────────────────────────────────────────');
{
    const bait = [
        'const a = obj?.thing;',
        'const b = x ?? y;',
        'const c = s.replaceAll("a", "b");',
        '// a comment saying ?. and ?? and .replaceAll( which must not count',
        'const d = "a string with ?. and ?? in it";',
        'const e = `a template with ?. and .replaceAll( inside`;',
    ].join('\n');
    const code = stripCommentsAndStrings(bait);
    const hit = name => {
        const f = FEATURES.find(x => x[0] === name);
        f[1].lastIndex = 0;
        return (code.match(f[1]) || []).length;
    };
    ok('optional chaining is detected in code', hit('optional chaining  ?.') === 1);
    ok('nullish coalescing is detected in code', hit('nullish coalescing  ??') === 1);
    ok('replaceAll is detected in code', hit('String.replaceAll') === 1);
    ok('and none of the three is counted inside a comment or a string',
        hit('optional chaining  ?.') === 1 && hit('nullish coalescing  ??') === 1);
}

// ── the client ─────────────────────────────────────────────────────────────
const SRC = process.argv[2] || L.profilesPath();
const code = stripCommentsAndStrings(fs.readFileSync(SRC, 'utf8'));

console.log();
console.log('── Web/profiles.js against Chromium ' + FLOOR + ' ────────────────────────');
const over = [];
const used = [];
for (const [name, re, since] of FEATURES) {
    re.lastIndex = 0;
    const hits = (code.match(re) || []).length;
    if (!hits) continue;
    if (since > FLOOR) over.push(`${name} needs Chromium ${since}, ${hits} use(s)`);
    else used.push(`${name} (${since}) x${hits}`);
}
// Class fields are Chromium 74 and only exist inside a class body. Found by locating the
// bodies rather than by pattern, so an ordinary assignment at the start of a line is not
// mistaken for one.
const classBodies = [];
for (const m of code.matchAll(/\bclass\s+\w+[^{]*\{/g)) {
    let depth = 1, i = m.index + m[0].length;
    const start = i;
    while (i < code.length && depth > 0) {
        if (code[i] === '{') depth++;
        if (code[i] === '}') depth--;
        i++;
    }
    classBodies.push(code.slice(start, i - 1));
}
const fieldy = classBodies.filter(b => /^\s*(static\s+)?#?[\w$]+\s*=[^=]/m.test(b));
if (fieldy.length) over.push(`class fields need Chromium 74, in ${fieldy.length} class body/ies`);
ok('the client declares no classes, so no class-field syntax to carry',
    classBodies.length === 0, classBodies.length + ' class body/ies found');

ok('nothing in the client script is newer than Chromium ' + FLOOR,
    over.length === 0, over.join('\n          '));
console.log('        in use, and old enough: ' + (used.join('; ') || 'nothing notable'));

// The dashboard is administrator-only and runs in a desktop browser, so it is held to
// no floor — but it is scanned, because "the admin page is fine" should be a fact
// rather than an assumption.
const dash = 'Web/profilesDashboard.html';
if (fs.existsSync(dash)) {
    const dashCode = stripCommentsAndStrings(fs.readFileSync(dash, 'utf8'));
    const dashOver = [];
    for (const [name, re, since] of FEATURES) {
        re.lastIndex = 0;
        const hits = (dashCode.match(re) || []).length;
        if (hits && since > FLOOR) dashOver.push(`${name} (${since}) x${hits}`);
    }
    console.log('        dashboard, for information: '
        + (dashOver.length ? dashOver.join('; ') : 'also nothing newer'));
}

console.log();
if (fails.length) {
    console.log('  Failures:');
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
