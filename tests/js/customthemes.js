/*
 * The web client and the administrator's own themes (1.7.2, editor stage B).
 *
 * profiles.js is cached for the life of the server, so a theme an administrator makes
 * cannot be spliced into it like the built-ins. The client reads GET themes?part=custom
 * once a page load, keeps a copy on the device so the next start is already in the
 * theme, and from then on treats a custom theme like a built-in: it normalises to itself,
 * it is offered, its name is shown, its font is declared, its gate background is cached.
 *
 * Run against the real functions and methods out of profiles.js, in a small stand-in for
 * the page. Against 5b837b4 it fails: none of it exists, and normalizeTheme turns every
 * custom theme into Bonfire.
 *
 *   node tests/js/customthemes.js [path/to/profiles.js]
 */
'use strict';

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
function finish() {
    console.log('');
    if (fails.length) { fails.forEach(f => console.log('   - ' + f)); console.log(pass + ' passed, ' + fails.length + ' failed'); process.exit(1); }
    console.log(pass + ' passed, 0 failed');
    process.exit(0);
}

const SRC = L.readProfiles();

function method(name) {
    const at = SRC.indexOf('        ' + name + ': function (');
    if (at === -1) return null;
    const open = SRC.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}' && --depth === 0) {
            return { params: SRC.slice(SRC.indexOf('(', at) + 1, SRC.indexOf(')', at)), body: SRC.slice(open + 1, i) };
        }
    }
    return null;
}

const FUNCS = ['readCustomThemes', 'offeredThemes', 'offeredChoice', 'themeNameText', 'customFontFaceCss', 'customThemesCss'];
const METHODS = ['restoreCustomThemes', 'loadCustomThemes', 'useCustomThemes', 'applyCustomThemesCss', 'applyTheme'];
console.log('\n── The pieces are in the client ──────────────────────────────');
FUNCS.forEach(n => ok(n + ' is in profiles.js', new RegExp('function\\s+' + n + '\\s*\\(').test(SRC)));
METHODS.forEach(n => ok(n + ' is a method', !!method(n)));
if (fails.length) finish();

// A theme file from the repository made into a custom one, as the server would send it.
const payload = L.themesPayload();
function custom(id, from, extra) {
    const t = JSON.parse(JSON.stringify(payload.themes.filter(x => x.id === (from || 'harvest'))[0]));
    t.id = id;
    t.name = { text: 'My ' + id };
    return Object.assign(t, extra || {});
}
const HASH = 'a'.repeat(32) + '.woff2';
const FONT = { family: 'Shop Sign', fallback: 'sans-serif', custom: true,
    files: [{ subset: null, url: '/plugins/profiles/themes/fonts/' + HASH, format: 'woff2', style: 'normal', weight: [400, 400], unicodeRange: null }] };

