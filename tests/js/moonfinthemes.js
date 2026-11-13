#!/usr/bin/env node
/*
 * Moonfin theme files: the theme editor writes one Moonfin's editor can load, and reads
 * one it wrote (2026-11-12).
 *
 *     node tests/js/moonfinthemes.js [/old/profilesDashboard.html]
 *
 * The dashboard keeps the conversion between "// moonfin:begin" and "// moonfin:end" as
 * plain functions, so it runs here with no page. The file rules below are written from
 * Moonfin's published format, not from the code under test.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const dashboardPath = process.argv[2] || path.join(__dirname, '..', '..', 'Web', 'profilesDashboard.html');
const themesDir = path.join(__dirname, '..', '..', 'Web', 'themes');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// ── The checker: Moonfin's rules, from the format's description ────────────────────
const COLOR = /^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$/;
const COLOR_KEYS = ['background', 'onBackground', 'surface', 'onSurface', 'surfaceVariant', 'scrim', 'accent', 'onAccent',
    'buttonNormal', 'buttonFocused', 'buttonDisabled', 'buttonActive', 'onButtonNormal', 'onButtonFocused', 'onButtonDisabled',
    'inputBackground', 'inputFocused', 'inputBorder', 'inputBorderFocused', 'rangeTrack', 'rangeProgress', 'rangeThumb',
    'seekbarBuffered', 'badgeBackground', 'onBadge', 'badgeUnplayed', 'badgeWatched', 'recordingActive', 'recordingScheduled'];
const SEMANTIC_KEYS = ['statusAvailable', 'statusRequested', 'statusPending', 'statusDownloading', 'mediaTypeBadgeMovie', 'mediaTypeBadgeShow'];
const BOOK_KEYS = ['background', 'accent', 'mutedText', 'primaryText', 'sectionTitle', 'divider', 'placeholder', 'shadow',
    'gradientTop', 'gradientBottom', 'inactiveChip'];

function check(file) {
    const errs = [];
    const bad = function (m) { errs.push(m); };
    const isObj = function (v) { return v !== null && typeof v === 'object' && !Array.isArray(v); };
    const color = function (where, v) { if (typeof v !== 'string' || !COLOR.test(v)) bad(where + ' is not a colour: ' + JSON.stringify(v)); };
    const range = function (where, v, lo, hi) { if (typeof v !== 'number' || !(v >= lo && v <= hi)) bad(where + ' is outside ' + lo + '..' + hi + ': ' + v); };
    if (!isObj(file)) return ['not an object'];
    if (file.schemaVersion !== undefined && (!Number.isInteger(file.schemaVersion) || file.schemaVersion > 1)) bad('schemaVersion');
    if (file.exportedByEditorVersion !== undefined && typeof file.exportedByEditorVersion !== 'string') bad('exportedByEditorVersion');
    if (typeof file.id !== 'string' || !/^[a-z0-9_-]{2,40}$/.test(file.id)) bad('id: ' + file.id);
    if (typeof file.displayName !== 'string' || !file.displayName.trim() || /<script/i.test(file.displayName)) bad('displayName');
    ['description', 'fontFamily'].forEach(function (k) { if (file[k] !== undefined && typeof file[k] !== 'string') bad(k); });
    ['transparentNavbarSurface', 'isGlass', 'isPixel'].forEach(function (k) { if (file[k] !== undefined && typeof file[k] !== 'boolean') bad(k); });
    if (!isObj(file.colors)) bad('colors'); else {
        COLOR_KEYS.forEach(function (k) { color('colors.' + k, file.colors[k]); });
        ['error', 'card'].forEach(function (k) { if (file.colors[k] !== undefined) color('colors.' + k, file.colors[k]); });
    }
    if (!isObj(file.semantic)) bad('semantic'); else {
        SEMANTIC_KEYS.forEach(function (k) { color('semantic.' + k, file.semantic[k]); });
        if (file.semantic.statusError !== undefined) color('semantic.statusError', file.semantic.statusError);
    }
    if (!isObj(file.book)) bad('book'); else {
        BOOK_KEYS.forEach(function (k) { color('book.' + k, file.book[k]); });
        const p = file.book.placeholderPalette;
        if (!Array.isArray(p) || p.length < 1 || p.length > 16) bad('book.placeholderPalette length');
        else p.forEach(function (c, i) { color('book.placeholderPalette[' + i + ']', c); });
    }
    const b = file.borders;
    if (!isObj(b)) bad('borders'); else {
        ['cardBorder', 'chipBorder', 'focusBorder'].forEach(function (k) {
            if (!isObj(b[k])) { bad('borders.' + k); return; }
            color('borders.' + k + '.color', b[k].color);
            range('borders.' + k + '.width', b[k].width, 0, 16);
        });
        ['cardRadius', 'chipRadius'].forEach(function (k) {
            if (isObj(b[k])) ['topLeft', 'topRight', 'bottomLeft', 'bottomRight'].forEach(function (c) { range('borders.' + k + '.' + c, b[k][c], 0, 9999); });
            else range('borders.' + k, b[k], 0, 9999);
        });
        color('borders.chipBackground', b.chipBackground);
        if (!Array.isArray(b.focusGlow) || b.focusGlow.length > 8) bad('borders.focusGlow');
        else b.focusGlow.forEach(function (g, i) { shadow('borders.focusGlow[' + i + ']', g, true); });
        if (b.navBorder !== undefined && b.navBorder !== null) {
            if (!isObj(b.navBorder)) bad('borders.navBorder'); else { color('borders.navBorder.color', b.navBorder.color); range('borders.navBorder.width', b.navBorder.width, 0, 16); }
        }
    }
    function shadow(where, g, spread) {
        if (!isObj(g)) { bad(where); return; }
        color(where + '.color', g.color);
        range(where + '.blurRadius', g.blurRadius, 0, 64);
        range(where + '.offsetX', g.offsetX, -500, 500);
        range(where + '.offsetY', g.offsetY, -500, 500);
        if (spread) range(where + '.spreadRadius', g.spreadRadius, -32, 32);
        else if (g.spreadRadius !== undefined && g.spreadRadius !== 0) bad(where + '.spreadRadius must be 0');
    }
    if (file.textGlow !== undefined) {
        if (!Array.isArray(file.textGlow)) bad('textGlow'); else file.textGlow.forEach(function (g, i) { shadow('textGlow[' + i + ']', g, false); });
    }
    if (file.navColorCycle !== undefined) {
        if (!Array.isArray(file.navColorCycle) || file.navColorCycle.length > 16) bad('navColorCycle');
        else file.navColorCycle.forEach(function (c, i) { color('navColorCycle[' + i + ']', c); });
    }
    return errs;
}

// ── Loading the dashboard's conversion ─────────────────────────────────────────────
function load() {
    const src = fs.readFileSync(dashboardPath, 'utf8');
    const a = src.indexOf('// moonfin:begin');
    const b = src.indexOf('// moonfin:end');
    if (a < 0 || b < a) return null;
    const ctx = {};
    vm.createContext(ctx);
    vm.runInContext(src.slice(a, b) + '\nthis.api = { ' +
        ['moonfinExport', 'moonfinImport', 'moonfinBaseId', 'mfIsMoonfin'].map(function (n) {
            return n + ': typeof ' + n + " === 'function' ? " + n + ' : undefined';
        }).join(', ') + ' };', ctx);
    return ctx.api;
}

const api = load();
ok('the dashboard has the Moonfin conversion', !!api && typeof api.moonfinExport === 'function',
    'no "// moonfin:begin" block with moonfinExport in ' + dashboardPath);
if (!api || typeof api.moonfinExport !== 'function') {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

// The checker has to be able to go red: a file with one bad colour and one missing key.
(function () {
    const t = JSON.parse(fs.readFileSync(path.join(themesDir, 'bonfire.json'), 'utf8'));
    const good = api.moonfinExport(t, 'Bonfire');
    const broken = JSON.parse(JSON.stringify(good));
    broken.colors.accent = '#FFB347FF00';
    delete broken.colors.scrim;
    broken.borders.focusGlow = new Array(9).fill(good.borders.focusGlow[0] || { color: '#FF000000', blurRadius: 1, offsetX: 0, offsetY: 0, spreadRadius: 0 });
    ok('the checker refuses a file with a bad colour, a missing key and nine glows', check(broken).length >= 3, JSON.stringify(check(broken)));
})();

// ── Export: every built-in theme passes every rule ─────────────────────────────────
const files = fs.readdirSync(themesDir).filter(function (f) { return /\.json$/.test(f) && f !== 'fonts.json'; });
ok('there are built-in themes to export', files.length >= 12, String(files.length));
files.forEach(function (f) {
    const theme = JSON.parse(fs.readFileSync(path.join(themesDir, f), 'utf8'));
    let out = null;
    try { out = api.moonfinExport(theme, f.replace(/\.json$/, '')); } catch (e) { ok(f + ' exports', false, String(e && e.stack)); return; }
    const errs = check(JSON.parse(JSON.stringify(out)));
    ok(f + ' exports a file that passes the format', errs.length === 0, errs.slice(0, 4).join('; '));
});

// ── Export: the mapping, with a theme of made-up values ────────────────────────────
const mine = {
    schema: 1, id: 'my-test', scheme: 'dark',
    colors: {
        accent: '#10a0f0', accentFocus: '#40c0ff', onAccent: '#001020', text1: '#e0e8f0cc', text2: '#e0e8f099', text3: '#e0e8f066',
        surface1: '#ffffff08', surface2: '#ffffff10', surface3: '#ffffff20', border1: '#ffffff14', border2: '#ffffff26', border3: '#ffffff42',
        panel: '#202830', ring: '#80d0ff', name: '#ffffff', nameFocus: '#ffffff', shadow: '#050a10', danger: '#e04050', warning: '#e0a000'
    },
    background: { fill: '#101820', layers: [] },
    shape: { radius: { small: 4, medium: 8, large: 12 }, avatar: { kind: 'square', radius: 14 }, ringWidth: 4, button: { kind: 'pill', radius: 9 }, input: { radius: 6 } },
    type: { font: { family: 'Lora', fallback: 'serif' } },
    depth: { title: { x: 1, y: 3, blur: 9, opacity: 0.5 } },
    focus: { scale: 1.05, glow: { x: 2, y: 6, blur: 20, color: '#10a0f040' } },
    parts: {
        button2: { fill: '#aabbcc33', text: '#112233', border: { width: 2, style: 'solid', color: '#445566aa' }, focus: { fill: '#778899', text: '#001122', border: { width: 1, style: 'solid', color: '#778899' }, glow: null } },
        input: { fill: '#303840', line: '#a0b0c080' }, card: { fill: '#ffffff08', line: '#99aabb55' }
    },
    decorations: []
};
const mf = api.moonfinExport(mine, 'My test');
ok('the file is named by the theme id', mf.id === 'my-test' && mf.displayName === 'My test');
ok('colours with alpha are written alpha first', mf.colors.onBackground === '#CCE0E8F0', mf.colors.onBackground);
ok('colours without alpha have six digits', mf.colors.accent === '#10A0F0' && mf.colors.background === '#101820', mf.colors.accent + ' ' + mf.colors.background);
ok('the second button maps to buttonNormal and buttonFocused', mf.colors.buttonNormal === '#33AABBCC' && mf.colors.buttonFocused === '#778899' &&
    mf.colors.onButtonNormal === '#112233' && mf.colors.onButtonFocused === '#001122', JSON.stringify([mf.colors.buttonNormal, mf.colors.buttonFocused]));
ok('the input and the ring map', mf.colors.inputBackground === '#303840' && mf.colors.inputBorder === '#80A0B0C0' && mf.colors.inputBorderFocused === '#80D0FF');
ok('danger is the error colour twice', mf.colors.error === '#E04050' && mf.semantic.statusError === '#E04050');
ok('the panel is the surface', mf.colors.surface === '#202830');
ok('the ring and its width are the focus border', mf.borders.focusBorder.color === '#80D0FF' && mf.borders.focusBorder.width === 4);
ok('the button border is the chip border', mf.borders.chipBorder.color === '#AA445566' && mf.borders.chipBorder.width === 2);
ok('the card line is the card border', mf.borders.cardBorder.color === '#5599AABB', mf.borders.cardBorder.color);
ok('a pill button is radius 999 and a square avatar keeps its radius', mf.borders.chipRadius === 999 && mf.borders.cardRadius === 14, mf.borders.chipRadius + ' ' + mf.borders.cardRadius);
ok('the focus glow is the first glow', mf.borders.focusGlow.length === 1 && mf.borders.focusGlow[0].color === '#4010A0F0' &&
    mf.borders.focusGlow[0].blurRadius === 20 && mf.borders.focusGlow[0].offsetX === 2 && mf.borders.focusGlow[0].offsetY === 6, JSON.stringify(mf.borders.focusGlow));
ok('the title shadow is the text glow, with no spread', mf.textGlow.length === 1 && mf.textGlow[0].blurRadius === 9 && mf.textGlow[0].offsetY === 3 &&
    mf.textGlow[0].color === '#80050A10' && !mf.textGlow[0].spreadRadius, JSON.stringify(mf.textGlow));
ok('the font family is carried', mf.fontFamily === 'Lora');
ok('no focus glow gives an empty list', api.moonfinExport(Object.assign({}, mine, { focus: { scale: 1, glow: null } }), 'x').borders.focusGlow.length === 0);
ok('a one-letter id is made long enough', check(api.moonfinExport(Object.assign({}, mine, { id: 'a' }), 'x')).length === 0);
ok('a name with a script tag is cleaned', !/<script/i.test(api.moonfinExport(mine, 'a<script>b').displayName));
ok('the glass theme says so', api.moonfinExport(JSON.parse(fs.readFileSync(path.join(themesDir, 'glass.json'), 'utf8')), 'Glass').isGlass === true);

// ── Import ─────────────────────────────────────────────────────────────────────────
ok('the dashboard has the Moonfin import', typeof api.moonfinImport === 'function' && typeof api.moonfinBaseId === 'function' && typeof api.mfIsMoonfin === 'function');
if (typeof api.moonfinImport !== 'function' || typeof api.moonfinBaseId !== 'function' || typeof api.mfIsMoonfin !== 'function') {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
const readJson = function (p) { return JSON.parse(fs.readFileSync(p, 'utf8')); };
const fixtures = path.join(__dirname, '..', 'moonfin');
const fonts = readJson(path.join(themesDir, 'fonts.json')).fonts.map(function (f) { return { family: f.family, fallback: f.fallback }; });
const builtIn = function (id) { return readJson(path.join(themesDir, id + '.json')); };
const handFile = readJson(path.join(fixtures, 'dark.theme.json'));
ok('the hand-written Moonfin file passes the format', check(handFile).length === 0, check(handFile).join('; '));
const copy = function (o) { return JSON.parse(JSON.stringify(o)); };
const importAs = function (file) { return api.moonfinImport(file, builtIn(api.moonfinBaseId(file)), fonts); };

// The three files the C# harness (tests/cs/themes) hands to the server's validator.
const light = copy(handFile);
Object.assign(light.colors, { background: '#F4F6F8', onBackground: '#14202B', surface: '#FFFFFF', onSurface: '#14202B', accent: '#0B7A5C', onAccent: '#FFFFFF',
    onButtonNormal: '#14202B', buttonNormal: '#1A14202B', buttonFocused: '#0B7A5C', onButtonFocused: '#FFFFFF', error: '#B3261E' });
Object.assign(light.colors, { inputBackground: '#FFFFFF', inputBorder: '#59000000' });
light.borders.focusBorder.color = '#0B7A5C';
light.id = 'pine_day';
light.displayName = 'Pine Day';
const glass = copy(handFile);
glass.isGlass = true;
glass.id = 'pine_glass';
glass.displayName = 'Pine Glass';
const outputs = { dark: handFile, light: light, glass: glass };
Object.keys(outputs).forEach(function (k) {
    const r = importAs(outputs[k]);
    const file = path.join(fixtures, k + '.bonfire.json');
    if (process.env.MOONFIN_WRITE === '1' && r) fs.writeFileSync(file, JSON.stringify(r.draft, null, 2) + '\n');
    ok('the ' + k + ' file imports', !!r);
    ok('and ' + k + '.bonfire.json holds what it imports to (the server checks that file)',
        !!r && fs.existsSync(file) && JSON.stringify(readJson(file)) === JSON.stringify(r.draft),
        'run MOONFIN_WRITE=1 node tests/js/moonfinthemes.js to rewrite it');
});

ok('a dark file starts from Bonfire, a light one from the light Jellyfin, a glass one from glass',
    api.moonfinBaseId(handFile) === 'bonfire' && api.moonfinBaseId(light) === 'jellyfin-light' && api.moonfinBaseId(glass) === 'glass');
const imp = importAs(handFile);
const d = imp && imp.draft;
const at = function (o, p) { return p.split('.').reduce(function (x, k) { return x == null ? x : x[k]; }, o); };
const same = function (p, want) { return JSON.stringify(at(d, p)) === JSON.stringify(want); };
ok('the name and id come from the file', same('name', { text: 'Pine Night' }) && d.id === 'pine-night' && d.schema === 1 && d.scheme === 'dark' && same('aliases', []));
ok('the background is the colour and the base\'s gradient is gone', same('background.fill', '#0e1a17') && same('background.layers', []));
ok('surface is the panel; text and accent map', same('colors.panel', '#16282a') && same('colors.text1', '#f0f7f4') && same('colors.accent', '#4fd1a5') && same('colors.onAccent', '#06201a'));
ok('the second button maps, alpha moved to the end', same('parts.button2.fill', '#aabbcc33') && same('parts.button2.text', '#f0f7f4') &&
    same('parts.button2.focus.fill', '#4fd1a5') && same('parts.button2.focus.text', '#06201a'), JSON.stringify(at(d, 'parts.button2')));
ok('the chip border and the input map', same('parts.button2.border.color', '#aabbcc66') && same('parts.button2.border.width', 2) &&
    same('parts.input.fill', '#1c2f31') && same('parts.input.line', '#ffffff40'));
ok('the focus border is the ring', same('colors.ring', '#9be8cf') && same('shape.ringWidth', 4));
ok('error is danger; the card border is the card line', same('colors.danger', '#ff7b7b') && same('parts.card.line', '#ffffff2e'));
ok('radius 18 is a square avatar of 18 and 999 is a pill', same('shape.avatar', { kind: 'square', radius: 18 }) && at(d, 'shape.button.kind') === 'pill', JSON.stringify(at(d, 'shape')));
ok('the focus glow and the text glow map', same('focus.glow', { x: 0, y: 8, blur: 24, color: '#4fd1a566', spread: 2 }) && same('depth.title', { x: 0, y: 2, blur: 12, opacity: 0.5 }),
    JSON.stringify([at(d, 'focus.glow'), at(d, 'depth.title')]));
ok('a font Bonfire has is taken, and nothing is said', same('type.font.family', 'Quicksand') && imp.font === null);
const odd = copy(handFile);
odd.fontFamily = 'Zzyzx Display';
const oddImp = importAs(odd);
ok('a font Bonfire does not have keeps the base font, and is named', !!oddImp && oddImp.font === 'Zzyzx Display' &&
    at(oddImp.draft, 'type.font.family') === 'Outfit');
const noGlow = copy(handFile);
noGlow.borders.focusGlow = [];
delete noGlow.textGlow;
const ng = importAs(noGlow);
ok('no glow in the file is none in the draft', !!ng && at(ng.draft, 'focus.glow') === null && at(ng.draft, 'depth.title') === null);

// Bonfire -> Moonfin -> Bonfire keeps a colour whose alpha is not ff.
const back = api.moonfinImport(api.moonfinExport(mine, 'My test'), builtIn('bonfire'), fonts);
const bd = back && back.draft;
[['colors.text1', '#e0e8f0cc'], ['parts.button2.fill', '#aabbcc33'], ['parts.button2.border.color', '#445566aa'], ['focus.glow.color', '#10a0f040'],
    ['parts.input.line', '#a0b0c080'], ['parts.card.line', '#99aabb55'], ['colors.accent', '#10a0f0'], ['colors.panel', '#202830'], ['colors.ring', '#80d0ff']].forEach(function (p) {
    ok('round trip keeps ' + p[0] + ' ' + p[1], !!bd && at(bd, p[0]) === p[1], bd && at(bd, p[0]));
});
ok('round trip keeps the font, the ring width and the title shadow', !!bd && at(bd, 'type.font.family') === 'Lora' && at(bd, 'shape.ringWidth') === 4 &&
    at(bd, 'depth.title.blur') === 9 && at(bd, 'depth.title.opacity') === 0.5);

// A bad colour refuses the whole file.
[['colors.accent', '#12345'], ['colors.onAccent', '#GG0000'], ['colors.scrim', '#FFB347FF00'], ['colors.background', 'red'], ['colors.surface', 16777215],
    ['semantic.statusPending', '#FFF'], ['book.placeholderPalette', ['#112233', 'nope']], ['borders.cardBorder.color', '#1234567'], ['borders.chipBackground', ''],
    ['borders.focusGlow.0.color', '#xyz'], ['textGlow.0.color', '80000000']].forEach(function (p) {
    const f = copy(handFile);
    const keys = p[0].split('.');
    let o = f;
    for (let i = 0; i < keys.length - 1; i++) o = o[keys[i]];
    o[keys[keys.length - 1]] = p[1];
    ok('a bad colour at ' + p[0] + ' is refused', api.moonfinImport(f, builtIn('bonfire'), fonts) === null);
});
const v2 = copy(handFile);
v2.schemaVersion = 2;
ok('a newer schemaVersion is refused', api.moonfinImport(v2, builtIn('bonfire'), fonts) === null);
ok('a Bonfire theme file is not taken for a Moonfin one', !api.mfIsMoonfin(builtIn('bonfire')) && !api.mfIsMoonfin({ colors: { accent: '#000000' } }));
ok('a Moonfin file is recognised', api.mfIsMoonfin(handFile) && api.mfIsMoonfin(api.moonfinExport(mine, 'x')));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
