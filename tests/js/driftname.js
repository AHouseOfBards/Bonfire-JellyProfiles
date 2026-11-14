// r2-23-01: a theme's drifting pattern of a decimal size (the validator allows 240.5)
// named its keyframes "jpf-drift-240.5", which is not a CSS identifier, so the whole
// animation was dropped. Runs the real themeCss on Bloom with a decimal size and checks
// the name, that the animation and the keyframes agree on it, and that the distance is
// still the tile's size so the loop joins up.
//
//   node tests/js/driftname.js [path/to/profiles.js]

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const gen = L.themeGenerator(L.readProfiles());
const patterns = L.themesPayload().patterns || {};
const bloom = JSON.parse(fs.readFileSync(path.join(L.themesDir(), 'bloom.json'), 'utf8'));

function sheetFor(size) {
    const t = JSON.parse(JSON.stringify(bloom));
    t.decorations.forEach(d => { if (d.name === 'pattern') d.size = size; });
    return gen.themeCss(t, patterns);
}

[260, 240.5].forEach(size => {
    const css = sheetFor(size);
    const use = /animation:\s*([^\s;]+)\s/.exec(css);
    const frames = /@keyframes\s+([^\s{]+)\s*\{[^}]*\{[^}]*\}\s*to\s*\{\s*transform:\s*translateY\(([^)]*)\)/.exec(css);
    ok('size ' + size + ': the sheet drifts', !!use && !!frames);
    ok('size ' + size + ': the name is an identifier', !!use && /^-?[_a-zA-Z][_a-zA-Z0-9-]*$/.test(use[1]), use && use[1]);
    ok('size ' + size + ': the animation and the keyframes agree', !!use && !!frames && use[1] === frames[1]);
    ok('size ' + size + ': it travels one whole tile', !!frames && frames[2] === size + 'px', frames && frames[2]);
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
