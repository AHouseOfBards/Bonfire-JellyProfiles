/*
 * Every DOM selector the injection code aims at something we do not own must be
 * accounted for in tests/upstream-selectors.json.
 *
 * The memory rule was "read the actual jellyfin-web component before touching an
 * injection selector". It was written down after shipping the wrong selector twice —
 * and then two more shipped anyway, because a rule that lives only in prose applies
 * when somebody happens to remember it. This is the same rule with teeth: you cannot
 * add a selector without recording what you checked it against.
 *
 * Offline by design. It reads a ledger, it does not fetch anything, so it is
 * deterministic on CI. Refresh the ledger's verdicts with selectors.verify.js, which
 * does use the network.
 *
 *   node tests/js/selectors.test.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

const LEDGER = path.join(L.ROOT, 'tests', 'upstream-selectors.json');

let passed = 0;
const failures = [];
function ok(cond, label, detail) {
    if (cond) { passed++; return true; }
    failures.push(label + (detail ? '\n      ' + detail : ''));
    return false;
}

// ── which functions reach outside our own markup ────────────────────────────
const INJECTION_FNS = [
    'syncUserMenuEntry', 'syncPreferencesMenuEntry',
    'injectProfilePageSection', '_findHeaderContainer', '_searchForHeaderContainer',
    '_findGeometricHeaderAnchor',
    '_insertBeforeUserBtn', 'closeUserMenu', 'syncHiddenProfiles',
    '_applyHiddenProfiles',
    // checkRoute was missed when this list was written, on the reading that it only
    // classifies the route. It also runs the playback-OSD query — four selectors aimed
    // squarely at jellyfin-web, in the hottest path in the client, with nothing tracking
    // them. Two of the six it used to carry were unqualified attribute-substring
    // selectors that walked the whole document twice a second.
    'checkRoute'
];

const src = L.readProfiles();
const lines = src.split('\n');

function methodBody(name) {
    let start = -1;
    for (let i = 0; i < lines.length; i++) {
        if (new RegExp('^\\s{8}' + name + ': function').test(lines[i])) { start = i; break; }
    }
    if (start === -1) return null;
    for (let j = start + 1; j < lines.length; j++) {
        if (/^\s{8}[a-zA-Z_$][\w$]*: (async )?function/.test(lines[j])) {
            return lines.slice(start, j).join('\n');
        }
    }
    return lines.slice(start).join('\n');
}

/**
 * Split a selector list on top-level commas only.
 * '[id^="a"], .b' must not split inside the brackets, and a naive split produced a
 * bogus '[id^=' fragment the first time this was written.
 */
