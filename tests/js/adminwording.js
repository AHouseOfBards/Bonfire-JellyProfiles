/*
 * The admin page says "main account" and "main PIN" where it means them, and nothing else.
 *
 * Households, limits, the PIN switch and first-time setup were written by turns, and call the
 * same thing "the account", "accounts", "a PIN" and "the main account PIN". The client script
 * and the API both say main account and main PIN; the dashboard now agrees. Names in the API
 * (masterUserId, requireMainPin) are not words on a screen and are not touched.
 *
 * Enumerated, key by key, in English and in every language file: a count of occurrences would
 * pass with one key fixed and another missed.
 *
 *     node tests/js/adminwording.js "" /path/to/old/profilesDashboard.html
 */
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

const html = L.readDashboard();
const script = L.dashboardScript(html);
const english = {};
(/const ADMIN_EN = \{([\s\S]*?)\n {12}\};/.exec(script) || [0, ''])[1].split('\n').forEach(line => {
    const m = /^\s*'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/.exec(line);
    if (m) english[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\'/g, "'");
});
(html.match(/data-i18n="[^"]+"[^>]*>[^<]*</g) || []).forEach(x => {
    const m = /data-i18n="([^"]+)"[^>]*>([^<]*)</.exec(x);
    if (m) english[m[1]] = m[2].replace(/&amp;/g, '&');
});

// key -> what each language file must say it with. The English says "main account" or
// "main PIN" in all of them; the languages' own words are the ones their files already use.
const TERMS = {
    ca: { account: /compte principal|comptes principals/i, pin: /PIN principal/i },
    es: { account: /cuenta principal|cuentas principales/i, pin: /PIN principal/i },
    fr: { account: /compte principal|comptes principaux/i, pin: /PIN principal/i },
    pl: { account: /kont[oa]? g[łl]ówn|konta g[łl]ówn|kontach g[łl]ówn|konto g[łl]ówne|konta główne|koncie głównym/i, pin: /g[łl]ównego PIN|g[łl]ówny PIN|g[łl]ównego kodu PIN/i },
    tr: { account: /ana hesa[pb]/i, pin: /ana PIN/i }
};
const KEYS = {
    'admin.households.maxProfiles': 'account',
    'admin.households.maxProfilesHint': 'account',
    // admin.orphans.lede left this list in 2.0: it spoke of the main account's profile limit,
    // and now says any account deleted in Dashboard → Users is cleaned up, main or not.
    'admin.security.requireMainPin': 'pin',
    'admin.security.requireMainPinHint': 'account',
    'admin.switcher.lede': 'account',
    'admin.onboarding.enableHint': 'account',
    'admin.onboarding.askBody': 'account',
    'admin.status.openPinsOne': 'account',
    'admin.status.openPinsMany': 'account',
    'admin.gs.pins': 'pin',
    'admin.audit.filter': 'account'
};

console.log('\n── English ─────────────────────────────────────────────────────');
Object.keys(KEYS).forEach(k => {
    const want = KEYS[k] === 'pin' ? /main PIN/i : /main accounts?/i;
    ok(k + ' says ' + (KEYS[k] === 'pin' ? '"main PIN"' : '"main account"'), want.test(english[k] || ''), english[k]);
});
ok('first-time setup says both, once each where it names them', /main PIN/.test(english['admin.onboarding.askBody']) && /main accounts/.test(english['admin.onboarding.askBody']));
ok('no admin string calls it the "master" anything', !Object.keys(english).some(k => /\bmaster\b/i.test(english[k].replace(/\{master\}/g, ''))),
   Object.keys(english).filter(k => /\bmaster\b/i.test(english[k].replace(/\{master\}/g, ''))).join());

console.log('\n── The languages ───────────────────────────────────────────────');
Object.keys(TERMS).forEach(lang => {
    const file = path.join(L.i18nDir(), lang + '.json');
    const json = JSON.parse(fs.readFileSync(file, 'utf8'));
    const bad = Object.keys(KEYS).filter(k => !TERMS[lang][KEYS[k]].test(json[k] || ''));
    ok(lang + ': every one of the ' + Object.keys(KEYS).length + ' uses its main account / main PIN words', bad.length === 0, bad.join(', '));
});

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
