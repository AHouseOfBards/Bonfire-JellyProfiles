/*
 * The bundle contract: what the server splices into profiles.js before serving it.
 *
 * The stylesheet is Web/styles.css now, not a template literal inside injectStyles.
 * That removes the failure mode that shipped 1.5.2 and 1.5.3-beta dead — a code comment
 * containing two backticks ended the literal early, the file still parsed, node --check
 * passed, and the script threw three calls into startup — but it introduces a new one
 * this file exists to close: the splice is done in TWO places, by
 * ProfilesController.PublishStyles for real clients and by _lib.readClientBundle for the
 * harnesses. If those drift, every harness downstream is asserting about a script nobody
 * is served, and all of them stay green while doing it.
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
// The stylesheet file alone. extractCss() also carries the CSS themeCss writes from the
// theme files, and that CSS is built from string fragments that do sit in profiles.js,
// so sampling it below would find the script "carrying the stylesheet" when it is not.
const css = L.stylesOnly();
const controller = fs.readFileSync(
    path.join(L.ROOT, 'Controllers', 'ProfilesController.cs'), 'utf8');

console.log();
console.log('── the stylesheet is a real file, not a literal ────────────────');

ok('Web/styles.css exists and is substantial', css.length > 40000, css.length + ' chars');
ok('profiles.js no longer carries the stylesheet',
    !/style\.(innerHTML|textContent)\s*=\s*`/.test(js));
/* This was `js.length < 450000`, and it is the reason this comment exists.
 *
 * An absolute ceiling answers "how big is this file". The question this section asks is
 * "has the stylesheet come back into it" — a coarser check standing in for a precise one,
 * which is the shape this repository has shipped three bugs past. It also rots: by 1.6.1
 * the client had grown to within a few hundred characters of the ceiling, so the next
 * honest change was going to fail it, and the obvious repair is to raise the number. Do
 * that twice and the check passes for years while measuring nothing in particular.
 *
 * So ask the real question. Every rule in styles.css opens with a selector line, and those
 * exact strings have no business being in a script unless the sheet is sitting inside it.
 * Sampled across the whole file rather than from the top, because the failure this guards
 * against — someone pasting the CSS back into a template literal — re-inlines all of it.
 *
 * Verified red against ad1bd3b^, the last build with the stylesheet inline. */
const selectorLines = css.split('\n')
    .map(l => l.trim())
    .filter(l => /^[.#@:a-zA-Z][^{}"']{15,}\{$/.test(l))
    // A bare @media or @supports condition is not the sheet: themeCss writes
    // "@media (max-width: 480px) {\n" itself for phone fills, and the sample landed on it
    // once the sheet grew. Selector lines are what a pasted stylesheet carries.
    .filter(l => !/^@(media|supports)\b/.test(l));

const stride = Math.max(1, Math.floor(selectorLines.length / 12));
const sample = [];
for (let i = 0; i < selectorLines.length && sample.length < 12; i += stride) {
    sample.push(selectorLines[i]);
}

// An empty sample would make "none of them appear" trivially true. That is exactly how
// selectors.verify.js once reported an attribute selector verified without having looked
// for a single character of it, so the sample is asserted before it is used.
ok('the stylesheet yields rules to look for', sample.length >= 8,
    sample.length + ' selector lines sampled from ' + selectorLines.length);

/* A selector line ENDS a line in a sheet: followed by a line break, raw in a template
 * literal or escaped as \n in a string. That is what a pasted stylesheet looks like.
 * themeCss writes a few of the same selectors as one-line rules ("x { a: b; }"), which
 * is the generator doing its job, not the sheet coming back, so a bare substring match
 * would call those inline. Ending the match at the line break keeps the question exact. */
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

/* Both sides must agree on the exact literal. A plain replace that silently matches
 * nothing is the documented failure mode of this mechanism, and it is invisible: the
 * script still parses and still runs, just unstyled or with no translations. */
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
// JSON, not interpolation. Interpolating would put the CSS back into the position the
// old template literal had, with the same class of defect waiting.
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

/* Evaluate only the declaration, and compare byte for byte. A length check would pass
 * against a sheet with one escape mangled. */
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

/* U+2028 and U+2029 are a separate problem, and the JavaScript side does NOT solve it:
 * JSON.stringify leaves them raw, and they are legal inside a string literal only from
 * ES2019 — a syntax error on the Chromium 68 televisions this has to run on. What
 * protects real clients is the C# encoder, whose default escapes every non-ASCII
 * character to a \uXXXX sequence. That holds only while nobody swaps in the relaxed
 * encoder to make the output prettier, so assert on that. A round trip through Node
 * would pass either way and prove nothing about a television.
 *
 * An earlier version of this file claimed the round trip above covered U+2028/9. It
 * did not; the test string never contained them. */
ok('the CSS really does contain non-ASCII, so the encoder choice matters',
    /[^\x00-\x7F]/.test(css));
ok('the server does not use the relaxed JSON encoder',
    controller.indexOf('UnsafeRelaxedJsonEscaping') === -1);
ok('and no raw U+2028/U+2029 is sitting in the stylesheet today',
    css.indexOf(' ') === -1 && css.indexOf(' ') === -1);

console.log();
console.log('── the themes marker, and the theme files it carries ───────────');

/* The same contract a third time. The theme files are spliced in by PublishThemes for
 * real clients and by _lib.readClientBundle for the harnesses; a drift between them is
 * every theme harness checking a script nobody is served, and a marker that matches
 * nothing is a client with no themes at all, which still parses and still runs. */
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

// P5-4: the cache-buster. Since 2.0 the tag is written by the Campground coordinator from
// Bonfire's registration, so both halves are checked: what Bonfire hands over, and that the
// shared tag still writes it as ?v=. (tests/cs/injection asserts the served tag itself.)
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
