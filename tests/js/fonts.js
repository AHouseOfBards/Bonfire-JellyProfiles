/*
 * The fonts Bonfire serves: that each file is the font it says it is, that its licence
 * travels with it, and that it can draw every letter of every language Bonfire ships.
 *
 * The last one is the point. A font that lacks "ł" does not fail: the browser draws that
 * one letter in the next font of the stack, and a Polish name comes out in two typefaces.
 * Nothing errors and no screenshot of an English screen shows it. So every character of
 * every translation is checked against every served font, one font and one character at a
 * time, in the file the browser would actually fetch for it (the subset whose
 * unicode-range holds it), read out of that file's own cmap.
 *
 *   node tests/js/fonts.js [path/to/fonts.json] [path/to/profiles.js]
 *
 * The font files are read from the fonts/ folder beside the list.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const W = require('./_woff2');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const LIST = process.argv[2] || path.join(L.themesDir(), 'fonts.json');
const DIR = path.join(path.dirname(LIST), 'fonts');
const list = JSON.parse(fs.readFileSync(LIST, 'utf8'));
const served = list.fonts.filter(f => f.files);
const SUBSETS = list.subsets || {};

console.log('\n── The served fonts are the files they claim ──────────────────');
ok('Noto Sans is served, and first', served.length > 0 && served[0].family === 'Noto Sans');
// The ceiling was twelve with ten served; fourteen were added on 2026-11-12, so it moved by
// fourteen and no further. A new font raises it on purpose, in the same commit.
ok('eight to twenty-six fonts are served (' + served.length + ')', served.length >= 8 && served.length <= 26);
ok('the latin and latin-ext ranges are given', !!SUBSETS.latin && !!SUBSETS['latin-ext']);

const read = {};
served.forEach(f => {
    Object.keys(SUBSETS).forEach(sub => {
        const name = f.files[sub];
        const file = name && path.join(DIR, name);
        ok(f.family + ' has a ' + sub + ' file', !!name && /^[a-z0-9-]+\.woff2$/.test(name) && fs.existsSync(file), name);
        if (!name || !fs.existsSync(file)) return;
        let r = null;
        try { r = W.read(fs.readFileSync(file)); } catch (e) { ok(name + ' reads as WOFF2', false, e.message); return; }
        read[name] = r;
        // Subset files are named for their default instance ("Montserrat Thin"), so the
        // family is a prefix of the name, not the whole of it.
        ok(name + ' is ' + f.family + ' (' + r.family + ')', !!r.family && r.family.indexOf(f.family.replace(/ Next$/, '')) === 0);
        // A single-weight family (Bebas Neue) has no wght axis: it is listed as [400, 400].
        ok(name + ' spans the weights fonts.json says (' + (r.weight ? r.weight.min + '-' + r.weight.max : 'static') + ')',
            Array.isArray(f.weight) && (r.weight ? r.weight.min === f.weight[0] && r.weight.max === f.weight[1]
                : f.weight[0] === 400 && f.weight[1] === 400));
        ok(name + ' starts with the wOF2 signature', fs.readFileSync(file).slice(0, 4).toString('latin1') === 'wOF2');
    });
    const lic = f.licence && path.join(DIR, f.licence);
    ok(f.family + ' carries its licence, the SIL Open Font License',
        !!lic && fs.existsSync(lic) && /SIL OPEN FONT LICENSE Version 1\.1/.test(fs.readFileSync(lic, 'utf8')));
});

const referenced = new Set();
served.forEach(f => { Object.keys(f.files).forEach(k => referenced.add(f.files[k])); referenced.add(f.licence); });
const stray = fs.existsSync(DIR) ? fs.readdirSync(DIR).filter(n => !referenced.has(n)) : [];
ok('every file in Web/themes/fonts/ is one fonts.json names' + (stray.length ? ' (not ' + stray.join(', ') + ')' : ''), stray.length === 0);

const csproj = fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8');
ok('the csproj embeds the font files and their licences',
    /Include="Web\/themes\/fonts\/\*\.woff2"/.test(csproj) && /Include="Web\/themes\/fonts\/\*\.txt"/.test(csproj));

console.log('\n── Every served font draws every letter Bonfire shows ─────────');

// The words on the screens a theme dresses: English inline in profiles.js, and every
// translation's switcher half. The dashboard's admin.* keys are left out; it wears
// Jellyfin's own font, not a theme's.
// Not L.readProfiles(), which takes argv[2]; here that is the font list.
const src = fs.readFileSync(process.argv[3] || path.join(L.ROOT, 'Web', 'profiles.js'), 'utf8');
const en = (function () {
    const at = src.indexOf('const EN_STRINGS = {');
    if (at === -1) throw new Error('fonts.js: no EN_STRINGS in the script');
    let depth = 0;
    for (let i = src.indexOf('{', at); i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return new Function('return ' + src.slice(src.indexOf('{', at), i + 1))();
    }
    throw new Error('fonts.js: EN_STRINGS is not closed');
})();
const texts = Object.keys(en).map(k => en[k]);
const langs = fs.readdirSync(L.i18nDir()).filter(n => /^[a-z]{2}(-[A-Za-z]+)?\.json$/.test(n));
langs.forEach(n => {
    const j = JSON.parse(fs.readFileSync(path.join(L.i18nDir(), n), 'utf8'));
    Object.keys(j).filter(k => k.indexOf('admin.') !== 0).forEach(k => texts.push(j[k]));
});
ok('the strings are found (' + texts.length + ' from English and ' + langs.join(', ') + ')',
    texts.length > 1000 && langs.length >= 5);

const chars = new Set();
texts.forEach(s => { for (const ch of String(s).replace(/<[^>]*>|\{[a-zA-Z0-9_]+\}/g, '')) chars.add(ch.codePointAt(0)); });
// Spot checks that the set is the real one, so an empty or English-only read cannot pass.
['ł', 'ğ', 'ı', 'ş', 'ç', 'à', 'é', 'ñ', 'ż'].forEach(ch =>
    ok('the strings include "' + ch + '"', chars.has(ch.codePointAt(0))));

const ranges = {};
Object.keys(SUBSETS).forEach(sub => { ranges[sub] = W.inRange(SUBSETS[sub]); });
const letter = c => /[\p{L}\p{N}]/u.test(String.fromCodePoint(c));
const show = c => '"' + String.fromCodePoint(c) + '" U+' + c.toString(16).toUpperCase().padStart(4, '0');

// A letter outside every range is fetched from no file at all, whatever the font: the
// ranges themselves are wrong for this language.
const outside = [...chars].filter(c => c > 0x20 && !Object.keys(ranges).some(s => ranges[s](c)));
const lettersOutside = outside.filter(letter);
ok('every letter and digit falls in a subset Bonfire serves' + (lettersOutside.length ? ' (not ' + lettersOutside.map(show).join(', ') + ')' : ''),
    lettersOutside.length === 0);
if (outside.length) console.log('        symbols left to the fallback font: ' + outside.map(show).join(', '));

served.forEach(f => {
    const missing = [];
    [...chars].filter(c => c > 0x20).forEach(c => {
        // The browser uses the first face whose range holds the character; fonts.json
        // lists latin first, as fontsource's CSS does.
        const sub = Object.keys(SUBSETS).find(s => ranges[s](c));
        if (!sub) return;
        const r = read[f.files[sub]];
        if (!r || !r.codepoints.has(c)) missing.push(show(c) + ' in ' + sub);
    });
    ok(f.family + ' draws every character of every language' + (missing.length ? ' (missing ' + missing.slice(0, 6).join(', ') + (missing.length > 6 ? ', …' : '') + ')' : ''),
        missing.length === 0);
});

console.log('\n── The web client declares them, and uses them ────────────────');
{
    const gen = L.themeGenerator(src);
    ok('the client has fontFaceCss', typeof gen.fontFaceCss === 'function');
    const css = gen.fontFaceCss ? gen.fontFaceCss(u => 'https://tv.example' + u) : '';
    const faces = css.match(/@font-face \{[^}]*\}/g) || [];
    ok('one @font-face per served file (' + faces.length + ')', faces.length === served.length * Object.keys(SUBSETS).length);
    served.forEach(f => Object.keys(SUBSETS).forEach(sub => {
        const face = faces.find(x => x.indexOf('/themes/fonts/' + f.files[sub] + '"') !== -1) || '';
        ok(f.files[sub] + ': its own name, its weights, its range, through the address given',
            face.indexOf("font-family: 'Bonfire " + f.family + "';") !== -1
            && face.indexOf('font-weight: ' + f.weight[0] + ' ' + f.weight[1] + ';') !== -1
            && face.indexOf('unicode-range: ' + SUBSETS[sub] + ';') !== -1
            && face.indexOf('src: url("https://tv.example/plugins/profiles/themes/fonts/' + f.files[sub] + '") format("woff2");') !== -1
            && face.indexOf('font-display: swap;') !== -1);
    }));

    // Under its own name, so declaring Noto Sans cannot change how jellyfin-web draws its
    // own Noto Sans, which it declares for itself.
    ok('no face is declared under a plain family name', faces.every(x => /font-family: 'Bonfire /.test(x)));

    // The stack a theme gets: the served copy first when there is one.
    const one = (fam) => {
        const t = JSON.parse(JSON.stringify(L.themesPayload().themes.find(x => x.id === 'harvest')));
        t.id = 'probe'; t.type.font.family = fam;
        return (/--jpf-font: ([^;]*);/.exec(gen.themeCss(t, {})) || [])[1] || '';
    };
    ok('a served family is asked for under Bonfire\'s name first (' + one('Lora') + ')', /^'Bonfire Lora', Lora, serif$/.test(one('Lora')));
    ok('one Bonfire does not serve keeps its installed names (' + one('Georgia') + ')', /^Georgia, /.test(one('Georgia')));

    // Whatever is in the list, nothing in it can leave the rule it is written into.
    const evil = {
        themes: [], patterns: {},
        subsets: { latin: 'U+0000-00FF', bad: 'U+0000; } body { display: none' },
        fonts: [
            { family: "X'; } body { display:none } /*", weight: [100, 900], files: { latin: 'x.woff2', bad: 'y.woff2' } },
            { family: 'Y', weight: ['1;}', 900], files: { latin: '../../etc/passwd' } }
        ]
    };
    const bad = L.themeGenerator(src, evil).fontFaceCss(u => u + '"); } body { x: url("');
    ok('a family name cannot end the rule', bad.indexOf('display:none') === -1 && bad.indexOf("X'") === -1);
    ok('nor a range, a file name or a weight', bad.indexOf('display: none') === -1 && bad.indexOf('passwd') === -1 && bad.indexOf('1;}') === -1);
    const srcs = bad.match(/src: [^\n]*/g) || [];
    ok('nor the address it is given (' + srcs.length + ' face)', srcs.length === 1
        && /^src: url\("[^"\\]*"\) format\("woff2"\);$/.test(srcs[0].replace(/\r$/, '')));

    const inject = /injectFonts: function \(\) \{([\s\S]*?)\n        \}/.exec(src);
    ok('fonts are declared only where variable fonts work, so an old TV keeps its installed ones',
        !!inject && /CSS\.supports\('font-variation-settings', 'normal'\)/.test(inject[1]) && /if \(!variable\) return;/.test(inject[1]));
    ok('once', !!inject && /getElementById\('jpf-fonts'\)\) return;/.test(inject[1]));
    ok('through pluginUrl, so a packaged client asks the server', !!inject && /fontFaceCss\(pluginUrl\)/.test(inject[1]));
    ok('when the sheet goes in, and again when the server answers',
        // The custom themes' block may sit between (1.7.2): it goes after the sheet too.
        /style\.textContent = BONFIRE_STYLES[^\n]*\n\s*document\.head\.appendChild\(style\);(?:\s*\/\/[^\n]*|\s*this\.applyCustomThemesCss\(\);)*\s*this\.injectFonts\(\);/.test(src)
        && /this\.applyTheme\(read\('theme'\)\);\s*this\.injectFonts\(\);/.test(src));
}

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
