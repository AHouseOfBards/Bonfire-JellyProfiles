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

// ── The settings page's English ─────────────────────────────────────────────
// The administrator's page keeps its English in two places, one per kind of text, and
// a key may live in only one of them:
//
//   - the markup: an element marked data-i18n="admin.…" holds its own English, as text,
//     or as HTML when it has tags inside (that is how the page applies a translation
//     too); data-i18n-placeholder and data-i18n-aria-label name an attribute's English;
//   - ADMIN_EN in the page's script, for what the script writes.
const DASHBOARD = path.join(HERE, '..', 'profilesDashboard.html');

function decodeEntities(s) {
    return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"')
        .replace(/&#0?39;/g, "'").replace(/&nbsp;/g, '\u00a0').replace(/&amp;/g, '&');
}

const VOID_TAGS = new Set(['input', 'br', 'img', 'hr', 'meta', 'link', 'source', 'wbr']);

/// The markup before the script, as a flat list of tags and text. Enough of a parser for
/// a page this file's own authors write; not a general one.
function tokenizeMarkup(html) {
    const end = html.indexOf('<script');
    const markup = end === -1 ? html : html.slice(0, end);
    const toks = [];
    const re = /<!--[\s\S]*?-->|<(\/?)([a-zA-Z][a-zA-Z0-9-]*)((?:[^>"']|"[^"]*"|'[^']*')*)>|[^<]+/g;
    let m;
    while ((m = re.exec(markup)) !== null) {
        if (m[0].startsWith('<!--')) continue;
        if (m[2]) toks.push({ tag: m[2].toLowerCase(), close: m[1] === '/', attrs: m[3] || '', raw: m[0], at: m.index });
        else toks.push({ text: m[0], at: m.index });
    }
    return { markup, toks };
}

function attr(attrs, name) {
    const m = new RegExp('(?:^|\\s)' + name + '="([^"]*)"').exec(attrs);
    return m ? m[1] : null;
}

/// key -> English for every data-i18n element and attribute in the markup. A key used on
/// several elements must say the same thing on all of them.
function readAdminMarkup(html, problems) {
    const { markup, toks } = tokenizeMarkup(html);
    const out = {};
    const put = (key, value, where) => {
        if (key in out && out[key] !== value) {
            problems.push(key + ': two different English strings in the markup (' + where + ').');
        }
        out[key] = value;
    };
    for (let i = 0; i < toks.length; i++) {
        const tk = toks[i];
        if (tk.text !== undefined || tk.close) continue;
        ['placeholder', 'aria-label'].forEach(name => {
            const key = attr(tk.attrs, 'data-i18n-' + name);
            if (key) put(key, decodeEntities(attr(tk.attrs, name) || ''), name);
        });
        const key = attr(tk.attrs, 'data-i18n');
        if (!key) continue;
        if (VOID_TAGS.has(tk.tag)) { problems.push(key + ': data-i18n on an empty <' + tk.tag + '>.'); continue; }
        // Walk to the matching close tag, counting nested tags of any kind.
        let depth = 0, j = i + 1, hasTags = false;
        for (; j < toks.length; j++) {
            const x = toks[j];
            if (x.text !== undefined) continue;
            if (VOID_TAGS.has(x.tag) || x.raw.endsWith('/>')) { hasTags = true; continue; }
            if (!x.close) { depth++; hasTags = true; continue; }
            if (depth === 0) break;
            depth--;
        }
        if (j >= toks.length) { problems.push(key + ': its element is never closed.'); continue; }
        const inner = markup.slice(tk.at + tk.raw.length, toks[j].at);
        const value = hasTags ? inner.replace(/\s+/g, ' ').trim() : decodeEntities(inner).replace(/\s+/g, ' ').trim();
        put(key, value, '<' + tk.tag + '>');
    }
    return out;
}

/// ADMIN_EN out of the page's script, read the same way EN_STRINGS is.
function readAdminScript(html) {
    const start = html.indexOf('const ADMIN_EN = {');
    if (start === -1) return {};
    const end = html.indexOf('\n            };', start);
    if (end === -1) fail('Could not find the end of ADMIN_EN in profilesDashboard.html.');
    const body = html.slice(start, end);
    const out = {};
    const re = /'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/g;
    let m;
    while ((m = re.exec(body)) !== null) {
        const raw = m[2] !== undefined ? m[2] : m[3];
        out[m[1]] = raw.replace(/\\(['"\\])/g, '$1');
    }
    return out;
}

/// Both halves, checked against each other. Problems go in `problems`.
function readAdminEnglish(problems) {
    if (!fs.existsSync(DASHBOARD)) return {};
    const html = fs.readFileSync(DASHBOARD, 'utf8');
    const markup = readAdminMarkup(html, problems);
    const script = readAdminScript(html);
    for (const k of Object.keys(script)) {
        if (k in markup) problems.push(k + ': in both the markup and ADMIN_EN. Keep it in one.');
    }
    for (const k of Object.keys(markup).concat(Object.keys(script))) {
        if (!k.startsWith('admin.')) problems.push(k + ': the settings page\'s keys start with admin.');
    }
    return Object.assign({}, markup, script);
}

// ── The mail's English ──────────────────────────────────────────────────────
// Bonfire's emails are written on the server, so their English lives in
// Mail/EmailText.cs, one ["email.…"] = "…" line per key. So do the messages it sends to
// a profile's apps, as notice.* keys. The server never sends either to a browser.
const MAIL_TEXT = path.join(HERE, '..', '..', 'Mail', 'EmailText.cs');
const REPO = path.join(HERE, '..', '..');

function readMailEnglish(problems) {
    if (!fs.existsSync(MAIL_TEXT)) return {};
    const src = fs.readFileSync(MAIL_TEXT, 'utf8');
    const out = {};
    const re = /^[ \t]*\["([^"]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"/gm;
    let m;
    while ((m = re.exec(src)) !== null) {
        if (m[1] in out) problems.push(m[1] + ': defined twice in EmailText.cs.');
        out[m[1]] = m[2].replace(/\\n/g, '\n').replace(/\\(["\\])/g, '$1');
    }
    if (Object.keys(out).length === 0) fail('Found no ["email.…"] lines in Mail/EmailText.cs. Has the format changed?');
    for (const k of Object.keys(out)) {
        if (!k.startsWith('email.') && !k.startsWith('notice.')) problems.push(k + ': server keys start with email. or notice.');
    }
    return out;
}

/// Every .cs file outside bin, obj and tests, for the "is it used" check.
function csSources(dir, acc) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        if (e.isDirectory()) {
            if (/^(bin|obj|tests|node_modules|\..*)$/.test(e.name)) continue;
            csSources(path.join(dir, e.name), acc);
        } else if (e.name.endsWith('.cs')) {
            acc.push(path.join(dir, e.name));
        }
    }
    return acc;
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

const appEnglish = readEnglish();
const adminProblems = [];
const adminEnglish = readAdminEnglish(adminProblems);
for (const k of Object.keys(appEnglish)) {
    if (k.startsWith('admin.')) adminProblems.push(k + ': EN_STRINGS cannot use admin.; the server serves those keys to the settings page only.');
}
const mailProblems = [];
const mailEnglish = readMailEnglish(mailProblems);
for (const k of Object.keys(appEnglish).concat(Object.keys(adminEnglish))) {
    if (k.startsWith('email.') || k.startsWith('notice.')) mailProblems.push(k + ': email. and notice. keys belong in Mail/EmailText.cs; the server keeps them from browsers.');
}
const english = Object.assign({}, appEnglish, adminEnglish, mailEnglish);
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

console.log('English catalogue: ' + Object.keys(appEnglish).length + ' keys for households, '
    + Object.keys(adminEnglish).length + ' for the settings page, '
    + Object.keys(mailEnglish).length + ' for email\n');

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

// ── The settings page's own catalogue ───────────────────────────────────────
//
// The same two questions as above, asked of the settings page: is every ADMIN_EN string
// used, and does every t('admin.…') the page's script makes name a string that exists?
// A typo in the second shows the administrator the key itself.
if (fs.existsSync(DASHBOARD)) {
    const html = fs.readFileSync(DASHBOARD, 'utf8');
    const scriptStart = html.indexOf('<script');
    const script = scriptStart === -1 ? '' : html.slice(scriptStart);
    const scriptKeys = readAdminScript(html);
    const problems = adminProblems.slice();

    Object.keys(scriptKeys).forEach(k => {
        if (script.split("'" + k + "'").length - 1 < 2) problems.push(k + ': in ADMIN_EN but never used.');
    });
    const called = [...script.matchAll(/\bt\('(admin\.[a-zA-Z0-9_.]+)'/g)].map(m => m[1]);
    [...new Set(called)].forEach(k => {
        if (!(k in scriptKeys)) problems.push(k + ': the script asks for it, but ADMIN_EN does not have it.');
    });

    if (problems.length) {
        bad++;
        console.log('✗ profilesDashboard.html  (' + problems.length + ' problem(s))');
        problems.forEach(p => console.log('    ' + p));
        console.log('');
    } else {
        console.log('✓ profilesDashboard.html  (every settings-page key is defined once and used)');
        console.log('');
    }
}

// ── The mail's catalogue ────────────────────────────────────────────────────
//
// Each email key must be named, as a literal, somewhere in the server's code other than
// its definition. A key built from parts would read as unused here; write it out.
if (Object.keys(mailEnglish).length) {
    const problems = mailProblems.slice();
    const others = csSources(REPO, []).filter(f => path.resolve(f) !== path.resolve(MAIL_TEXT))
        .map(f => fs.readFileSync(f, 'utf8')).join('\n');
    Object.keys(mailEnglish).forEach(k => {
        if (others.indexOf('"' + k + '"') === -1) problems.push(k + ': in EmailText.cs but never used.');
    });
    if (problems.length) {
        bad++;
        console.log('✗ Mail/EmailText.cs  (' + problems.length + ' problem(s))');
        problems.forEach(p => console.log('    ' + p));
        console.log('');
    } else {
        console.log('✓ Mail/EmailText.cs  (every email key is defined once and used)');
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
