#!/usr/bin/env node
/*
 * The way out of every Bonfire screen is in the same place: bottom right.
 *
 * Reported as: "make sure exit buttons line up in similar spots — the Done button on one
 * of our screens is on the opposite end of the UI from the Cancel button on others." 1.7.2
 * put the way out first, bottom left. Reported again on 1.9.0.4-beta: "Who's watching?" had
 * Manage profiles, Cancel (back on the right) while "Manage profiles" had Done, Settings
 * (back on the left). The owner's rule is the first: the button that leaves is on the
 * right, the action on the left.
 *
 * The rule, checked one button at a time rather than as a count: each exit button sits in
 * a row of buttons (.jpf-actions, an inline .jpf-row-gap, or the gate's .profiles-footer),
 * and no other button comes after it in that row. The row spreads to both edges, so last
 * means right. A lone way out is pushed right too.
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

// Every button that leaves a screen without doing its main thing: the TV Back key's list
// (_dismissSurface) and the rest of the screens with a Cancel, Back, Skip or Done.
const EXITS = [
    'prompt-cancel-btn', 'select-cancel-btn', 'dialog-cancel-btn',
    'pin-cancel-btn', 'master-pin-cancel-btn', 'create-cancel-btn', 'edit-cancel-btn',
    'bonfire-back-btn', 'settings-back-btn', 'theme-settings-back-btn', 'switcher-mode-back-btn',
    'account-back-btn', 'pin-reset-back', 'self-cancel-btn', 'main-pin-skip', 'onboarding-skip',
    'profiles-panic-cancel', 'profiles-libart-cancel', 'profiles-crop-cancel',
    'profiles-resume-btn', 'account-email-cancel'
];
const ROW = /jpf-actions|jpf-row-gap|profiles-footer|jpf-inline-row/;

console.log('\n── Every screen\'s way out is last in its row ─────────────────');

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

/** Where the <div> opened at `row.index` closes. */
function closeOf(src, row) {
    const re = /<div\b[^>]*>|<\/div>/g;
    re.lastIndex = row.index;
    let depth = 0, m;
    while ((m = re.exec(src)) !== null) {
        depth += m[0] === '</div>' ? -1 : 1;
        if (depth === 0) return m.index;
    }
    return src.length;
}

function check(where, at, ignore) {
    // The row is the nearest one above it; a group inside the row (the edit form's Save
    // and Delete) is allowed between them.
    let row = enclosingDiv(SRC, at);
    let guard = 0;
    while (row && !ROW.test(row.tag) && guard++ < 3) row = enclosingDiv(SRC, row.index);
    ok(where + ' is in a row of buttons', !!row && ROW.test(row.tag), row ? row.tag.slice(0, 90) : 'no enclosing row');
    if (!row) return;
    const after = SRC.slice(at + 1, closeOf(SRC, row));
    // `ignore`: a button written after it that is never drawn with it (Cancel and Done are
    // the two ways out of the gate, one in each mode, behind one ternary each).
    const later = (after.match(/<button\b[^>]*>/g) || []).filter(b => !(ignore && ignore.test(b))).length;
    ok(where + ' comes after every other button in it', later === 0, later + ' button(s) after it');
}

EXITS.forEach(id => {
    const re = new RegExp('<button (?:type="button" )?id="' + id + '"', 'g');
    let m, seen = 0;
    while ((m = re.exec(SRC)) !== null) {
        seen++;
        check(id + (seen > 1 ? ' (' + seen + ')' : ''), m.index,
            id === 'profiles-resume-btn' ? /id="profiles-toggle-manage-btn"/ : null);
    }
    ok(id + ' exists', seen > 0);
});

// Done on Manage profiles is the gate's way out in manage mode; in the other mode the same
// button is Manage profiles, which is an action and comes first.
const manage = /this\.isManageMode \? `<button id="profiles-toggle-manage-btn"/.exec(SRC);
ok('Done (manage mode) exists', !!manage);
if (manage) check('Done on Manage profiles', manage.index + manage[0].indexOf('<button'));
const open = /this\.isManageMode \? '' : `<button id="profiles-toggle-manage-btn"/.exec(SRC);
ok('Manage profiles (the other mode) comes before Cancel', !!open
    && open.index < SRC.indexOf('<button id="profiles-resume-btn"'));
const settings = SRC.indexOf('<button id="profiles-settings-btn"');
ok('Settings comes before Done on Manage profiles', settings !== -1 && manage && settings < manage.index);

console.log('\n── The row puts last on the right ────────────────────────────');
const rule = (/\.jpf-actions\s*\{([^}]*)\}/.exec(CSS) || [])[1] || '';
ok('.jpf-actions spreads its buttons to both edges', /justify-content:\s*space-between/.test(rule));
ok('and no lone way out is pushed back to the middle or the left',
   !/class="bonfire-dialog-actions[^"]*"[^>]*justify-content:\s*(center|flex-start)/.test(SRC));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
