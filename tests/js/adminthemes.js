/*
 * The settings page's Themes section (1.7.2): the administrator's own themes, the
 * built-ins they hide, and uploaded fonts.
 *
 * Runs the page's own functions, lifted out of the dashboard, against stub pages and stub
 * servers, and the client script's real theme generator for the preview. What it holds:
 *
 *   1. the section is there, with every control the editor and the font upload need;
 *   2. the default-theme select lists custom themes and leaves hidden ones out, and Save
 *      never turns a custom default into Bonfire because its option arrived late;
 *   3. nothing an administrator typed (a theme name, a font name, a key in an imported
 *      file) reaches markup as markup;
 *   4. each problem the server can report is said in the administrator's language, and
 *      each error code the theme routes send is too;
 *   5. Simple's controls keep a theme readable, and Advanced covers every field any
 *      built-in theme uses, with no field pointing at a path no theme has;
 *   6. the preview is drawn by profiles.js's own themeCss, through the one thing the client
 *      exposes for it, and the page says so rather than throwing when it isn't there;
 *   7. the font upload refuses before sending what the server would refuse, and sends the
 *      file itself with the rights confirmed;
 *   8. a new theme cannot silently overwrite another by taking its id.
 *
 * Point it at older files to watch it fail (argv[2] is the client script, argv[3] the
 * dashboard, as for dashboardxss.js):
 *
 *     node tests/js/adminthemes.js /old/profiles.js /old/profilesDashboard.html
 *
 * (tests/cs/themeeditor is the server's side of the same routes.)
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
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

const html = L.readDashboard();
const script = L.dashboardScript(html);
const markup = html.slice(0, html.indexOf('<script'));
const clientSrc = L.readProfiles();

const EVIL = '<img src=x onerror=alert(1)>';
const EVIL_ATTR = '" onmouseover="alert(1)';
function live(s) { return s.indexOf('<img src=x') !== -1 || s.indexOf('" onmouseover="') !== -1; }

// The theme files, as the server reads them.
const BUILT_IN = fs.readdirSync(L.themesDir()).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
    .map(f => JSON.parse(fs.readFileSync(path.join(L.themesDir(), f), 'utf8')));
const bonfireFile = BUILT_IN.filter(t => t.id === 'bonfire')[0];

/**
 * The named dashboard functions with the page's translation core in front, and the globals
 * they use passed in. Null when any is missing, so an older page fails the checks that
 * need it instead of stopping the harness.
 */
function lift(names, globals) {
    const g = Object.assign({
        ApiClient: { getUrl: (u, q) => '/' + u + (q ? '?' + Object.keys(q).map(k => k + '=' + encodeURIComponent(q[k])).join('&') : '') },
        window: {}, document: {}, Dashboard: {}, fetch: () => Promise.reject(new Error('no fetch')),
        setTimeout: fn => { fn(); return 0; }, clearTimeout() {}
    }, globals || {});
    let src;
    try {
        src = L.dashboardI18n(script) + names.map(n => L.extractFunction(script, n)).join('\n');
    } catch (e) {
        return null;
    }
    const keys = Object.keys(g);
    // eslint-disable-next-line no-new-func
    const factory = new Function(keys.join(', '),
        'const THEME_PREVIEW_ID = "jpf--editor-preview";\n' + src +
        '\nreturn { ' + names.map(n => n + ': ' + n).join(', ') +
        ', setStrings: function (s) { adminStrings = s; } };');
    return factory.apply(null, keys.map(k => g[k]));
}

/** French for every admin.themes.* key, so "translated" can be told from "English". */
function frenchStrings() {
    const fr = JSON.parse(fs.readFileSync(path.join(L.i18nDir(), 'fr.json'), 'utf8'));
    const out = {};
    Object.keys(fr).filter(k => k.indexOf('admin.') === 0).forEach(k => { out[k] = fr[k]; });
    return out;
}

