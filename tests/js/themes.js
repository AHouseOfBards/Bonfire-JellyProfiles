/*
 * Themes: one look per profile for Bonfire's own screens.
 *
 * A theme is an id in three places that nothing else ties together — Themes.All on the
 * server, THEMES in profiles.js, and a file in Web/themes/ that themeCss turns into a
 * [data-jpf-theme] block (styles.css until 1.7.2) — plus the
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
// What the browser has: the sheet, then (since 1.7.2) the blocks themeCss writes from
// Web/themes/. A sheet named on the command line is read with the blocks of the script
// named with it, so an older pair is checked as it shipped.
const CSS_RAW = process.argv[3]
    ? fs.readFileSync(process.argv[3], 'utf8') + (SRC.indexOf(L.THEMES_MARKER) === -1 ? '' : '\n' + L.themesCss(SRC))
    : L.extractCss(SRC);
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
        if (r.at && /prefers-color-scheme|prefers-reduced-motion|max-width|min-width/.test(r.at)) return;
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
/**
 * Every colour a background shows: one for a flat colour, each stop of a gradient.
 *
 * A see-through stop is never seen on its own; it is painted over the layers beneath it.
 * Until 1.7.2 one was read as if opaque: harmless while every see-through stop was dark
 * (Glass's), wrong once a glow arrived (Campfire's orange at 28%, read as solid orange).
 * So each see-through stop is laid over every opaque stop in the value, one pair at a
 * time, which is at least as strict as the layer actually under it. Opaque stops are
 * checked as they are, as before.
 */
function stops(v) {
    const all = (String(v).match(/#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b|rgba?\([^)]*\)/g) || []).map(parseColour).filter(Boolean);
    const solid = all.filter(c => c.a >= 1);
    if (!solid.length) return all;
    return all.reduce((out, c) => out.concat(c.a >= 1 ? [c] : solid.map(s => over(c, s))), []);
}

// ── 1. The lists agree ──────────────────────────────────────────────────────────
console.log('\n── Every list names the same themes ─────────────────────────');

const serverIds = (function () {
    const consts = {};
    const re = /public const string (\w+) = "([a-z0-9-]+)";/g;
    const block = SERVER.slice(SERVER.indexOf('public static class Themes'));
    const body = block.slice(0, block.indexOf('public static readonly string[] All') + 400);
    let m;
    while ((m = re.exec(body)) !== null) consts[m[1]] = m[2];
    const all = /public static readonly string\[\] All\s*=\s*\{([^}]*)\}/.exec(body);
    return all ? all[1].split(',').map(x => consts[x.trim()]).filter(Boolean) : [];
})();
const clientIds = (function () {
    const m = /const THEMES = \[([^\]]*)\]/.exec(SRC);
    return m ? (m[1].match(/'([a-z0-9-]+)'/g) || []).map(x => x.replace(/'/g, '')) : [];
})();
const cssIds = Array.from(new Set((CSS.match(/\[data-jpf-theme="([a-z0-9-]+)"\]/g) || [])
    .map(x => /"([a-z0-9-]+)"/.exec(x)[1])));
const dashIds = (function () {
    const sel = /<select[^>]*id="defaultTheme"[^>]*>([\s\S]*?)<\/select>/.exec(DASH);
    return sel ? (sel[1].match(/value="([a-z0-9-]+)"/g) || []).map(x => /"([a-z0-9-]+)"/.exec(x)[1]) : [];
})();
const dashNormalize = (function () {
    const m = /function normalizeTheme[\s\S]*?const ids = \[([^\]]*)\]/.exec(DASH);
    return m ? (m[1].match(/'([a-z0-9-]+)'/g) || []).map(x => x.replace(/'/g, '')) : [];
})();

const fileIds = fs.readdirSync(path.join(__dirname, '..', '..', 'Web', 'themes'))
    .filter(f => /\.json$/.test(f) && f !== 'fonts.json').map(f => f.replace(/\.json$/, ''));
ok('the server lists twelve themes (' + serverIds.join(', ') + ')', serverIds.length === 12);
serverIds.forEach(id => ok('Web/themes/' + id + '.json exists', fileIds.indexOf(id) !== -1));
fileIds.forEach(id => ok('Web/themes/' + id + '.json is a server theme', serverIds.indexOf(id) !== -1));
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
    '--jpf-title-weight', '--jpf-title-spacing', '--jpf-title-shadow',
    // Shape, not colour. Two themes that differ only in palette were the complaint: the
    // services these follow differ as much in tiles, names and buttons as in colour.
    '--jpf-title-scale', '--jpf-avatar-shadow', '--jpf-ring-w',
    '--jpf-name-color', '--jpf-name-hover', '--jpf-name-weight', '--jpf-name-opacity', '--jpf-name-scale',
    '--jpf-btn-radius', '--jpf-btn2-bg', '--jpf-btn2-color', '--jpf-btn2-border', '--jpf-btn2-case',
    '--jpf-btn2-spacing', '--jpf-btn2-weight', '--jpf-btn2-hover-bg', '--jpf-btn2-hover-color',
    '--jpf-btn2-hover-border', '--jpf-btn2-hover-glow',
    '--jpf-add-bg', '--jpf-add-border', '--jpf-add-color', '--jpf-add-hover-bg',
    // And the screens behind the gate: the profile forms, Settings, Bonfire, PIN entry.
    '--jpf-sheet-bg', '--jpf-sheet-border', '--jpf-sheet-shadow',
    '--jpf-card-bg', '--jpf-card-border', '--jpf-input-bg', '--jpf-input-border', '--jpf-input-r'
];
// The panel behind each household's tiles (layout.households in the file).
const SECTION_TOKENS = ['--jpf-section-bg', '--jpf-section-border', '--jpf-section-shadow',
    '--jpf-section-pad', '--jpf-section-rule'];
// What every theme used to inherit from one shared block: the neutrals and the accent
// tints. Since 1.7.2 each block carries its own, because a light theme's neutrals are
// not a dark theme's, and a shared block's two attribute selectors outranked a theme's.
const NEUTRAL_TOKENS = ['--jpf-accent-a08', '--jpf-accent-a18', '--jpf-accent-a30', '--jpf-accent-a40',
    '--jpf-accent-a45', '--jpf-accent-a50', '--jpf-accent-a60',
    '--jpf-surface-1', '--jpf-surface-2', '--jpf-surface-3', '--jpf-border-1', '--jpf-border-2', '--jpf-border-3',
    '--jpf-text-1', '--jpf-text-2', '--jpf-text-3', '--jpf-shadow-1', '--jpf-shadow-2', '--jpf-shadow-3',
    '--jpf-danger', '--jpf-danger-quiet', '--jpf-danger-deep', '--jpf-warn', '--jpf-deco', '--jpf-deco-size'];
// Bonfire is a theme like the rest since 1.7.2: a fixed look, not the server's accent, so
// its block owes everything any block owes.
const BLOCK_TOKENS = THEME_TOKENS.concat(SECTION_TOKENS, NEUTRAL_TOKENS);

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
    BLOCK_TOKENS.forEach(tk =>
        ok(id + ' sets ' + tk, decls[tk] !== undefined));
});

