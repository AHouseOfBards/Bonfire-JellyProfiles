/*
 * Shared plumbing for the JS harnesses.
 *
 * Each item here was hardcoded in a dozen files and broke a dozen times at once:
 *
 *  - Six harnesses had "d:/JellyfinProfiles/..." baked in, which fails on CI immediately.
 *  - Five resolved "Web/profiles.js" against the current directory, so they only ran
 *    from the repository root.
 *  - Four located the injected stylesheet by searching for the literal
 *    `style.innerHTML = ` + backtick. 1.5.4 changed that to textContent and all four
 *    silently found no CSS, which reads as an empty sheet rather than an error.
 *
 * Resolve paths from __dirname, and find things by shape, not by a literal free to change.
 */
'use strict';

const fs = require('fs');
const path = require('path');

/** Repository root, from this file's location, not the current directory. */
const ROOT = path.resolve(__dirname, '..', '..');

/**
 * The client script under test.
 *
 * argv[2] overrides it so any harness can be pointed at another checkout. That is how a
 * new harness is shown to FAIL against the build carrying the bug before it may pass.
 */
function profilesPath() {
    return process.argv[2] || path.join(ROOT, 'Web', 'profiles.js');
}

function dashboardPath() {
    return process.argv[3] || path.join(ROOT, 'Web', 'profilesDashboard.html');
}

function i18nDir() {
    return path.join(ROOT, 'Web', 'i18n');
}

function readProfiles() {
    return fs.readFileSync(profilesPath(), 'utf8');
}

function readDashboard() {
    return fs.readFileSync(dashboardPath(), 'utf8');
}

/** Web/styles.css, the stylesheet as a real file. */
function stylesPath() {
    return path.join(ROOT, 'Web', 'styles.css');
}

/**
 * The stylesheet.
 *
 * It is a real file (Web/styles.css), spliced into the served script by
 * ProfilesController.PublishStyles. Before 1.5.9 it was a template literal inside
 * injectStyles, which a stray backtick truncated: the defect that shipped 1.5.2 and
 * 1.5.3-beta dead.
 *
 * `src` is still honored so a harness can be pointed at an older checkout to prove it
 * fails there. When the argument has the old shape (CSS inside the script) it is parsed
 * the old way. Drop this when nothing needs to bisect against a pre-1.5.9 build.
 */
function extractCss(src) {
    if (typeof src === 'string' && src.indexOf('injectStyles: function') !== -1) {
        const at = src.indexOf('injectStyles: function');
        const m = /style\.(innerHTML|textContent)\s*=\s*`/.exec(src.slice(at));
        if (m) {
            const open = at + m.index + m[0].length;
            const close = src.indexOf('`;', open);
            if (close === -1) throw new Error('extractCss: unterminated stylesheet literal');
            const css = src.slice(open, close);
            if (css.length < 1000) {
                throw new Error('extractCss: got only ' + css.length + ' characters — the '
                    + 'literal is probably being ended early by a stray backtick');
            }
            return css;
        }
        // Falls through: a current build has injectStyles but no literal to find.
    }

    const css = stylesOnly();
    // Since the themes became files (1.7.2), the browser has the sheet followed by what
    // themeCss writes from them. A build from before that has its themes in the sheet.
    const js = typeof src === 'string' ? src : readProfiles();
    return js.indexOf(THEMES_MARKER) === -1 ? css : css + '\n' + themesCss(js);
}

/** Web/styles.css alone: what PublishStyles splices in. */
function stylesOnly() {
    const css = fs.readFileSync(stylesPath(), 'utf8');
    // THROWS on a miss: a harness handed '' still runs and reports most assertions as passing.
    if (css.length < 1000) {
        throw new Error('extractCss: Web/styles.css is only ' + css.length + ' characters');
    }
    return css;
}

const THEMES_MARKER = 'let BONFIRE_THEMES = { themes: [], patterns: {} }; // __BONFIRE_THEMES__';

function themesDir() {
    return path.join(ROOT, 'Web', 'themes');
}

/**
 * What ProfilesController.PublishThemes puts in place of the themes marker, built the way
 * ThemeCatalog.ClientPayload builds it: every theme file, Bonfire first, the font list,
 * and each pattern as a data: URL escaped like Uri.EscapeDataString (encodeURIComponent
 * leaves !'()* alone; EscapeDataString does not). tests/js/bundle.js holds the two
 * together.
 */
