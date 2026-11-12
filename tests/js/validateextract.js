/*
 * Web/i18n/validate.js must read every English entry whole.
 *
 * It used to read one string literal per entry, so a value written 'a ' + 'b' was read as
 * 'a ' and a template literal was skipped, and a translation was then checked against
 * half a string (or not at all). It also decoded only quote and backslash escapes, so
 * a \n or \uXXXX in the English never matched the real character in a translation.
 *
 * Each case runs the validator itself, copied into a scratch tree beside a planted
 * profiles.js, and reads its exit code. argv[2] is the validate.js to test, so it can be
 * pointed at an older copy.
 *
 *   node tests/js/validateextract.js [path/to/validate.js]
 */
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const L = require('./_lib');

const validator = process.argv[2] || path.join(L.ROOT, 'Web', 'i18n', 'validate.js');
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

/// Runs the validator over a scratch tree whose EN_STRINGS body is `entries` and whose
/// French file is `fr`. Returns { code, out }.
function run(entries, fr) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'valx-'));
    try {
        const i18n = path.join(root, 'Web', 'i18n');
        fs.mkdirSync(i18n, { recursive: true });
        fs.copyFileSync(validator, path.join(i18n, 'validate.js'));
        fs.mkdirSync(path.join(root, 'Web', 'themes'));
        // Every key is used once, or the validator reports it as defined and never used.
        const uses = (entries.match(/^\s*'([^']+)':/gm) || [])
            .map(l => "    t('" + /'([^']+)'/.exec(l)[1] + "');\n").join('');
        fs.writeFileSync(path.join(root, 'Web', 'profiles.js'),
            "(function () {\n    const EN_STRINGS = {\n" + entries + "\n    };\n" + uses + "})();\n");
        fs.writeFileSync(path.join(i18n, 'fr.json'), JSON.stringify(fr));
        const r = cp.spawnSync(process.execPath, [path.join(i18n, 'validate.js'), 'fr'], { encoding: 'utf8' });
        return { code: r.status, out: (r.stdout || '') + (r.stderr || '') };
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}

const BASE = "        'x.base': 'Base',\n";
const FR = { 'x.base': 'Base' };

// Sanity: a plain tree is accepted, so the cases below fail for their own reason.
const plain = run(BASE, FR);
ok('a plain entry passes', plain.code === 0, plain.out.trim().split('\n').slice(-3).join(' | '));

// A split value is read whole. The placeholder lives in the second half: read as the
// first literal only, English has no {n} and the correct translation looks wrong.
const split = run(BASE + "        'x.split': 'Hello ' +\n            '{n} there',\n",
    Object.assign({ 'x.split': 'Bonjour {n}' }, FR));
ok('a value split with + is read whole (placeholder in the second half)', split.code === 0,
   split.out.trim().split('\n').slice(-3).join(' | '));

// A template literal is read, not skipped.
const tpl = run(BASE + "        'x.tpl': `Hello {n}`,\n", Object.assign({ 'x.tpl': 'Bonjour {n}' }, FR));
ok('a template literal is read', tpl.code === 0, tpl.out.trim().split('\n').slice(-3).join(' | '));

// A template with ${} cannot be read whole, so the run must fail rather than skip it.
const interp = run(BASE + "        'x.dyn': `Hello ${name}`,\n", Object.assign({ 'x.dyn': 'Bonjour' }, FR));
ok('an entry it cannot read whole fails the run', interp.code === 2, 'exit ' + interp.code);

const call = run(BASE + "        'x.call': tr('x') + ' more',\n", Object.assign({ 'x.call': 'Bonjour' }, FR));
ok('a value that is not a literal fails the run', call.code === 2, 'exit ' + call.code);

// Escapes: English written with \n or … is the real character; the translation file
// holds the real character. A decode that left the backslash form in place reads a
// different tag/placeholder structure; here the visible symptom is the placeholder
// inside a \u escape sequence neighbour.
const esc = run(BASE + "        'x.esc': 'Line\\n{n}\\u2026',\n", Object.assign({ 'x.esc': 'Ligne\n{n}…' }, FR));
ok('\\n and \\u escapes are decoded', esc.code === 0, esc.out.trim().split('\n').slice(-3).join(' | '));

// Decoded text must differ from the raw escape: a < is "<", so "<b>x</b>"
// is a tag in the English and a translation that drops it must be reported.
const tag = run(BASE + "        'x.tag': '\\u003cb>Hi\\u003c/b>',\n", Object.assign({ 'x.tag': 'Salut' }, FR));
ok('an escaped tag is seen (a translation dropping it is reported)', tag.code !== 0, 'exit ' + tag.code);

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
