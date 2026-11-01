#!/usr/bin/env node
/*
 * The way out of every Bonfire screen is in the same place: bottom left.
 *
 * Reported as: "make sure exit buttons line up in similar spots — the Done button on one
 * of our screens is on the opposite end of the UI from the Cancel button on others." It
 * was. The PIN screens, the create form and the confirm dialog put the main action first
 * and Cancel second; the prompts put Cancel first; the edit form grouped Save with Cancel
 * on one side and Delete on the other; Settings, Switcher Style, Theme and Bonfire had a
 * lone Back or Done in the middle. On a television, where the remote has to travel to it,
 * that is a different hunt on every screen.
 *
 * The rule, checked one button at a time rather than as a count: each exit button sits in
 * a row marked .jpf-actions, and no other button comes before it in that row. .jpf-actions
 * spreads the row to both edges, so first means left.
 *
 * Usage: node tests/js/exitbuttons.js [path/to/profiles.js] [path/to/styles.css]
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

const SRC = fs.readFileSync(process.argv[2] || L.profilesPath(), 'utf8');
const CSS = fs.readFileSync(process.argv[3] || L.stylesPath(), 'utf8');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

// Every button that leaves a screen without doing its main thing. The TV Back key's list
// (_dismissSurface) is most of them; the prompts are the rest.
const EXITS = [
    'prompt-cancel-btn', 'select-cancel-btn', 'dialog-cancel-btn',
    'pin-cancel-btn', 'master-pin-cancel-btn', 'create-cancel-btn', 'edit-cancel-btn',
    'bonfire-back-btn', 'settings-back-btn', 'theme-settings-back-btn', 'switcher-mode-back-btn'
];

console.log('\n── Every screen\'s way out is first in its row ────────────────');

/** The opening tag of the nearest <div> that contains `at`, and where it starts. */
function enclosingDiv(src, at) {
    let depth = 0;
    const re = /<div\b[^>]*>|<\/div>/g;
    const tags = [];
    let m;
    while ((m = re.exec(src)) !== null && m.index < at) tags.push({ index: m.index, tag: m[0] });
    for (let i = tags.length - 1; i >= 0; i--) {
        if (tags[i].tag === '</div>') depth++;
        else if (depth === 0) return tags[i];
        else depth--;
    }
    return null;
}

EXITS.forEach(id => {
    const re = new RegExp('<button id="' + id + '"', 'g');
    let m, seen = 0;
    while ((m = re.exec(SRC)) !== null) {
        seen++;
        // The row is the nearest .jpf-actions above it; a group inside the row (the edit
        // form's Cancel and Delete) is allowed between them.
        let row = enclosingDiv(SRC, m.index);
        let guard = 0;
        while (row && !/jpf-actions/.test(row.tag) && guard++ < 3) row = enclosingDiv(SRC, row.index);
        const where = id + (seen > 1 ? ' (' + seen + ')' : '');
        ok(where + ' is in a .jpf-actions row', !!row && /jpf-actions/.test(row.tag),
           row ? row.tag.slice(0, 90) : 'no enclosing row');
        if (!row) continue;
        const before = SRC.slice(row.index, m.index);
        const earlier = (before.match(/<button\b/g) || []).length;
        ok(where + ' comes before every other button in it', earlier === 0, earlier + ' button(s) before it');
    }
    ok(id + ' exists', seen > 0);
});

console.log('\n── The row puts first on the left ────────────────────────────');
const rule = (/\.jpf-actions\s*\{([^}]*)\}/.exec(CSS) || [])[1] || '';
ok('.jpf-actions spreads its buttons to both edges', /justify-content:\s*space-between/.test(rule));
ok('and no lone way out is pushed back to the middle',
   !/class="bonfire-dialog-actions[^"]*"[^>]*justify-content:\s*center/.test(SRC));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
