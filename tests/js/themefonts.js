/*
 * Every built-in theme's font fallback must match the one fonts.json gives that family.
 * bloom.json said "sans-serif" for Nunito while fonts.json says "rounded", so the theme
 * and the bundled font disagreed about what to show before the file loaded.
 *
 *   node tests/js/themefonts.js [themes dir]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

const dir = process.argv[2] || path.join(L.ROOT, 'Web', 'themes');
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const fonts = JSON.parse(fs.readFileSync(path.join(dir, 'fonts.json'), 'utf8')).fonts;
const byFamily = {};
fonts.forEach(f => { byFamily[f.family] = f; });

const themes = fs.readdirSync(dir).filter(f => f.endsWith('.json') && f !== 'fonts.json');
ok('there are built-in themes to check', themes.length > 0);
themes.forEach(file => {
    const t = JSON.parse(fs.readFileSync(path.join(dir, file), 'utf8'));
    const font = t.type && t.type.font;
    if (!font) return;
    const entry = byFamily[font.family];
    ok(file + ': family ' + font.family + ' is in fonts.json', !!entry);
    if (entry) {
        ok(file + ': fallback matches fonts.json',
           font.fallback === entry.fallback,
           'theme says ' + font.fallback + ', fonts.json says ' + entry.fallback);
    }
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
