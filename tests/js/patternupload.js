// An uploaded pattern picture (png or webp, named by a hash) reaches the stylesheet as a
// url() to the pattern route, and the ten built-in pictures exist as files with a display
// name in every language. Runs the real themeCss.
//
//   node tests/js/patternupload.js [path/to/profiles.js]

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const gen = L.themeGenerator(L.readProfiles());
const patterns = L.themesPayload().patterns || {};
const bloom = JSON.parse(fs.readFileSync(path.join(L.themesDir(), 'bloom.json'), 'utf8'));

function deco(image) {
    const t = JSON.parse(JSON.stringify(bloom));
    t.decorations.forEach(d => { if (d.name === 'pattern') d.image = image; });
    return t;
}
function deco_of(css) {
    const m = /--jpf-deco:\s*([^;]*);/.exec(css);
    return m ? m[1] : null;
}

const hash = 'a'.repeat(32);
['.png', '.webp'].forEach(ext => {
    const v = deco_of(gen.themeCss(deco(hash + ext), patterns));
    ok('an uploaded ' + ext + ' is drawn from the pattern route', v === 'url("/plugins/profiles/themes/pattern-files/' + hash + ext + '")', v);
});
['.jpg', '.svg', '.gif'].forEach(ext => {
    ok('an uploaded ' + ext + ' draws nothing', deco_of(gen.themeCss(deco(hash + ext), patterns)) === 'none');
});
['../' + hash + '.png', hash + '.png"', 'a'.repeat(31) + '.png', 'A'.repeat(32) + '.png', hash + '.png\n'].forEach(n => {
    ok('a name that is not a stored picture\'s draws nothing: ' + JSON.stringify(n), deco_of(gen.themeCss(deco(n), patterns)) === 'none');
});
// A second pattern's upload is drawn too; it was only looked up among the built-in ones.
function second(image) {
    const t = deco('snow');
    t.decorations.push({ name: 'pattern2', image: image, size: 120, drift: 'none' });
    return t;
}
const var2 = css => { const m = /--jpf-deco2:\s*([^;]*);/.exec(css); return m ? m[1] : null; };
['.png', '.webp'].forEach(ext => {
    const got = var2(gen.themeCss(second(hash + ext), patterns));
    ok('an uploaded second pattern (' + ext + ') is drawn from the pattern route',
        got === 'url("/plugins/profiles/themes/pattern-files/' + hash + ext + '")', String(got));
});
ok('a second pattern naming a path draws nothing', var2(gen.themeCss(second('../' + hash + '.png'), patterns)) === null);
ok('a built-in second pattern still draws inline', /^url\("data:image\/svg\+xml,/.test(var2(gen.themeCss(second('stars'), patterns)) || ''));
ok('a built-in name still draws inline', /^url\("data:image\/svg\+xml,/.test(deco_of(gen.themeCss(deco('snow'), patterns)) || ''));

const NAMES = ['blossoms', 'bubbles', 'confetti', 'embers', 'hearts', 'leaves', 'moons', 'snow', 'stars', 'waves'];
const files = fs.readdirSync(path.join(L.themesDir(), 'patterns')).filter(f => /\.svg$/.test(f)).map(f => f.replace(/\.svg$/, '')).sort();
ok('there are ten built-in pictures, by the agreed names', JSON.stringify(files) === JSON.stringify(NAMES), files.join(','));

const i18n = path.join(L.ROOT, 'Web', 'i18n');
fs.readdirSync(i18n).filter(f => /\.json$/.test(f)).forEach(f => {
    const j = JSON.parse(fs.readFileSync(path.join(i18n, f), 'utf8'));
    const missing = NAMES.filter(n => typeof j['admin.themes.pattern.' + n] !== 'string');
    ok(f + ' names every picture', missing.length === 0, missing.join(','));
});
const html = fs.readFileSync(path.join(L.ROOT, 'Web', 'profilesDashboard.html'), 'utf8');
ok('the dashboard has an English name for every picture',
    NAMES.every(n => html.indexOf("'admin.themes.pattern." + n + "':") >= 0));
ok('and lists every picture for the editor\'s drop-down',
    NAMES.every(n => new RegExp('\\b' + n + ": 'admin\\.themes\\.pattern\\." + n + "'").test(html)));

// A non-transparent tile hides the theme under it, and one that leaves its own edge has
// a seam. Proxies: no background rectangle, nothing past the canvas.
NAMES.forEach(n => {
    const svg = fs.readFileSync(path.join(L.themesDir(), 'patterns', n + '.svg'), 'utf8');
    ok(n + ' has a transparent background', !/<rect[^>]*width=['"]100%/.test(svg) && !/<rect[^>]*width=['"]240['"]/.test(svg));
    ok(n + ' is a 240 or 260 pixel tile', /width='(240|260)' height='(240|260)'/.test(svg));
    ok(n + ' has no script or external reference', !/<script|onload|href=['"](?!#)|<image|<foreignObject/i.test(svg));
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
