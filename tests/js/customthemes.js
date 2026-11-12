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
const METHODS = ['restoreCustomThemes', 'loadCustomThemes', 'useCustomThemes', 'applyCustomThemesCss', 'applyTheme', 'drawLogo', 'loadThemePhoto', 'loadThemePhotoOf'];
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
        _setThemeSettings(prefs) { this.settingsCalls.push(prefs); this.applyTheme(prefs.theme); },
        // The logo's backing is measured in a browser by logobacking.js.
        fitLogoBacking() {}
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

console.log('\n── An accent with an alpha byte ────────────────────────────────');
{
    // webOS 4.5 (Chromium 53) drops #RRGGBBAA. The hover colour falls back to the accent when
    // the theme has no accentFocus, and handed it over as written.
    const w = world();
    const t = JSON.parse(JSON.stringify(payload.themes.filter(x => x.id === 'nightfall')[0]));
    t.colors.accent = '#FF8A3DC6';
    delete t.colors.accentFocus;
    const css = w.helpers.themeCss(t, {});
    const eight = css.match(/#[0-9a-fA-F]{8}\b/g) || [];
    ok('no 8-digit hex reaches the stylesheet (' + eight.join(', ') + ')', eight.length === 0);
    ok('the hover colour is the accent without its alpha', /--jpf-accent-hover: #ff8a3d;/i.test(css));
}

console.log('\n── Preview rings come from the file ──────────────────────────');
{
    // styles.css carried Nightfall's three preview colours by hand, so a custom theme with
    // rings had none in its preview, and an edit to nightfall.json left the old ones.
    const sheet = require('fs').readFileSync(require('path').join(L.ROOT, 'Web', 'styles.css'), 'utf8');
    const named = sheet.match(/\[data-jpf-theme="[a-z0-9-]+"\]/g) || [];
    ok('styles.css names no theme (' + named.join(', ') + ')', named.length === 0);

    const w = world();
    const h = w.helpers;
    const night = payload.themes.filter(x => x.id === 'nightfall')[0];
    const rings = night.decorations.filter(d => d.name === 'rings')[0];
    const mix = (a, b) => '#' + [1, 3, 5].map(i => ('0' + Math.round((parseInt(a.slice(i, i + 2), 16) + parseInt(b.slice(i, i + 2), 16)) / 2).toString(16)).slice(-2)).join('');
    const css = h.themeCss(night, {});
    [1, 2, 3].forEach(k => {
        const g = rings.gradients[(k - 1) % rings.gradients.length];
        const want = '.jpf-theme-preview[data-jpf-theme="nightfall"] .jpf-theme-preview-tile:nth-child(' + k + ') { box-shadow: 0 0 0 2px '
            + rings.gap.toLowerCase() + ', 0 0 0 4px ' + mix(g[0], g[1]) + '; }';
        ok('Nightfall\'s preview tile ' + k + ' is framed halfway along its gradient (' + mix(g[0], g[1]) + ')', css.indexOf(want) !== -1);
    });
    const ringed = custom('ringed', 'nightfall');
    w.obj.useCustomThemes({ themes: [ringed] }, false);
    ok('and so is a custom theme\'s, from its own file',
        h.themeCss(w.state().themes[0], {}).indexOf('.jpf-theme-preview[data-jpf-theme="ringed"] .jpf-theme-preview-tile:nth-child(1)') !== -1);
    ok('a theme without rings writes none', h.themeCss(payload.themes.filter(x => x.id === 'harvest')[0], {}).indexOf('.jpf-theme-preview[') === -1);
}

console.log('\n── The logo ──────────────────────────────────────────────────');
{
    const h = world().helpers;
    const IMG = '/plugins/profiles/themes/logo/' + 'c'.repeat(32) + '.png';
    const read = l => h.readCustomThemes({ logo: l }).logo;
    ok('an image at the logo address is read, with its alt', JSON.stringify(read({ image: IMG, text: null, alt: 'The Hayes House' }))
        === JSON.stringify({ image: IMG, text: null, alt: 'The Hayes House', corners: 'rounded' }));
    ok('an image anywhere else is not', read({ image: 'https://evil.example/' + 'c'.repeat(32) + '.png' }) === null
        && read({ image: '/plugins/profiles/themes/logo/../../x.png' }) === null
        && read({ image: IMG.replace('.png', '.svg') }) === null);
    ok('text alone is a logo, trimmed', JSON.stringify(read({ text: '  Hayes  ' })) === JSON.stringify({ image: null, text: 'Hayes', alt: null, corners: 'rounded' }));
    ok('text of two lines, or past forty characters, is not', read({ text: 'a\nb' }) === null && read({ text: 'x'.repeat(41) }) === null);
    ok('an alt past a hundred is dropped, the logo kept', read({ image: IMG, alt: 'y'.repeat(101) }).alt === null);
    ok('square corners are read, and anything else is rounded', read({ image: IMG, corners: 'square' }).corners === 'square'
        && ['round', 1, null, undefined].every(v => read({ image: IMG, corners: v }).corners === 'rounded'));
    ok('nothing, or nothing usable, is no logo', read(null) === null && read({ image: null, text: '' }) === null && read('x') === null);

    // drawLogo against a DOM just real enough: elements with children, attributes, text.
    function el(tag) {
        return {
            tagName: tag, id: '', className: '', attrs: {}, children: [], parentNode: null, _text: '',
            get firstChild() { return this.children[0] || null; },
            get textContent() { return this._text + this.children.map(c => c.textContent).join(''); },
            set textContent(v) { this.children = []; this._text = String(v); },
            setAttribute(k, v) { this.attrs[k] = String(v); }, getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; },
            appendChild(c) { c.parentNode = this; this.children.push(c); return c; },
            removeChild(c) { this.children = this.children.filter(x => x !== c); c.parentNode = null; return c; }
        };
    }
    const gate = el('div');
    gate.id = 'profiles-gate-overlay';
    const byId = id => {
        const walk = n => (n.id === id ? n : n.children.reduce((f, c) => f || walk(c), null));
        return walk(gate);
    };
    const doc = { createElement: el, getElementById: byId };
    const drawFactory = new Function('document', 'pluginUrl', 'state',
        'let CUSTOM_THEMES = state;\nconst obj = { drawLogo: function (' + method('drawLogo').params + ') {' + method('drawLogo').body + '} };'
        + '\nreturn { obj: obj, set: s => { CUSTOM_THEMES = s; } };');
    const d = drawFactory(doc, u => 'https://srv' + u, { themes: [], fonts: [], hidden: [], logo: null });

    d.obj.drawLogo(gate);
    ok('no logo, nothing drawn', !byId('profiles-logo'));
    d.set({ logo: read({ image: IMG, alt: '<b>Hayes</b>' }) });
    d.obj.drawLogo(gate);
    let box = byId('profiles-logo');
    const img = box && box.children[0];
    ok('an image logo is an <img> on the gate, through the server address', !!img && img.tagName === 'img' && box.parentNode === gate
        && img.src === 'https://srv' + IMG);
    ok('its alt is the administrator\'s text as text, never markup', img && img.alt === '<b>Hayes</b>');
    d.obj.drawLogo(gate);
    ok('drawn again, it is the same element, not a second one', gate.children.filter(c => c.id === 'profiles-logo').length === 1 && byId('profiles-logo') === box);

    d.set({ logo: read({ image: IMG, text: 'Hayes <script>', alt: null }) });
    d.obj.drawLogo(gate);
    box = byId('profiles-logo');
    const failed = box.children[0].onerror;
    if (typeof failed === 'function') failed();
    ok('an image that will not load falls back to the text, as text', typeof failed === 'function' && box.textContent === 'Hayes <script>'
        && box.children.length === 0 && box.className === 'is-text');

    d.set({ logo: read({ text: 'The Hayes House' }) });
    d.obj.drawLogo(gate);
    box = byId('profiles-logo');
    ok('a changed logo replaces the old one', box.textContent === 'The Hayes House' && gate.children.filter(c => c.id === 'profiles-logo').length === 1);
    d.set({ logo: null });
    d.obj.drawLogo(gate);
    ok('a logo removed on the server goes from the gate', !byId('profiles-logo'));

    // R-1251: a logo with neither picture nor words is not drawn as an empty box. The
    // reader never produces one; this is what the drawing does if one gets through.
    d.set({ logo: { image: null, text: null, alt: null } });
    d.obj.drawLogo(gate);
    ok('a logo with nothing to show leaves no empty box on the gate', !byId('profiles-logo'));

    const sheet = require('fs').readFileSync(require('path').join(L.ROOT, 'Web', 'styles.css'), 'utf8');
    const tv = /\.jpf-tv #profiles-logo \{([^}]*)\}/.exec(sheet);
    ok('on a TV it sits 5% in from the top and the right, the safe area the docs promise',
        !!tv && /top: 5vh/.test(tv[1]) && /right: 5vw/.test(tv[1]), tv ? tv[1] : 'no rule');
    const base = /#profiles-logo \{([^}]*)\}/.exec(sheet);
    ok('it never takes a click or a focus from a profile', !!base && /pointer-events: none/.test(base[1]));
    ok('drawn whenever the gate is, and when the answer changes; not from checkRoute',
        method('renderOverlayContent').body.indexOf('this.drawLogo(overlay)') !== -1
        && method('useCustomThemes').body.indexOf('this.drawLogo(') !== -1
        && method('checkRoute').body.indexOf('drawLogo') === -1);
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
    // The copy is read by the same reader as the answer. It used to be the reader's own
    // output, a different shape, so an uploaded font came back from it as nothing and the
    // theme's text drew in the fallback until the server answered.
    ok('with its uploaded font, from the copy alone', w.state().fonts.length === 1
        && w.els['jpf-themes-custom'].textContent.indexOf('Bonfire Shop Sign') !== -1);

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

    // Owner, 2026-11-12: first-time setup's theme step with one theme on offer is one card
    // and nothing to choose. The server leaves the step out; the client skips it too, for a
    // custom theme deleted since the server answered.
    {
        const m = method('_onboardingTheme');
        const run = offered => {
            const seen = { skipped: 0, framed: 0 };
            // eslint-disable-next-line no-new-func
            const fn = new Function('offeredThemes', 'offeredChoice', 't', 'escapeHtml', 'themeName',
                'return function (' + m.params + ') {' + m.body + '};')(
                () => offered, () => '', k => k, s => s, s => s);
            fn.call({
                _themeSettings: { allow: true, defaultTheme: 'bonfire' },
                currentProfiles: [{ isMaster: true }],
                renderThemePreview: () => '',
                _onboardingFrame: () => { seen.framed++; return null; }
            }, () => {}, () => { seen.skipped++; });
            return seen;
        };
        const one = run(['bonfire']);
        ok('the theme step with one theme on offer skips itself', one.skipped === 1 && one.framed === 0, JSON.stringify(one));
        const two = run(['bonfire', 'crimson']);
        ok('with two it is shown', two.skipped === 0 && two.framed === 1, JSON.stringify(two));
    }

    // R-1251: a request that failed was kept as the answer for the life of the page, so a
    // server that was down at startup meant no custom themes until the next reload.
    {
        let calls = 0;
        const holder = new Function('fetch', 'pluginUrl',
            'const obj = { _customThemesLoad: null, useCustomThemes() {}, loadCustomThemes: function (' + method('loadCustomThemes').params + ') {'
            + method('loadCustomThemes').body + '} };\nreturn obj;')(
            () => { calls++; return calls === 1 ? Promise.reject(new TypeError('Failed to fetch')) : Promise.resolve({ ok: true, json: () => Promise.resolve({}) }); },
            v => v);
        await holder.loadCustomThemes();
        await holder.loadCustomThemes();
        ok('after a failed request the next call asks again', calls === 2, 'asked ' + calls + ' time(s)');
        await holder.loadCustomThemes();
        ok('after an answer it is not asked again', calls === 2, 'asked ' + calls + ' time(s)');
    }

    // 2.0: a custom theme's background picture. Drawn only once it has loaded, so a missing
    // or failed picture leaves the theme exactly as it is without one.
    console.log('\n── A background picture ──────────────────────────────────────');
    {
        const gen = world({}, {}).helpers;
        const PIC = 'b'.repeat(32) + '.jpg';
        const withPic = (file, dim) => custom('photo', 'harvest', {
            background: Object.assign({}, payload.themes.filter(x => x.id === 'harvest')[0].background, { image: { file: file, dim: dim } })
        });
        const plainCss = gen.themeCss(custom('photo'), {});
        const picCss = gen.themeCss(withPic(PIC, 0.8), {});
        const ready = 'html[data-jpf-photos~="' + PIC + '"]';
        ok('the picture is written under a rule that waits for it to load', picCss.indexOf(ready + '[data-jpf-theme="photo"]') !== -1
            && picCss.indexOf(ready + ' [data-jpf-theme="photo"]') !== -1, picCss.slice(-600));
        const start = picCss.indexOf(ready);
        const end = picCss.indexOf('\n}\n', start) + 3;
        const rule = picCss.slice(start, end);
        // Apart from that rule, everything is the theme without a picture: what shows while
        // it loads or when it fails. The accent text included: it is held against the
        // picture too, but only inside the picture's rule (themephoto.js caught it outside).
        ok('and everything else is the theme without one', start > 0 && picCss.slice(0, start) + picCss.slice(end) === plainCss);
        ok('the accent text the picture needs is in its rule', /--jpf-accent-text: #[0-9a-f]{6};/.test(rule), rule.slice(-80));
        const bgHex = payload.themes.filter(x => x.id === 'harvest')[0].background.fill.slice(0, 7);
        const k = [1, 3, 5].map(i => parseInt(bgHex.slice(i, i + 2), 16));
        const veil = 'rgba(' + k.join(', ') + ', 0.8)';
        ok('over the picture, a layer of the fill at the dim', rule.indexOf('linear-gradient(' + veil + ', ' + veil + ')') !== -1, rule.slice(0, 200));
        ok('the picture fills the screen', /url\("[^"]*\/plugins\/profiles\/themes\/backgrounds\/b{32}\.jpg"\) center \/ cover no-repeat/.test(rule), rule.slice(0, 300));
        ok('the theme\'s own background is under it, for a picture with clear parts',
            rule.indexOf('no-repeat, ' + /--jpf-gate-bg: ([^;]*);/.exec(plainCss)[1] + ';') !== -1);
        ok('a dim past 0.95 is drawn at 0.95', gen.themeCss(withPic(PIC, 5), {}).indexOf('rgba(' + k.join(', ') + ', 0.95)') !== -1);
        ['../x.png', 'a.png', 'B'.repeat(32) + '.jpg', 'b'.repeat(32) + '.svg', 'b'.repeat(32) + '.jpg") , url("http://x', 42].forEach(bad => {
            ok('a name the server could not have stored draws nothing: ' + String(bad).slice(0, 20),
                gen.themeCss(withPic(bad, 0.8), {}) === plainCss);
        });

        // The loader: marks <html> only when the picture loads, through pluginUrl.
        const made = [];
        global.Image = function () { made.push(this); };
        // Two custom themes with pictures, the page wearing one. Every custom theme's picture
        // used to be fetched on every page load, forty on a TV to show one.
        const PIC2 = 'd'.repeat(32) + '.png';
        const other = Object.assign(withPic(PIC2, 0.8), { id: 'other', name: { text: 'My other' } });
        const w = world({}, { themes: [withPic(PIC, 0.8), other], fonts: [], hidden: [] });
        w.addSheet();
        w.obj.applyTheme('photo');
        await w.obj.loadCustomThemes();
        ok('of two custom themes with pictures, only the one in use is fetched', made.length === 1 && made[0].src === '/plugins/profiles/themes/backgrounds/' + PIC, made.map(m => m.src).join());
        ok('and nothing is marked before it loads', !w.attrs['data-jpf-photos']);
        made[0].onload();
        ok('once it loads, <html> names it', w.attrs['data-jpf-photos'] === PIC, w.attrs['data-jpf-photos']);
        w.obj.applyCustomThemesCss();
        ok('it is fetched once', made.length === 1);
        w.obj.loadThemePhoto('c'.repeat(32) + '.png');
        made[1].onerror();
        ok('a picture that fails is never marked', w.attrs['data-jpf-photos'] === PIC);
        w.obj.loadThemePhoto('c'.repeat(32) + '.png');
        ok('and is tried again the next time', made.length === 3);
        w.obj.loadThemePhoto('../evil.png');
        ok('a bad name is never fetched', made.length === 3);
        w.obj.applyTheme('other');
        ok('the other theme\'s picture is fetched when it is put on', made.length === 4 && made[3].src === '/plugins/profiles/themes/backgrounds/' + PIC2, made.map(m => m.src).join());
        w.obj.applyTheme('bonfire');
        w.obj.applyTheme('photo');
        ok('and nothing is fetched twice', made.length === 4, made.length + ' fetched');
        delete global.Image;
    }

    finish();
})();
