// A refused request is said in the person's language, by the code the server sends, and
// the server's English body is never shown (P2: errors say what to do).
//
// Every error from a Bonfire route carries a stable code in X-Bonfire-Error. The client
// used to show the response body, about twenty-five times over: English, written for a
// developer, and untranslated on a television in Spanish. This reads Controllers/ErrorCodes.cs
// for the codes that exist and holds the two pages to them, then drives the shared plumbing
// with an English body that must never appear.
//
//   node tests/js/refusals.js [path/to/profiles.js [path/to/profilesDashboard.html]]
//
// Red against 1.7.5 (no ERROR_KEYS, every site shows res.text()).

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();
const DASH = L.readDashboard();
const ROOT = L.ROOT;
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

// ── Every code the server can send ──────────────────────────────────────────
console.log('\n-- Every code has a sentence ----------------------------------');
const cs = fs.readFileSync(path.join(ROOT, 'Controllers', 'ErrorCodes.cs'), 'utf8');
const codes = new Set();
let m;
const rule = /\(\s*"(?:[^"\\]|\\.)*",\s*"([a-z_]+)"\s*\)/g;
while ((m = rule.exec(cs)) !== null) codes.add(m[1]);
const forStart = cs.indexOf('public static string For(');
const forBody = cs.slice(forStart, cs.indexOf('/// <summary>', forStart));
const lit = /"([a-z_]+)"/g;
while ((m = lit.exec(forBody)) !== null) codes.add(m[1]);
// Codes a route sets itself, because its message is not matchable.
for (const f of fs.readdirSync(path.join(ROOT, 'Controllers')).filter(f => f.endsWith('.cs'))) {
    const src = fs.readFileSync(path.join(ROOT, 'Controllers', f), 'utf8');
    const own = /Headers\[ErrorCodes\.Header\]\s*=\s*"([a-z_]+)"/g;
    while ((m = own.exec(src)) !== null) codes.add(m[1]);
}
ok('the codes were read from the server (' + codes.size + ')', codes.size > 60);

// Not shown by a sentence, on purpose.
const EXEMPT = {
    invalid_request: 'the code of every refusal nothing else matched; the action\'s own fallback names what failed',
    maintenance: 'the administrator\'s own words, shown by the maintenance screen'
};

function mapOf(src, from) {
    const start = src.indexOf('const ERROR_KEYS = {', from || 0);
    if (start === -1) return null;
    const rest = src.slice(start);
    const closer = /\n\s*\};/.exec(rest);
    const stop = start + (closer ? closer.index : rest.length);
    const body = src.slice(start, stop);
    const out = {};
    const pair = /([a-z_]+):\s*'([A-Za-z.]+)'/g;
    let p;
    while ((p = pair.exec(body)) !== null) out[p[1]] = p[2];
    return out;
}
const clientMap = mapOf(SRC);
const dashMap = mapOf(DASH);
ok('profiles.js has a code-to-sentence map', !!clientMap && Object.keys(clientMap).length > 40);
ok('the administrator page has one', !!dashMap && Object.keys(dashMap).length > 20);