function themesPayload() {
    const dir = themesDir();
    const themes = fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')))
        .sort((a, b) => (a.id === 'bonfire') !== (b.id === 'bonfire') ? (a.id === 'bonfire' ? -1 : 1)
            : a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
    const fontList = JSON.parse(fs.readFileSync(path.join(dir, 'fonts.json'), 'utf8'));
    const fonts = fontList.fonts;
    const subsets = fontList.subsets || {};
    const patterns = {};
    fs.readdirSync(path.join(dir, 'patterns')).filter(f => /\.svg$/.test(f)).sort().forEach(f => {
        patterns[f.replace(/\.svg$/, '')] = 'data:image/svg+xml,' + encodeURIComponent(
            fs.readFileSync(path.join(dir, 'patterns', f), 'utf8').trim())
            .replace(/[!'()*]/g, ch => '%' + ch.charCodeAt(0).toString(16).toUpperCase());
    });
    return { themes, fonts, subsets, patterns };
}

/**
 * The CSS the web client writes from the theme files, by running its own themeCss and
 * allThemesCss out of `src`: the generator under test, not a copy of it.
 */
function themesCss(src) {
    return themeGenerator(src).allThemesCss();
}

/**
 * The generator's functions, run out of `src` with the theme files as BONFIRE_THEMES:
 * { themeCss, allThemesCss, fontFaceCss, gateBackground }, whichever that build has.
 */
function themeGenerator(src, payload, custom) {
    const js = typeof src === 'string' ? src : readProfiles();
    // The custom-theme half (1.7.2), where the build has it: CUSTOM_THEMES is passed in raw
    // like BONFIRE_THEMES (an answer from GET themes?part=custom) and read through the
    // build's own readCustomThemes, so a harness gives it what the server would.
    const names = ['themeCss', 'allThemesCss', 'servedFontName', 'fontFaceCss', 'gateBackground',
        'customTheme', 'readLogo', 'readCustomThemes', 'customFontFaceCss', 'customThemesCss']
        .filter(n => new RegExp('function\\s+' + n + '\\s*\\(').test(js));
    const themes = /const THEMES = (\[[^\]]*\]);/.exec(js);
    const factory = new Function('BONFIRE_THEMES', 'CUSTOM_RAW', 'THEMES',
        'const MAX_CUSTOM_THEMES = 40, MAX_CUSTOM_FONTS = 20;\n'
        + names.map(n => extractFunction(js, n)).join('\n')
        + '\nconst CUSTOM_THEMES = typeof readCustomThemes === "function" ? readCustomThemes(CUSTOM_RAW) : { themes: [], fonts: [], hidden: [] };'
        + '\nreturn {' + names.concat(['CUSTOM_THEMES']).join(', ') + '};');
    // eslint-disable-next-line no-new-func
    return factory(payload || themesPayload(), custom || null, themes ? new Function('return ' + themes[1])() : []);
}

/**
 * The source of one named function declaration, brace-matched from its opening `{`.
 *
 * For running one function out of a file that is otherwise a single IIFE, where
 * evaluating the whole thing would need the entire page and its globals. THROWS on a
 * miss, like extractCss.
 *
 * Brace counting, not a parser: a brace inside a string or comment fools it, so the
 * caller's evaluation checks the balance. A truncated function is a syntax error, not a
 * silent wrong answer.
 */
function extractFunction(src, name) {
    const re = new RegExp('function\\s+' + name + '\\s*\\(');
    const m = re.exec(src);
    if (!m) throw new Error('extractFunction: no function named ' + name);

    const open = src.indexOf('{', m.index);
    if (open === -1) throw new Error('extractFunction: no body for ' + name);

    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
            depth--;
            if (depth === 0) return src.slice(m.index, i + 1);
        }
    }
    throw new Error('extractFunction: unterminated body for ' + name);
}

/**
 * The script as the browser receives it: profiles.js with the stylesheet spliced in, as
 * ProfilesController.PublishStyles does it.
 *
 * Use this wherever a harness asserts about "the shipped client". Reading Web/profiles.js
 * alone stopped being that when the stylesheet moved to its own file: four harnesses
 * searched the script for CSS rules and silently found none.
 *
 * The encoding mirrors the server's: JSON, so a quote or backslash in the CSS is data.
 * tests/js/bundle.js checks this function against the C# one, so the harnesses cannot
 * test a file nobody is served.
 */
function readClientBundle(src) {
    const js = typeof src === 'string' ? src : readProfiles();
    const marker = 'let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__';
    if (js.indexOf(marker) === -1) return js;   // an older build, CSS still inline
    // An older build has no themes marker and its themes in the sheet; the sheet is then
    // all there is to splice.
    const styled = js.replace(
        marker,
        'let BONFIRE_STYLES = ' + JSON.stringify(js.indexOf(THEMES_MARKER) === -1 ? extractCss(js) : stylesOnly())
            + '; // __BONFIRE_STYLES__');
    return styled.replace(THEMES_MARKER,
        'let BONFIRE_THEMES = ' + JSON.stringify(themesPayload()) + '; // __BONFIRE_THEMES__');
}

/**
 * The script and the stylesheet as plain text, concatenated.
 *
 * For harnesses that SEARCH the source rather than run it. In readClientBundle the CSS is
 * a JSON-encoded string literal, so a double quote in a selector is backslash-quote and a
 * newline is backslash-n. Searching for `[aria-expanded="true"]` there finds nothing and
 * reads as a missing rule, which is how three uitest assertions failed the day the
 * stylesheet moved.
 *
 * Run it: readClientBundle. Read it: this.
 */
