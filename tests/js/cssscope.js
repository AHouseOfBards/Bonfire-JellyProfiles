#!/usr/bin/env node
/*
 * Web/styles.css is loaded on every Jellyfin page, so a rule on a generic class name
 * (.btn-primary, .form-group, .profile-card) restyles another plugin's page that uses the
 * same name. Every comma-separated selector in every rule must therefore contain a Bonfire
 * class (jpf-, profiles-, bonfire-) or a Bonfire id, or be on the allowlist below, which is
 * the jellyfin-web classes Bonfire styles on purpose, each with its reason.
 *
 * Checked one selector at a time, not by counting: a rule whose first selector is scoped
 * does not cover its second.
 *
 *     node tests/js/cssscope.js [path/to/styles.css]
 */
'use strict';

const fs = require('fs');
const path = require('path');

const file = process.argv[2] || path.join(__dirname, '..', '..', 'Web', 'styles.css');

// A selector that is exactly one of these (whitespace normalised, pseudo-states removed) may
// stand without a Bonfire class. Each is jellyfin-web's own element, styled on purpose.
const ALLOW = {
    '.material-icons': 'jellyfin-web icon font; Bonfire sets its size and alignment inside its own buttons',
    '.docloader': 'jellyfin-web start-up loader, hidden while the profile picker is up',
    '.mainLoader': 'jellyfin-web start-up loader, hidden while the profile picker is up',
    '.loadingSpinner': 'jellyfin-web start-up loader, hidden while the profile picker is up',
    '.spinner': 'jellyfin-web start-up loader, hidden while the profile picker is up',
    '.view-loader': 'jellyfin-web start-up loader, hidden while the profile picker is up',
    '.headerButton': 'jellyfin-web header button, hidden while the profile picker is up',
    '.paper-icon-button-light': 'jellyfin-web header button, hidden while the profile picker is up'
};

const BONFIRE = /\.(jpf-|profiles-|bonfire-)[\w-]*|#(jpf|profiles|bonfire|libart|jellyfinProfiles)[\w-]*/i;

function selectorsOf(css) {
    css = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const out = [];
    const re = /([^{}]+)\{/g;
    let m;
    while ((m = re.exec(css))) {
        const head = m[1].trim();
        if (head[0] === '@') continue; // @media / @keyframes: their rules are matched on their own
        // Keyframe steps (from, to, 40%) are not selectors.
        if (/^(from|to|\d+(\.\d+)?%)(\s*,\s*(from|to|\d+(\.\d+)?%))*$/.test(head)) continue;
        // A comma inside :not(...) or :is(...) does not split the selector.
        let depth = 0, cur = '';
        for (const ch of head) {
            if (ch === '(') depth++;
            if (ch === ')') depth--;
            if (ch === ',' && depth === 0) { out.push(cur.trim()); cur = ''; } else cur += ch;
        }
        if (cur.trim()) out.push(cur.trim());
    }
    return out;
}

function unscoped(css) {
    const bad = [];
    selectorsOf(css).forEach(function (sel) {
        const plain = sel.replace(/"[^"]*"|'[^']*'/g, '');
        if (BONFIRE.test(plain)) return;
        // Bare element, attribute, :root and html/body selectors carry no class to collide on.
        if (!/[.#]/.test(plain.replace(/\[[^\]]*\]/g, ''))) return;
        const bare = plain.replace(/::?[\w-]+(\([^)]*\))?/g, '').replace(/\s+/g, ' ').trim();
        if (ALLOW[bare]) return;
        bad.push(sel);
    });
    return bad;
}

const css = fs.readFileSync(file, 'utf8');
let pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// The detector first: it must go red on a generic rule, or a green means nothing.
ok('detector: a generic .btn-primary rule is caught', unscoped('.btn-primary { color: red; }').length === 1);
ok('detector: one unscoped selector in a comma list is caught', unscoped('.jpf-a, .form-group { color: red; }').length === 1);
ok('detector: a prefixed rule passes', unscoped('.jpf-a .btn-primary { color: red; }').length === 0);
ok('detector: an allowlisted jellyfin-web class passes', unscoped('.material-icons { color: red; }').length === 0);
ok('detector: a state class alone is caught', unscoped('.is-open { color: red; }').length === 1);

const bad = unscoped(css);
ok('every selector in styles.css carries a Bonfire class or id, or is allowlisted', bad.length === 0,
    bad.length + ' unscoped, e.g. ' + bad.slice(0, 12).join(' | '));

console.log('\n' + pass + ' passed, ' + fail + ' failed');
process.exit(fail ? 1 : 0);
