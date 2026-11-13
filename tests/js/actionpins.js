/*
 * Every `uses:` in the workflows is pinned to a full commit SHA.
 *
 * A tag is a pointer the action's owner can move. `actions/checkout@v4` runs whatever v4
 * points at today, with this repository's write token, inside the job that publishes the
 * plugin. A 40-hex commit cannot be moved. The tag stays beside it as a trailing comment
 * so a person can still read what it is and a bot can still bump it.
 *
 *   node tests/js/actionpins.js [workflows-dir]
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

const dir = process.argv[2] || path.join(L.ROOT, '.github', 'workflows');
const files = fs.readdirSync(dir).filter(f => /\.ya?ml$/.test(f)).sort();
ok('there are workflows to check (' + files.length + ')', files.length >= 2);

let total = 0;
files.forEach(f => {
    fs.readFileSync(path.join(dir, f), 'utf8').split(/\r?\n/).forEach((line, i) => {
        // Whole-line comments talk about `uses:` freely; only a real key counts.
        if (/^\s*#/.test(line)) return;
        const m = /^\s*(?:-\s+)?uses:\s*(\S+)(.*)$/.exec(line);
        if (!m) return;
        total++;
        const where = f + ':' + (i + 1) + ' ' + m[1];
        // A reusable workflow in this repository (release.yml calling ci.yml) is read from
        // the same commit as the caller, so there is nothing to pin and no tag to carry.
        // It passes only when it names a workflow scanned here, which this loop then checks
        // too; any other local path or a docker:// image still fails, because a silent pass
        // on one would be a hole.
        const local = /^\.\/\.github\/workflows\/([\w.-]+\.ya?ml)$/.exec(m[1]);
        if (local) {
            ok(where + ' is a workflow in this repository that is itself checked', files.indexOf(local[1]) >= 0);
            ok(where + ' has nothing after it', m[2].trim() === '', JSON.stringify(m[2]));
            return;
        }
        const pinned = /^[\w.-]+\/[\w.\/-]+@[0-9a-f]{40}$/.test(m[1]);
        ok(where + ' is pinned to a 40-hex commit', pinned);
        // The tag is what a human reads and what a bot bumps. A bare SHA says nothing.
        ok(where + ' carries its tag as a trailing comment', /^\s+#\s*v\d+\.\d+\.\d+\s*$/.test(m[2]),
           JSON.stringify(m[2]));
    });
});
ok('at least one `uses:` was found (' + total + '), so the scan is not blind', total >= 1);

console.log('');
if (fails.length) {
    fails.forEach(x => console.log('   - ' + x));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
