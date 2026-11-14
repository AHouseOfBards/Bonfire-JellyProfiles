/*
 * The bundle contract: what the server splices into profiles.js before serving it.
 *
 * The stylesheet is Web/styles.css, not a template literal inside injectStyles. That
 * removes the failure that shipped 1.5.2 and 1.5.3-beta dead (a code comment with two
 * backticks ended the literal early; the file still parsed and threw on startup). The
 * splice now happens in two places: ProfilesController.PublishStyles for real clients and
 * _lib.readClientBundle for the harnesses. If they drift, every harness checks a script
 * nobody is served.
 *
 * So the markers are compared against the C# source character for character, and the
 * bundle is evaluated to check the CSS survives the round trip exactly.
 *
 *   node tests/js/bundle.js [path/to/profiles.js]
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
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const js = L.readProfiles();
// The stylesheet file alone. extractCss() also carries the CSS themeCss builds from string
// fragments in profiles.js, so sampling it would find the script "carrying the stylesheet"
// when it is not.
const css = L.stylesOnly();
const controller = fs.readFileSync(
    path.join(L.ROOT, 'Controllers', 'ProfilesController.cs'), 'utf8');

console.log();
console.log('── the stylesheet is a real file, not a literal ────────────────');

ok('Web/styles.css exists and is substantial', css.length > 40000, css.length + ' chars');
ok('profiles.js no longer carries the stylesheet',
    !/style\.(innerHTML|textContent)\s*=\s*`/.test(js));
/* This was `js.length < 450000`. An absolute ceiling asks how big the file is, not whether
 * the stylesheet came back into it, and it rots: by 1.6.1 the client was within a few
 * hundred characters of the ceiling, and the obvious repair is to raise the number.
 *
 * So ask the real question. Every rule in styles.css opens with a selector line, and those
 * strings have no business being in a script unless the sheet is inside it. Sample across
 * the whole file, because pasting the CSS back into a template literal re-inlines all of it.
 *
 * Verified red against ad1bd3b^, the last build with the stylesheet inline. */
