/*
 * Themes: one look per profile for Bonfire's own screens.
 *
 * A theme is an id in three places that nothing else ties together — Themes.All on the
 * server, THEMES in profiles.js, and a [data-jpf-theme] block in styles.css — plus the
 * administrator's list on the dashboard. Miss one and nothing errors: the server stores a
 * theme the sheet has no block for, and the page renders in Bonfire's tokens with every
 * accent tint resolving to nothing, because rgba(var(--jpf-accent-rgb), …) has no
 * --jpf-accent-rgb to substitute.
 *
 * So this checks, one theme and one token at a time rather than as a count:
 *   - the four lists name the same themes;
 *   - every theme sets every token a theme is responsible for, and its tint source
 *     matches its accent;
 *   - text, buttons and focus rings can be read on each theme's own background — every
 *     stop of a gradient, not an average of them;
 *   - the rules that used to carry the literals now read the tokens, resolved by order
 *     so a later rule restating the literal would be caught;
 *   - the client applies, caches and offers themes the way the server describes them,
 *     executed rather than read.
 *
 *   node tests/js/themes.js [profiles.js] [styles.css] [profilesDashboard.html] [PluginConfiguration.cs]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const SRC = fs.readFileSync(process.argv[2] || L.profilesPath(), 'utf8');
const CSS_RAW = fs.readFileSync(process.argv[3] || L.stylesPath(), 'utf8');
const DASH = fs.readFileSync(process.argv[4] || path.join(L.ROOT, 'Web', 'profilesDashboard.html'), 'utf8');
const SERVER = fs.readFileSync(process.argv[5]
    || path.join(L.ROOT, 'Configuration', 'PluginConfiguration.cs'), 'utf8');

// ── A small CSS reader ──────────────────────────────────────────────────────────
// Comments out first: a "why" comment sits above most rules here, and a block regex
// would swallow it into the selector. Then every rule in source order, at-rules
// flattened, each with the at-rule it sat in.
const CSS = CSS_RAW.replace(/\/\*[\s\S]*?\*\//g, '');
const RULES = (function () {
    const out = [];
    let i = 0, at = null, depth = 0, start = 0;
    while (i < CSS.length) {
        const c = CSS[i];
        if (c === '{') {
            const head = CSS.slice(start, i).trim();
            if (head.charAt(0) === '@') { at = head; depth++; start = i + 1; i++; continue; }
            const end = CSS.indexOf('}', i);
            const body = CSS.slice(i + 1, end);
            const decls = {};
            body.split(';').forEach(d => {
                const k = d.indexOf(':');
                if (k === -1) return;
                decls[d.slice(0, k).trim()] = d.slice(k + 1).trim();
            });
            out.push({
                selectors: head.split(',').map(x => x.trim().replace(/\s+/g, ' ')),
                decls: decls, at: at, index: out.length
            });
            i = end + 1; start = i; continue;
        }
        if (c === '}') { depth--; if (depth <= 0) { at = null; depth = 0; } start = i + 1; }
        i++;
    }
    return out;
})();

/** The value the last rule naming exactly this selector gives the property, or null. */
function winning(selector, prop) {
    let v = null;
    RULES.forEach(r => {
        if (r.at && /prefers-color-scheme|max-width|min-width/.test(r.at)) return;
        if (r.selectors.indexOf(selector) !== -1 && r.decls[prop] !== undefined) v = r.decls[prop];
    });
    return v;
}