// ── 1. The section ────────────────────────────────────────────────────────────
console.log('\n── The Themes section is on the page ───────────────────────────');
{
    const ids = ['jpfThemes', 'themesBuiltIn', 'themesCustom', 'themesFonts', 'themeNew', 'themeImport', 'themeImportFile',
        'themeEditor', 'teName', 'teId', 'teBase', 'teAccent', 'teBg1', 'teBg2', 'teText', 'teFont', 'teAvatar', 'teButton',
        'tePattern', 'teAdvancedFields', 'tePreview', 'teProblems', 'teSave', 'teCancel',
        'fontChoose', 'fontFile', 'fontFamily', 'fontFallback', 'fontRights', 'fontUpload'];
    const missing = ids.filter(id => markup.indexOf('id="' + id + '"') === -1);
    ok('every control is in the markup', missing.length === 0, 'missing: ' + missing.join(', '));
    const section = markup.indexOf('id="jpfThemes"');
    ok('it sits in the Experience tab, after the default theme',
       section > markup.indexOf('id="defaultTheme"') && section < markup.indexOf('id="jpfViewing"')
       && section > markup.indexOf('data-panel="experience"'));
    const fallback = /<select[^>]*id="fontFallback"[^>]*>([\s\S]*?)<\/select>/.exec(markup);
    const values = fallback ? (fallback[1].match(/value="([^"]*)"/g) || []).map(v => v.slice(7, -1)) : [];
    ok('a font falls back to sans-serif or serif, nothing else', JSON.stringify(values) === '["sans-serif","serif"]', values.join(','));
    ok('the font picker takes WOFF2 only', /id="fontFile"[^>]*accept="\.woff2,font\/woff2"/.test(markup)
       || /accept="\.woff2,font\/woff2"[^>]*id="fontFile"/.test(markup));
    ok('the rights box is a checkbox', /type="checkbox" id="fontRights"/.test(markup));
    ok('Simple and Advanced are both there', /data-view="simple"/.test(markup) && /data-view="advanced"/.test(markup));
    ok('its loader starts with the page and its handlers are bound with the rest',
       /loadThemes\(page\);/.test(script) && /bindThemes\(page\);/.test(script));
    // The mode buttons must not be .jpf-tab: initTabs binds every one of those to a panel.
    ok('the mode buttons are not page tabs', !/class="jpf-tab[ "][^>]*data-view=/.test(markup));
}

// ── 2. The default theme ──────────────────────────────────────────────────────
console.log('\n── The default-theme select ────────────────────────────────────');
const DATA = {
    'default': 'shop',
    maxCustomThemes: 40, maxCustomFonts: 20, maxFontBytes: 2097152,
    builtIn: ['bonfire', 'crimson', 'glass', 'harvest'].map(id => ({ id: id, hidden: id === 'glass' })),
    custom: [
        { id: 'shop', theme: Object.assign({}, bonfireFile, { id: 'shop', name: { text: 'Shop' } }), problems: [] },
        { id: 'broken', theme: Object.assign({}, bonfireFile, { id: 'broken', name: { text: 'Broken' } }),
          problems: [{ path: 'colors.text1', code: 'contrast', detail: '1.20:1, 7:1 needed, on background.fill.' }] }
    ],
    fonts: [], builtInFonts: ['Outfit', 'Lora']
};
{
    const fns = lift(['normalizeTheme']);
    ok('normalizeTheme can be run', !!fns);
    if (fns) {
        const offered = ['bonfire', 'crimson', 'shop'];
        ok('an offered custom theme is kept', fns.normalizeTheme('shop', offered) === 'shop');
        ok('a hidden built-in is not', fns.normalizeTheme('glass', offered) === 'bonfire');
        ok('nor is an unknown id', fns.normalizeTheme('gone', offered) === 'bonfire');
        // Before the Themes section answers: a custom default must survive a Save.
        ok('before the list arrives, a custom default is kept rather than turned into Bonfire',
           fns.normalizeTheme('shop') === 'shop');
        ok('and a value that is not an id at all is still Bonfire', fns.normalizeTheme('<x>') === 'bonfire');
    }
    ok('Save falls back to the default it loaded when the select has no option for it yet',
       /normalizeTheme\(page\.querySelector\('#defaultTheme'\)\.value \|\| page\._wantedDefaultTheme, page\._themeOffered\)/.test(script));

    const sel = lift(['normalizeTheme', 'themeDisplayName', 'themeOfferedIds', 'getThemePath', 'renderDefaultThemeOptions'], {
        document: { createElement: () => ({ value: '', textContent: '' }) }
    });
    ok('the select is drawn by a function a test can run', !!sel);
    if (sel) {
        const ids = sel.themeOfferedIds(DATA, 'shop');
        ok('Bonfire and the shown built-ins are offered', ids.indexOf('bonfire') === 0 && ids.indexOf('crimson') !== -1);
        ok('a hidden built-in is not', ids.indexOf('glass') === -1);
        ok('a custom theme that passes is', ids.indexOf('shop') !== -1);
        ok('one with problems is not', ids.indexOf('broken') === -1);
        ok('unless it is the saved default', sel.themeOfferedIds(DATA, 'broken').indexOf('broken') !== -1);
        ok('Bonfire is offered even if a list says it is hidden',
           sel.themeOfferedIds({ builtIn: [{ id: 'bonfire', hidden: true }], custom: [] }, '').indexOf('bonfire') === 0);

        // A select whose value follows its options, as a browser's does.
        const staticOptions = ['bonfire', 'crimson', 'glass', 'harvest'].map(v => ({ value: v, textContent: 'static ' + v }));
        const select = {
            _opts: staticOptions.slice(), _v: '',
            get options() { return this._opts; },
            set innerHTML(v) { this._opts = []; this._v = ''; },
            appendChild(o) { this._opts.push(o); },
            get value() { return this._v; },
            set value(v) { this._v = this._opts.some(o => o.value === v) ? v : ''; }
        };
        const page = { _themes: { data: DATA }, _wantedDefaultTheme: 'shop', querySelector: s => (s === '#defaultTheme' ? select : null) };
        sel.renderDefaultThemeOptions(page);
        const listed = select.options.map(o => o.value);
        ok('the select lists the custom theme', listed.indexOf('shop') !== -1, listed.join(','));
        ok('and not the hidden one', listed.indexOf('glass') === -1, listed.join(','));
        ok('the saved custom default is selected once its option exists', select.value === 'shop', select.value);
        ok('the built-in options are the markup\'s own, so their translations stay',
           select.options.filter(o => o.value === 'crimson')[0] === staticOptions[1]);
        ok('a custom option is named by its text', select.options.filter(o => o.value === 'shop')[0].textContent === 'Shop');
        ok('and Save is told what is offered', JSON.stringify(page._themeOffered) === JSON.stringify(listed));
    }
}