// The helpers, the generator and the methods in one scope, as they are in the IIFE.
function world(store, fetchAnswer) {
    store = store || {};
    const els = {};
    const appended = [];
    const attrs = {};
    const fetched = [];
    let writes = 0;
    const document = {
        documentElement: { getAttribute: k => (k in attrs ? attrs[k] : null), setAttribute: (k, v) => { attrs[k] = String(v); } },
        getElementById: id => els[id] || null,
        createElement: () => {
            const el = { id: '', _text: '' };
            Object.defineProperty(el, 'textContent', { get() { return this._text; }, set(v) { writes++; this._text = v; } });
            el.parentNode = { removeChild: e => { delete els[e.id]; } };
            return el;
        },
        head: { appendChild: el => { els[el.id] = el; appended.push(el.id); } }
    };
    const localStorage = {
        getItem: k => (k in store ? store[k] : null),
        setItem: (k, v) => { store[k] = String(v); },
        removeItem: k => { delete store[k]; }
    };
    const fetch = url => {
        fetched.push(url);
        return Promise.resolve({ ok: true, json: () => Promise.resolve(fetchAnswer) });
    };
    const CSS = { supports: () => true };
    const t = key => key;
    const pluginUrl = v => v;
    const obj = {
        config: { customThemesKey: 'jellyfin_profiles_custom_themes' },
        settingsCalls: [],
        _setThemeSettings(prefs) { this.settingsCalls.push(prefs); this.applyTheme(prefs.theme); }
    };
    const gen = 'const BONFIRE_THEMES = ' + JSON.stringify(payload) + ';\n'
        + ['themeCss', 'servedFontName', 'gateBackground', 'customFontFaceCss', 'customThemesCss']
            .map(n => L.extractFunction(SRC, n)).join('\n');
    const factory = new Function('document', 'localStorage', 'fetch', 'CSS', 't', 'pluginUrl', 'obj', 'window',
        L.themeHelpers(SRC) + '\n' + gen + '\n'
        + METHODS.map(n => 'obj.' + n + ' = function (' + method(n).params + ') {' + method(n).body + '};').join('\n')
        + '\nreturn { obj, helpers: { readCustomThemes, offeredThemes, offeredChoice, themeName, themeNameText, normalizeTheme, gateBackground, customFontFaceCss, themeCss }, state: () => CUSTOM_THEMES };');
    const w = factory(document, localStorage, fetch, CSS, t, pluginUrl, obj, { CSS });
    return Object.assign(w, { els, appended, attrs, store, fetched, writes: () => writes, addSheet: () => { els['jpf-styles'] = { id: 'jpf-styles' }; } });
}

console.log('\n── Reading an answer ─────────────────────────────────────────');
{
    const h = world().helpers;
    const many = [];
    for (let i = 0; i < 45; i++) many.push(custom('t' + i));
    const read = h.readCustomThemes({
        themes: [custom('mine'), custom('harvest'), custom('Bad Id'), custom('mine', 'crimson'), null, 'x'].concat(many),
        fonts: [FONT, Object.assign({}, FONT, { family: 'Evil', files: [{ url: 'https://evil.example/x.woff2', weight: [400, 400] }] }),
            Object.assign({}, FONT, { family: 'Built', custom: false }), Object.assign({}, FONT, { fallback: 'cursive', family: 'Other' })],
        hidden: ['glass', 'bonfire', 'mine', 'nonsense', 'glass']
    });
    ok('a custom theme is read', read.themes[0] && read.themes[0].id === 'mine');
    ok('one with a built-in\'s id is not, so no built-in is drawn twice', !read.themes.some(x => x.id === 'harvest'));
    ok('nor a malformed id, nor the second of two with one id',
        !read.themes.some(x => x.id === 'Bad Id') && read.themes.filter(x => x.id === 'mine').length === 1
        && read.themes[0].background.fill === payload.themes.filter(x => x.id === 'harvest')[0].background.fill);
    ok('at most forty, the server\'s own limit (' + read.themes.length + ')', read.themes.length === 40);
    ok('an uploaded font is read, its address and weights', read.fonts[0] && read.fonts[0].family === 'Shop Sign'
        && read.fonts[0].file === '/plugins/profiles/themes/fonts/' + HASH && read.fonts[0].weight.join() === '400,400');
    ok('one at any other address is not, nor a font that is not an upload',
        !read.fonts.some(f => f.family === 'Evil' || f.family === 'Built'));
    ok('an unknown fallback becomes sans-serif', read.fonts.filter(f => f.family === 'Other')[0].fallback === 'sans-serif');
    ok('hidden: built-ins only, never Bonfire, once each', read.hidden.join() === 'glass');
    ok('nothing at all reads as nothing', h.readCustomThemes(null).themes.length === 0 && h.readCustomThemes('x').fonts.length === 0);
}

