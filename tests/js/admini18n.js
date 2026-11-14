/*
 * The settings page speaks the administrator's language.
 *
 * Up to 1.7.2 the page was English only. It is translated now, like the household screens,
 * and this checks four things:
 *
 *   1. every word of the markup is under a data-i18n key (or marked translate="no", or is a
 *      command inside <code>), so nothing is left in English;
 *   2. the script writes no English of its own outside t(); the bug report, which Bonfire's
 *      maintainers read, is the one deliberate exception;
 *   3. the page applies a translation to its markup and can go back to English, and text goes
 *      in as text: only a string whose English holds tags goes in as HTML;
 *   4. it asks the server for the right file: the admin half only, longest tag first, nothing
 *      for English, and it survives a failed fetch.
 *
 *   node tests/js/admini18n.js [path/to/profilesDashboard.html]
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name + (detail ? '  — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}
function finish() {
    console.log('');
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
    process.exit(0);
}

const html = fs.readFileSync(process.argv[2] || L.dashboardPath(), 'utf8');
const script = L.dashboardScript(html);

// ── 1. The markup ─────────────────────────────────────────────────────────────
console.log('\n── Every word of the markup has a key ─────────────────────────');
{
    const markup = html.slice(0, html.indexOf('<script'));
    const VOID = new Set(['input', 'br', 'img', 'hr', 'meta', 'link', 'source', 'wbr']);
    const re = /<!--[\s\S]*?-->|<(\/?)([a-zA-Z][a-zA-Z0-9-]*)((?:[^>"']|"[^"]*"|'[^']*')*)>|[^<]+/g;
    const stack = [];
    const bare = [];
    const keys = [];
    let m;
    while ((m = re.exec(markup)) !== null) {
        if (m[0].startsWith('<!--')) continue;
        if (!m[2]) {
            const text = m[0].replace(/\s+/g, ' ').trim();
            // Glyphs, counts and punctuation carry no language.
            if (!/[A-Za-z]/.test(text)) continue;
            const exempt = stack.some(e => /data-i18n="/.test(e.attrs) || /translate="no"/.test(e.attrs)
                || e.tag === 'code' || e.tag === 'style');
            if (!exempt) bare.push(markup.slice(0, m.index).split('\n').length + ': ' + text.slice(0, 60));
            continue;
        }
        const tag = m[2].toLowerCase();
        const attrs = m[3] || '';
        (attrs.match(/data-i18n(?:-[a-z-]+)?="([^"]+)"/g) || []).forEach(a => keys.push(a.replace(/^[^"]+"|"$/g, '')));
        if (m[1]) {
            for (let i = stack.length - 1; i >= 0; i--) if (stack[i].tag === tag) { stack.length = i; break; }
        } else if (!VOID.has(tag) && !attrs.trim().endsWith('/')) {
            stack.push({ tag, attrs });
        }
        // An attribute a reader sees needs a key beside it, unless it is not language.
        ['placeholder', 'aria-label', 'title', 'alt'].forEach(name => {
            const v = new RegExp('(?:^|\\s)' + name + '="([^"]*)"').exec(attrs);
            if (v && /[A-Za-z]{2}/.test(v[1]) && !/translate="no"/.test(attrs)
                && !new RegExp('data-i18n-' + name + '="').test(attrs)) {
                bare.push(markup.slice(0, m.index).split('\n').length + ': ' + name + '="' + v[1] + '"');
            }
        });
    }
    ok('no visible English is left without a key', bare.length === 0,
       bare.length + ' found, first: ' + bare.slice(0, 4).join(' | '));
    ok('the markup carries keys at all', keys.length > 100, String(keys.length));
    const stray = keys.filter(k => k.indexOf('admin.') !== 0);
    ok('every markup key is an admin.* key, so the server routes it to this page', stray.length === 0, stray.join(', '));
}

// ── 2. The script ─────────────────────────────────────────────────────────────
console.log('\n── The script writes no English of its own ────────────────────');
{
    let body = script;
    // The catalogue itself is English by definition.
    const cat = body.indexOf('const ADMIN_EN = {');
    if (cat !== -1) body = body.slice(0, cat) + body.slice(body.indexOf('\n            };', cat));
    // The bug report is for Bonfire's maintainers and stays English on purpose.
    try { body = body.replace(L.extractFunction(body, 'buildReport'), ''); } catch (e) { /* not there */ }
    body = body.replace(/\/\*[\s\S]*?\*\//g, '')
        .split('\n').filter(l => !/^\s*\/\//.test(l)).map(l => l.replace(/\s\/\/.*$/, '')).join('\n')
        .replace(/console\.(?:log|warn|error)\([^;]*;/g, '');

    // What a reader would see of each literal: tags and attributes taken out.
    // Not shown to anyone: a client name and a header sent to the server, key names a
    // keydown handler compares, and field names read off a server response or a file.
    // Named one by one, so a button labelled "Edit" is still caught.
    const ALLOWED = ['Jellyfin Web', 'Authorization', 'Home', 'End', 'Dashboard',
                     'Timestamp', 'Client', 'Mappings', 'Avatar'];
    const english = [];
    const lit = /'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"/g;
    let m;
    while ((m = lit.exec(body)) !== null) {
        const raw = m[1] !== undefined ? m[1] : m[2];
        if (/^admin\.[a-zA-Z0-9_.]+$/.test(raw) || ALLOWED.indexOf(raw) !== -1) continue;
        const seen = raw.replace(/<[^>]*>?/g, ' ').replace(/[\w-]+="[^"]*"?/g, ' ').replace(/[\w-]+:[^;]*;/g, ' ');
        // A capitalised word followed by another word, or a lone capitalised word that
        // ends a sentence, is copy. Class lists, ids and CSS are lower-case or hyphenated.
        if (/\b[A-Z][a-z]+ [a-z]{2,}/.test(seen) || /^\s*[A-Z][a-z]+[.!?…]?\s*$/.test(seen)
            || /\b(the|could|not|is|are|of|to)\b [a-z]/.test(seen)) {
            english.push(raw.slice(0, 60));
        }
    }
    ok('every string the script shows goes through t()', english.length === 0,
       english.length + ' found, first: ' + english.slice(0, 20).join(' | '));
    ok('the report stays English, for whoever reads the issue',
       /'Client script: '/.test(script) && script.indexOf("'Bonfire ' + (page.dataset.pluginVersion") !== -1);
}

// ── 3. Applying a translation ─────────────────────────────────────────────────
console.log('\n── The markup takes a translation and gives it back ───────────');
let core = null, apply = null, load = null;
try {
    core = L.dashboardI18n(script);
    if (!core) throw new Error('no ADMIN_EN');
    const fns = ['adminLanguage', 'loadAdminStrings', 'applyMarkupStrings'].map(n => L.extractFunction(script, n)).join('\n');
    const factory = new Function('document', 'localStorage', 'navigator', 'ApiClient', 'fetch',
        core + fns + '\nreturn { apply: applyMarkupStrings, load: loadAdminStrings, t: t,'
        + ' set: function (s) { adminStrings = s; }, get: function () { return adminStrings; } };');
    apply = factory;
} catch (e) {
    ok('the translation code is there to test', false, e.message);
    finish();
}

function el(attrs, text, children) {
    const e = {
        attrs: Object.assign({}, attrs), _text: text || '', _html: null,
        children: children || [], writes: [],
        getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; },
        hasAttribute(k) { return k in this.attrs; },
        setAttribute(k, v) { this.attrs[k] = String(v); },
        get textContent() { return this._text; },
        set textContent(v) { this._text = v; this._html = null; this.children = []; this.writes.push('text'); },
        get innerHTML() { return this._html !== null ? this._html : this._text; },
        set innerHTML(v) { this._html = v; this.writes.push('html'); }
    };
    return e;
}
{
    const plain = el({ 'data-i18n': 'admin.tab.status' }, 'Status');
    const rich = el({ 'data-i18n': 'admin.banner.howToFix' }, '', [{}]);
    rich._html = '<strong>How to fix:</strong> run it.';
    const input = el({ 'data-i18n-placeholder': 'admin.audit.filterPlaceholder', placeholder: 'Any name' });
    const tabs = el({ 'data-i18n-aria-label': 'admin.tab.label', 'aria-label': 'Bonfire settings sections' });
    const page = {
        querySelectorAll(sel) {
            if (sel === '[data-i18n]') return [plain, rich];
            if (sel === '[data-i18n-placeholder]') return [input];
            if (sel === '[data-i18n-aria-label]') return [tabs];
            return [];
        }
    };
    const api = apply({}, {}, {}, {}, () => Promise.reject(new Error('no fetch here')));

    api.set({ 'admin.tab.status': 'Statut <b>', 'admin.banner.howToFix': '<strong>Solution :</strong> lancez-la.',
              'admin.audit.filterPlaceholder': 'Tout nom', 'admin.tab.label': 'Sections' });
    api.apply(page);
    ok('a plain string goes in as text, so a < in it stays a character',
       plain.textContent === 'Statut <b>' && plain.writes[plain.writes.length - 1] === 'text');
    ok('a string whose English has tags goes in as HTML',
       rich._html === '<strong>Solution :</strong> lancez-la.');
    ok('placeholders are translated', input.attrs.placeholder === 'Tout nom');
    ok('and the tab strip\'s accessible name', tabs.attrs['aria-label'] === 'Sections');

    const before = rich.writes.length;
    api.apply(page);
    ok('applying the same language again leaves HTML alone, so filled-in values survive',
       rich.writes.length === before);

    api.set(null);
    api.apply(page);
    ok('back to English: the text', plain.textContent === 'Status');
    ok('back to English: the HTML', rich._html === '<strong>How to fix:</strong> run it.');
    ok('back to English: the attributes', input.attrs.placeholder === 'Any name'
       && tabs.attrs['aria-label'] === 'Bonfire settings sections');

    api.set({ 'admin.ann.none': 'Aucune.' });
    ok('t() uses the translation', api.t('admin.ann.none') === 'Aucune.');
    ok('and falls back to English per key', api.t('admin.ann.edit') === 'Edit');
    ok('and fills placeholders', api.t('admin.audit.showing', { shown: 2, total: 9 }) === 'Showing 2 of 9 switches.');
}

// ── 4. Fetching it ────────────────────────────────────────────────────────────
console.log('\n── It asks for the right file, and only the admin half ────────');
async function run(language, answer) {
    const asked = [];
    const doc = { documentElement: { getAttribute: () => '' } };
    const storage = { getItem: k => (k === 'u1-language' ? language : null) };
    const ApiClient = {
        getCurrentUserId: () => 'u1',
        getUrl: (p, q) => '/' + p + (q ? '?' + Object.keys(q).map(k => k + '=' + q[k]).join('&') : '')
    };
    const fetch = url => { asked.push(url); return answer(url); };
    const api = apply(doc, storage, { language: 'en-US' }, ApiClient, fetch);
    await api.load();
    return { asked, strings: api.get() };
}
const res = (status, body) => Promise.resolve({ status, ok: status >= 200 && status < 300, json: () => Promise.resolve(body) });
(async () => {
    let r = await run('fr-CA', url => url.indexOf('/fr-ca.json') !== -1 ? res(404) : res(200, { 'admin.ann.none': 'Aucune.' }));
    ok('a regional tag is tried first, then the language', r.asked.length === 2
       && /\/i18n\/fr-ca\.json/.test(r.asked[0]) && /\/i18n\/fr\.json/.test(r.asked[1]), r.asked.join(' , '));
    ok('every request asks for the admin half only', r.asked.every(u => /[?&]part=admin\b/.test(u)));
    ok('and the strings it finds are used', r.strings && r.strings['admin.ann.none'] === 'Aucune.');

    r = await run('en-GB', () => res(200, {}));
    ok('English asks for nothing', r.asked.length === 0 && r.strings === null);

    r = await run('', () => res(200, {}));
    ok('"Auto" follows the device\'s language (English here) and asks for nothing', r.asked.length === 0);

    r = await run('de', () => res(404));
    ok('a language the server has not got leaves English', r.asked.length === 1 && r.strings === null);

    r = await run('fr', () => Promise.reject(new Error('offline')));
    ok('a failed fetch leaves English and still resolves', r.strings === null);

    r = await run('fr', () => res(503));
    ok('a server error leaves English, without trying a shorter tag', r.asked.length === 1 && r.strings === null);

    r = await run('../../x', () => res(200, {}));
    ok('a tag that is not a language never reaches a URL', r.asked.length === 0);

    // ── 5. Order ──────────────────────────────────────────────────────────────
    console.log('\n── The language arrives before anything is drawn ──────────────');
    const show = script.indexOf("addEventListener('pageshow'");
    const handler = show === -1 ? '' : script.slice(show, script.indexOf('function showPage', show));
    ok('pageshow loads the strings, then applies them, then starts the page',
       /loadAdminStrings\(\)\.then\(start, start\)/.test(handler)
       && /applyMarkupStrings\(page\)[\s\S]*showPage\(page\)/.test(handler));
    ok('and does not start any loader before the strings arrive', handler.indexOf('loadConfiguration') === -1);

    finish();
})();