// Bonfire stopped following the server's accent and the desktop's light or dark setting
// in 1.7.2 (a fixed look of its own). Either one coming back would put a second set of
// tokens over every theme on some machines and not others.
ok('nothing redefines a theme token for a light desktop', RULES.every(r =>
    !(r.at && /prefers-color-scheme/.test(r.at) && Object.keys(r.decls).some(k => BLOCK_TOKENS.indexOf(k) !== -1))));
ok('and nothing points Bonfire\'s accent at Jellyfin\'s', !/var\(--jpf-bonfire-accent\)|--jpf-bonfire-accent\s*:/.test(CSS));
// A theme's tokens are set by its own block and nowhere else. A second rule that sets
// them, on any selector naming a theme, is the shared block of 1.7.1 coming back.
const stray = RULES.filter(r => r.selectors.some(s => /data-jpf-theme/.test(s))
    && !serverIds.some(id => themeBlock(id)[0] === r)
    && Object.keys(r.decls).some(k => BLOCK_TOKENS.indexOf(k) !== -1));
ok('no rule but a theme\'s own block sets its tokens' + (stray.length ? ' (' + stray[0].selectors[0] + ')' : ''),
   stray.length === 0);

// The tints are built from the accent, which is only right if they are of the accent.
serverIds.forEach(id => {
    const d = theme[id];
    const accent = parseColour(d['--jpf-accent']);
    const rgb = String(d['--jpf-accent-rgb'] || '').split(',').map(x => +x.trim());
    ok(id + ': --jpf-accent-rgb is its accent (' + d['--jpf-accent-rgb'] + ' vs ' + d['--jpf-accent'] + ')',
       !!accent && rgb.length === 3 && rgb[0] === accent.r && rgb[1] === accent.g && rgb[2] === accent.b);
    ['a08', 'a18', 'a30', 'a40', 'a45', 'a50', 'a60'].forEach(a => {
        const tint = parseColour(d['--jpf-accent-' + a]);
        ok(id + ': tint ' + a + ' is its accent at ' + (+a.slice(1) / 100),
           !!accent && !!tint && tint.r === accent.r && tint.g === accent.g && tint.b === accent.b
           && Math.abs(tint.a - +a.slice(1) / 100) < 1e-9);
    });
});

// ── 3. Each theme can be read ───────────────────────────────────────────────────
console.log('\n── Each theme can be read on its own background ──────────────');