console.log('\n── A custom theme is a theme ─────────────────────────────────');
{
    const w = world();
    const h = w.helpers;
    ok('before the answer, it is Bonfire', h.normalizeTheme('mine') === 'bonfire');
    w.obj.useCustomThemes({ themes: [custom('mine', 'harvest', { name: { text: '<img src=x onerror=alert(1)>' } })], fonts: [FONT], hidden: ['glass'] }, false);
    ok('after, it is itself, in any case', h.normalizeTheme('MINE') === 'mine');
    ok('a hidden built-in still normalises to itself: a preview of it must not turn into Bonfire', h.normalizeTheme('glass') === 'glass');
    const offered = h.offeredThemes();
    ok('the pickers offer the built-ins less the hidden one, then the custom one (' + offered.join(', ') + ')',
        offered[0] === 'bonfire' && offered.indexOf('glass') === -1 && offered[offered.length - 1] === 'mine' && offered.indexOf('crimson') !== -1);
    ok('a profile stored on a hidden theme has nothing ticked', h.offeredChoice('glass') === '' && h.offeredChoice('Mine') === 'mine');
    ok('its name, as text, is what the administrator typed', h.themeNameText('mine') === '<img src=x onerror=alert(1)>');
    ok('and for markup it is escaped', h.themeName('mine').indexOf('<img') === -1 && h.themeName('mine').indexOf('&lt;img') !== -1);
    ok('a built-in\'s name is still its string', h.themeNameText('crimson') === 'theme.crimson');
    ok('its gate background is known, for the startup cover', /gradient|#/.test(h.gateBackground('mine')));
}

console.log('\n── Its font ──────────────────────────────────────────────────');
{
    const w = world();
    const withFont = custom('shop');
    withFont.type = JSON.parse(JSON.stringify(withFont.type));
    withFont.type.font = { family: 'Shop Sign', fallback: 'sans-serif' };
    w.obj.useCustomThemes({ themes: [withFont], fonts: [FONT, Object.assign({}, FONT, { family: 'Wide',
        files: [{ url: '/plugins/profiles/themes/fonts/' + 'b'.repeat(32) + '.woff2', weight: [200, 800] }] })] }, false);
    const css = w.helpers.themeCss(w.state().themes[0], {});
    ok('a theme naming an uploaded font asks for the served copy first (' + (/--jpf-font: ([^;]*);/.exec(css) || [])[1] + ')',
        /--jpf-font: 'Bonfire Shop Sign', 'Shop Sign', sans-serif;/.test(css));
    const faces = w.helpers.customFontFaceCss(u => 'https://srv' + u, true);
    ok('it is declared once, at its address, with its weight and no range',
        (faces.match(/@font-face/g) || []).length === 2 && faces.indexOf("font-family: 'Bonfire Shop Sign'") !== -1
        && faces.indexOf('url("https://srv/plugins/profiles/themes/fonts/' + HASH + '")') !== -1
        && faces.indexOf('font-weight: 400;') !== -1 && faces.indexOf('unicode-range') === -1);
    const old = w.helpers.customFontFaceCss(u => u, false);
    ok('on an engine without variable fonts, a static font is still declared and a variable one is not',
        old.indexOf('Bonfire Shop Sign') !== -1 && old.indexOf('Bonfire Wide') === -1);
    const bad = w.helpers.customFontFaceCss(() => 'x"); } body { display: none } a { b: url("', true);
    const srcs = bad.match(/src: [^\n]*/g) || [];
    ok('nothing in the address can end the rule', srcs.length === 2
        && srcs.every(x => /^src: url\("[^"\\]*"\) format\("woff2"\);$/.test(x)), srcs.join(' | '));
}

console.log('\n── Startup, the answer, and the copy on the device ────────────');
(async () => {
    const answer = { themes: [custom('mine')], fonts: [FONT], hidden: ['glass'] };

    // A first visit: nothing stored. The page asks for "mine" before the answer arrives.
    let w = world({}, answer);
    w.obj.restoreCustomThemes();
    w.obj.applyTheme('mine');
    ok('first visit: Bonfire until the server answers', w.attrs['data-jpf-theme'] === 'bonfire');
    w.addSheet();
    await w.obj.loadCustomThemes();
    ok('it asks for ?part=custom', w.fetched.length === 1 && w.fetched[0] === '/plugins/profiles/themes?part=custom', w.fetched.join());
    ok('and then the page is in the theme it was asked for', w.attrs['data-jpf-theme'] === 'mine');
    ok('its CSS in a style element of its own, after the sheet',
        !!w.els['jpf-themes-custom'] && w.els['jpf-themes-custom'].textContent.indexOf('[data-jpf-theme="mine"]') !== -1);
    ok('with its font declared there too', w.els['jpf-themes-custom'].textContent.indexOf('Bonfire Shop Sign') !== -1);
    ok('a copy is kept on the device', JSON.parse(w.store.jellyfin_profiles_custom_themes).themes[0].id === 'mine');
    await w.obj.loadCustomThemes();
    ok('asked once a page load, however often it is called', w.fetched.length === 1);
    const writes = w.writes();
    w.obj.useCustomThemes(answer, true);
    ok('the same answer again rewrites nothing', w.writes() === writes);

    // The next start: the copy is read before anything is drawn.
    const store = w.store;
    w = world(store, answer);
    w.obj.restoreCustomThemes();
    w.obj.applyTheme('mine');
    ok('next start: in the theme from the first frame, before the server answers', w.attrs['data-jpf-theme'] === 'mine');
    ok('but its block waits for the sheet, so it goes in after it', !w.els['jpf-themes-custom']);
    w.addSheet();
    w.obj.applyCustomThemesCss();
    ok('and is there once the sheet is', !!w.els['jpf-themes-custom']);

    // The server's preferences arrived before the themes: they are read again after.
    w = world({}, answer);
    w.obj._lastThemePrefs = { theme: 'mine', defaultTheme: 'bonfire' };
    w.addSheet();
    await w.obj.loadCustomThemes();
    ok('preferences that named it before it was known are read again',
        w.obj.settingsCalls.length === 1 && w.attrs['data-jpf-theme'] === 'mine');

    // Deleted on the server: the copy follows, and the page falls back.
    w = world(store, { themes: [], fonts: [], hidden: [] });
    w.obj.restoreCustomThemes();
    w.obj.applyTheme('mine');
    w.addSheet();
    await w.obj.loadCustomThemes();
    ok('a theme deleted on the server: Bonfire, the block gone, the copy emptied',
        w.attrs['data-jpf-theme'] === 'bonfire' && !w.els['jpf-themes-custom']
        && JSON.parse(w.store.jellyfin_profiles_custom_themes).themes.length === 0);

    // Too large to keep.
    const big = { themes: [], fonts: [], hidden: [] };
    for (let i = 0; i < 40; i++) big.themes.push(custom('big' + i, 'harvest', { name: { text: 'x'.repeat(40) }, description: 'y'.repeat(14000) }));
    w = world({ jellyfin_profiles_custom_themes: '{"themes":[]}' }, big);
    w.addSheet();
    await w.obj.loadCustomThemes();
    ok('an answer past 512 KB is used but not kept, and the old copy is dropped',
        w.state().themes.length === 40 && !('jellyfin_profiles_custom_themes' in w.store));

    console.log('\n── Where it runs ─────────────────────────────────────────────');
    const init = method('init').body;
    const at = n => init.indexOf("this._step('" + n + "'");
    ok('init restores the copy before it applies the cached theme', at('restoreCustomThemes') !== -1 && at('restoreCustomThemes') < at('applyCachedTheme'));
    ok('and asks the server after the sheet is in', at('loadCustomThemes') > at('injectStyles'));
    ok('never from checkRoute, which runs twice a second', method('checkRoute').body.indexOf('CustomThemes') === -1);
    const pickers = ['renderThemePicker', '_onboardingTheme', 'showThemeSettings'].map(method);
    ok('every picker offers what the server offers, not the built-in list',
        pickers.every(m => m && m.body.indexOf('offeredThemes()') !== -1 && m.body.indexOf('THEMES.map') === -1));

    finish();
})();
