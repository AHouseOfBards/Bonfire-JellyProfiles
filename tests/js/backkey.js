#!/usr/bin/env node
/*
 * P2-201: the TV Back key has to leave every dialog.
 *
 * _dismissSurface answers Back by clicking the first control on the surface that matches
 * a fixed list of ids. A dialog whose cancel button is not on that list swallows Back and
 * does nothing, which on a remote reads as the screen being stuck. It was true of the
 * select and prompt dialogs, and of the time-up dialog (showActionsDialog), whose buttons
 * had no ids at all.
 *
 * Checked one dialog at a time: every dialog built here that is a trap surface and has a
 * cancel button must have that button's id on the list. Not a count of ids.
 *
 *   node tests/js/backkey.js [path/to/profiles.js]
 */
'use strict';

const L = require('./_lib');
const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const dismiss = (/_dismissSurface: function[\s\S]*?\n        \},/.exec(SRC) || [''])[0];
const listed = new Set((dismiss.match(/#[a-z0-9-]+/g) || []).map(s => s.slice(1)));
ok('found the Back key list', listed.size > 10, listed.size + ' ids');

console.log('\n── Every dialog\'s cancel button is on the Back key\'s list ───');

// Each dialog function, by the cancel button it draws.
const DIALOGS = [
    { fn: 'showPromptDialog', id: 'prompt-cancel-btn' },
    { fn: 'showSelectDialog', id: 'select-cancel-btn' },
    { fn: 'showConfirmDialog', id: 'dialog-cancel-btn' },
    { fn: 'showActionsDialog', id: 'actions-cancel-btn' }
];

DIALOGS.forEach(d => {
    const at = SRC.indexOf(d.fn + ': function');
    ok(d.fn + ' exists', at !== -1);
    if (at === -1) return;
    const next = SRC.indexOf('\n        },\n', at);
    const body = SRC.slice(at, next === -1 ? at + 6000 : next);
    ok(d.fn + ' is a trap surface, so Back reaches it', /jpf-trap-surface/.test(body));
    ok(d.fn + ' draws #' + d.id, body.indexOf(d.id) !== -1);
    ok('#' + d.id + ' is on the Back key\'s list', listed.has(d.id));
});

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
