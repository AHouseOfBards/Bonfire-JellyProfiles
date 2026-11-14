/*
 * The settings page's theme previews: a small card beside each built-in theme in the Themes
 * list, and one under the Default theme select that follows what is selected.
 *
 * Runs the page's own functions, lifted out of the dashboard like adminthemes.js does, and
 * the client script's real theme generator for the sheets. What it holds:
 *
 *   1. the slot for the card is in the markup, under the select it follows;
 *   2. the card is the editor's card, from one function (not a second copy of its markup),
 *      and a built-in's row carries one only when its sheet could be drawn;
 *   3. each card wears an id of its own, with a sheet written for it, so it does not depend
 *      on the theme being the one the page wears, and a hidden built-in still gets one;
 *   4. the card under the select follows the selection, and is empty for a theme with no
 *      sheet rather than showing the wrong one;
 *   5. nothing an administrator typed reaches the card as markup.
 *
 *     node tests/js/adminthemepreview.js [/old/profiles.js /old/profilesDashboard.html]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name + (detail ? '  - ' + detail : '')); console.log('  FAIL  ' + name + (detail ? '  - ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);
const markup = html.slice(0, html.indexOf('<script'));
const clientSrc = L.readProfiles();

const BUILT_IN = fs.readdirSync(L.themesDir()).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
    .map(f => JSON.parse(fs.readFileSync(path.join(L.themesDir(), f), 'utf8')));

function lift(names, globals) {
    const g = Object.assign({ ApiClient: {}, window: {}, document: {}, Dashboard: {} }, globals || {});
    let src;
    try {
        src = L.dashboardI18n(script) + names.map(n => L.extractFunction(script, n)).join('\n');
    } catch (e) {
        return null;
    }
    const keys = Object.keys(g);
    // eslint-disable-next-line no-new-func
    const factory = new Function(keys.join(', '),
        'const THEME_PREVIEW_ID = "jpf--editor-preview";\n' + src + '\nreturn { ' + names.map(n => n + ': ' + n).join(', ') + ' };');
    return factory.apply(null, keys.map(k => g[k]));
}

// ── 1. The slot ──────────────────────────────────────────────────────────
console.log('\n── The card under the default select ───────────────────────────');
{
    const select = markup.indexOf('id="defaultTheme"');
    const slot = markup.indexOf('id="defaultThemePreview"');
    const next = markup.indexOf('id="allowProfileThemes"');
    ok('the slot is in the markup, after the select and before the next setting',
       slot !== -1 && slot > select && slot < next, 'select ' + select + ', slot ' + slot + ', next ' + next);
}

// ── 2. One card ─────────────────────────────────────────────────────────
console.log('\n── The card is the editor\'s card ───────────────────────────────');
const DATA = {
    'default': 'bonfire',
    builtInNames: { bonfire: 'Bonfire (night sky)', crimson: 'Crimson (velvet theatre)', glass: 'Glass (colour blur)' },
    builtIn: [{ id: 'bonfire', hidden: false }, { id: 'crimson', hidden: false }, { id: 'glass', hidden: true }],
    custom: []
};
const fns = lift(['escapeHtml', 'getThemePath', 'themeDisplayName', 'themeShortName', 'themePreviewCardHtml', 'builtInThemeRowsHtml']);
ok('the card and the rows are built by functions a test can run', !!fns);
if (fns) {
    const card = fns.themePreviewCardHtml('jpf--list-crimson', fns.themeShortName('Crimson (velvet theatre)'));
    ok('a card is the preview: a name, three tiles, a button, wearing its theme',
       /class="jpf-theme-preview" data-jpf-theme="jpf--list-crimson"/.test(card)
       && (card.match(/class="jpf-theme-preview-tile"/g) || []).length === 3
       && /jpf-theme-preview-button/.test(card) && />Crimson</.test(card) && card.indexOf('velvet') === -1);
    ok('it is hidden from a screen reader, which would otherwise read a picture of a screen', /aria-hidden="true"/.test(card));

    const none = fns.builtInThemeRowsHtml(DATA);
    ok('rows drawn without sheets carry no card', none.indexOf('jpf-theme-preview') === -1);
    const some = fns.builtInThemeRowsHtml(DATA, { crimson: 'jpf--list-crimson', glass: 'jpf--list-glass' });
    const rowsOf = h => h.split('<div class="jpf-ob-row">').slice(1);
    const byId = {};
    rowsOf(some).forEach(r => { const m = /data-theme="([^"]+)"/.exec(r); if (m) byId[m[1]] = r; });
    ok('a built-in with a sheet has its card in its own row',
       /data-jpf-theme="jpf--list-crimson"/.test(byId.crimson) && /data-jpf-theme="jpf--list-glass"/.test(byId.glass));
    ok('and one without has none', byId.bonfire.indexOf('jpf-theme-preview') === -1);
    ok('the card comes first in the row', byId.crimson.indexOf('jpf-theme-preview') < byId.crimson.indexOf('jpf-ob-name'));

    const hostile = fns.themePreviewCardHtml('x" onmouseover="alert(1)', '<img src=x onerror=alert(1)>');
    ok('neither the id nor the name can become markup',
       hostile.indexOf('" onmouseover="') === -1 && hostile.indexOf('<img') === -1 && hostile.indexOf('&lt;img') !== -1);
}
{
    // One source for the markup: the editor calls the function instead of writing its own.
    // The tiles' container and its three tiles, in the one function.
    const copies = script.split('jpf-theme-preview-tile').length - 1;
    ok('the card\'s markup is written once (' + copies + ' mentions of its tiles, four in the function)', copies === 4);
    ok('the editor draws its card with it', /themePreviewCardHtml\(THEME_PREVIEW_ID/.test(script));
}

// ── 3. A sheet for each ─────────────────────────────────────────────────
console.log('\n── Each card has a sheet of its own ────────────────────────────');
{
    const styles = {};
    const created = [];
    const document = {
        head: { appendChild: el => { styles[el.id] = el; } },
        getElementById: id => styles[id] || null,
        createElement: () => { const el = { id: '', textContent: '' }; created.push(el); return el; }
    };
    const shown = ['bonfire', 'crimson'].map(id => BUILT_IN.filter(t => t.id === id)[0]);
    const hiddenFile = BUILT_IN.filter(t => t.id === 'glass')[0];
    const seen = [];
    const api = {
        css: theme => { seen.push(theme.id); return '[data-jpf-theme="' + theme.id + '"] { color: red; }\n'; },
        builtIns: () => [hiddenFile]
    };
    const lifted = lift(['escapeHtml', 'getThemePath', 'themeDisplayName', 'themeShortName', 'themeBases', 'themeListId', 'writeThemeListCss'],
        { document, window: { BonfireThemePreview: api } });
    ok('the sheets are written by a function a test can run', !!lifted);
    if (lifted) {
        const page = { _themes: { data: Object.assign({}, DATA, {
            custom: [{ id: 'shop', theme: Object.assign({}, shown[0], { id: 'shop' }), problems: [] },
                     { id: 'x" y', theme: Object.assign({}, shown[0], { id: 'x" y' }), problems: [] }] }),
            catalog: { themes: shown } } };
        const ids = lifted.writeThemeListCss(page);
        ok('every built-in with a file gets a card id, the hidden one included (its sheet is not served)',
           ids.bonfire === 'jpf--list-bonfire' && ids.crimson === 'jpf--list-crimson' && ids.glass === 'jpf--list-glass', JSON.stringify(ids));
        ok('so does a custom theme', ids.shop === 'jpf--list-shop');
        ok('an id that is not an id gets none, and no sheet', !('x" y' in ids) && seen.indexOf('jpf--list-x" y') === -1);
        const css = (styles.jpfThemeListCss || {}).textContent || '';
        ok('one style element holds the sheets, each under its card\'s id',
           created.length === 1 && /data-jpf-theme="jpf--list-crimson"/.test(css) && /data-jpf-theme="jpf--list-glass"/.test(css));
        ok('and drawing again replaces it rather than stacking another',
           (lifted.writeThemeListCss(page), created.length === 1));
    }
    // And with the client's real generator, which is what makes it the theme's own look.
    const generator = L.themeGenerator(clientSrc);
    const out = generator.themeCss(Object.assign({}, BUILT_IN.filter(t => t.id === 'starlight')[0], { id: 'jpf--list-starlight' }), L.themesPayload().patterns || {});
    ok('the real generator writes a sheet under that id, with the theme\'s tokens',
       /\[data-jpf-theme="jpf--list-starlight"\]/.test(out) && /--jpf-accent:/.test(out));
}

// ── 4. The card follows the select ──────────────────────────────────────
console.log('\n── The card under the select follows it ────────────────────────');
{
    const render = lift(['escapeHtml', 'getThemePath', 'themeDisplayName', 'themeShortName', 'themePreviewCardHtml', 'renderDefaultThemePreview']);
    ok('the card is drawn by a function a test can run', !!render);
    if (render) {
        const slot = { innerHTML: '' };
        const select = { value: 'crimson' };
        const page = {
            _themes: { data: DATA },
            _themeListIds: { bonfire: 'jpf--list-bonfire', crimson: 'jpf--list-crimson' },
            querySelector: s => (s === '#defaultThemePreview' ? slot : s === '#defaultTheme' ? select : null)
        };
        render.renderDefaultThemePreview(page);
        ok('it shows the selected theme', /data-jpf-theme="jpf--list-crimson"/.test(slot.innerHTML) && />Crimson</.test(slot.innerHTML), slot.innerHTML.slice(0, 80));
        select.value = 'bonfire';
        render.renderDefaultThemePreview(page);
        ok('and the next one when the selection moves', /data-jpf-theme="jpf--list-bonfire"/.test(slot.innerHTML)
           && slot.innerHTML.indexOf('crimson') === -1, slot.innerHTML.slice(0, 80));
        select.value = 'glass';
        render.renderDefaultThemePreview(page);
        ok('a theme with no sheet leaves it empty, not showing another\'s', slot.innerHTML === '', slot.innerHTML.slice(0, 80));
    }
    ok('a change of the select redraws it', /querySelector\('#defaultTheme'\)\.addEventListener\('change', function \(\) \{ renderDefaultThemePreview\(page\); \}\)/.test(script));
    ok('the themes list draws it when it arrives', /renderDefaultThemeOptions\(page\);\s*renderDefaultThemePreview\(page\);/.test(script));
    ok('and the saved default draws it when the configuration arrives after the list',
       /_wantedDefaultTheme;\s*if \(page\._themes\) renderDefaultThemePreview\(page\);/.test(script));
}

console.log();
console.log(fails.length ? fails.map(f => '   - ' + f).join('\n') + '\n' : '', pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