function readSourceAndStyles(src) {
    const js = typeof src === 'string' ? src : readProfiles();
    // An older checkout still has the CSS inline; appending it again would double every
    // rule and break any "declared exactly once" assertion.
    if (js.indexOf('let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__') === -1) return js;
    return js + '\n\n' + extractCss(js);
}

/** The inline <script> block of the dashboard page. */
function dashboardScript(html) {
    const m = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
    if (!m) throw new Error('dashboardScript: no script block found');
    return m[1];
}

/**
 * Resolves a CSS value through the --jpf-* custom properties, so a check can assert what
 * a rule paints rather than how it is spelled.
 *
 * Written because moving thirty white-alpha literals onto nine tokens turned four
 * assertions red with no pixel changing: they matched `rgba(0, 0, 0, 0.62)` against
 * `var(--jpf-scrim)`. Spelling passes for a token redefined to the wrong color and fails
 * for a literal that is exactly right.
 *
 * Only the floor definitions count (the first `:root, body` block). A token redefined
 * under @supports or @media is ignored, because the floor is what the oldest television
 * renders.
 */
function resolveCssValue(css, value, depth) {
    depth = depth || 0;
    if (depth > 8 || value.indexOf('var(') === -1) return value;

    // Every block on :root and body: the sheet's own (what no theme sets) and, since 1.7.2,
    // Bonfire's, which themeCss writes on :root as well as on its attribute.
    let floor = '';
    const blocks = /(^|\})\s*:root,\s*body\b[^{]*\{([^}]*)\}/g;
    const bare = css.replace(/\/\*[\s\S]*?\*\//g, '');
    let b;
    while ((b = blocks.exec(bare)) !== null) floor += b[2] + ';';

    const resolved = value.replace(
        /var\(\s*(--jpf-[a-z0-9-]+)\s*(?:,[^()]*)?\)/g,
        (whole, name) => {
            const m = floor.match(new RegExp(name + '\\s*:\\s*([^;]+);'));
            return m ? m[1].trim() : whole;
        });

    // No progress means the token is undefined; returning stops an infinite descent.
    return resolved === value ? resolved : resolveCssValue(css, resolved, depth + 1);
}

/** The alpha of an rgba() value, or 1 when it carries none. */
function alphaOf(value) {
    const m = /rgba?\([^)]*?,\s*([0-9.]+)\s*\)/.exec(value);
    return m ? parseFloat(m[1]) : 1;
}

/**
 * The settings page's translation core (ADMIN_EN, adminStrings and t()) as source to put
 * in front of any dashboard function a harness lifts out to run, since nearly all of them
 * call t(). English, unless the harness assigns adminStrings itself.
 *
 * Empty for a dashboard from before the page was translated, so a harness pointed at an
 * old checkout still builds its functions and fails on what it is testing.
 */
function dashboardI18n(script) {
    const start = script.indexOf('const ADMIN_EN = {');
    if (start === -1) return '';
    const end = script.indexOf('\n            };', start);
    if (end === -1) throw new Error('dashboardI18n: ADMIN_EN has no end');
    return script.slice(start, end + '\n            };'.length) + '\nlet adminStrings = null;\n'
        + extractFunction(script, 't') + '\n';
}

/**
 * The client's theme list and everything that answers "which themes are there": THEMES,
 * the custom-theme state (1.7.2) and the functions over it, as source to put in front of a
 * harness's own code. Only what `src` has, so an older checkout still loads.
 *
 * One place, because these grew from three declarations to eleven and hand-listed copies
 * in three harnesses would drift into running a normalizeTheme with no custom list.
 */
function themeHelpers(src) {
    const js = typeof src === 'string' ? src : readProfiles();
    const out = [];
    const decl = re => { const m = re.exec(js); if (m) out.push(m[0]); };
    decl(/const THEMES = \[[^\]]*\];/);
    decl(/let CUSTOM_THEMES = \{[^\n]*\};/);
    decl(/const MAX_CUSTOM_THEMES = \d+;/);
    decl(/const MAX_CUSTOM_FONTS = \d+;/);
    ['escapeHtml', 'customTheme', 'isThemeId', 'offeredThemes', 'offeredChoice', 'themeNameText', 'themeName',
        'readLogo', 'readCustomThemes', 'normalizeTheme']
        .filter(n => new RegExp('function\\s+' + n + '\\s*\\(').test(js))
        .forEach(n => out.push(extractFunction(js, n)));
    return out.join('\n');
}

module.exports = {
    themeHelpers,
    ROOT,
    profilesPath, dashboardPath, i18nDir,
    readProfiles, readDashboard,
    extractCss, extractFunction, dashboardScript, dashboardI18n, stylesPath, readClientBundle, readSourceAndStyles,
    stylesOnly, themesDir, themesPayload, themesCss, themeGenerator, THEMES_MARKER,
    resolveCssValue, alphaOf
};
