/*
 * American spelling in the docs.
 *
 * The owner's rule is American English in all English text, and the docs had drifted to
 * British (colour, licence, Authorisation, recognise). copylint.js covers the strings the
 * product shows; nothing read the Markdown, so this does. CHANGELOG.md is left out on
 * purpose: published release text is history.
 *
 * It reads prose only. Fenced blocks and `code spans` are skipped, because an identifier
 * the API uses (the JSON field `licence`) is not prose and must keep its spelling. Link
 * targets are skipped for the same reason.
 *
 * "picture" is checked too: the docs say "image" for an avatar or a profile image.
 *
 *   node tests/js/docspelling.js [repo root]
 */
'use strict';
const fs = require('fs');
const path = require('path');

const root = path.resolve(process.argv[2] || path.join(__dirname, '..', '..'));

const BRITISH = new RegExp('\\b(?:colou?r(?:ed|s|ing)?|' +
    'licen[cs]e[sd]?|artefacts?|catalogues?|centred|centres?|theatres?|harbours?|greys?|' +
    'favourites?|behaviours?|neighbours?|honour(?:s|ed)?|' +
    'authoris\\w*|unrecognis\\w*|recognis\\w*|organis\\w*|customis\\w*|initialis\\w*|' +
    'serialis\\w*|summaris\\w*|minimis\\w*|normalis\\w*|synchronis\\w*|utilis\\w*|' +
    'optimis\\w*|prioritis\\w*|analys(?:e|es|ed|ing)|cancell(?:ed|ing|ation)|labell\\w*|' +
    'whilst|amongst|judgement|programmes?|defence|enrol|fulfil)\\b', 'gi');
// The American forms of the words above that happen to match the pattern: colour is
// matched by colou?r only to catch both, so the American "color" is allowed back through.
const AMERICAN = /^(?:colors?|colored|coloring|license[sd]?)$/i;
const PICTURE = /\bpictures?\b/gi;

function walk(dir, out) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) walk(p, out); else out.push(p);
    }
    return out;
}

const files = [];
['README.md', 'CONTRIBUTING.md', 'SECURITY.md'].forEach(function (f) {
    if (fs.existsSync(path.join(root, f))) files.push(path.join(root, f));
});
['docs', '.github'].forEach(function (d) {
    const p = path.join(root, d);
    if (fs.existsSync(p)) walk(p, []).forEach(function (f) {
        if (/\.(md|ya?ml)$/i.test(f)) files.push(f);
    });
});

// A line's prose: no code spans, no link targets, no URLs. In a workflow file only comments
// and the ::error:: messages are prose; the rest is configuration.
function prose(line, yml) {
    if (yml && !/^\s*#/.test(line) && !/echo "::error::/.test(line)) return '';
    return line.split('`').filter(function (_, i) { return i % 2 === 0; }).join(' ')
        .replace(/\]\([^)]*\)/g, ']').replace(/https?:\/\/\S+/g, ' ');
}

let pass = 0, fail = 0;
const bad = [];
files.forEach(function (file) {
    const yml = /\.ya?ml$/i.test(file);
    let fence = false;
    fs.readFileSync(file, 'utf8').split(/\r?\n/).forEach(function (line, i) {
        if (/^\s*```/.test(line)) { fence = !fence; return; }
        if (fence) return;
        const text = prose(line, yml);
        const hits = [];
        (text.match(BRITISH) || []).forEach(function (w) { if (!AMERICAN.test(w)) hits.push(w); });
        (text.match(PICTURE) || []).forEach(function (w) { hits.push(w); });
        if (hits.length) {
            fail++;
            bad.push(path.relative(root, file) + ':' + (i + 1) + '  ' + hits.join(', '));
        } else pass++;
    });
});

// The detector must be able to go red: prove it on lines it has to catch and lines it has to leave.
const CATCH = ['Pick a colour', 'Authorisation: anonymous', 'Each licence text', 'a profile picture',
    'unrecognised device', 'a harbour at dusk', 'Blue-grey buttons'];
const LEAVE = ['Pick a color', 'Authorization: anonymous', 'Each license text', 'a profile image',
    'the JSON field `licence`', 'see [x](#uploading-a-picture)'];
CATCH.forEach(function (s) {
    const t = prose(s, false);
    const hit = (t.match(BRITISH) || []).some(function (w) { return !AMERICAN.test(w); }) || PICTURE.test(t);
    PICTURE.lastIndex = 0;
    if (hit) pass++; else { fail++; console.log('FAIL detector missed: ' + s); }
});
LEAVE.forEach(function (s) {
    const t = prose(s, false);
    const hit = (t.match(BRITISH) || []).some(function (w) { return !AMERICAN.test(w); }) || PICTURE.test(t);
    PICTURE.lastIndex = 0;
    if (!hit) pass++; else { fail++; console.log('FAIL detector flagged: ' + s); }
});

bad.forEach(function (b) { console.log('FAIL ' + b); });
console.log(fail ? fail + ' failed' : 'ok: ' + files.length + ' docs read');
console.log('(' + pass + ' passed, ' + fail + ' failed)');
process.exit(fail ? 1 : 0);
