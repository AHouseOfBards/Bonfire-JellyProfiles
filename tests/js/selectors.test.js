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
    'injectSidebarLink', 'syncUserMenuEntry', 'syncPreferencesMenuEntry',
    'injectProfilePageSection', '_findHeaderContainer', '_findGeometricHeaderAnchor',
    '_insertBeforeUserBtn', 'closeUserMenu', 'monitorAndHideShadowProfiles',
    'applyUsersHide'
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

for (const fn of INJECTION_FNS) {
    const body = methodBody(fn);
    if (body === null) continue;   // deleted, e.g. by P3-2
    const re = /(?:querySelector(?:All)?|closest|matches)\(\s*(['"])([\s\S]*?)\1/g;
    let m;
    while ((m = re.exec(body)) !== null) {
        splitSelectorList(m[2]).forEach(s => note(s, fn));
    }
}
// Selector constants used by the same code.
const constRe = /(?:USER_MENU_SELECTOR|TRAP_SURFACE_SELECTOR):\s*(['"])([\s\S]*?)\1/g;
let cm;
while ((cm = constRe.exec(src)) !== null) {
    splitSelectorList(cm[2]).forEach(s => note(s, 'selector constant'));
}

// ── the ledger ──────────────────────────────────────────────────────────────
const ledger = JSON.parse(fs.readFileSync(LEDGER, 'utf8'));
const known = new Map(ledger.selectors.map(e => [e.selector, e]));

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