// English: EN_STRINGS in the client, ADMIN_EN in the page.
const enStart = SRC.indexOf('const EN_STRINGS = {');
const enBody = SRC.slice(enStart, SRC.indexOf('\n    };', enStart));
const en = {};
const kv = /'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/g;
while ((m = kv.exec(enBody)) !== null) en[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\(['"\\])/g, '$1');
const adminStart = DASH.indexOf('const ADMIN_EN = {');
const adminBody = DASH.slice(adminStart, DASH.indexOf('\n            };', adminStart));
const adminEn = {};
while ((m = kv.exec(adminBody)) !== null) adminEn[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\(['"\\])/g, '$1');
// Markup holds the rest of the dashboard's English; these keys are all in the script.

const missing = [];
for (const code of [...codes].sort()) {
    if (EXEMPT[code]) continue;
    const ck = clientMap && clientMap[code];
    const dk = dashMap && dashMap[code];
    if (!ck && !dk) { missing.push(code); continue; }
}
ok('every code but the exempt ones has a sentence on one page or the other', missing.length === 0, 'no sentence: ' + missing.join(', '));
for (const code of Object.keys(EXEMPT)) {
    ok(code + ' is exempt for a stated reason, and is not also mapped', !(clientMap && clientMap[code]) && !(dashMap && dashMap[code]));
}
const stale = [...Object.keys(clientMap || {}), ...Object.keys(dashMap || {})].filter(c => !codes.has(c));
ok('and no map names a code the server never sends', stale.length === 0, stale.join(', '));

// The client's own codes: the household pages reach all but the ones only the
// administrator's page can (themes, fonts, the logo, announcements, maintenance, mail settings).
const ADMIN_ONLY = /^(theme_|font_|logo_|announcement_|maintenance_invalid|email_settings_invalid)/;
const hole = [...codes].filter(c => !EXEMPT[c] && !ADMIN_ONLY.test(c) && !(clientMap && clientMap[c]) && !['image_invalid'].includes(c));
ok('a household screen has a sentence for every code it can meet', hole.length === 0, hole.join(', '));

function checkCopy(label, key, text) {
    const sentences = (text.match(/[.!?](\s|$)/g) || []).length;
    const probs = [];
    if (!text) probs.push('no English');
    if (sentences > 2) probs.push('more than two sentences');
    if (!/[.!?]$/.test(text)) probs.push('no full stop');
    if (/_|\bpicture\b|\bmaster\b/i.test(text)) probs.push('raw code or a banned word');
    if (text.length > 130) probs.push('long (' + text.length + ')');
    return probs.length ? label + ' ' + key + ': ' + probs.join(', ') : null;
}
const copy = [];
for (const k of new Set(Object.values(clientMap || {}))) { const p = checkCopy('client', k, en[k] || ''); if (p) copy.push(p); }
for (const k of new Set(Object.values(dashMap || {}))) {
    const p = checkCopy('page', k, adminEn[k] || ''); if (p) copy.push(p);
}
ok('each sentence exists, is one or two short sentences, with no raw code', copy.length === 0, copy.join(' | '));

// ── Nothing reads a body to show it ─────────────────────────────────────────
console.log('\n-- No response body reaches a screen --------------------------');
// Enumerated: every .text() call in the client, with the line it is on. The two that
// remain read a body for something other than showing it.
const lines = SRC.split('\n');
const textCalls = [];
lines.forEach((ln, i) => { if (/\.text\(\)/.test(ln)) textCalls.push({ n: i + 1, ln: ln.trim() }); });
const readsToShow = textCalls.filter(c => /res\.text\(\)\.then\(\s*(text|body)\s*=>\s*\{\s*(throw|fail|say)/.test(c.ln)
    || /res\.text\(\)\.then\(text => \{ throw/.test(c.ln)
    || /res\.text\(\)\.then\(body => \{ throw/.test(c.ln));
ok('no site throws, says or fails with the response body (' + readsToShow.length + ' do)', readsToShow.length === 0,
    readsToShow.map(c => 'line ' + c.n).join(', '));
ok('client .text() calls left: only the reply parse and the 401 test (' + textCalls.length + ')', textCalls.length <= 2,
    textCalls.map(c => 'line ' + c.n).join(', '));
const dashText = DASH.split('\n').map((ln, i) => ({ n: i + 1, ln: ln })).filter(c => /\.text\(\)/.test(c.ln));
ok('the administrator page reads a body only for a theme\'s list of problems (' + dashText.length + ')', dashText.length <= 1,
    dashText.map(c => 'line ' + c.n).join(', '));

// ── The shared plumbing, with an English body that must never appear ───────
console.log('\n-- What the person is told ------------------------------------');
const env = D.loadPlugin(SRC);
const { PP, setRespond, settle } = env;
const BODY = 'Invalid PIN code. SERVER ENGLISH';
const sayings = {};
async function attempt(label, status, headers, call) {
    setRespond(() => ({ status: status, body: BODY, headers: headers }));
    let shown = null;
    try { await call(); shown = '(no error)'; } catch (e) { shown = e && e.message; }
    await settle();
    sayings[label] = shown;
    return shown;
}
PP.readMasterState = () => ({ masterUserId: 'main', token: 'main-token' });
PP.handleSessionExpired = () => {};

(async () => {
    const limits = () => PP._limitsCall('kid', 'x', { a: 1 });
    const known = await attempt('limits known', 400, { 'X-Bonfire-Error': 'limits_invalid' }, limits);
    ok('a known code is said by its sentence', typeof known === 'string' && /viewing limits/.test(known), known);
    const unknown = await attempt('limits unknown', 400, { 'X-Bonfire-Error': 'brand_new_code' }, limits);
    ok('an unknown code falls back to the action\'s own words', unknown === "Couldn't save that.", unknown);
    const bare = await attempt('limits none', 400, null, limits);
    ok('an old server with no code falls back too', bare === "Couldn't save that.", bare);
    const generic = await attempt('limits generic', 400, { 'X-Bonfire-Error': 'invalid_request' }, limits);
    ok('so does invalid_request', generic === "Couldn't save that.", generic);
    ok('none of them shows the body', !Object.values(sayings).some(s => /SERVER ENGLISH|Invalid PIN code/.test(String(s))), JSON.stringify(sayings));

    const email = () => PP._emailCall('account/email', { address: 'a@b.c' });
    const e1 = await attempt('email known', 400, { 'X-Bonfire-Error': 'email_invalid' }, email);
    ok('the email calls too', e1 === 'Enter a valid email address.', e1);
    const e2 = await attempt('email none', 400, null, email);
    ok('and fall back to the generic error without one', e2 === 'Error' && !/SERVER ENGLISH/.test(e2), e2);

    // The switch: the PIN screen's own line.
    PP.setSwitchBusy = () => {};
    PP.interceptHomeAndShowProfiles = () => {};
    PP.showAlert = () => {};
    const switched = (headers) => new Promise(resolve => {
        setRespond(() => ({ status: 400, body: BODY, headers: headers }));
        PP._switchLock = false;
        PP.executeProfileSwitch('kid', '1234', (text) => resolve(text));
    });
    const s1 = await switched({ 'X-Bonfire-Error': 'pin_wrong' });
    ok('a wrong PIN on a switch is said by its sentence', s1 === 'Wrong PIN. Try again.', s1);
    // That sentence is now the PIN screen's own line too, so it cannot show the code was read.
    // A code whose sentence no fallback has does.
    const s1b = await switched({ 'X-Bonfire-Error': 'too_many_sessions' });
    ok('and a switch refused for too many screens says so, by its code', s1b === 'This profile is open on too many screens. Close one and try again.', s1b);
    const s2 = await switched(null);
    ok('and without a code is the PIN screen\'s own line, not the body', s2 === 'Wrong PIN. Try again.' || (/PIN/.test(s2) && !/SERVER ENGLISH/.test(s2)), s2);

    // Translated: the sentence follows the language.
    const fr = JSON.parse(fs.readFileSync(path.join(ROOT, 'Web', 'i18n', 'fr.json'), 'utf8'));
    ok('and French has the sentence the code points to', !!fr['refused.pinWrong'] && fr['refused.pinWrong'] !== "That PIN isn't right. Try again.");

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