const selectorLines = css.split('\n')
    .map(l => l.trim())
    .filter(l => /^[.#@:a-zA-Z][^{}"']{15,}\{$/.test(l))
    // A bare @media or @supports condition is not the sheet: themeCss writes
    // "@media (max-width: 480px) {\n" itself for phone fills.
    .filter(l => !/^@(media|supports)\b/.test(l));

const stride = Math.max(1, Math.floor(selectorLines.length / 12));
const sample = [];
for (let i = 0; i < selectorLines.length && sample.length < 12; i += stride) {
    sample.push(selectorLines[i]);
}

// An empty sample would make "none of them appear" trivially true (selectors.verify.js once
// reported an attribute selector verified without looking for it), so assert the sample.
ok('the stylesheet yields rules to look for', sample.length >= 8,
    sample.length + ' selector lines sampled from ' + selectorLines.length);

/* A selector line ENDS a line in a sheet: followed by a line break, raw in a template
 * literal or escaped as \n in a string. themeCss writes a few of the same selectors as
 * one-line rules ("x { a: b; }"), so a bare substring match would call those inline. */
const inlined = sample.filter(sel => {
    for (let at = js.indexOf(sel); at !== -1; at = js.indexOf(sel, at + 1)) {
        if (/^(\r?\n|\\n)/.test(js.slice(at + sel.length, at + sel.length + 2))) return true;
    }
    return false;
});
ok('and not one of them appears in profiles.js', inlined.length === 0,
    inlined.length ? inlined.length + ' of ' + sample.length + ' are inline, e.g. ' + inlined[0] : '');
ok('the csproj embeds the stylesheet',
    fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8')
        .indexOf('Web/styles.css') !== -1);

console.log();
console.log('── the two markers, against the C# that fills them ─────────────');

/* Both sides must agree on the exact literal. A plain replace that matches nothing is
 * invisible: the script still parses and runs, just unstyled or without translations. */
const STYLES_MARKER = 'let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__';
const LOCALES_MARKER = 'let SUPPORTED_LOCALES = []; // __BONFIRE_LOCALES__';

ok('the styles marker appears in profiles.js exactly once',
    js.split(STYLES_MARKER).length - 1 === 1);
ok('the locales marker appears in profiles.js exactly once',
    js.split(LOCALES_MARKER).length - 1 === 1);

// The C# writes the marker with escaped quotes; compare the unescaped form.
const csStyles = controller.indexOf('let BONFIRE_STYLES = \\"\\"; // __BONFIRE_STYLES__');
ok('PublishStyles looks for the same styles marker', csStyles !== -1,
    'ProfilesController.cs and profiles.js disagree on the literal');
ok('PublishLocales looks for the same locales marker',
    controller.indexOf(LOCALES_MARKER) !== -1);

ok('PublishStyles is actually called when the script is served',
    /CachedProfilesJs\s*=\s*PublishThemes\(PublishStyles\(/.test(controller));
ok('and PublishLocales still is too',
    /PublishStyles\(PublishLocales\(/.test(controller));
// JSON, not interpolation, which would put the CSS back where the old template literal was.
ok('the CSS is JSON-encoded rather than interpolated',
    /JsonSerializer\.Serialize\(css\)/.test(controller));

console.log();
console.log('── the round trip: the CSS survives the splice exactly ─────────');

const bundle = L.readClientBundle(js);

ok('the bundle no longer contains the unfilled marker',
    bundle.indexOf(STYLES_MARKER) === -1);
ok('the bundle parses as JavaScript', (() => {
    try { new vm.Script(bundle, { filename: 'bundle.js' }); return true; }
    catch (e) { return false; }
})());

/* Evaluate only the declaration and compare byte for byte; a length check would pass with
 * one escape mangled. */
const decl = /let BONFIRE_STYLES = ([\s\S]*?); \/\/ __BONFIRE_STYLES__/.exec(bundle);
ok('the declaration is present in the bundle', !!decl);
if (decl) {
    let round = null;
    try { round = vm.runInNewContext('(' + decl[1] + ')'); } catch (e) { round = null; }
    ok('it evaluates to a string', typeof round === 'string');
    ok('and the string is the stylesheet, character for character', round === css,
        round === css ? '' : 'got ' + (round === null ? 'a parse failure'
            : round.length + ' chars vs ' + css.length));
}

/* The characters that can end a string literal, round-tripped. */
const nasty = 'a"b\\c\nd`e f g</script>';
const encoded = JSON.stringify(nasty);
ok('the encoding survives quotes, backslashes, newlines and backticks',
    vm.runInNewContext('(' + encoded + ')') === nasty);

/* U+2028 and U+2029 are a separate problem, and the JavaScript side does not solve it:
 * JSON.stringify leaves them raw, and they are legal in a string literal only from ES2019,
 * a syntax error on the Chromium 68 televisions. Real clients are protected by the C#
 * encoder, whose default escapes every non-ASCII character to \uXXXX. That holds only
 * while nobody swaps in the relaxed encoder, so assert on that. A round trip through Node
 * would pass either way. An earlier version of this file claimed the round trip above
 * covered U+2028/9; its test string never contained them. */
ok('the CSS really does contain non-ASCII, so the encoder choice matters',
    /[^\x00-\x7F]/.test(css));
ok('the server does not use the relaxed JSON encoder',
    controller.indexOf('UnsafeRelaxedJsonEscaping') === -1);
ok('and no raw U+2028/U+2029 is sitting in the stylesheet today',
    css.indexOf(' ') === -1 && css.indexOf(' ') === -1);

console.log();
console.log('── the themes marker, and the theme files it carries ───────────');

/* The same contract a third time. PublishThemes splices the theme files for real clients
 * and _lib.readClientBundle for the harnesses; a drift means every theme harness checks a
 * script nobody is served, and an unmatched marker means a client with no themes. */
const THEMES_MARKER = L.THEMES_MARKER;
ok('the themes marker appears in profiles.js exactly once',
    js.split(THEMES_MARKER).length - 1 === 1);
ok('PublishThemes looks for the same themes marker',
    controller.indexOf(THEMES_MARKER) !== -1,
    'ProfilesController.cs and profiles.js disagree on the literal');
ok('the theme payload comes from ThemeCatalog',
    /ThemeCatalog\.ClientPayload\(\)/.test(controller));
const catalog = fs.readFileSync(path.join(L.ROOT, 'Configuration', 'ThemeCatalog.cs'), 'utf8');
ok('and ThemeCatalog does not use the relaxed JSON encoder either',
    catalog.indexOf('UnsafeRelaxedJsonEscaping') === -1 && catalog.indexOf('Encoder =') === -1);
ok('the csproj embeds the theme files and their patterns',
    /Include="Web\/themes\/\*\.json"/.test(fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8'))
    && /Include="Web\/themes\/patterns\/\*\.svg"/.test(fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8')));

ok('the bundle no longer contains the unfilled themes marker',
    bundle.indexOf(THEMES_MARKER) === -1);
const tdecl = /let BONFIRE_THEMES = ([\s\S]*?); \/\/ __BONFIRE_THEMES__/.exec(bundle);
ok('the themes declaration is present in the bundle', !!tdecl);
if (tdecl) {
    let themes = null;
    try { themes = vm.runInNewContext('(' + tdecl[1] + ')'); } catch (e) { themes = null; }
    const want = L.themesPayload();
    ok('it evaluates to the theme payload, exactly',
        themes !== null && JSON.stringify(themes) === JSON.stringify(want),
        themes === null ? 'a parse failure' : '');
    const files = fs.readdirSync(L.themesDir()).filter(f => f.endsWith('.json') && f !== 'fonts.json');
    ok('and it carries every theme file in Web/themes/',
        themes !== null && themes.themes.length === files.length && themes.themes.length >= 9,
        themes === null ? '' : themes.themes.length + ' themes vs ' + files.length + ' files');
    ok('Bonfire first, because each later block wins its ties by coming after it',
        themes !== null && themes.themes[0] && themes.themes[0].id === 'bonfire');
    const pats = fs.readdirSync(path.join(L.themesDir(), 'patterns')).filter(f => f.endsWith('.svg'));
    ok('every pattern picture is carried as a data: URL',
        themes !== null && pats.every(p => /^data:image\/svg\+xml,/.test(themes.patterns[p.replace(/\.svg$/, '')] || '')),
        pats.length + ' patterns');
}

console.log();
console.log('── contracts the split must not have broken ────────────────────');

// P5-4: the cache-buster. Since 2.0 the Campground coordinator writes the tag from Bonfire's
// registration, so check what Bonfire hands over and that the shared tag still writes ?v=.
// (tests/cs/injection asserts the served tag itself.)
const inj = fs.readFileSync(path.join(L.ROOT, 'Injection', 'WebInjection.cs'), 'utf8');
const sharedTag = fs.readFileSync(path.join(L.ROOT, 'Campground.Shared', 'src', 'Injection', 'IndexHtml.cs'), 'utf8');
ok('Bonfire registers profiles.js with ScriptVersion as its version',
    /Registration => new\(\s*PluginIds\.Bonfire,\s*ScriptPath,\s*ScriptVersion,/.test(inj)
    && inj.indexOf('ScriptPath = "/plugins/profiles/profiles.js"') !== -1);
ok('and the shared tag writes that version as ?v=',
    sharedTag.indexOf('{reg.Path}?v={Uri.EscapeDataString(reg.Version)}') !== -1);

// P5-5: the locales marker is what lets a contributor add a language as one file.
ok('SUPPORTED_LOCALES still ships empty, for the server to fill',
    /let SUPPORTED_LOCALES = \[\];/.test(js));

// The served script is still one file at one route.
ok('still served from a single profiles.js route',
    /\[HttpGet\("profiles\.js"\)\]/.test(controller));

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