// ── Colour arithmetic, WCAG 2 ───────────────────────────────────────────────────
function parseColour(v) {
    v = String(v || '').trim().toLowerCase();
    let m = /^#([0-9a-f]{3})$/.exec(v);
    if (m) return { r: parseInt(m[1][0] + m[1][0], 16), g: parseInt(m[1][1] + m[1][1], 16), b: parseInt(m[1][2] + m[1][2], 16), a: 1 };
    m = /^#([0-9a-f]{6})$/.exec(v);
    if (m) return { r: parseInt(m[1].slice(0, 2), 16), g: parseInt(m[1].slice(2, 4), 16), b: parseInt(m[1].slice(4, 6), 16), a: 1 };
    m = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([0-9.]+))?\s*\)$/.exec(v);
    if (m) return { r: +m[1], g: +m[2], b: +m[3], a: m[4] === undefined ? 1 : +m[4] };
    return null;
}
function over(fg, bg) {
    return { r: fg.r * fg.a + bg.r * (1 - fg.a), g: fg.g * fg.a + bg.g * (1 - fg.a), b: fg.b * fg.a + bg.b * (1 - fg.a), a: 1 };
}
function lum(c) {
    const ch = x => { x /= 255; return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); };
    return 0.2126 * ch(c.r) + 0.7152 * ch(c.g) + 0.0722 * ch(c.b);
}
function contrast(a, b) {
    const x = lum(a), y = lum(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}
/** Every colour stop in a background value: one for a flat colour, each stop of a gradient. */
function stops(v) {
    return (String(v).match(/#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b|rgba?\([^)]*\)/g) || []).map(parseColour).filter(Boolean);
}

// ── 1. The lists agree ──────────────────────────────────────────────────────────
console.log('\n── Every list names the same themes ─────────────────────────');

const serverIds = (function () {
    const consts = {};
    const re = /public const string (\w+) = "([a-z]+)";/g;
    const block = SERVER.slice(SERVER.indexOf('public static class Themes'));
    const body = block.slice(0, block.indexOf('public static readonly string[] All') + 400);
    let m;
    while ((m = re.exec(body)) !== null) consts[m[1]] = m[2];
    const all = /public static readonly string\[\] All = \{([^}]*)\}/.exec(body);
    return all ? all[1].split(',').map(x => consts[x.trim()]).filter(Boolean) : [];
})();
const clientIds = (function () {
    const m = /const THEMES = \[([^\]]*)\]/.exec(SRC);
    return m ? (m[1].match(/'([a-z]+)'/g) || []).map(x => x.replace(/'/g, '')) : [];
})();
const cssIds = Array.from(new Set((CSS.match(/\[data-jpf-theme="([a-z]+)"\]/g) || [])
    .map(x => /"([a-z]+)"/.exec(x)[1])));
const dashIds = (function () {
    const sel = /<select[^>]*id="defaultTheme"[^>]*>([\s\S]*?)<\/select>/.exec(DASH);
    return sel ? (sel[1].match(/value="([a-z]+)"/g) || []).map(x => /"([a-z]+)"/.exec(x)[1]) : [];
})();
const dashNormalize = (function () {
    const m = /function normalizeTheme[\s\S]*?const ids = \[([^\]]*)\]/.exec(DASH);
    return m ? (m[1].match(/'([a-z]+)'/g) || []).map(x => x.replace(/'/g, '')) : [];
})();

ok('the server lists six themes (' + serverIds.join(', ') + ')', serverIds.length === 6);
ok('bonfire is one of them, and first', serverIds[0] === 'bonfire');
[['profiles.js THEMES', clientIds], ['styles.css blocks', cssIds],
 ['the dashboard select', dashIds], ['the dashboard normalizeTheme', dashNormalize]].forEach(([where, ids]) => {
    serverIds.forEach(id => ok(where + ' has ' + id, ids.indexOf(id) !== -1));
    ids.forEach(id => ok(where + ': ' + id + ' is a server theme', serverIds.indexOf(id) !== -1));
});

// ── 2. Every theme sets every token ─────────────────────────────────────────────
console.log('\n── Every theme sets what a theme is responsible for ──────────');

// What changes between themes. A token a theme forgets is inherited from Bonfire on the
// page, and from the surrounding theme inside a preview, so it shows up as one theme
// wearing a piece of another.
const THEME_TOKENS = [
    '--jpf-accent', '--jpf-accent-rgb', '--jpf-accent-hover', '--jpf-on-accent',
    '--jpf-gate-bg', '--jpf-panel', '--jpf-font',
    '--jpf-r-sm', '--jpf-r-md', '--jpf-r-lg', '--jpf-avatar-r',
    '--jpf-ring', '--jpf-hover-scale', '--jpf-hover-glow',
    '--jpf-title-weight', '--jpf-title-spacing', '--jpf-title-shadow'
];
// What Bonfire's own block restates so a Bonfire preview inside another theme is Bonfire.
// Its accent, hover and corners come from the definitions at the top, which that block
// cannot repeat without moving them.
const BONFIRE_TOKENS = ['--jpf-on-accent', '--jpf-gate-bg', '--jpf-panel', '--jpf-font',
    '--jpf-avatar-r', '--jpf-ring', '--jpf-hover-scale', '--jpf-hover-glow',
    '--jpf-title-weight', '--jpf-title-spacing', '--jpf-title-shadow'];

function themeBlock(id) {
    const want = id === 'bonfire'
        ? [':root', 'body', '[data-jpf-theme="bonfire"]']
        : ['[data-jpf-theme="' + id + '"]', '[data-jpf-theme="' + id + '"] body'];
    return RULES.filter(r => !r.at && want.every(s => r.selectors.indexOf(s) !== -1));
}

const theme = {};
serverIds.forEach(id => {
    const blocks = themeBlock(id);
    ok(id + ' has exactly one block' + (id === 'bonfire' ? '' : ', on the attribute and on body'),
       blocks.length === 1, blocks.length + ' found');
    const decls = blocks.length ? blocks[0].decls : {};
    theme[id] = decls;
    (id === 'bonfire' ? BONFIRE_TOKENS : THEME_TOKENS).forEach(tk =>
        ok(id + ' sets ' + tk, decls[tk] !== undefined));
});
ok('Bonfire puts its own accent back on a preview',
   RULES.some(r => !r.at && r.selectors.length === 1 && r.selectors[0] === '[data-jpf-theme="bonfire"]'
       && r.decls['--jpf-accent'] === 'var(--jpf-bonfire-accent)'));

// Every theme block must come after the light-scheme block, which ties with it on html.
const lightIndex = RULES.findIndex(r => r.at && /prefers-color-scheme: light/.test(r.at));
serverIds.filter(id => id !== 'bonfire').forEach(id => {
    const b = themeBlock(id)[0];
    ok(id + ' comes after the light-scheme block, so it wins the tie on html',
       !!b && lightIndex !== -1 && b.index > lightIndex);
});

// The tints. Shared by every non-Bonfire theme, built from --jpf-accent-rgb, which is
// only right if it is the accent.
const shared = RULES.find(r => !r.at && r.selectors.indexOf('[data-jpf-theme]:not([data-jpf-theme="bonfire"])') !== -1);
ok('the dark neutrals and tints are shared by every theme but Bonfire', !!shared);
['a08', 'a18', 'a30', 'a40', 'a45', 'a50', 'a60'].forEach(a =>
    ok('the shared block builds tint ' + a + ' from --jpf-accent-rgb',
       !!shared && /^rgba\(var\(--jpf-accent-rgb\), 0?\.\d+\)$/.test(shared.decls['--jpf-accent-' + a] || '')));
['--jpf-surface-1', '--jpf-border-1', '--jpf-text-1', '--jpf-text-3'].forEach(tk =>
    ok('and restates the dark ' + tk + ', so a light desktop cannot wash a dark theme out',
       !!shared && /^rgba\(255, 255, 255,/.test(shared.decls[tk] || '')));

serverIds.filter(id => id !== 'bonfire').forEach(id => {
    const d = theme[id];
    const accent = parseColour(d['--jpf-accent']);
    const rgb = String(d['--jpf-accent-rgb'] || '').split(',').map(x => +x.trim());
    ok(id + ': --jpf-accent-rgb is its accent (' + d['--jpf-accent-rgb'] + ' vs ' + d['--jpf-accent'] + ')',
       !!accent && rgb.length === 3 && rgb[0] === accent.r && rgb[1] === accent.g && rgb[2] === accent.b);
});

// ── 3. Each theme can be read ───────────────────────────────────────────────────
console.log('\n── Each theme can be read on its own background ──────────────');

const text1 = parseColour(shared ? shared.decls['--jpf-text-1'] : '');
const text3 = parseColour(shared ? shared.decls['--jpf-text-3'] : '');
serverIds.filter(id => id !== 'bonfire').forEach(id => {
    const d = theme[id];
    const accent = parseColour(d['--jpf-accent']);
    const onAccent = parseColour(d['--jpf-on-accent']);
    const hover = parseColour(d['--jpf-accent-hover']);
    const ring = parseColour(d['--jpf-ring']);
    const panel = parseColour(d['--jpf-panel']);
    const bg = stops(d['--jpf-gate-bg']);

    ok(id + ': the background has colours to check (' + bg.length + ')', bg.length > 0);
    if (!accent || !onAccent || !hover || !ring || !panel || !text1 || !text3) {
        ok(id + ': every colour token parses', false);
        return;
    }
    const r = (x) => x.toFixed(2);
    ok(id + ': button text on the accent, ' + r(contrast(onAccent, accent)) + ':1 (4.5 needed)',
       contrast(onAccent, accent) >= 4.5);
    ok(id + ': button text on the hovered accent, ' + r(contrast(onAccent, hover)) + ':1 (4.5 needed)',
       contrast(onAccent, hover) >= 4.5);
    ok(id + ': body text on its panel, ' + r(contrast(over(text1, panel), panel)) + ':1 (7 needed)',
       contrast(over(text1, panel), panel) >= 7);
    bg.forEach((stop, n) => {
        const label = id + ' background stop ' + (n + 1);
        ok(label + ': the title, ' + r(contrast(over(text1, stop), stop)) + ':1 (7 needed)',
           contrast(over(text1, stop), stop) >= 7);
        ok(label + ': the faintest text, ' + r(contrast(over(text3, stop), stop)) + ':1 (3 needed)',
           contrast(over(text3, stop), stop) >= 3);
        // The accent is the focus outline on a television and the "current" ring; the
        // ring is the hover and D-pad frame. Both are non-text, so 3:1.
        ok(label + ': the accent as a focus outline, ' + r(contrast(accent, stop)) + ':1 (3 needed)',
           contrast(accent, stop) >= 3);
        ok(label + ': the hover ring, ' + r(contrast(over(ring, stop), stop)) + ':1 (3 needed)',
           contrast(over(ring, stop), stop) >= 3);
    });
});

// ── 4. The rules read the tokens ────────────────────────────────────────────────
console.log('\n── The screens read the tokens instead of literals ───────────');

[
    ['#profiles-gate-overlay', 'background', 'var(--jpf-gate-bg)'],
    ['#profiles-gate-overlay', 'font-family', 'var(--jpf-font)'],
    ['.profiles-title', 'font-weight', 'var(--jpf-title-weight)'],
    ['.profiles-title', 'letter-spacing', 'var(--jpf-title-spacing)'],
    ['.profiles-title', 'text-shadow', 'var(--jpf-title-shadow)'],
    ['.profile-avatar', 'border-radius', 'var(--jpf-avatar-r)'],
    ['.profile-avatar-overlay-wrap', 'border-radius', 'var(--jpf-avatar-r)'],
    ['.profile-card:hover .profile-avatar', 'transform', 'scale(var(--jpf-hover-scale))'],
    ['.profile-card:focus .profile-avatar', 'box-shadow', 'var(--jpf-hover-glow)'],
    ['.profile-card:focus-within .profile-avatar', 'border-color', 'var(--jpf-ring)'],
    ['.profile-card.manage-mode:hover .profile-avatar', 'transform', 'scale(var(--jpf-hover-scale))'],
    ['.btn-primary', 'color', 'var(--jpf-on-accent)'],
    ['.btn-primary:hover', 'background-color', 'var(--jpf-accent-hover)'],
    ['.create-profile-container > .profile-dialog-actions', 'background', 'var(--jpf-panel)'],
    ['.form-group select option', 'background-color', 'var(--jpf-panel)'],
    ['.devices-dropdown-list', 'background', 'var(--jpf-panel)']
].forEach(([sel, prop, want]) => {
    const got = winning(sel, prop);
    ok(sel + ' { ' + prop + ' } is ' + want + (got === want ? '' : ' (wins: ' + got + ')'), got === want);
});

// The tokens Bonfire's own screens read. A theme token nothing reads is a setting that
// does nothing, which is how a theme ends up looking like the one next to it.
const consumers = RULES.filter(r => !r.selectors.some(s => /data-jpf-theme|^:root$|^body$/.test(s)));
THEME_TOKENS.filter(tk => tk !== '--jpf-accent-rgb').forEach(tk => {
    const used = consumers.some(r => Object.keys(r.decls).some(k => r.decls[k].indexOf('var(' + tk + ')') !== -1));
    ok(tk + ' is read by at least one rule', used);
});

// ── 5. The client, executed ─────────────────────────────────────────────────────
console.log('\n── The client applies, caches and offers what the server says ');

/** One method of the profiles object, by name, brace-matched. */
function method(name) {
    const re = new RegExp('\\n\\s+' + name + ': function \\(([^)]*)\\) \\{');
    const m = re.exec(SRC);
    if (!m) return null;
    let depth = 0, i = SRC.indexOf('{', m.index + m[0].length - 1);
    for (let j = i; j < SRC.length; j++) {
        if (SRC[j] === '{') depth++;
        else if (SRC[j] === '}' && --depth === 0) {
            return { params: m[1], body: SRC.slice(i + 1, j) };
        }
    }
    return null;
}

const constants = (function () {
    const a = /const THEMES = \[[^\]]*\];/.exec(SRC);
    const b = /const THEME_NAMES = \{[\s\S]*?\};/.exec(SRC);
    const c = /function normalizeTheme\(value\) \{[\s\S]*?\n    \}/.exec(SRC);
    return a && b && c ? a[0] + '\n' + b[0] + '\n' + c[0] : null;
})();
ok('THEMES, THEME_NAMES and normalizeTheme are in the client', !!constants);

const names = ['applyTheme', 'applyCachedTheme', '_setThemeSettings', 'renderThemePicker', 'readThemePicker'];
const methods = {};
names.forEach(n => { methods[n] = method(n); ok(n + ' exists', !!methods[n]); });

if (constants && names.every(n => methods[n])) {
    function makeWorld(currentUser) {
        const attrs = {};
        const store = {};
        const els = {};
        const world = {
            attrs: attrs, store: store, els: els,
            document: {
                documentElement: {
                    getAttribute: k => (k in attrs ? attrs[k] : null),
                    setAttribute: (k, v) => { attrs[k] = String(v); }
                },
                getElementById: id => els[id] || null
            },
            localStorage: {
                getItem: k => (k in store ? store[k] : null),
                setItem: (k, v) => { store[k] = String(v); }
            },
            ApiClient: { getCurrentUserId: () => currentUser }
        };
        const obj = {
            config: { themeKey: 'jellyfin_profiles_theme' },
            _themeSettings: null,
            normalizeGuid: g => String(g || '').replace(/-/g, '').toLowerCase()
        };
        const factory = new Function('document', 'localStorage', 'ApiClient', 't', 'obj',
            constants + '\n' + names.map(n =>
                'obj.' + n + ' = function (' + methods[n].params + ') {' + methods[n].body + '};').join('\n')
            + '\nreturn obj;');
        const tStub = (key, vars) => key + (vars ? JSON.stringify(vars) : '');
        world.obj = factory(world.document, world.localStorage, world.ApiClient, tStub, obj);
        return world;
    }

    let w = makeWorld('aaaa-1111');
    w.obj.applyTheme('CRIMSON');
    ok('applyTheme lowercases a known id', w.attrs['data-jpf-theme'] === 'crimson');
    w.obj.applyTheme('neon');
    ok('and turns an unknown one into bonfire, never an id the sheet lacks', w.attrs['data-jpf-theme'] === 'bonfire');

    w = makeWorld('aaaa-1111');
    w.obj._setThemeSettings({ askOnStartup: true });
    ok('an older server that says nothing about themes changes nothing',
       w.attrs['data-jpf-theme'] === undefined && w.obj._themeSettings === null);

    w.obj._setThemeSettings({ theme: 'glass', defaultTheme: 'harbor', allowProfileThemes: true });
    ok('the server\'s theme is applied', w.attrs['data-jpf-theme'] === 'glass');
    const cached = JSON.parse(w.store.jellyfin_profiles_theme || 'null');
    ok('and cached for this account', !!cached && cached.theme === 'glass' && cached.userId === 'aaaa-1111');

    w = makeWorld('aaaa-1111');
    w.obj._setThemeSettings({ Theme: 'Starlight', DefaultTheme: 'Crimson', AllowProfileThemes: false });
    ok('PascalCase from the server reads the same', w.attrs['data-jpf-theme'] === 'starlight'
       && w.obj._themeSettings.defaultTheme === 'crimson' && w.obj._themeSettings.allow === false);

    // The cache is per account: the next person on a shared browser must not inherit it.
    w = makeWorld('bbbb-2222');
    w.store.jellyfin_profiles_theme = JSON.stringify({ userId: 'aaaa-1111', theme: 'crimson' });
    w.obj.applyCachedTheme();
    ok('a theme cached for someone else is not applied', w.attrs['data-jpf-theme'] === undefined);
    w = makeWorld('AAAA1111');
    w.store.jellyfin_profiles_theme = JSON.stringify({ userId: 'aaaa-1111', theme: 'crimson' });
    w.obj.applyCachedTheme();
    ok('one cached for this account is, whatever the id\'s spelling', w.attrs['data-jpf-theme'] === 'crimson');
    w = makeWorld('');
    w.store.jellyfin_profiles_theme = '{not json';
    let threw = false;
    try { w.obj.applyCachedTheme(); } catch (e) { threw = true; }
    ok('a corrupt cache is ignored, not thrown', !threw && w.attrs['data-jpf-theme'] === undefined);

    // The picker.
    w = makeWorld('aaaa-1111');
    ok('no picker before the server has said', w.obj.renderThemePicker('edit', null) === '');
    w.obj._themeSettings = { theme: 'bonfire', defaultTheme: 'harbor', allow: false };
    ok('no picker when the server does not let profiles choose', w.obj.renderThemePicker('edit', 'glass') === '');
    w.obj._themeSettings = { theme: 'bonfire', defaultTheme: 'harbor', allow: true };
    const html = w.obj.renderThemePicker('edit', 'Glass');
    const options = (html.match(/<option value="([a-z]*)"/g) || []).map(x => /"([a-z]*)"/.exec(x)[1]);
    ok('it offers "server default" first, then every theme (' + options.join(', ') + ')',
       options[0] === '' && serverIds.every(id => options.indexOf(id) !== -1) && options.length === serverIds.length + 1);
    ok('the default option names the server default', /profileForm\.themeDefault\{"name":"Harbor"\}/.test(html));
    ok('the stored choice is selected', /<option value="glass" selected>/.test(html));
    ok('and the preview wears it', /id="edit-theme-preview" data-jpf-theme="glass"/.test(html));
    const followHtml = w.obj.renderThemePicker('create', null);
    ok('a profile following the default selects that option, and previews the default',
       /<option value="" selected>/.test(followHtml) && /data-jpf-theme="harbor"/.test(followHtml));

    ok('with no picker on the form, nothing is sent — the server leaves the theme alone',
       w.obj.readThemePicker('edit') === undefined);
    w.els['edit-theme-select'] = { value: '' };
    ok('"server default" is sent as an empty string', w.obj.readThemePicker('edit') === '');
    w.els['edit-theme-select'] = { value: 'Glass' };
    ok('a theme is sent as its id', w.obj.readThemePicker('edit') === 'glass');
}

// ── 6. Wiring ───────────────────────────────────────────────────────────────────
console.log('\n── Wiring ────────────────────────────────────────────────────');

ok('the create form renders the picker', /renderThemePicker\('create', null\)/.test(SRC));
ok('the edit form renders it with the profile\'s choice', /renderThemePicker\('edit', profile\.theme\)/.test(SRC));
ok('the create save sends it', /theme: this\.readThemePicker\('create'\)/.test(SRC));
ok('the edit save sends it', /theme: this\.readThemePicker\('edit'\)/.test(SRC));
ok('both previews follow their select',
   /bindThemePicker\('create'\)/.test(SRC) && /bindThemePicker\('edit'\)/.test(SRC));
ok('the preferences response is read for the theme', (function () {
    const m = method('loadSwitcherPrefs');
    return !!m && /this\._setThemeSettings\(prefs\)/.test(m.body);
})());
ok('the cached theme goes on before the stylesheet', (function () {
    const m = method('init');
    if (!m) return false;
    const a = m.body.indexOf("'applyCachedTheme'"), b = m.body.indexOf("'injectStyles'");
    return a !== -1 && b !== -1 && a < b;
})());
ok('/list carries each profile\'s choice into the form', /theme: pick\(p, 'theme', null\)/.test(SRC));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
