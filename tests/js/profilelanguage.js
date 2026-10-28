/*
 * A profile's audio and subtitle language belong to the profile once it exists.
 *
 * Creating a profile seeds AudioLanguagePreference, SubtitleLanguagePreference and
 * SubtitleMode from the master, which is a sensible starting point. Switching into the
 * profile used to copy them again, so a child set to Polish under an English master was
 * back on English after every switch (issue #36). The switch path must not write them.
 *
 *   node tests/js/profilelanguage.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

// Strip comments so the prose explaining the rule cannot satisfy or break the check.
const src = fs.readFileSync(path.join(L.ROOT, 'Controllers', 'ProfilesController.cs'), 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/.*$/gm, '');

const prefs = ['AudioLanguagePreference', 'SubtitleLanguagePreference', 'SubtitleMode'];

// Assignments to an existing configuration object (targetConfig.X = ...) are the switch-time
// copy. The creation path builds a new UserConfiguration with object-initialiser syntax
// (X = masterConfig.X,), which is deliberately still allowed.
for (const p of prefs) {
    const assigned = new RegExp('\\b\\w+\\.' + p + '\\s*=(?!=)').test(src);
    ok('nothing assigns ' + p + ' onto an existing configuration', !assigned);
}

const seeded = prefs.every(p => new RegExp('\\b' + p + '\\s*=\\s*masterConfig\\.' + p).test(src));
ok('profile creation still seeds the preferences from the master', seeded);

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) { fails.forEach(f => console.log('  ✗ ' + f)); process.exit(1); }
