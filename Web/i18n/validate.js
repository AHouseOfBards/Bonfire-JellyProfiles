#!/usr/bin/env node
//
// Checks every translation in this folder against the English catalogue in profiles.js.
//
//     node Web/i18n/validate.js            # all files
//     node Web/i18n/validate.js fr         # just one
//
// No dependencies — plain node, any recent version. Run it before opening a pull
// request; it catches the four things that go wrong in a translation and that reading
// the diff will not reliably show you.
//
// See docs/developer-api.md, "Adding a translation", for the whole process.

const fs = require('fs');
const path = require('path');

const HERE = __dirname;
const PROFILES_JS = path.join(HERE, '..', 'profiles.js');

// ── The English catalogue ───────────────────────────────────────────────────
// EN_STRINGS in profiles.js is the single source of truth for what keys exist.
// Read out of the file rather than duplicated here, so this cannot drift from it.
function readEnglish() {
    const src = fs.readFileSync(PROFILES_JS, 'utf8');
    const start = src.indexOf('const EN_STRINGS = {');
    if (start === -1) {
        fail('Could not find EN_STRINGS in profiles.js. Has it been renamed?');
    }
    const end = src.indexOf('\n    };', start);
    if (end === -1) fail('Could not find the end of EN_STRINGS in profiles.js.');

    const body = src.slice(start, end);
    const out = {};

    // 'key': 'value',  — and 'key': "value", which is how any English string containing
    // an apostrophe is written ("Who's Watching?"). Missing the double-quoted form made
    // every one of those look like a key the translation had invented.
    const re = /'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/g;
    let m;
    while ((m = re.exec(body)) !== null) {
        const raw = m[2] !== undefined ? m[2] : m[3];
        out[m[1]] = raw.replace(/\\(['"\\])/g, '$1');
    }
    if (Object.keys(out).length === 0) fail('Parsed EN_STRINGS but found no keys.');
    return out;
}

function fail(msg) {
    console.error('validate.js: ' + msg);
    process.exit(2);
}

/// The {token} placeholders in a string, as a sorted list. A translation that drops one
/// renders the literal word "{name}" to a user; one that invents a new token renders it
/// verbatim too, because t() only substitutes what the caller passed.
function tokens(str) {
    return (str.match(/\{[a-zA-Z0-9_]+\}/g) || []).sort();
}

/// Tags a translator must carry across. t() interpolates into templates that reach
/// innerHTML, so a mismatch here is a broken layout rather than a wrong word.
function tags(str) {
    return (str.match(/<\/?[a-zA-Z][^>]*>/g) || [])
        .map(t => t.toLowerCase().replace(/\s+/g, ' '))
        .sort();
}

const english = readEnglish();
const englishKeys = Object.keys(english).sort();

const only = process.argv[2] ? process.argv[2].replace(/\.json$/i, '') : null;
const files = fs.readdirSync(HERE)
    .filter(f => f.endsWith('.json'))
    .filter(f => !only || f === only + '.json');

if (only && files.length === 0) fail('No such translation: ' + only + '.json');
if (files.length === 0) {
    console.log('No translation files yet. English is built in — nothing to check.');
    process.exit(0);
}

console.log('English catalogue: ' + englishKeys.length + ' keys\n');

let bad = 0;

for (const file of files) {
    const code = file.replace(/\.json$/, '');
    const problems = [];
    const notes = [];

    // The filename is the locale code the browser is matched against, so it has to be a
    // tag a browser actually sends: "fr", "pt-BR", "zh-Hans".
    if (!/^[a-z]{2,3}(-[a-z0-9]{2,8})*$/i.test(code)) {
        problems.push('Filename is not a locale code. Use fr.json, pt-BR.json, zh-Hans.json.');
    }

    let data;
    try {
        data = JSON.parse(fs.readFileSync(path.join(HERE, file), 'utf8'));
    } catch (e) {
        console.log('✗ ' + file + '\n    Not valid JSON: ' + e.message + '\n');
        bad++;
        continue;
    }

    if (!data || typeof data !== 'object' || Array.isArray(data)) {
        console.log('✗ ' + file + '\n    Must be a JSON object of key/value strings.\n');
        bad++;
        continue;
    }

    const keys = Object.keys(data);

    // Missing keys are not fatal at runtime — t() falls back to English per key — but a
    // half-translated screen is worth knowing about before it ships.
    const missing = englishKeys.filter(k => !(k in data));
    const unknown = keys.filter(k => !(k in english));

    if (unknown.length) {
        problems.push(unknown.length + ' key(s) not in the English catalogue (typo, or '
            + 'left over from an older version): ' + unknown.slice(0, 8).join(', ')
            + (unknown.length > 8 ? ', …' : ''));
    }

    for (const k of keys) {
        if (!(k in english)) continue;

        if (typeof data[k] !== 'string') {
            problems.push(k + ': value must be a string.');
            continue;
        }
        if (data[k].trim() === '') {
            problems.push(k + ': empty. Remove the key instead — it will fall back to English.');
            continue;
        }

        const want = tokens(english[k]);
        const got = tokens(data[k]);
        if (want.join('|') !== got.join('|')) {
            problems.push(k + ': placeholders differ. English has ' + (want.join(' ') || '(none)')
                + ', this file has ' + (got.join(' ') || '(none)') + '.');
        }

        const wantTags = tags(english[k]);
        const gotTags = tags(data[k]);
        if (wantTags.join('|') !== gotTags.join('|')) {
            problems.push(k + ': HTML tags differ. English has ' + (wantTags.join(' ') || '(none)')
                + ', this file has ' + (gotTags.join(' ') || '(none)') + '.');
        }
    }

    if (missing.length) {
        notes.push(missing.length + ' key(s) not translated, will show in English: '
            + missing.slice(0, 8).join(', ') + (missing.length > 8 ? ', …' : ''));
    }

    if (problems.length) {
        bad++;
        console.log('✗ ' + file + '  (' + keys.length + ' keys)');
        problems.forEach(p => console.log('    ' + p));
        notes.forEach(n => console.log('    note: ' + n));
    } else {
        console.log('✓ ' + file + '  (' + keys.length + '/' + englishKeys.length + ' keys)');
        notes.forEach(n => console.log('    note: ' + n));
    }
    console.log('');
}

// ── Strings nobody will ever see ────────────────────────────────────────────
//
// A key defined in EN_STRINGS but referenced nowhere else is dead: it renders in no
// interface, and every translator who fills it in has done that work for nothing.
// `switcher.switchProfileSuffix` was exactly this — added with the French translation,
// its only caller removed by the route-poll work three releases later, and translated
// into two languages in between.
//
// Counted as a bare quoted literal rather than as t('key'), because several keys are
// legitimately reached through a table (the parental-rating options are a list of
// [value, key] pairs) and a t()-only scan would call all of those dead too.
{
    const profilesJs = fs.readFileSync(PROFILES_JS, 'utf8');
    const defined = [...profilesJs.matchAll(/^\s{8}'([a-zA-Z0-9_.]+)':/gm)].map(m => m[1]);
    const dead = defined.filter(k => {
        const literal = "'" + k + "'";
        let count = 0, at = 0;
        while ((at = profilesJs.indexOf(literal, at)) !== -1) { count++; at += literal.length; }
        return count < 2;   // the definition itself is the first
    });

    if (dead.length) {
        bad++;
        console.log('✗ EN_STRINGS  (' + dead.length + ' key(s) defined but never used)');
        dead.forEach(k => console.log('    ' + k));
        console.log('    Remove them, or use them — a translator cannot tell the difference.');
        console.log('');
    } else {
        console.log('✓ EN_STRINGS  (every key is referenced)');
        console.log('');
    }
}

// ── Copy that cannot reach t() at all ───────────────────────────────────────
//
// A `content:` string in the stylesheet is user-facing text with no route to a
// translation file: t() cannot reach it, so a Polish household read one English
// sentence in the middle of an otherwise translated form. There was exactly one —
// the tag list's empty state — and it is now `content: attr(data-empty-label)`
// with the label set from t().
//
// This is the check that stops a second one appearing. `content: ''` and
// `content: ""` are decorative pseudo-elements and are fine; anything with a
// character in it is copy.
const stylesPath = path.join(__dirname, '..', 'styles.css');
if (fs.existsSync(stylesPath)) {
    const css = fs.readFileSync(stylesPath, 'utf8');
    const literals = [];
    const re = /content:\s*(['"])((?:(?!\1)[^\\]|\\.)*)\1/g;
    let m;
    while ((m = re.exec(css)) !== null) {
        if (m[2].length) {
            const line = css.slice(0, m.index).split('\n').length;
            literals.push('styles.css:' + line + '  content: ' + m[1] + m[2] + m[1]);
        }
    }
    if (literals.length) {
        bad++;
        console.log('✗ styles.css  (untranslatable copy in content:)');
        literals.forEach(l => console.log('    ' + l));
        console.log('    Use content: attr(data-…) and set the attribute from t().');
        console.log('');
    } else {
        console.log('✓ styles.css  (no untranslatable copy)');
        console.log('');
    }
}

if (bad) {
    console.log(bad + ' file(s) need attention.');
    process.exit(1);
}
console.log('All translations valid.');