// ── 3. Nothing typed becomes markup ───────────────────────────────────────────
console.log('\n── Names an administrator typed are text ───────────────────────');
{
    const fns = lift(['escapeHtml', 'getThemePath', 'themeDisplayName', 'builtInThemeNames', 'builtInThemeRowsHtml', 'customThemeRowsHtml',
        'themeFontCssName', 'fontRowsHtml', 'themeProblemText', 'themeProblemsHtml']);
    ok('the lists are built by functions a test can run', !!fns);
    if (fns) {
        // A built-in's name is markup English (admin.theme.*), not ADMIN_EN, so t() cannot
        // give it; the list takes it from the select's options, which hold it in the page's
        // language.
        const names = fns.builtInThemeNames([{ value: 'bonfire', textContent: 'Bonfire (night sky)' },
            { value: 'crimson', textContent: ' Crimson (velvet theatre) ' }]);
        const rows = fns.builtInThemeRowsHtml({ 'default': 'bonfire', builtInNames: names,
            builtIn: [{ id: 'bonfire', hidden: false }, { id: 'crimson', hidden: true }] });
        ok('a built-in is listed by its name, in English too', rows.indexOf('Crimson (velvet theatre)') !== -1, rows.slice(0, 120));
        ok('the hidden one is ticked; Bonfire, the default here, cannot be',
           /data-theme="crimson" checked/.test(rows) && /data-theme="bonfire" disabled/.test(rows));
        ok('the names are read when the lists are drawn', /data\.builtInNames = builtInThemeNames\(/.test(script));

        const hostile = {
            'default': 'bonfire', maxCustomThemes: 40, maxCustomFonts: 20,
            builtIn: [{ id: 'bonfire', hidden: false }, { id: EVIL_ATTR, hidden: true }],
            custom: [{ id: EVIL_ATTR, theme: { id: EVIL_ATTR, name: { text: EVIL }, background: { fill: '#000" onmouseover="x' } },
                       problems: [{ path: EVIL, code: 'x', detail: EVIL }] },
                     { id: 'ok', theme: { id: 'ok', name: { text: 'Fine' }, background: { fill: 'red;background:url(//x)' } }, problems: [] }],
            fonts: [{ family: EVIL, file: EVIL_ATTR, url: 'https://evil.example/x.woff2', weightMin: 400, weightMax: 400,
                      fallback: 'serif', usedBy: [EVIL_ATTR] }]
        };
        const built = fns.builtInThemeRowsHtml(hostile);
        ok('a built-in id cannot leave its attribute', !live(built) && built.indexOf('&quot; onmouseover') !== -1);
        const custom = fns.customThemeRowsHtml(hostile);
        ok('a custom theme\'s name is text', !live(custom) && custom.indexOf('&lt;img src=x') !== -1);
        ok('its id cannot leave its data attribute', custom.indexOf('data-id="&quot; onmouseover') !== -1);
        ok('a colour that is not a colour never reaches a style attribute',
           custom.indexOf('url(') === -1 && custom.indexOf('style="background: #000') === -1);
        const good = fns.customThemeRowsHtml({ custom: [{ id: 'a', theme: { name: { text: 'A' }, background: { fill: '#112233' } }, problems: [] }] });
        ok('and one that is, does', good.indexOf('style="background: #112233"') !== -1);
        const fonts = fns.fontRowsHtml(hostile);
        ok('a font\'s name is text', !live(fonts) && fonts.indexOf('&lt;img src=x') !== -1);
        ok('the face a sample is drawn in keeps only letters, digits, spaces and hyphens',
           /font-family: 'img srcx onerroralert1'/.test(fonts), (/font-family:[^;"]*/.exec(fonts) || [''])[0]);
        ok('a font in use cannot be deleted from the list', /jpf-te-font-delete[^>]*disabled/.test(fonts));
        const probs = fns.themeProblemsHtml([{ path: EVIL, code: 'unknown', detail: EVIL }, { path: 'x', code: 'zzz', detail: EVIL }]);
        ok('a problem\'s path and detail are text', !live(probs) && probs.indexOf('&lt;img src=x') !== -1);
    }

    const faces = lift(['themeFontCssName', 'themeFontUrl', 'themeFontFaceCss']);
    ok('the font faces are written by a function a test can run', !!faces);
    if (faces) {
        const css = faces.themeFontFaceCss({ fonts: [
            { family: 'Shop Sign', url: '/plugins/profiles/themes/fonts/0123456789abcdef0123456789abcdef.woff2', weightMin: 100, weightMax: 900 },
            { family: "x'); } body { color: red", url: '/plugins/profiles/themes/fonts/0123456789abcdef0123456789abcdef.woff2', weightMin: 400, weightMax: 400 },
            { family: 'Off Site', url: 'https://evil.example/x.woff2', weightMin: 400, weightMax: 400 }] });
        ok('an uploaded font is declared under its plain name', /font-family: 'Shop Sign'; [^}]*font-weight: 100 900/.test(css));
        ok('a name cannot close the rule', css.indexOf('} body') === -1);
        ok('an address that is not one of Bonfire\'s fonts is never written', css.indexOf('evil.example') === -1 && css.indexOf('Off Site') === -1);
    }
}

// ── 4. Problems and errors in the administrator's language ───────────────────
console.log('\n── Problems are said in the page\'s language ───────────────────');
{
    const fns = lift(['themeProblemText']);
    ok('themeProblemText can be run', !!fns);
    if (fns) {
        const contrast = { path: 'colors.text2', code: 'contrast', detail: '2.10:1, 4.5:1 needed, on background.layers[0].stops[1].' };
        const en = fns.themeProblemText(contrast);
        ok('a contrast problem names the ratio, what it was on and the minimum',
           en.indexOf('2.10') !== -1 && en.indexOf('4.5') !== -1 && en.indexOf('background.layers[0].stops[1]') !== -1, en);
        fns.setStrings(frenchStrings());
        const fr = fns.themeProblemText(contrast);
        ok('and in French, in French', /Contraste 2\.10:1 sur background\.layers\[0\]\.stops\[1\]\. Il faut 4\.5:1\./.test(fr), fr);
        ok('a range is read out of the detail', /0.+64/.test(fns.themeProblemText({ path: 'shape.radius.small', code: 'range', detail: 'Must be from 0 to 64.' }))
           && fns.themeProblemText({ path: 'shape.radius.small', code: 'range', detail: 'Must be from 0 to 64.' }).indexOf('Must') === -1);
        fns.setStrings(null);

        // The server's own codes, read out of its source, so a new one cannot go unsaid.
        const cs = fs.readFileSync(path.join(L.ROOT, 'Configuration', 'ThemeValidator.cs'), 'utf8')
            + fs.readFileSync(path.join(L.ROOT, 'Configuration', 'ThemeCatalog.cs'), 'utf8');
        const codes = Array.from(new Set((cs.match(/(?:Add|ThemeProblem)\([^;]*?,\s*"([a-z]+)",\s*(?:"|Ratio|\$")/g) || [])
            .map(m => /,\s*"([a-z]+)",\s*(?:"|Ratio|\$")/.exec(m)[1])));
        ok('the server\'s problem codes were found (' + codes.join(', ') + ')', codes.length >= 10);
        const unsaid = codes.filter(c => fns.themeProblemText({ path: 'x', code: c, detail: 'SERVER-DETAIL' }) === 'SERVER-DETAIL');
        ok('every one has a sentence of the page\'s own', unsaid.length === 0, unsaid.join(', '));
        // The contrast sentence is parsed out of the server's English; if that changes
        // shape the page falls back to a shorter sentence, and this says so.
        ok('the server still writes contrast as the page reads it',
           cs.indexOf('":1, " + min.ToString("0.#", CultureInfo.InvariantCulture) + ":1 needed,"') !== -1
           && /Ratio\(r, min\) \+ " on " \+ path \+ "\."/.test(cs));
        ok('an unknown code shows the server\'s detail', fns.themeProblemText({ code: 'zzz', detail: 'As sent.' }) === 'As sent.');
    }

    const errs = lift(['readThemeError']);
    ok('readThemeError can be run', !!errs);
    if (errs) {
        const response = (code, body) => ({
            headers: { get: h => (h === 'X-Bonfire-Error' ? code : null) },
            text: () => Promise.resolve(body)
        });
        const ec = fs.readFileSync(path.join(L.ROOT, 'Controllers', 'ErrorCodes.cs'), 'utf8')
            + fs.readFileSync(path.join(L.ROOT, 'Controllers', 'ProfilesController.ThemeEditor.cs'), 'utf8');
        const codes = Array.from(new Set((ec.match(/"((?:theme|font)_[a-z_]+)"/g) || []).map(s => s.slice(1, -1))));
        (async () => {
            ok('the theme and font error codes were found (' + codes.join(', ') + ')', codes.length >= 8);
            errs.setStrings(frenchStrings());
            const english = [];
            for (const c of codes) {
                const e = await errs.readThemeError(response(c, 'Server sentence.'), 'admin.themes.saveFailed', { maxCustomThemes: 40 });
                if (e.message === 'Server sentence.') english.push(c);
            }
            ok('a translated page says each in its own words', english.length === 0, english.join(', '));
            const limit = await errs.readThemeError(response('font_limit', 'x'), 'admin.themes.saveFailed', { maxCustomFonts: 20 });
            ok('with the number filled in', limit.message.indexOf('20') !== -1 && limit.message.indexOf('{max}') === -1, limit.message);
            errs.setStrings(null);
            const en = await errs.readThemeError(response('font_taken', 'Bonfire already has a font called Shop.'), 'admin.themes.saveFailed', {});
            ok('in English the server\'s own sentence is used', en.message === 'Bonfire already has a font called Shop.');
            const invalid = await errs.readThemeError(response('theme_invalid',
                JSON.stringify({ message: 'That theme isn\'t valid.', problems: [{ path: 'colors.text1', code: 'contrast', detail: 'd' }] })),
                'admin.themes.saveFailed', {});
            ok('a refused save hands back its problems', invalid.problems && invalid.problems.length === 1 && invalid.message === 'That theme isn\'t valid.');
            const offline = await errs.readThemeError(new TypeError('Failed to fetch'), 'admin.themes.saveFailed', {});
            ok('no answer at all still says something', offline.message === 'Couldn\'t save.', offline.message);
            part5();
        })().catch(e => { ok('the error checks ran', false, e.stack); finish(); });
    } else {
        part5();
    }
}

// ── 5. Simple and Advanced ────────────────────────────────────────────────────
function part5() {
    console.log('\n── Simple keeps a theme readable; Advanced has every field ─────');
    const fns = lift(['getThemePath', 'setThemePath', 'themePathOwns', 'themeRgb', 'themeMix', 'themeLuminance', 'themeContrast',
        'simpleThemeValues', 'simpleThemeEdit', 'themeSlug', 'uniqueThemeId', 'newThemeDraft', 'themeFieldList',
        'importedThemeDraft', 'themeDraftForSave']);
    ok('the editor\'s functions can be run', !!fns);
    if (!fns) { part6(); return; }

    // Contrast worked out here, independently of the page's own helper.
    const lum = h => {
        const c = [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255)
            .map(v => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4)));
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    };
    const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };

    // #757575 and its neighbours sit where black and white text read about equally badly.
    const accents = ['#ffb347', '#1a73e8', '#00ff00', '#7a0000', '#ffffff', '#000000', '#808080', '#e04ed9',
        '#737373', '#757575', '#777777', '#ff0000', '#0000ff'];
    const weak = [];
    BUILT_IN.forEach(theme => accents.forEach(a => {
        const d = JSON.parse(JSON.stringify(theme));
        fns.simpleThemeEdit(d, 'accent', a, []);
        const on = d.colors.onAccent;
        const r1 = ratio(on, d.colors.accent), r2 = ratio(on, d.colors.accentFocus);
        if (d.colors.accent !== a || r1 < 4.5 || r2 < 4.5) weak.push(theme.id + ' ' + a + ' (' + r1.toFixed(2) + ', ' + r2.toFixed(2) + ')');
        if (d.parts.button2.focus.fill !== a || d.parts.button2.focus.text !== on) weak.push(theme.id + ' ' + a + ' button2.focus not following');
    }));
    ok('a new accent brings text that reads on it, at rest and in focus, on every built-in', weak.length === 0, weak.slice(0, 4).join('; '));

    const d = JSON.parse(JSON.stringify(bonfireFile));
    fns.simpleThemeEdit(d, 'bg1', '#f4f1ea', []);
    fns.simpleThemeEdit(d, 'bg2', '#f4f1ea', []);
    ok('one background colour is a fill with no layers', d.background.fill === '#f4f1ea' && d.background.layers.length === 0);
    ok('a light background makes the scheme light', d.scheme === 'light');
    ok('and the opaque panel follows it', d.colors.panel === '#f4f1ea');
    fns.simpleThemeEdit(d, 'bg2', '#202020', []);
    const layer = d.background.layers[0];
    ok('two are a top-to-bottom gradient', d.background.layers.length === 1 && layer.type === 'linear' && layer.angle === 180
       && layer.stops[0].color === '#f4f1ea' && layer.stops[1].color === '#202020');
    const v = fns.simpleThemeValues(d);
    ok('and Simple reads back what it wrote', v.bg1 === '#f4f1ea' && v.bg2 === '#202020');

    fns.simpleThemeEdit(d, 'text', '#222222', []);
    ok('text sets all three steps and the name', d.colors.text1 === '#222222' && d.colors.text2 === '#222222b3'
       && d.colors.text3 === '#22222280' && d.colors.name === '#222222' && d.parts.button2.text === '#222222');
    fns.simpleThemeEdit(d, 'pattern', 'leaves', []);
    fns.simpleThemeEdit(d, 'pattern', 'snow', []);
    const patterns = d.decorations.filter(x => x.name === 'pattern');
    ok('a pattern replaces the one there, never adds a second', patterns.length === 1 && patterns[0].image === 'snow');
    fns.simpleThemeEdit(d, 'pattern', '', []);
    ok('and None takes it away', !d.decorations.some(x => x.name === 'pattern'));
    fns.simpleThemeEdit(d, 'avatar', 'rounded', []);
    ok('a rounded tile gets a radius in its range', d.shape.avatar.kind === 'rounded' && d.shape.avatar.radius > 0 && d.shape.avatar.radius <= 50);
    fns.simpleThemeEdit(d, 'avatar', 'circle', []);
    ok('a circle needs none', d.shape.avatar.kind === 'circle');
    fns.simpleThemeEdit(d, 'button', 'rounded', []);
    ok('a rounded button keeps a radius', d.shape.button.kind === 'rounded' && typeof d.shape.button.radius === 'number');
    fns.simpleThemeEdit(d, 'font', 'Lora', [{ family: 'Lora', fallback: 'serif' }]);
    ok('a font brings its own fallback', d.type.font.family === 'Lora' && d.type.font.fallback === 'serif');

    // Advanced: every field a built-in theme uses has a control, and every control is a field.
    const fields = fns.themeFieldList();
    const paths = [];
    fields.forEach(g => g[1].forEach(f => paths.push(f[0])));
    const shared = ['schema', 'id', 'revision', 'name', 'aliases']; // set elsewhere or fixed
    const leaves = new Set();
    const walk = (o, p) => {
        if (o && typeof o === 'object' && !Array.isArray(o)) Object.keys(o).forEach(k => walk(o[k], p ? p + '.' + k : k));
        else leaves.add(p);
    };
    BUILT_IN.forEach(t => walk(t, ''));
    const uncovered = Array.from(leaves).filter(l => shared.indexOf(l.split('.')[0]) === -1
        && !paths.some(p => fns.themePathOwns(p, l)));
    ok('Advanced covers every value a built-in theme sets', uncovered.length === 0, uncovered.join(', '));
    const typos = paths.filter(p => !Array.from(leaves).some(l => fns.themePathOwns(p, l)));
    ok('and has no field no theme has', typos.length === 0, typos.join(', '));
    ok('each group heading is a string key', fields.every(g => /^admin\.themes\.group\.[a-z]+$/.test(g[0])));
    ok('a problem inside a list belongs to the list', fns.themePathOwns('background.layers', 'background.layers[0].stops[1].color')
       && !fns.themePathOwns('background.fill', 'background.fillx'));

    // The data-paths of Simple's controls: a contrast problem lands beside the control
    // that caused it.
    const owners = (markup.match(/class="[^"]*jpf-te-field[^"]*" data-paths="([^"]*)"/g) || [])
        .map(m => /data-paths="([^"]*)"/.exec(m)[1].split(' '));
    const placed = p => owners.some(list => list.some(o => fns.themePathOwns(o, p)));
    ['colors.text1', 'colors.text2', 'colors.onAccent', 'colors.accent', 'name.text', 'id', 'type.font.family', 'decorations[0].image']
        .forEach(p => ok('a problem at ' + p + ' has a Simple field to sit beside', placed(p)));

    // New themes.
    const taken = ['bonfire', 'my-theme', 'my-theme-2'];
    const n = fns.newThemeDraft(bonfireFile, 'My Théme', taken);
    ok('a new theme gets an id of its own', n.id === 'my-theme-3', n.id);
    ok('its name is text, and it has no aliases', n.name.text === 'My Théme' && !('key' in n.name) && n.aliases.length === 0);
    ok('and starts at revision 1', n.revision === 1);
    ok('the starting theme is copied, not shared', n.colors !== bonfireFile.colors && bonfireFile.id === 'bonfire');
    ok('a name with nothing usable in it still gets an id', fns.themeSlug('日本') === 'theme');
    ok('an id stays within 40', fns.uniqueThemeId('a'.repeat(40), ['a'.repeat(40)]).length <= 40);
    const imported = fns.importedThemeDraft({ id: 'harvest', name: { key: 'theme.harvest' }, aliases: ['autumn'] });
    ok('an exported built-in, imported, is put right for the editor', imported.aliases.length === 0
       && imported.name.text === '' && !('key' in imported.name));
    const saved = fns.themeDraftForSave({ mode: 'edit', revision: 4, draft: { id: 'x', revision: 4 } });
    ok('saving an edit moves the revision on, so clients re-read it', saved.revision === 5);
    part6();
}