// Every theme, Bonfire included now that its colours are its own, against its own text.
//
// Known failures, tracked rather than hidden; each entry FAILS once its button passes, so
// none can outlive its problem. Empty since the 1.7.2 theme review: Bonfire's button was
// Jellyfin's white on #00a4dc, 2.86:1, until Campfire gave it dark text on gold.
const KNOWN_ON_ACCENT = {};
Object.keys(KNOWN_ON_ACCENT).forEach(id => {
    const d = theme[id] || {};
    const a = parseColour(d['--jpf-accent']), o = parseColour(d['--jpf-on-accent']);
    ok(id + '\'s button is still a known contrast failure (' + KNOWN_ON_ACCENT[id] + '); remove the entry if not',
       !!a && !!o && contrast(o, a) < 4.5);
});
serverIds.forEach(id => {
    const d = theme[id];
    const text1 = parseColour(d['--jpf-text-1']);
    const text3 = parseColour(d['--jpf-text-3']);
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
    if (!KNOWN_ON_ACCENT[id]) {
        ok(id + ': button text on the accent, ' + r(contrast(onAccent, accent)) + ':1 (4.5 needed)',
           contrast(onAccent, accent) >= 4.5);
        ok(id + ': button text on the hovered accent, ' + r(contrast(onAccent, hover)) + ':1 (4.5 needed)',
           contrast(onAccent, hover) >= 4.5);
    }
    ok(id + ': body text on its panel, ' + r(contrast(over(text1, panel), panel)) + ':1 (7 needed)',
       contrast(over(text1, panel), panel) >= 7);
    // The accent is also text: "Forgot PIN?", the "current" badge, the announcement icon
    // and a few section headings. Text, so 4.5, against the panel they sit on.
    const accentText = parseColour(d['--jpf-accent-text']);
    ok(id + ': the accent as text is a colour (' + d['--jpf-accent-text'] + ')', !!accentText);
    if (!accentText) return;
    ok(id + ': the accent as text on its panel, ' + r(contrast(accentText, panel)) + ':1 (4.5 needed)',
       contrast(accentText, panel) >= 4.5);
    // Hints, subtitles and result messages are drawn in text-3 on the panel too.
    ok(id + ': the faintest text on its panel, ' + r(contrast(over(text3, panel), panel)) + ':1 (4.5 needed)',
       contrast(over(text3, panel), panel) >= 4.5);
    bg.forEach((stop, n) => {
        const label = id + ' background stop ' + (n + 1);
        ok(label + ': the title, ' + r(contrast(over(text1, stop), stop)) + ':1 (7 needed)',
           contrast(over(text1, stop), stop) >= 7);
        ok(label + ': the faintest text, ' + r(contrast(over(text3, stop), stop)) + ':1 (4.5 needed)',
           contrast(over(text3, stop), stop) >= 4.5);
        ok(label + ': the accent as text (the "current" badge), ' + r(contrast(over(accentText, stop), stop)) + ':1 (4.5 needed)',
           contrast(over(accentText, stop), stop) >= 4.5);
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
    ['.jpf-text-link', 'color', 'var(--jpf-accent-text, var(--jpf-accent))'],
    ['.profile-current-badge', 'color', 'var(--jpf-accent-text, var(--jpf-accent))'],
    ['.btn-primary:hover', 'background-color', 'var(--jpf-accent-hover)'],
    ['.create-profile-container > .profile-dialog-actions', 'background', 'var(--jpf-panel)'],
    ['.form-group select option', 'background-color', 'var(--jpf-panel)'],
    ['.devices-dropdown-list', 'background', 'var(--jpf-panel)'],
    ['.profiles-home-section', 'background', 'var(--jpf-section-bg)'],
    ['.profile-avatar', 'border', 'var(--jpf-ring-w) solid transparent'],
    ['.profile-name', 'color', 'var(--jpf-name-color)'],
    ['.profiles-btn', 'border-radius', 'var(--jpf-btn-radius)'],
    ['.btn-secondary', 'text-transform', 'var(--jpf-btn2-case)'],
    ['.btn-secondary:focus', 'background', 'var(--jpf-btn2-hover-bg)'],
    ['.add-avatar', 'border', 'var(--jpf-add-border)'],
    ['.create-profile-container', 'background', 'var(--jpf-sheet-bg)'],
    ['.profile-section', 'background', 'var(--jpf-card-bg)'],
    ['.jpf-choice', 'background', 'var(--jpf-card-bg)'],
    ['.form-group select', 'border-radius', 'var(--jpf-input-r)'],
    ['#profile-pin-input', 'background', 'var(--jpf-input-bg)']
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
    const b = /function themeName\(id\) \{[\s\S]*?\n    \}/.exec(SRC);
    const c = /function normalizeTheme\(value\) \{[\s\S]*?\n    \}/.exec(SRC);
    // The checks above say the three are there; what runs is every helper they now lean
    // on (custom themes, 1.7.2), from the one list in _lib.
    return a && b && c ? L.themeHelpers(SRC) : null;
})();
ok('THEMES, themeName and normalizeTheme are in the client', !!constants);

const names = ['applyTheme', '_readThemeCache', 'applyCachedTheme', '_setThemeSettings', 'renderThemePreview', 'renderThemePicker', 'readThemePicker'];
const methods = {};
names.forEach(n => { methods[n] = method(n); ok(n + ' exists', !!methods[n]); });

if (constants && names.every(n => methods[n])) {
    function makeWorld(currentUser, sharedStore) {
        const attrs = {};
        const store = sharedStore || {};
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
            fontCalls: 0,
            injectFonts() { this.fontCalls++; },
            normalizeGuid: g => String(g || '').replace(/-/g, '').toLowerCase()
        };
        // Since 1.7.2 _setThemeSettings caches each theme's gate background, which it gets
        // by running themeCss over the theme files.
        const generator = SRC.indexOf('function gateBackground(') === -1 ? ''
            : 'const BONFIRE_THEMES = ' + JSON.stringify(L.themesPayload()) + ';\n'
              + ['themeCss', 'servedFontName', 'gateBackground'].filter(n => SRC.indexOf('function ' + n + '(') !== -1)
                  .map(n => L.extractFunction(SRC, n)).join('\n') + '\n';
        const factory = new Function('document', 'localStorage', 'ApiClient', 't', 'obj',
            generator + constants + '\n' + names.map(n =>
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
    // A packaged client has an ApiClient by now and may not have had one at startup.
    ok('and the served fonts are declared then, if they were not already', w.obj.fontCalls === 1);
    const cached = JSON.parse(w.store.jellyfin_profiles_theme || 'null');
    ok('and cached for this account', !!cached && !!cached.users && cached.users.aaaa1111 === 'glass'
       && cached.last === 'glass');
    // The startup cover's colour. The head script reads it from here, and it has to be the
    // gate's background exactly, or the cover and the gate are two colours a beat apart.
    ok('with the gate\'s background for it, as the stylesheet has it',
       !!cached && !!cached.bg && cached.bg.glass === theme.glass['--jpf-gate-bg'],
       cached && cached.bg ? String(cached.bg.glass).slice(0, 60) : 'no bg');

    // One entry per account. With one for the device, moving from the main account to a
    // child's profile replaced the parent's theme, and the next start drew the gate in
    // Bonfire's colours.
    const shared = {};
    makeWorld('aaaa-1111', shared).obj._setThemeSettings({ theme: 'crimson', defaultTheme: 'bonfire', allowProfileThemes: true });
    makeWorld('cccc-3333', shared).obj._setThemeSettings({ theme: 'starlight', defaultTheme: 'bonfire', allowProfileThemes: true });
    const parent = makeWorld('aaaa-1111', shared);
    parent.obj.applyCachedTheme();
    ok('after a child\'s profile is used, the parent still opens in the parent\'s theme',
       parent.attrs['data-jpf-theme'] === 'crimson');
    const child = makeWorld('cccc-3333', shared);
    child.obj.applyCachedTheme();
    ok('and the child in the child\'s', child.attrs['data-jpf-theme'] === 'starlight');
    const nobody = makeWorld('', shared);
    nobody.obj.applyCachedTheme();
    ok('before sign-in, the last one used on this device', nobody.attrs['data-jpf-theme'] === 'starlight');
    const bgs = JSON.parse(shared.jellyfin_profiles_theme).bg || {};
    ok('and a background is kept for each theme an account uses (' + Object.keys(bgs).join(', ') + ')',
       bgs.crimson === theme.crimson['--jpf-gate-bg'] && bgs.starlight === theme.starlight['--jpf-gate-bg']);
    makeWorld('aaaa-1111', shared).obj._setThemeSettings({ theme: 'harbor', defaultTheme: 'bonfire', allowProfileThemes: true });
    const pruned = Object.keys(JSON.parse(shared.jellyfin_profiles_theme).bg || {}).sort().join(', ');
    ok('and only for those: one nobody uses any more is dropped (' + pruned + ')', pruned === 'harbor, starlight');

    const many = {};
    for (let i = 0; i < 25; i++) {
        makeWorld('user-' + i, many).obj._setThemeSettings({ theme: 'glass', defaultTheme: 'bonfire', allowProfileThemes: true });
    }
    const kept = Object.keys(JSON.parse(many.jellyfin_profiles_theme).users);
    ok('a browser that has seen many accounts keeps the latest twenty (' + kept.length + ')',
       kept.length === 20 && kept.indexOf('user24') !== -1 && kept.indexOf('user0') === -1);

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
    const options = (html.match(/<option value="([a-z0-9-]*)"/g) || []).map(x => /"([a-z0-9-]*)"/.exec(x)[1]);
    ok('it offers "server default" first, then every theme (' + options.join(', ') + ')',
       options[0] === '' && serverIds.every(id => options.indexOf(id) !== -1) && options.length === serverIds.length + 1);
    ok('the default option names the server default', /profileForm\.themeDefault\{"name":"theme\.harbor"\}/.test(html));
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

// ── 6. The startup cover wears the theme from the first frame ────────────────────
console.log('\n── The startup cover is the theme\'s colour before the sheet loads ');

const HEAD_CS = fs.readFileSync(process.argv[6] || path.join(L.ROOT, 'Injection', 'WebInjection.cs'), 'utf8');
/** The head script, reassembled from the C# constant the way headscript.js does it. */
const headBody = (function () {
    const at = HEAD_CS.indexOf('internal const string HeadScript =');
    if (at === -1) return '';
    const end = HEAD_CS.indexOf('";', at);
    const region = HEAD_CS.slice(at, end + 2).split('\n')
        .map(line => { const c = line.indexOf('//'); return c === -1 ? line : line.slice(0, c); })
        .join('\n');
    return (region.match(/"(?:[^"\\]|\\.)*"/g) || [])
        .map(p => p.slice(1, -1).replace(/\\(["'\\])/g, '$1')).join('')
        .replace(/^<script id="jpf-eh">/, '').replace(/<\/script>$/, '');
})();

// Until 1.7.2 the head script carried a copy of every built-in's background, checked
// here against the sheet. A theme an administrator makes could never be in that copy, so
// it reads them from the cache profiles.js writes (checked in section 5) instead.
// The one colour it may hold is jellyfin-web's own page colour, its fallback for a stored
// background that is not a colour; Jellyfin Dark's plain background is that same #101010,
// so that single assignment is taken out before looking, and nothing else is.
const headWithoutFallback = headBody.split("b='#101010';").join('');
ok('the head script\'s one fallback colour is jellyfin-web\'s #101010, once',
   headBody.split("b='#101010';").length === 2);
ok('the head script carries no theme\'s background of its own',
   headBody.length > 0 && !/tb=\{[a-z]/.test(headBody) && serverIds.every(id =>
       !theme[id] || headWithoutFallback.indexOf(String(theme[id]['--jpf-gate-bg']).slice(0, 40)) === -1));

if (headBody) {
    const vm = require('vm');
    /** Runs the head script on a signed-in home page with the given theme cache. */
    function runHead(themeCache) {
        const attrs = {};
        const styles = [];
        const store = {
            jellyfin_credentials: JSON.stringify({ Servers: [{ AccessToken: 't', UserId: 'AAAA-1111' }] }),
            jellyfin_profiles_switcher_mode: JSON.stringify({ askOnStartup: true, location: 'button', masterUserId: 'aaaa1111' }),
            jellyfin_profiles_theme: themeCache === undefined ? undefined : JSON.stringify(themeCache)
        };
        const root = {
            style: {},
            setAttribute: (k, v) => { attrs[k] = String(v); },
            removeAttribute: k => { delete attrs[k]; },
            getAttribute: k => (k in attrs ? attrs[k] : null)
        };
        const sandbox = {
            localStorage: { getItem: k => (store[k] === undefined ? null : store[k]), removeItem() {}, setItem() {} },
            sessionStorage: { getItem: () => null },
            location: { pathname: '/web/home', hash: '' },
            document: {
                documentElement: root,
                head: { appendChild: el => styles.push(el) },
                createElement: () => ({}),
                addEventListener() {}
            },
            setTimeout: () => 1, clearTimeout() {},
            JSON, String, Object, Array
        };
        sandbox.window = sandbox;
        vm.createContext(sandbox);
        new vm.Script(headBody).runInContext(sandbox);
        return { attrs: attrs, css: styles.map(s => s.textContent).join('') };
    }

    // Every built-in's background, as profiles.js would have cached them.
    const allBg = {};
    serverIds.forEach(id => { allBg[id] = theme[id]['--jpf-gate-bg']; });
    const rule = (id, bg) => 'html[data-jpf-cover][data-jpf-theme="' + id + '"]::before{background:' + bg + '}';

    const mine = runHead({ last: 'glass', users: { cccc3333: 'glass', aaaa1111: 'crimson' }, bg: allBg });
    ok('the cover goes up on a signed-in home page that asks', 'data-jpf-cover' in mine.attrs);
    ok('and wears this account\'s cached theme', mine.attrs['data-jpf-theme'] === 'crimson');
    ok('with a rule giving each cached theme its gate\'s background, exactly',
       serverIds.every(id => mine.css.indexOf(rule(id, allBg[id])) !== -1));
    const made = runHead({ users: { aaaa1111: '9lives' }, bg: { '9lives': 'linear-gradient(90deg, #102030 0%, #304050 100%)' } });
    ok('a theme an administrator made gets its cover too, its id quoted though it starts with a digit',
       made.attrs['data-jpf-theme'] === '9lives'
       && made.css.indexOf(rule('9lives', 'linear-gradient(90deg, #102030 0%, #304050 100%)')) !== -1);
    const theirs = runHead({ last: 'glass', users: { bbbb2222: 'glass' }, bg: allBg });
    ok('someone else\'s cached theme is not applied', theirs.attrs['data-jpf-theme'] === undefined
       && 'data-jpf-cover' in theirs.attrs);
    const unknown = runHead({ users: { aaaa1111: 'neon' }, bg: allBg });
    ok('a theme with no cached background is not applied', unknown.attrs['data-jpf-theme'] === undefined);
    const none = runHead(undefined);
    ok('no cache: the cover is Bonfire\'s, as before', none.attrs['data-jpf-theme'] === undefined
       && 'data-jpf-cover' in none.attrs);
    const older = runHead({ last: 'glass', users: { aaaa1111: 'glass' } });
    ok('a cache from before 1.7.2, with no backgrounds: Bonfire\'s cover, no theme put on early',
       older.attrs['data-jpf-theme'] === undefined && 'data-jpf-cover' in older.attrs);

    // Storage is same-origin, so not a threat boundary; but whatever is in it is written
    // into a style rule, and a value that ends the rule or fetches something must not be.
    [['ends the rule', 'red}html{display:none'], ['quotes', "#000'"], ['fetches', 'url(x.png)'],
     ['fetches by image()', 'image(#000)'], ['is not a string', 7]].forEach(([what, bad]) => {
        const r = runHead({ users: { aaaa1111: 'evil' }, bg: { evil: bad } });
        ok('a cached background that ' + what + ' is not used', r.css.indexOf('evil') === -1
           && r.attrs['data-jpf-theme'] === undefined);
    });
    const badId = runHead({ users: { aaaa1111: 'x"]{}' }, bg: { 'x"]{}': '#000' } });
    ok('nor one under an id that is not an id', badId.css.indexOf('x"') === -1);
}

// ── 7. Wiring ───────────────────────────────────────────────────────────────────
console.log('\n── Wiring ────────────────────────────────────────────────────');

// Since 1.7.2 through renderThemeSection, which wraps the picker in a titled section and
// draws nothing when profiles may not choose.
ok('the create form renders the picker', /renderThemeSection\('create', null\)/.test(SRC)
   && /renderThemeSection: function \(prefix, stored\) \{\s*const picker = this\.renderThemePicker\(prefix, stored\);/.test(SRC));
ok('the edit form renders it with the profile\'s choice', /renderThemeSection\('edit', profile\.theme\)/.test(SRC));
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

// ── 8. The themes are not one look in six colours ───────────────────────────────
console.log('\n── Every pair of themes differs in shape, not only colour ─────');

// The complaint that started v2: "not distinct enough, nor match their inspirations".
// Colour alone is what 1.6.6.1 changed, and it read as one design recoloured. So every
// pair is compared on what is not colour, one pair at a time — an aggregate would let
// two near-identical themes hide behind four very different ones.
const SHAPE = ['--jpf-avatar-r', '--jpf-ring-w', '--jpf-hover-scale', '--jpf-title-weight', '--jpf-title-scale',
    '--jpf-name-weight', '--jpf-name-scale', '--jpf-btn-radius', '--jpf-btn2-case', '--jpf-btn2-border',
    '--jpf-btn2-weight', '--jpf-add-border', '--jpf-input-r', '--jpf-font'];
const MIN_SHAPE_DIFFERENCES = 5;
const lookIds = serverIds.filter(id => id !== 'bonfire');
// A light and a dark version of one look are meant to share every shape, so a pair named
// here is held to the opposite: identical in shape, different in colour. Named pairs
// only, so nothing else can slip in under it.
const SCHEME_PAIRS = [['jellyfin-dark', 'jellyfin-light']];
const isPair = (x, y) => SCHEME_PAIRS.some(p => (p[0] === x && p[1] === y) || (p[0] === y && p[1] === x));
SCHEME_PAIRS.forEach(([x, y]) => {
    const a = theme[x] || {}, b = theme[y] || {};
    // A border token is "width style colour"; the colour is the scheme's, so only the
    // width and style are shape.
    const shapeOf = (tk, v) => (/border/.test(tk) ? String(v).split(' ').slice(0, 2).join(' ') : v);
    const differ = SHAPE.filter(tk => shapeOf(tk, a[tk]) !== shapeOf(tk, b[tk]));
    ok(x + ' and ' + y + ' share every shape token' + (differ.length ? ' (not ' + differ.join(', ') + ')' : ''),
       !!theme[x] && !!theme[y] && differ.length === 0);
    ok(x + ' and ' + y + ' differ in background', !!theme[x] && !!theme[y] && a['--jpf-gate-bg'] !== b['--jpf-gate-bg']);
});
for (let i = 0; i < lookIds.length; i++) {
    for (let j = i + 1; j < lookIds.length; j++) {
        if (isPair(lookIds[i], lookIds[j])) continue;
        const a = theme[lookIds[i]], b = theme[lookIds[j]];
        const differ = SHAPE.filter(tk => a[tk] !== undefined && b[tk] !== undefined && a[tk] !== b[tk]);
        ok(lookIds[i] + ' and ' + lookIds[j] + ' differ in ' + differ.length + ' of ' + SHAPE.length
           + ' shape tokens (' + MIN_SHAPE_DIFFERENCES + ' needed)', differ.length >= MIN_SHAPE_DIFFERENCES);
    }
}
// And each against Bonfire, which is the screen everyone already knows.
lookIds.forEach(id => {
    const differ = SHAPE.filter(tk => theme[id][tk] !== undefined && theme.bonfire[tk] !== undefined
        && theme[id][tk] !== theme.bonfire[tk]);
    ok(id + ' differs from Bonfire in ' + differ.length + ' shape tokens', differ.length >= MIN_SHAPE_DIFFERENCES);
});
ok('only Bonfire puts a panel behind the tiles',
   lookIds.every(id => theme[id]['--jpf-section-bg'] === 'transparent')
   && theme.bonfire['--jpf-section-bg'] === 'var(--jpf-surface-1)');

// ── 9. A change shows without a reload ──────────────────────────────────────────
console.log('\n── A changed theme shows without reloading ──────────────────');

{
    const bind = method('_bindSettingsRefresh');
    ok('_bindSettingsRefresh exists', !!bind);
    if (bind) {
        const listeners = { window: {}, document: {} };
        let fetched = 0;
        let clock = 1000000;
        const on = (where) => (type, fn) => { (listeners[where][type] = listeners[where][type] || []).push(fn); };
        const fire = (where, type, ev) => (listeners[where][type] || []).forEach(fn => fn(ev || {}));
        const doc = { visibilityState: 'visible', addEventListener: on('document') };
        const win = { addEventListener: on('window') };
        const obj = { SETTINGS_REFRESH_MS: 60000, loadSwitcherPrefs: () => { fetched++; } };
        const realNow = Date.now;
        Date.now = () => clock;
        try {
            obj._bindSettingsRefresh = new Function('window', 'document',
                'return function (' + bind.params + ') {' + bind.body + '};')(win, doc);
            obj._bindSettingsRefresh();
            obj._bindSettingsRefresh();

            fire('window', 'jpf-settings-changed');
            ok('the admin page saving in this window re-reads the preferences, once', fetched === 1, fetched + ' reads');
            fire('window', 'storage', { key: 'jellyfin_profiles_settings_epoch' });
            ok('so does a save in another tab', fetched === 2);
            fire('window', 'storage', { key: 'something_else' });
            ok('but not any other storage write', fetched === 2);
            clock += 30000;
            fire('document', 'visibilitychange');
            ok('coming back within a minute does not ask again', fetched === 2);
            clock += 31000;
            fire('document', 'visibilitychange');
            ok('coming back after a minute does', fetched === 3);
            doc.visibilityState = 'hidden';
            clock += 120000;
            fire('document', 'visibilitychange');
            ok('and going away never does', fetched === 3);
        } finally {
            Date.now = realNow;
        }
    }
    ok('init binds it', /this\._bindSettingsRefresh\(\);/.test(SRC));
    ok('the admin page announces a save to this window and to other tabs',
       /dispatchEvent\(new Event\('jpf-settings-changed'\)\)/.test(DASH)
       && /localStorage\.setItem\('jellyfin_profiles_settings_epoch'/.test(DASH));
    const lp = method('loadSwitcherPrefs');
    ok('and what it re-reads applies the theme', !!lp && /this\._setThemeSettings\(prefs\)/.test(lp.body));
}

// ── 10. Settings → Theme ────────────────────────────────────────────────────────
console.log('\n── Settings has a Theme page when profiles may choose ────────');

{
    const menu = method('showSettingsMenu');
    ok('Settings offers Theme only when the server lets profiles choose',
       !!menu && /this\._themeSettings && this\._themeSettings\.allow\s*\?\s*entry\('settings-theme'/.test(menu.body));
    ok('and opens the Theme page from it', !!menu && /open\('settings-theme', \(\) => this\.showThemeSettings\(null\)\)/.test(menu.body));
    const page = method('showThemeSettings');
    ok('the Theme page saves through /theme, which touches nothing else on the profile',
       !!page && /plugins\/profiles\/theme/.test(page.body) && !/plugins\/profiles\/update/.test(page.body));
    // A remote's Back key clicks the screen's own back button, from a fixed list. A screen
    // missing from it swallows Back with nothing happening, which on Tizen is a dead remote.
    const dismiss = method('_dismissSurface');
    ok('the TV Back key leaves the Theme page', !!dismiss && /#theme-settings-back-btn/.test(dismiss.body)
       && !!page && /id="theme-settings-back-btn"/.test(page.body));
    ok('and its cards take the thick focus ring a television needs',
       /\.jpf-tv \.jpf-theme-option:focus/.test(CSS) && /\.jpf-tv \.jpf-theme-person:focus/.test(CSS)
       && /\.jpf-tv \.jpf-choice:focus/.test(CSS));
    ok('it sends the main account\'s PIN, which /theme checks', !!page && /masterPin: this\.masterPin/.test(page.body));
    ok('and puts the theme on now when the profile is the one signed in',
       !!page && /=== signedIn\)\s*\{\s*this\._setThemeSettings\(/.test(page.body));

    // The page itself, run: what it draws for a profile following the default, and for
    // one that chose a theme. Reported against 1.6.6.2: a "Server default (Crimson)" card
    // and a Crimson card that looked the same, "Saved." under the cards after every pick,
    // and only Bonfire drawing a sheet behind them.
    if (page && methods.renderThemePreview && constants) {
        const render = (stored, defaultTheme) => {
            let html = '';
            const content = {
                set innerHTML(v) { html = v; }, get innerHTML() { return html; },
                querySelector: () => null, querySelectorAll: () => []
            };
            const doc = { querySelector: s => (s === '.profiles-modal-content' ? content : null) };
            const obj = {
                _themeSettings: { theme: 'bonfire', defaultTheme: defaultTheme, allow: true },
                currentProfiles: [{ profileUserId: 'aaaa', profileName: 'Bard', isMaster: true, theme: stored }],
                beginNavigation() {}, initTVCheckboxes() {}, showSettingsMenu() { html = 'MENU'; },
                readMasterState: () => ({ token: 'x' }),
                normalizeGuid: g => String(g || '').toLowerCase()
            };
            const factory = new Function('document', 'ApiClient', 't', 'escapeHtml', 'setTimeout', 'obj',
                constants + '\nobj.renderThemePreview = function (' + methods.renderThemePreview.params + ') {'
                + methods.renderThemePreview.body + '};\nobj.showThemeSettings = function (' + page.params + ') {'
                + page.body + '};\nreturn obj;');
            factory(doc, { getCurrentUserId: () => 'aaaa' }, (k, v) => k + (v ? JSON.stringify(v) : ''),
                s => String(s), () => {}, obj).showThemeSettings(null);
            const cards = [];
            html.replace(/<button type="button" class="jpf-theme-option[^"]*"[^>]*data-theme="([a-z0-9-]*)"\s+aria-checked="(true|false)">\s*<div class="jpf-theme-preview"[^>]*data-jpf-theme="([a-z0-9-]+)"/g,
                (m, value, checked, shows) => { cards.push({ value: value, checked: checked === 'true', shows: shows }); return m; });
            return { html: html, cards: cards };
        };

        const follow = render(null, 'crimson');
        const shows = follow.cards.map(c => c.shows);
        ok('one card per theme, none twice (' + shows.join(', ') + ')',
           shows.length === serverIds.length && serverIds.every(id => shows.indexOf(id) !== -1));
        const dflt = follow.cards.filter(c => c.shows === 'crimson')[0];
        ok('the server default\'s own card is the one that follows the default',
           !!dflt && dflt.value === '' && follow.cards.filter(c => c.value === '').length === 1);
        ok('and it carries the Default tag, once',
           (follow.html.match(/themeSettings\.default/g) || []).length === 1
           && /theme\.crimson <span class="jpf-theme-option-tag">themeSettings\.default<\/span>/.test(follow.html));
        ok('a profile following the default has that card ticked, and only that one',
           follow.cards.filter(c => c.checked).length === 1 && dflt && dflt.checked);
        const chose = render('glass', 'crimson');
        const glass = chose.cards.filter(c => c.shows === 'glass')[0];
        ok('one that chose a theme has its card ticked, and only that one',
           chose.cards.filter(c => c.checked).length === 1 && glass && glass.checked && glass.value === 'glass');
        const same = render('crimson', 'crimson');
        ok('one that chose the default theme by name still shows one ticked card',
           same.cards.filter(c => c.checked).length === 1);
        ok('the page draws no sheet of its own', /class="create-profile-container jpf-theme-page"/.test(follow.html));
    }
    ok('a pick says nothing on success: the tick moving is the answer',
       !!page && !/themeSettings\.saved/.test(page.body) && !/themeSettings\.saved/.test(SRC));
    ok('choosing the ticked card again sends nothing', !!page && /aria-checked'\) === 'true'\) return;/.test(page.body));

    // Resolved, not string-matched: the page rule has to beat every rule that gives the
    // container a sheet, in every theme, including Glass's frost.
    {
        const pageRule = /\.create-profile-container\.jpf-theme-page\s*\{([^}]*)\}/.exec(CSS);
        const body = pageRule ? pageRule[1] : '';
        ok('the Theme page drops the sheet in every theme',
           /background:\s*transparent/.test(body) && /border-color:\s*transparent/.test(body)
           && /box-shadow:\s*none/.test(body) && /(^|[^-])backdrop-filter:\s*none/.test(body));
        const at = pageRule ? pageRule.index : -1;
        const rivals = [];
        CSS.replace(/([^{}]*\.create-profile-container[^{}]*)\{([^}]*)\}/g, (m, sel, decl, idx) => {
            if (/jpf-theme-page|::|is-two-col|>/.test(sel)) return m;
            if (!/background|border|box-shadow|backdrop-filter|padding/.test(decl)) return m;
            sel.split(',').map(s => s.trim()).filter(s => /\.create-profile-container$/.test(s)).forEach(s => {
                const spec = [(s.match(/#/g) || []).length,
                              (s.match(/\.|\[|:(?!:)/g) || []).length];
                const beats = spec[0] > 0 || spec[1] > 2 || (spec[1] === 2 && idx > at);
                if (beats) rivals.push(s.slice(0, 70));
            });
            return m;
        });
        ok('and nothing that draws one outranks it' + (rivals.length ? ' (' + rivals.join('; ') + ')' : ''),
           at >= 0 && rivals.length === 0);
    }

    // A picture marked as a cut-out is not clipped to the avatar's box, so an opaque square
    // one stood outside Nightfall's round ring. It has to stay inside the theme's shape.
    {
        const rule = /\.profile-avatar\.is-transparent img\s*\{([^}]*)\}/.exec(CSS);
        ok('a cut-out picture is rounded to the theme\'s avatar shape',
           !!rule && /border-radius:\s*var\(--jpf-avatar-r\)/.test(rule[1]));
        const missing = serverIds.filter(id => !theme[id] || !theme[id]['--jpf-avatar-r']);
        ok('and every theme says what that shape is' + (missing.length ? ' (' + missing.join(', ') + ')' : ''),
           missing.length === 0);
    }

    if (methods.renderThemePreview && constants) {
        const fn = new Function('t', constants + '\nreturn function (' + methods.renderThemePreview.params + ') {'
            + methods.renderThemePreview.body + '};')(k => k);
        const out = fn.call({}, 'Nightfall', 'x-preview');
        ok('a preview wears its theme, by id', /data-jpf-theme="nightfall"/.test(out) && /id="x-preview"/.test(out));
        ok('and is a little profile screen: a title, three tiles and a button',
           (out.match(/class="jpf-theme-preview-tile"/g) || []).length === 3
           && /jpf-theme-preview-name/.test(out) && /jpf-theme-preview-button/.test(out));
    }
}

// ── 11. The seasonal themes' decoration ─────────────────────────────────────────
console.log('\n── The seasonal themes carry their season, Starlight its stars ─');
{
    // Named "seasonal" for the three it began with; Starlight's sparse starfield joined
    // them in the 1.7.2 theme review.
    const SEASONAL = { harvest: 'leaves', snowfall: 'snow', bloom: 'blossoms', starlight: 'stars' };
    Object.keys(SEASONAL).forEach(id => {
        const d = theme[id] || {};
        ok(id + ' draws its ' + SEASONAL[id] + ' as an inline picture, nothing fetched',
           /^url\("data:image\/svg\+xml,/.test(d['--jpf-deco'] || '') && /px$/.test(d['--jpf-deco-size'] || ''));
    });
    // A theme that forgets to say "none" inherits the page's: a Crimson preview on a
    // Snowfall page would snow. Each says it in its own block. Not a shared block: its
    // two attribute selectors outrank a theme block's one, so a none there beat the snow
    // everywhere, which is how the first build of this shipped no decoration at all. The
    // "no rule but a theme's own block" check above holds that.
    const plain = serverIds.filter(id => !SEASONAL[id] && theme[id]['--jpf-deco'] !== 'none');
    ok('every other theme says none in its own block' + (plain.length ? ' (not ' + plain.join(', ') + ')' : ''),
       plain.length === 0);

    const layer = RULES.filter(r => !r.at && r.selectors.indexOf('#profiles-gate-overlay::before') !== -1)[0];
    ok('the decoration is a layer under the gate\'s content, which it never takes a click from',
       !!layer && layer.decls['z-index'] === '-1' && layer.decls['pointer-events'] === 'none'
       && /var\(--jpf-deco\)/.test(layer.decls['background-image'] || ''));
    ok('and not part of the gate background the startup cover copies',
       serverIds.every(id => !/url\(/.test(theme[id]['--jpf-gate-bg'] || '')));
    const prev = RULES.filter(r => !r.at && r.selectors.indexOf('.jpf-theme-preview::before') !== -1)[0];
    ok('a preview wears its season too', !!prev && /var\(--jpf-deco\)/.test(prev.decls['background-image'] || ''));

    // The snow moves. Only where that is cheap and wanted.
    const anim = /@media \(prefers-reduced-motion: no-preference\)\s*\{\s*\[data-jpf-theme="snowfall"\]:not\(\.jpf-tv\) #profiles-gate-overlay::before\s*\{([^}]*)\}/.exec(CSS);
    // The keyframes are named for the tile they travel, since any theme's pattern may drift.
    ok('the snow falls only for people who have not asked for less motion, and not on a TV',
       !!anim && /animation:\s*jpf-drift-240 /.test(anim[1]));
    ok('by transform, which the compositor moves without repainting',
       /@keyframes jpf-drift-240\s*\{\s*from\s*\{\s*transform:\s*translateY\(0\);\s*\}\s*to\s*\{\s*transform:\s*translateY\(240px\);/.test(CSS)
       && !!anim && /top:\s*-240px/.test(anim[1]));
    ok('and loops on one tile of it, so it joins up', theme.snowfall['--jpf-deco-size'] === '240px');
    const unguarded = /animation:\s*jpf-drift-/g;
    let m, count = 0;
    while ((m = unguarded.exec(CSS)) !== null) count++;
    ok('nowhere else starts it', count === 2);
}

// ── 12. Previews fit their cards on a phone ─────────────────────────────────────
// Reported from a phone: two cards to a row, and the tiles and Crimson's spaced capitals
// ran out past the preview and the card. Computed, not eyeballed: the narrowest card,
// less every padding and border inside it, against three tiles, their gaps and the
// widest ring any theme draws on them.
console.log('\n── A preview fits the narrowest card ──────────────────────────');
{
    const px = v => parseFloat(String(v || '0'));
    const phone = CSS.slice(CSS.indexOf('@media (max-width: 480px)', CSS.indexOf('.jpf-theme-preview-button')));
    const block = /@media \(max-width: 480px\)\s*\{([\s\S]*?)\n\}/.exec(phone);
    const body = block ? block[1] : '';
    const get = (sel, prop) => {
        const r = new RegExp(sel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*\\{([^}]*)\\}').exec(body);
        const v = r && new RegExp('(?:^|;|\\s)' + prop + ':\\s*([^;]+)').exec(r[1]);
        return v ? v[1].trim() : null;
    };
    const card = /\.jpf-theme-grid > \.jpf-theme-option\s*\{[^}]*min-width:\s*(\d+)px/.exec(CSS);
    const opt = /(?:^|\n)\.jpf-theme-option\s*\{([^}]*)\}/.exec(CSS);
    const tile = px(get('.jpf-theme-preview-tile', 'width'));
    const gap = px(get('.jpf-theme-preview-tiles', 'gap'));
    const pad = (get('.jpf-theme-preview', 'padding') || '').split(/\s+/);
    const padX = px(pad[1] || pad[0]);
    const optPad = opt ? px((/padding:\s*([^;]+)/.exec(opt[1]) || [])[1]) : 0;
    const optBorder = opt ? px((/border:\s*([^;]+)/.exec(opt[1]) || [])[1]) : 0;
    const inner = (card ? +card[1] : 0) - 2 * optPad - 2 * optBorder - 2 * padX - 2;
    // The widest ring: a theme's ring width on the first tile, or Nightfall's 4px pair.
    const ringMax = Math.max(4, ...serverIds.map(id => px(theme[id]['--jpf-ring-w'])));
    const need = 3 * tile + 2 * gap + 2 * ringMax;
    ok('three tiles and the widest ring, ' + need + 'px, fit inside the narrowest card\'s preview, ' + inner + 'px',
       tile > 0 && inner > 0 && need <= inner);
    // Every rule on the preview alone, merged in order: the decoration's position rule
    // and the layout rule are separate.
    const prev = Object.assign({}, ...RULES.filter(r => !r.at && r.selectors.length === 1
        && r.selectors[0] === '.jpf-theme-preview').map(r => r.decls));
    ok('and the preview clips anything wider instead of spilling over the card',
       prev.overflow === 'hidden' && prev['min-width'] === '0');
    const btn = RULES.filter(r => !r.at && r.selectors.indexOf('.jpf-theme-preview-button') !== -1 && r.selectors.length === 1)[0];
    ok('its button stays inside the preview, and wraps rather than being cut to MANAGE PR...',
       !!btn && btn.decls['max-width'] === '100%' && btn.decls['white-space'] === 'normal'
       && get('.jpf-theme-preview-button', 'white-space') === null);
}

// ── 13. What themeCss writes does not win by coming last ────────────────────────
console.log('\n── The generated rules win on specificity, never on order ────');
/* Until 1.7.2 the theme rules sat inside styles.css, before the rules that override them.
 * Now themeCss writes them after the whole sheet, so every tie on specificity that the
 * sheet used to win is now won by the theme. The first build of this put Glass's frost
 * back on the Theme page that way, past a check that string-matched the page rule.
 *
 * So, one pair at a time: a generated rule and a sheet rule that name the same element
 * (the leading class or id of the last compound, and its pseudo-element) and set a
 * property in common must not be equal on specificity. Equal means the outcome is decided
 * by where the files sit, which nobody reading either one can see. Only checked when the
 * script writes themes; an older build's rules are all in the sheet and ordered there. */
if (SRC.indexOf(L.THEMES_MARKER) !== -1) {
    const strip = css => css.replace(/\/\*[\s\S]*?\*\//g, '');
    const flat = css => {
        const out = [];
        let i = 0, start = 0, at = null;
        while (i < css.length) {
            const c = css[i];
            if (c === '{') {
                const head = css.slice(start, i).trim();
                if (head.charAt(0) === '@') { at = head; start = i + 1; i++; continue; }
                const end = css.indexOf('}', i);
                const decls = {};
                css.slice(i + 1, end).split(';').forEach(d => {
                    const k = d.indexOf(':');
                    if (k > 0) decls[d.slice(0, k).trim()] = true;
                });
                head.split(',').map(s => s.trim().replace(/\s+/g, ' ')).forEach(s => out.push({ s: s, decls: decls, at: at }));
                i = end + 1; start = i; continue;
            }
            if (c === '}') { at = null; start = i + 1; }
            i++;
        }
        return out;
    };
    // :not() counts as its argument, as in the spec.
    const spec = s => {
        const x = s.replace(/:not\(([^)]*)\)/g, ' $1');
        return [(x.match(/#[\w-]+/g) || []).length,
            (x.match(/\.[\w-]+|\[[^\]]*\]|:(?!:)[\w-]+/g) || []).length,
            (x.replace(/\[[^\]]*\]/g, '').match(/(^|[\s>+~])[a-z]+/g) || []).length].join(',');
    };
    const subject = s => {
        const parts = s.replace(/\[data-jpf-theme[^\]]*\]/g, '').trim().split(/[\s>+~]+/);
        const tail = parts[parts.length - 1];
        const m = /^[#.]?[\w-]+/.exec(tail.replace(/:not\([^)]*\)/g, ''));
        return (m ? m[0] : '') + ((/::[\w-]+/.exec(tail) || [''])[0]);
    };
    const sheet = flat(strip(L.stylesOnly())).filter(r => !/data-jpf-theme/.test(r.s));
    const generated = flat(strip(L.themesCss(SRC))).filter(r =>
        !/^(:root|body|\[data-jpf-theme="[a-z0-9-]+"\]( body)?|from|to)$/.test(r.s));
    ok('there are generated rules to compare (' + generated.length + ')', generated.length >= 20);
    const ties = [];
    generated.forEach(g => sheet.forEach(q => {
        if (subject(g.s) !== subject(q.s) || spec(g.s) !== spec(q.s)) return;
        const common = Object.keys(g.decls).filter(k => q.decls[k]);
        if (common.length) ties.push(g.s + '  vs  ' + q.s + ' [' + common.join(', ') + ']');
    }));
    ok('no generated rule ties with a sheet rule on the same element and property'
       + (ties.length ? ' (' + ties.length + ': ' + ties[0] + ')' : ''), ties.length === 0);
}

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
