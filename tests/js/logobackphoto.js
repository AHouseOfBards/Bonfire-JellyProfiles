/*
 * The logo's backing on a theme with a background picture (2.0).
 *
 * fitLogoBacking decides whether a see-through logo needs a backing by holding its pixels
 * against the gate's solid colors (gateBackground). A custom theme's picture is drawn over
 * those and under the theme's fill at its dim, so at a low dim what is behind the logo can
 * be close to the picture: a white logo over a white photograph on a dark theme, or a
 * near-black one over a dark photograph on a light theme, read as fine and got no backing.
 * The server's contrast check treats a picture as its worst case (the fill at the dim over
 * white and over black); the backing is now judged against those too.
 *
 * Runs the real fitLogoBacking and the color functions out of profiles.js, with the theme
 * and the logo's pixels stubbed. logobacking.js is the on-screen check, and needs Chrome.
 *
 *   node tests/js/logobackphoto.js [path/to/profiles.js]
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const SRC = process.argv[2] ? fs.readFileSync(process.argv[2], 'utf8') : L.readProfiles();

function method(name) {
    const at = SRC.indexOf('        ' + name + ': function (');
    if (at === -1) throw new Error('no method ' + name);
    const open = SRC.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}' && --depth === 0) return SRC.slice(open + 1, i);
    }
    throw new Error('unterminated ' + name);
}

// Whatever helpers the build has: the old one has no picture helper, and must still run.
const helpers = ['relLum', 'contrastOf', 'gateColours', 'gatePhotoColours']
    .filter(n => new RegExp('function\\s+' + n + '\\s*\\(').test(SRC))
    .map(n => L.extractFunction(SRC, n)).join('\n');

/** The backing fitLogoBacking puts on a logo of luminances `lums`, on theme `theme`. */
function backing(theme, gateBg, lums) {
    const classes = {};
    const box = {
        classList: { toggle: (c, on) => { classes[c] = on; } },
        querySelector: () => ({ _jpfLums: lums })
    };
    const document = {
        getElementById: id => (id === 'profiles-logo' ? box : null),
        documentElement: { getAttribute: () => theme.id }
    };
    const fn = new Function('document', 'customTheme', 'gateBackground',
        helpers + '\nreturn function () {' + method('fitLogoBacking') + '};');
    fn(document, id => (id === theme.id ? theme : null), () => gateBg).call({});
    return classes['jpf-logo-back-dark'] ? 'dark' : classes['jpf-logo-back-light'] ? 'light' : '';
}

const lin = x => { x /= 255; return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); };
const lum = k => 0.2126 * lin(k[0]) + 0.7152 * lin(k[1]) + 0.0722 * lin(k[2]);
const white = Array(200).fill(lum([250, 250, 250]));
const nearBlack = Array(200).fill(lum([20, 20, 20]));

const dark = { id: 'den', background: { fill: '#121521' } };
const darkPhoto = { id: 'den', background: { fill: '#121521', image: { file: 'a'.repeat(32) + '.jpg', dim: 0.3 } } };
const light = { id: 'attic', background: { fill: '#f4f4f4' } };
const lightPhoto = { id: 'attic', background: { fill: '#f4f4f4', image: { file: 'b'.repeat(32) + '.jpg', dim: 0.3 } } };
const darkHeavy = { id: 'den', background: { fill: '#121521', image: { file: 'a'.repeat(32) + '.jpg', dim: 0.95 } } };

console.log('\n── Without a picture, as before ──────────────────────────────');
ok('a white logo on a dark theme is left bare', backing(dark, '#121521', white) === '');
ok('a near-black logo on a light theme is left bare', backing(light, '#f4f4f4', nearBlack) === '');
ok('a near-black logo on a dark theme is backed light', backing(dark, '#121521', nearBlack) === 'light');

console.log('\n── With a picture at a low dim ───────────────────────────────');
ok('a white logo on a dark theme over a picture at 0.3 is backed dark', backing(darkPhoto, '#121521', white) === 'dark',
    backing(darkPhoto, '#121521', white) || 'no backing');
ok('a near-black logo on a light theme over a picture at 0.3 is backed light', backing(lightPhoto, '#f4f4f4', nearBlack) === 'light',
    backing(lightPhoto, '#f4f4f4', nearBlack) || 'no backing');

console.log('\n── A dim strong enough that the picture barely shows ─────────');
ok('a white logo on a dark theme at 0.95 is still left bare', backing(darkHeavy, '#121521', white) === '');

console.log('');
console.log(pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
