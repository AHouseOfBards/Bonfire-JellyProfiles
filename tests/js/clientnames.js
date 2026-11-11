/*
 * The client screens use one name per thing: Theme (not Look), Avatar (not picture or photo),
 * US spelling, sentence case, Viewing limits, Rating limit / No limit, Libraries, Switch button
 * with its two choices, "the server's network", "the profile screen", and "Host a Bonfire" /
 * "Join a Bonfire" in the same words wherever they appear.
 *
 * tests/js/clientnames.json is the ledger: each key that changed, with the English and the five
 * languages' wording. Every entry is compared key by key, in EN_STRINGS and in each language
 * file. A word scan covers the rest of EN_STRINGS, so a screen added later cannot bring an old
 * name back; its exceptions are named, with the reason.
 *
 * Messages and errors (errors.*, refused.*) are a separate pass and are skipped by the scan.
 *
 *     node tests/js/clientnames.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const src = L.readProfiles();
const english = {};
const start = src.indexOf('const EN_STRINGS = {');
const end = src.indexOf('\n    };', start);
src.slice(start, end).split(/\r?\n/).forEach(line => {
    const m = /^\s*'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")\s*,?\s*$/.exec(line);
    if (m) english[m[1]] = (m[2] !== undefined ? m[2].replace(/\\'/g, "'") : m[3].replace(/\\"/g, '"'));
});
ok('EN_STRINGS was read', Object.keys(english).length > 500, Object.keys(english).length + ' keys');

const ledger = JSON.parse(fs.readFileSync(path.join(__dirname, 'clientnames.json'), 'utf8'));
const LANGS = ['ca', 'es', 'fr', 'pl', 'tr'];
const files = {};
LANGS.forEach(l => { files[l] = JSON.parse(fs.readFileSync(path.join(L.i18nDir(), l + '.json'), 'utf8')); });

Object.keys(ledger).forEach(key => {
    const want = ledger[key];
    if (want.en !== undefined) ok('en ' + key, english[key] === want.en, JSON.stringify(english[key]));
    LANGS.forEach(l => {
        if (want[l] !== undefined) ok(l + ' ' + key, files[l][key] === want[l], JSON.stringify(files[l][key]));
    });
});

// Word scan over every client string that is not an error or a refusal.
const banned = [
    [/\bpictures?\b/i, 'avatar, not picture'],
    [/\bphotos?\b/i, 'avatar, not photo'],
    [/colour/i, 'US spelling: color'],
    [/\bviewing hours\b/i, 'Viewing limits'],
    [/\bLook\b/, 'Theme'],
    [/\bhome network\b/i, "the server's network"],
    [/\bEnabled Libraries\b/, 'Libraries'],
    [/\bSwitcher Style\b/, 'Switch button'],
];
// A picture here is artwork on a library tile, not a profile's avatar.
const exceptions = { 'profileForm.artworkPicture': 1, 'profileForm.artworkExplainer': 1 };
Object.keys(english).forEach(key => {
    if (/^(errors|refused)\./.test(key) || exceptions[key]) return;
    banned.forEach(b => ok('scan ' + key + ' (' + b[1] + ')', !b[0].test(english[key]), english[key]));
});

// Sentence case: titles and labels in the ledger do not capitalise a second ordinary word.
Object.keys(ledger).forEach(key => {
    const v = ledger[key].en;
    if (v === undefined || !/(Title|title|Label)$|^gate\.|^switcher\.title$/.test(key)) return;
    ok('sentence case ' + key, !/^\S+ (?:\S+ )*[A-Z][a-z]/.test(v.replace(/Bonfire|Jellyfin|PIN/g, 'x')), v);
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