// ── 6. The preview ────────────────────────────────────────────────────────────
function part6() {
    console.log('\n── The preview is the client\'s own generator ──────────────────');

    // profiles.js as served, run far enough to define what it exposes.
    let api = null;
    try {
        let src = L.readClientBundle(clientSrc).replace('ProfilesPlugin.init();', '');
        const noop = () => {};
        const el = () => ({ style: {}, classList: { add: noop, remove: noop, contains: () => false }, setAttribute: noop,
            appendChild: noop, addEventListener: noop });
        const sandbox = {
            console: { log: noop, warn: noop, error: noop },
            localStorage: { getItem: () => null, setItem: noop, removeItem: noop },
            sessionStorage: { getItem: () => null, setItem: noop, removeItem: noop },
            navigator: { userAgent: 'Mozilla/5.0', language: 'en' },
            setTimeout: noop, clearTimeout: noop, setInterval: noop, clearInterval: noop,
            fetch: () => Promise.resolve({ ok: false, json: () => Promise.resolve(null) }),
            location: { protocol: 'https:', pathname: '/web/', hash: '' },
            document: { addEventListener: noop, querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
                createElement: el, head: { appendChild: noop }, body: el(), documentElement: el() },
            JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
        };
        sandbox.window = sandbox;
        vm.createContext(sandbox);
        new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
        api = sandbox.BonfireThemePreview || null;
    } catch (e) {
        ok('profiles.js runs far enough to expose the preview', false, e.message);
    }
    ok('profiles.js exposes window.BonfireThemePreview', !!api && typeof api.css === 'function');
    if (api) {
        const draft = Object.assign(JSON.parse(JSON.stringify(bonfireFile)), { id: 'jpf--editor-preview' });
        draft.colors.accent = '#12ab34';
        const css = api.css(draft);
        ok('css() writes the theme\'s tokens on the id it is given',
           css.indexOf('[data-jpf-theme="jpf--editor-preview"]') !== -1 && css.indexOf('--jpf-accent: #12ab34') !== -1);
        const generator = L.themeGenerator(clientSrc);
        ok('and it is themeCss itself, not a copy', css === generator.themeCss(draft, L.themesPayload().patterns));
        // A theme with a pattern, so the pictures are shown to be passed through too.
        const harvest = Object.assign(JSON.parse(JSON.stringify(BUILT_IN.filter(t => t.id === 'harvest')[0])), { id: 'jpf--editor-preview' });
        const withPattern = api.css(harvest);
        ok('its pattern pictures included', withPattern === generator.themeCss(harvest, L.themesPayload().patterns)
           && /--jpf-deco: url\("data:image\/svg/.test(withPattern));
        ok('css() writes nothing for a file that is not a theme', api.css({ id: '<x>' }) === '');
        const builtIns = typeof api.builtIns === 'function' ? api.builtIns() : [];
        ok('builtIns() has every built-in, hidden ones included', builtIns.length === BUILT_IN.length, String(builtIns.length));
        builtIns[0].colors = null;
        ok('and is a copy the editor may change', api.builtIns()[0].colors !== null);
        ok('refresh() is offered', typeof api.refresh === 'function');
    }

    // The dashboard side: it writes what css() returns, under the preview's id, and says so
    // plainly when there is no client script.
    const head = [];
    const style = { id: '', textContent: '' };
    const nameEl = { textContent: '' };
    const card = { querySelector: s => (s === '.jpf-theme-preview-name' ? nameEl : null) };
    const box = { innerHTML: '', querySelector: s => (s === '.jpf-theme-preview' ? card : null) };
    const page = {
        _themeEditor: { draft: Object.assign(JSON.parse(JSON.stringify(bonfireFile)), { id: 'shop', name: { text: EVIL } }) },
        querySelector: s => (s === '#tePreview' ? box : null)
    };
    const seen = [];
    const win = { BonfireThemePreview: { css: t => { seen.push(t); return 'CSS for ' + t.id; } } };
    const doc = {
        getElementById: id => (id === 'jpfThemeEditorCss' && head.length ? head[0] : null),
        createElement: () => style, head: { appendChild: s => head.push(s) }
    };
    const fns = lift(['getThemePath', 'escapeHtml', 'renderThemeDraftPreview'], { window: win, document: doc });
    ok('the preview is drawn by a function a test can run', !!fns);
    if (fns) {
        fns.renderThemeDraftPreview(page);
        ok('it asks the client script for the CSS', seen.length === 1);
        ok('under the preview\'s id, never the theme\'s own', seen[0] && seen[0].id === 'jpf--editor-preview');
        ok('without changing the draft', page._themeEditor.draft.id === 'shop');
        ok('and puts it in the page', style.textContent === 'CSS for jpf--editor-preview' && head.length === 1);
        ok('the preview\'s title is the name, as text', nameEl.textContent === EVIL);
        delete win.BonfireThemePreview;
        let threw = null;
        try { fns.renderThemeDraftPreview(page); } catch (e) { threw = e; }
        ok('with no client script it says so instead of throwing', threw === null && /Bonfire/.test(box.innerHTML), box.innerHTML);
    }
    part7();
}

// ── 7. The font upload ────────────────────────────────────────────────────────
function part7() {
    console.log('\n── The font upload ─────────────────────────────────────────────');
    const names = lift(['fontNameFromFile']);
    ok('fontNameFromFile can be run', !!names);
    if (names) {
        ok('a Google variable font is named by its family', names.fontNameFromFile('Outfit-VariableFont_wght.woff2') === 'Outfit');
        ok('a style suffix goes', names.fontNameFromFile('Lobster-Regular.woff2') === 'Lobster');
        ok('dashes and underscores are spaces', names.fontNameFromFile('Shop_Sign.woff2') === 'Shop Sign');
        ok('nothing a name can\'t hold is kept', !/[<>"'{}]/.test(names.fontNameFromFile('<b>"x"{}.woff2')));
    }

    const run = async (opts) => {
        const sent = [];
        const els = {
            '#fontRights': { checked: opts.rights !== false },
            '#fontFallback': { value: opts.fallback || 'serif' },
            '#fontFamily': { value: opts.family === undefined ? 'Shop Sign' : opts.family },
            '#fontUpload': { disabled: false },
            '#fontFile': { value: 'x' },
            '#fontFileName': { textContent: 'x' },
            '#fontResult': { textContent: '', style: {}, classList: { toggle() {} } }
        };
        const file = opts.file === null ? null : Object.assign({ name: 'shop.woff2', size: 1000 }, opts.file || {});
        const page = {
            _themeFontFile: file,
            _themes: { data: { maxFontBytes: 2097152, maxCustomFonts: 20 } },
            querySelector: s => els[s] || null
        };
        const fetchStub = (url, init) => {
            sent.push({ url: url, init: init });
            return Promise.resolve({ ok: true, json: () => Promise.resolve({ family: 'Shop Sign' }) });
        };
        // loadThemes and authorizationHeader are the page's own, run elsewhere; stood in for.
        const fns = lift(['showThemeStatus', 'readThemeError', 'refreshClientThemes', 'uploadThemeFont'], {
            fetch: fetchStub, window: {},
            loadThemes: () => {}, authorizationHeader: () => 'MediaBrowser Token="tok"',
            ApiClient: { getUrl: (u, q) => '/' + u + (q ? '?' + Object.keys(q).map(k => k + '=' + encodeURIComponent(q[k])).join('&') : '') }
        });
        if (!fns) return null;
        fns.uploadThemeFont(page);
        for (let i = 0; i < 5; i++) await new Promise(r => setImmediate(r));
        return { sent: sent, result: els['#fontResult'].textContent, page: page, file: file };
    };
    (async () => {
        const base = await run({});
        ok('uploadThemeFont can be run', !!base);
        if (base) {
            ok('one request', base.sent.length === 1, String(base.sent.length));
            const req = base.sent[0] || { url: '', init: {} };
            ok('a POST of the file itself', req.init.method === 'POST' && req.init.body === base.file);
            ok('and the form is ready for the next one', base.page._themeFontFile === null);
            ok('to the fonts route with the rights confirmed', /\/plugins\/profiles\/admin\/themes\/fonts\?/.test(req.url) && /[?&]rights=true\b/.test(req.url));
            ok('the fallback and the name as chosen', /[?&]fallback=serif\b/.test(req.url) && /[?&]family=Shop%20Sign\b/.test(req.url));
            ok('signed the way Jellyfin 12 reads', /^MediaBrowser /.test((req.init.headers || {}).Authorization || ''));
            ok('and it says the font was added', base.result.indexOf('Shop Sign') !== -1, base.result);
            const blank = await run({ family: '' });
            ok('a blank name is left out, so the server reads the file\'s', blank.sent[0] && !/family=/.test(blank.sent[0].url));
            const noRights = await run({ rights: false });
            ok('nothing is sent without the rights box', noRights.sent.length === 0 && noRights.result.length > 0);
            const notWoff2 = await run({ file: { name: 'shop.ttf' } });
            ok('nor for a file that is not WOFF2', notWoff2.sent.length === 0);
            const big = await run({ file: { size: 2097153 } });
            ok('nor for one over the limit', big.sent.length === 0 && /2/.test(big.result));
            const none = await run({ file: null });
            ok('nor with no file chosen', none.sent.length === 0);
        }
        part8();
    })().catch(e => { ok('the upload checks ran', false, e.stack); finish(); });
}

// ── 8. A new theme can't take another's id ────────────────────────────────────
function part8() {
    console.log('\n── A new theme cannot overwrite another ────────────────────────');
    const fns = lift(['localThemeProblems', 'themeProblemText']);
    ok('localThemeProblems can be run', !!fns);
    if (fns) {
        const page = (mode, id) => ({
            _themes: { data: DATA },
            _themeEditor: { mode: mode, draft: { id: id }, jsonErrors: {} }
        });
        const clash = fns.localThemeProblems(page('new', 'shop'));
        ok('a new theme under a custom theme\'s id is stopped before it is sent', clash.length === 1 && clash[0].path === 'id');
        ok('and says why', /Another/.test(fns.themeProblemText(clash[0])));
        ok('editing that theme is not', fns.localThemeProblems(page('edit', 'shop')).length === 0);
        ok('an id of the wrong shape is stopped too', fns.localThemeProblems(page('new', 'Shop Theme')).length === 1);
        const json = page('edit', 'shop');
        json._themeEditor.jsonErrors['background.layers'] = true;
        ok('as is a list that is not JSON', fns.localThemeProblems(json).some(p => p.path === 'background.layers' && p.code === 'json'));
    }
    ok('Save checks them before sending', /function saveThemeDraft[\s\S]*?if \(localThemeProblems\(page\)\.length\)[\s\S]*?return;[\s\S]*?type: 'PUT'/.test(script));
    finish();
}
