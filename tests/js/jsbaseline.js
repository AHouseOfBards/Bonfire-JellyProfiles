/*
 * The JavaScript baseline.
 *
 * `tests/js/cssbaseline.js` covers the stylesheet against Chromium 68, because a
 * declaration those browsers cannot parse is dropped in silence. The script fails harder:
 * one token of syntax newer than the engine and the entire file fails to parse, so Bonfire
 * does not run (no gate, no switcher, no message). That is how 1.5.2 and 1.5.3 were dead
 * on arrival.
 *
 * The floor is Chromium 53: webOS 4.5, the 2019 LG sets. webOS 5 (the LG set in issue #16)
 * is 68, Tizen 6.0 is 76. `node --check` cannot answer this: it runs today's V8, where all
 * of these parse.
 *
 *     node tests/js/jsbaseline.js                     # the working tree
 *     node tests/js/jsbaseline.js path/to/other.js    # any other client build
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

const FLOOR = 53;

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

/// True when a '/' here opens a regex literal, not a division: what came before it is an
/// operator, an opening bracket, the start of the file, or a keyword that takes a value.
function regexAllowedAfter(out) {
    let k = out.length - 1;
    while (k >= 0 && /\s/.test(out[k])) k--;
    if (k < 0) return true;
    const ch = out[k];
    if (/[\w$]/.test(ch)) {
        let b = k;
        while (b >= 0 && /[\w$]/.test(out[b])) b--;
        return /^(return|typeof|case|in|of|delete|void|throw|else|do|new)$/.test(out.slice(b + 1, k + 1));
    }
    if (ch === ')' || ch === ']') return false;
    // `i++ / 2` is a division. Read as a regex it swallowed the rest of the line unscanned.
    if ((ch === '+' || ch === '-') && out[k - 1] === ch) return false;
    if (ch === '"' && out.slice(k - 3, k + 1) === ' "" ') return false;
    return true;
}

/// Comments and string bodies come out first: prose says "you can use ?. here" and a
/// template literal carries a stylesheet, and both gave false positives in every hand-rolled
/// scan of this file. `${...}` is kept because it is real code.
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
        if (c === '/' && regexAllowedAfter(out)) {
            // A regex literal. /"/g and /'/g are enough to open a string that never closes and
            // hide hundreds of lines (R-1277). Only a lookbehind or a named group is kept from
            // the body, because those are real features.
            let j = i + 1;
            let inClass = false;
            while (j < n && s[j] !== '\n' && (inClass || s[j] !== '/')) {
                if (s[j] === '\\') { j += 2; continue; }
                if (s[j] === '[') inClass = true;
                else if (s[j] === ']') inClass = false;
                j++;
            }
            const body = s.slice(i + 1, j);
            j++;
            while (j < n && /[a-z]/i.test(s[j])) j++;
            out += ' RX ' + (body.match(/\(\?<[=!A-Za-z]/g) || []).join(' ') + ' ';
            i = j;
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
/// Syntax first, since those take the whole file down; then library and DOM calls, which
/// only throw where they are reached.
const FEATURES = [
    // Syntax. One of these anywhere and nothing in the file runs.
    ['optional chaining  ?.',        /[\w)\]]\?\./g,                 80],
    ['nullish coalescing  ??',       /\?\?[^=]/g,                    80],
    ['logical assignment  ||= &&=',  /(\|\|=|&&=|\?\?=)/g,           85],
    ['numeric separators  1_000',    /\b\d+_\d/g,                    75],
    // Class fields are checked separately, below. The obvious pattern for them matches every
    // ordinary assignment at the start of a line, and this file is one IIFE of object
    // literals: 74 false positives, which is how a check gets ignored.
    ['private class members  #x',    /this\.#\w/g,                   74],
    ['exponent assignment  **=',     /\*\*=/g,                       52],
    ['async functions',              /\basync\s+(function\b|\(|[\w$]+\s*(=>|\())/g, 55],
    ['optional catch binding',       /\bcatch\s*\{/g,                 66],
    ['trailing comma in a call  f(a,)', /,\s*\)/g,                     58],
    ['regex lookbehind  (?<=',       /\(\?<[=!]/g,                   62],
    ['regex named groups  (?<x>',    /\(\?<[A-Za-z]/g,               64],
    // Object spread and rest are counted below: `...` is also array and call spread
    // (Chromium 46), and only the enclosing bracket tells them apart.

    // Library and DOM. These throw where they are called.
    ['Object.entries / values',      /Object\.(entries|values)\b/g,  54],
    ['String.padStart / padEnd',     /\.(padStart|padEnd)\s*\(/g,    57],
    ['Promise.prototype.finally',    /\.finally\s*\(/g,               63],
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
    // Added by the test audit: the table was a list of what someone had thought of, and
    // each of these passed it untouched. AbortController is mentioned twice in the client,
    // both times in a comment saying why it is not used (58; webOS 4.5 has no such thing).
    ['AbortController',              /\bAbortController\b/g,         58],
    ['BigInt',                       /\bBigInt\b|\b\d+n\b/g,         67],
    ['String.trimStart / trimEnd',   /\.(trimStart|trimEnd)\s*\(/g,  66],
    ['Object.getOwnPropertyDescriptors', /getOwnPropertyDescriptors/g, 54],
    ['element.prepend / replaceWith', /\.(prepend|replaceWith)\s*\(/g, 54],
    ['Intl.RelativeTimeFormat and friends', /Intl\.(RelativeTimeFormat|ListFormat|PluralRules|DisplayNames|Segmenter)/g, 63],
    ['Symbol.asyncIterator',         /Symbol\.asyncIterator/g,       63],
];

/// One line of code that really uses each feature newer than the floor, so the table below
/// can prove every pattern matches something. A pattern that never matches looks the same as
/// a clean client; before the audit only three of thirty-odd were ever shown to match.
const BAIT = {
    'optional chaining  ?.': 'var a = b?.c; var d = e?.[0]; f?.();',
    'nullish coalescing  ??': 'var a = b ?? c;',
    'logical assignment  ||= &&=': 'a ||= 1; b &&= 2; c ??= 3;',
    'numeric separators  1_000': 'var a = 1_000;',
    'private class members  #x': 'var a = this.#x;',
    'async functions': 'async function f() {} var g = async () => 1; var h = { async m() {} };',
    'optional catch binding': 'try { f(); } catch { }',
    'trailing comma in a call  f(a,)': 'f(a, b,);',
    'regex lookbehind  (?<=': 'var r = /(?<=a)b/;',
    'regex named groups  (?<x>': 'var r = /(?<n>a)/;',
    'Object.entries / values': 'Object.entries(o); Object.values(o);',
    'String.padStart / padEnd': 's.padStart(3); s.padEnd(3);',
    'Promise.prototype.finally': 'p.finally(f);',
    'String.replaceAll': 's.replaceAll("a", "b");',
    'Array/String .at()': 'a.at(-1); a.at(0);',
    'Array.flat / flatMap': 'a.flat(); a.flatMap(f);',
    'Object.fromEntries': 'Object.fromEntries(a);',
    'String.matchAll': 's.matchAll(r);',
    'globalThis': 'var g = globalThis;',
    'Promise.allSettled': 'Promise.allSettled(a);',
    'Promise.any': 'Promise.any(a);',
    'Object.hasOwn': 'Object.hasOwn(o, "a");',
    'structuredClone': 'structuredClone(o);',
    'queueMicrotask': 'queueMicrotask(f);',
    'element.replaceChildren': 'e.replaceChildren();',
    'element.toggleAttribute': 'e.toggleAttribute("a");',
    'element.checkVisibility': 'e.checkVisibility();',
    'AbortSignal.timeout': 'AbortSignal.timeout(5);',
    'ResizeObserver': 'new ResizeObserver(f);',
    'scrollIntoView(options)': 'e.scrollIntoView({ block: "center" });',
    'AbortController': 'var c = new AbortController();',
    'BigInt': 'var a = 10n; var b = BigInt(1);',
    'String.trimStart / trimEnd': 's.trimStart(); s.trimEnd();',
    'Object.getOwnPropertyDescriptors': 'Object.getOwnPropertyDescriptors(o);',
    'element.prepend / replaceWith': 'e.prepend(x); e.replaceWith(x);',
    'Intl.RelativeTimeFormat and friends': 'new Intl.RelativeTimeFormat();',
    'Symbol.asyncIterator': 'var s = Symbol.asyncIterator;',
};

/// `...` whose innermost open bracket is a brace: object spread or rest, Chromium 60.
function objectSpreads(code) {
    const open = [];
    let hits = 0;
    for (let i = 0; i < code.length; i++) {
        const c = code[i];
        if (c === '{' || c === '[' || c === '(') open.push(c);
        else if (c === '}' || c === ']' || c === ')') open.pop();
        else if (code.startsWith('...', i)) {
            if (open[open.length - 1] === '{') hits++;
            i += 2;
        }
    }
    return hits;
}

/// What the FEATURES table cannot see, because it reads code with every string blanked out.
///
/// 1. Bracket calls. `a["flat"]()` is the same call as `a.flat()` and, with the string
///    blanked, looks like `a[ "" ]()`. The names are the method names above that are newer
///    than the floor. This pass reads the raw text, so a comment that quotes such a call
///    would trip it; none does, and a false alarm is cheap.
/// 2. `.append(`. Element.append / ParentNode.append is Chromium 54, over the floor, but
///    FormData, URLSearchParams and Headers have had `.append` far longer and this client
///    uses them. A static read sees only the receiver's name, so a receiver is fine if the
///    file assigns that name `new FormData(`, `new URLSearchParams(` or `new Headers(`, or
///    is such a `new` expression itself. Any other receiver is reported; use appendChild,
///    or name the object for what it is.
const NEWER_METHODS = ['flat', 'flatMap', 'replaceAll', 'at', 'padStart', 'padEnd', 'finally',
    'matchAll', 'replaceChildren', 'toggleAttribute', 'checkVisibility', 'trimStart', 'trimEnd',
    'prepend', 'replaceWith', 'append', 'entries', 'values', 'fromEntries', 'hasOwn', 'allSettled', 'any'];
const SAFE_APPEND_CTORS = 'FormData|URLSearchParams|Headers';

function bracketCalls(raw) {
    const out = [];
    const re = /\[\s*(['"`])([A-Za-z]+)\1\s*\]\s*\(/g;
    let m;
    while ((m = re.exec(raw))) {
        if (NEWER_METHODS.indexOf(m[2]) !== -1) out.push('["' + m[2] + '"]( written as a bracket call');
    }
    return out;
}

function appendCalls(raw, code) {
    const out = [];
    const re = /([\w$]*)\s*([)\]]?)\s*\.\s*append\s*\(/g;
    let m;
    while ((m = re.exec(code))) {
        const id = m[1], closer = m[2];
        if (closer) {
            // `new FormData(form).append(`: the ctor call just before the dot.
            const before = code.slice(Math.max(0, m.index - 80), m.index + m[0].length);
            if (new RegExp('new\\s+(' + SAFE_APPEND_CTORS + ')\\s*\\([^()]*\\)\\s*\\.\\s*append').test(before)) continue;
            out.push('.append( on the result of a call');
        } else if (id && new RegExp('\\b' + id.replace(/\$/g, '\\$') + '\\s*=\\s*new\\s+(' + SAFE_APPEND_CTORS + ')\\b').test(code)) {
            continue;
        } else {
            out.push('.append( on "' + (id || '?') + '", which is not a FormData, URLSearchParams or Headers (Element.append is Chromium 54)');
        }
    }
    return out;
}

function newerCalls(raw) {
    return bracketCalls(raw).concat(appendCalls(raw, stripCommentsAndStrings(raw)));
}


// ── the detector has to be able to go red ──────────────────────────────────
// A baseline check that only ever runs over clean source looks the same as one that matches
// nothing. Three of the patterns, and the object spread count, are put to a file that
// really carries the feature, with the same stripper in front of them.
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
    const chainHits = src => (stripCommentsAndStrings(src).match(/[\w)\]]\?\./g) || []).length;
    ok('a regex literal holding a quote does not hide what follows it (R-1277)',
        chainHits("x.replace(/\"/g,'a'); y = a?.b;") === 1);
    ok('a regex with a slash inside a class is skipped whole (R-1277)',
        chainHits("x.split(/[/\"]/); y = a?.b;") === 1);
    ok('a division is not taken for a regex (R-1277)',
        chainHits("var r = a / b; var q = c / d; z = e?.f;") === 1);
    // Every pattern newer than the floor, pairwise: the bait for it exists, and the
    // pattern finds it after the stripper has been over it.
    FEATURES.filter(f => f[2] > FLOOR).forEach(f => {
        const bait2 = BAIT[f[0]];
        if (!bait2) { ok('there is bait for the pattern "' + f[0] + '"', false, 'add it to BAIT'); return; }
        f[1].lastIndex = 0;
        const n = (stripCommentsAndStrings(bait2).match(f[1]) || []).length;
        ok('"' + f[0] + '" is detected in code that uses it', n >= 1, 'bait: ' + bait2);
    });
    // Bracket calls and .append(, which the table cannot see. Both directions: what must
    // be reported, and what is fine on Chromium 53 and must not raise an alarm.
    ok('a bracket call to a newer method is caught: a["flat"]()', newerCalls('var x = a["flat"]();').length === 1);
    ok('and with single quotes and a gap: a[ \'replaceAll\' ] (', newerCalls("s[ 'replaceAll' ] ('a', 'b');").length === 1);
    ok('a bracket call to an old method is not', newerCalls('a["push"](1); o["hasOwnProperty"]("x");').length === 0);
    ok('el.append(child) is caught', newerCalls('var box = document.createElement("div"); box.append(child);').length === 1);
    ok('a bracket append is caught', newerCalls('box["append"](child);').length === 1);
    ok('FormData.append is not', newerCalls('var fd = new FormData(); fd.append("a", b);').length === 0);
    ok('URLSearchParams.append is not', newerCalls('var params = new URLSearchParams(); params.append("a", b);').length === 0);
    ok('Headers.append is not', newerCalls('var h = new Headers(); h.append("a", b);').length === 0);
    ok('new FormData(form).append is not', newerCalls('new FormData(form).append("a", b);').length === 0);
    ok('an append on something that is not shown to be one is', newerCalls('var fd2 = make(); fd2.append("a", b);').length === 1);
    ok('a postfix ++ before a division is not taken for the start of a regex',
        chainHits('var i = 1; var z = i++ / 2; var q = a?.b;') === 1);
    ok('object spread is detected, and array and call spread are not',
        objectSpreads(stripCommentsAndStrings(
            'const f = { a: 1, ...b }; const g = [...b]; h(...b); const { x, ...rest } = f;')) === 2);
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
const spreads = objectSpreads(code);
if (spreads) over.push(`object spread / rest needs Chromium 60, ${spreads} use(s)`);

const nc = newerCalls(fs.readFileSync(SRC, 'utf8'));
if (nc.length) over.push(nc.length + ' call(s) the table cannot see: ' + nc.join('; '));

const fieldy = classBodies.filter(b => /^\s*(static\s+)?#?[\w$]+\s*=[^=]/m.test(b));
if (fieldy.length) over.push(`class fields need Chromium 74, in ${fieldy.length} class body/ies`);
ok('the client declares no classes, so no class-field syntax to carry',
    classBodies.length === 0, classBodies.length + ' class body/ies found');

ok('nothing in the client script is newer than Chromium ' + FLOOR,
    over.length === 0, over.join('\n          '));
console.log('        in use, and old enough: ' + (used.join('; ') || 'nothing notable'));

// ── the Campground runtime ─────────────────────────────────────────────────
// Since 2.0 the shared coordinator inlines Campground.Shared/web/campground.js into every
// index.html, ahead of everything of ours, so it reaches every television profiles.js does.
// It is a separate inline <script>, so a token Chromium 53 cannot parse takes down only that
// script, but window.Campground goes with it, for every Campground plugin. Same floor.
// Missing is a failure: the plugin embeds it, so the build has lost its copy.
//
//     node tests/js/jsbaseline.js "" path/to/campground.js
const RUNTIME = process.argv[3] || path.join(L.ROOT, 'Campground.Shared', 'web', 'campground.js');
console.log();
console.log('── Campground.Shared/web/campground.js against Chromium ' + FLOOR + ' ──────');
if (!fs.existsSync(RUNTIME)) {
    ok('the vendored Campground runtime is there to check', false, RUNTIME);
} else {
    const rt = stripCommentsAndStrings(fs.readFileSync(RUNTIME, 'utf8'));
    const rtOver = [];
    for (const [name, re, since] of FEATURES) {
        re.lastIndex = 0;
        const hits = (rt.match(re) || []).length;
        if (hits && since > FLOOR) rtOver.push(`${name} needs Chromium ${since}, ${hits} use(s)`);
    }
    const rtSpreads = objectSpreads(rt);
    if (rtSpreads) rtOver.push(`object spread / rest needs Chromium 60, ${rtSpreads} use(s)`);
    const rtNc = newerCalls(fs.readFileSync(RUNTIME, 'utf8'));
    if (rtNc.length) rtOver.push(rtNc.join('; '));
    // No class bodies to search for fields in: ES5 has no classes, so any is too new.
    const rtClasses = (rt.match(/\bclass\s+\w+[^{]*\{/g) || []).length;
    if (rtClasses) rtOver.push(`${rtClasses} class declaration(s); the runtime is ES5`);
    ok('nothing in the Campground runtime is newer than Chromium ' + FLOOR,
        rtOver.length === 0, rtOver.join('\n          '));
}

// The dashboard is administrator-only and runs in a desktop browser, so it is held to no
// floor, but it is scanned so that "the admin page is fine" is a fact, not an assumption.
const dash = 'Web/profilesDashboard.html';
if (fs.existsSync(dash)) {
    const dashCode = stripCommentsAndStrings(fs.readFileSync(dash, 'utf8'));
    const dashOver = [];
    for (const [name, re, since] of FEATURES) {
        re.lastIndex = 0;
        const hits = (dashCode.match(re) || []).length;
        if (hits && since > FLOOR) dashOver.push(`${name} (${since}) x${hits}`);
    }
    const dashSpreads = objectSpreads(dashCode);
    if (dashSpreads) dashOver.push(`object spread / rest (60) x${dashSpreads}`);
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