function splitSelectorList(s) {
    const out = [];
    let depth = 0, quote = null, cur = '';
    for (const c of s) {
        if (quote) { cur += c; if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'") { quote = c; cur += c; continue; }
        if (c === '[' || c === '(') depth++;
        if (c === ']' || c === ')') depth--;
        if (c === ',' && depth === 0) { out.push(cur.trim()); cur = ''; continue; }
        cur += c;
    }
    if (cur.trim()) out.push(cur.trim());
    return out.filter(Boolean);
}

const used = new Map();   // selector -> Set of functions that use it
function note(sel, fn) {
    if (!used.has(sel)) used.set(sel, new Set());
    used.get(sel).add(fn);
}

/**
 * The selector argument of a query call, including one written as several string
 * literals joined by `+`.
 *
 * This used to be a single `(['"])([\s\S]*?)\1` capture, which stops at the first
 * literal. _findHeaderContainer's Strategy B is written across three lines:
 *
 *     document.querySelector(
 *         '.btnCurrentUser, .headerButtonUser, .headerButton-user, ' +
 *         '.btnCast, .headerButton-cast, ' +
 *         '[class*="headerButton"]:not(#profiles-floating-bubble)'
 *     );
 *
 * so only the first three of its six selectors were ever extracted — and the ledger
 * cannot flag what it is never shown. The two it missed included an unqualified
 * attribute-substring selector, which is the exact kind this file exists to find.
 */
function queryArguments(body) {
    const out = [];
    const call = /(?:querySelector(?:All)?|closest|matches)\s*\(\s*/g;
    let m;
    while ((m = call.exec(body)) !== null) {
        let i = m.index + m[0].length;
        let joined = '';
        for (;;) {
            const quote = body[i];
            if (quote !== '"' && quote !== "'") break;
            let j = i + 1, lit = '';
            while (j < body.length && body[j] !== quote) {
                if (body[j] === '\\') { lit += body[j + 1]; j += 2; continue; }
                lit += body[j]; j++;
            }
            joined += lit;
            i = j + 1;
            // Only a `+` continues the same selector; anything else ends the argument.
            const rest = /^\s*\+\s*/.exec(body.slice(i));
            if (!rest) break;
            i += rest[0].length;
        }
        if (joined) out.push(joined);
    }
    return out;
}

for (const fn of INJECTION_FNS) {
    const body = methodBody(fn);
    // Was `continue`, silently: rename a listed function and every selector in it stopped
    // being checked while this stayed green. A function that goes away, or is renamed, now
    // has to be taken off the list (or put on it under its new name) on purpose.
    ok(body !== null, 'injection function ' + fn + ' is still where this list expects it',
        'rename or remove it in INJECTION_FNS, or its selectors go unchecked');
    if (body === null) continue;
    // A selector the extractor cannot read is a selector nobody checked: a variable, a
    // template literal, a concatenation that starts with one. The only non-literal
    // argument accepted is a named selector constant, which constRe below reads.
    const nonLiteral = /(?:querySelector(?:All)?|closest|matches)\s*\(\s*([^'"\s)][^)\n]*)/g;
    let nl;
    while ((nl = nonLiteral.exec(body)) !== null) {
        ok(/^this\.(USER_MENU_SELECTOR|TRAP_SURFACE_SELECTOR|USERS_CARD_SELECTOR)\b/.test(nl[1]),
            fn + ' passes a selector the ledger check cannot read',
            nl[0].trim() + ' - write it as a string literal, or as one of the named selector constants');
    }
    queryArguments(body).forEach(arg => {
        splitSelectorList(arg).forEach(s => note(s, fn));
    });
    // getElementsByClassName names classes, not a selector; the ledger writes them as one.
    const byClass = /getElementsByClassName\s*\(\s*(['"])([^'"]+)\1/g;
    let bc;
    while ((bc = byClass.exec(body)) !== null) {
        bc[2].split(/\s+/).filter(Boolean).forEach(c => note('.' + c, fn));
    }
}
// Selector constants used by the same code.
const constRe = /(?:USER_MENU_SELECTOR|TRAP_SURFACE_SELECTOR|USERS_CARD_SELECTOR):\s*(['"])([\s\S]*?)\1/g;
let cm;
while ((cm = constRe.exec(src)) !== null) {
    splitSelectorList(cm[2]).forEach(s => note(s, 'selector constant'));
}

// ── the ledger ──────────────────────────────────────────────────────────────
const ledger = JSON.parse(fs.readFileSync(LEDGER, 'utf8'));
const known = new Map(ledger.selectors.map(e => [e.selector, e]));

// ── selectors outside the listed functions ─────────────────────────────────
//
// INJECTION_FNS is a list somebody has to remember to extend. A query added to any other
// function that reaches into jellyfin-web was invisible here. So every literal query in the
// whole file is read, and a class or id in it must be one of ours: written somewhere in the
// script outside a query call (markup, a className), or defined in styles.css. One that is
// neither is aimed at somebody else's page and belongs in the ledger, in a listed function.
{
    const css = L.stylesOnly();
    const queryLiteral = /(?:querySelector(?:All)?|closest|matches)\s*\(\s*(['"])((?:\\.|(?!\1).)*)\1/g;
    const outsideQueries = src.replace(queryLiteral, 'Q()');
    const owned = name => new RegExp('(?<![\\w-])' + name.replace(/[-]/g, '\\-') + '(?![\\w-])').test(outsideQueries)
        || new RegExp('(?<![\\w-])' + name.replace(/[-]/g, '\\-') + '(?![\\w-])').test(css);
    const foreign = [];
    let cur = '(top)';
    lines.forEach((l, i) => {
        const mm = /^\s{8}([a-zA-Z_$][\w$]*): (async )?function/.exec(l);
        if (mm) cur = mm[1];
        if (INJECTION_FNS.indexOf(cur) !== -1) return;
        let q;
        queryLiteral.lastIndex = 0;
        while ((q = queryLiteral.exec(l)) !== null) {
            splitSelectorList(q[2]).forEach(sel => {
                if (known.has(sel)) return;
                (sel.match(/[.#][A-Za-z_][\w-]*/g) || []).forEach(tok => {
                    if (!owned(tok.slice(1))) foreign.push(tok + ' in ' + cur + ' (line ' + (i + 1) + ')');
                });
            });
        }
    });
    ok(foreign.length === 0, 'no function outside INJECTION_FNS queries a class or id that is not ours',
        foreign.join('\n      ') + '\n      Add the function to INJECTION_FNS and record the selector in the ledger.');
}

ok(used.size > 20, 'the extractor actually found selectors',
    'only found ' + used.size + ' — has the injection code moved?');

// 1. Nothing unaccounted for. This is the check with teeth.
const unknown = [...used.keys()].filter(s => !known.has(s));
ok(unknown.length === 0,
    'every injection selector is recorded in tests/upstream-selectors.json',
    unknown.length
        ? unknown.map(s => '· ' + s + '   (used by ' + [...used.get(s)].join(', ') + ')'
            ).join('\n      ')
          + '\n\n      Add each one with a status, and for an upstream selector the file you'
          + '\n      verified it in. Do not infer the markup from an older release — read the'
          + '\n      component. This rule exists because four dead selectors shipped without it.'
        : '');

// 2. The ledger must not rot into a list of things nobody uses.
const stale = ledger.selectors
    .filter(e => !used.has(e.selector) && e.status !== 'ours' && e.status !== 'generic')
    .map(e => e.selector);
ok(stale.length === 0, 'the ledger has no entries the code no longer uses',
    stale.length ? stale.join(', ') + '\n      Remove them, or the ledger stops describing the code.' : '');

// 3. Entries must be well formed.
ledger.selectors.forEach(e => {
    const valid = ['ours', 'generic', 'runtime', 'theme', 'upstream', 'dead'];
    ok(valid.indexOf(e.status) !== -1, 'status of "' + e.selector + '" is a known kind',
        'got "' + e.status + '"');
    if (e.status === 'upstream') {
        ok(typeof e.source === 'string' && e.source.startsWith('src/'),
            '"' + e.selector + '" names the jellyfin-web file it was verified in',
            'source = ' + JSON.stringify(e.source));
    }
    if (e.status === 'dead') {
        ok(typeof e.trackedBy === 'string' && e.trackedBy.length > 0,
            'dead selector "' + e.selector + '" names the task that removes it');
    }
});

// ── report ──────────────────────────────────────────────────────────────────
const dead = ledger.selectors.filter(e => e.status === 'dead' && used.has(e.selector));

console.log('selectors.test.js  ' + used.size + ' selectors used by ' + INJECTION_FNS.length + ' injection functions');
console.log('  checked against ' + ledger.verifiedAgainst + ' (' + ledger.verifiedOn + ')');
const byStatus = {};
[...used.keys()].forEach(s => {
    const st = known.has(s) ? known.get(s).status : 'UNKNOWN';
    byStatus[st] = (byStatus[st] || 0) + 1;
});
console.log('  ' + Object.keys(byStatus).sort().map(k => k + '=' + byStatus[k]).join('  '));

if (dead.length) {
    console.log('');
    console.log('  ' + dead.length + ' selector(s) still in use that do not exist in jellyfin-web:');
    dead.forEach(e => console.log('    · ' + e.selector + '   (' + e.trackedBy + ')'));
    console.log('  Known debt, not a failure. Delete the entries when the task lands and this');
    console.log('  section disappears on its own.');
}

console.log('');
console.log('  ' + passed + '/' + (passed + failures.length) + ' assertions passed');
if (failures.length) {
    console.log('');
    failures.forEach(f => console.log('  FAIL  ' + f));
    console.log('');
    process.exit(1);
}
console.log('  OK');
