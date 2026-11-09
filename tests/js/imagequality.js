/*
 * Pictures are rendered at the size the administrator chose.
 *
 * Every resize happens in the browser, on a canvas. The server's picture quality setting
 * (standard, high, maximum) is only worth anything if the browser actually renders at the
 * size it names — otherwise "high" stores the same 512-pixel picture under a bigger limit.
 * The setting reaches the client with the avatar list, which every picker has already
 * fetched by the time anybody can save a picture.
 *
 * Runs the shipped file with init() swapped for an export:
 *
 *     node tests/js/imagequality.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

let src = fs.readFileSync(L.profilesPath(), 'utf8');
src = src.replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');

const canvases = [];
let serverImage = null;

const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    navigator: { userAgent: 'Mozilla/5.0', language: 'en' },
    setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
    fetch: () => Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ allowCustomUploads: true, avatars: [], image: serverImage })
    }),
    document: {
        addEventListener() {}, removeEventListener() {},
        querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
        createElement: (tag) => {
            const c = {
                tag, width: 0, height: 0, encoded: [],
                getContext: () => ({ drawImage() {} }),
                toDataURL(type, q) { c.encoded.push([type, q]); return 'data:' + type + ';base64,AAAA'; },
                style: {}, classList: { add() {}, remove() {} }, appendChild() {}
            };
            if (tag === 'canvas') canvases.push(c);
            return c;
        },
        head: { appendChild() {} },
        body: { classList: { add() {}, remove() {}, contains: () => false }, appendChild() {} },
        documentElement: { style: { cssText: '', removeProperty() {}, setProperty() {} },
                           classList: { add() {}, remove() {}, contains: () => false } }
    },
    JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
};
sandbox.window = sandbox;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
const P = sandbox.__PROFILES;
P.getAuthHeaders = () => ({});

(async function main() {

console.log();
console.log('── The sizes come from the server ──────────────────────────────');

ok('the client can read a picture quality at all', typeof P.parseImageSpec === 'function');
if (typeof P.parseImageSpec === 'function') {
    const standard = P.parseImageSpec(null);
    ok('with nothing from the server it renders as it always did',
        standard.masterSize === 512 && standard.thumbSize === 128 && standard.jpegQuality === 0.85);

    const garbage = P.parseImageSpec({ masterSize: 999999, thumbSize: -4, jpegQuality: 7 });
    ok('an out-of-range answer falls back rather than being trusted',
        garbage.masterSize === 512 && garbage.thumbSize === 128 && garbage.jpegQuality === 0.85);

    const pascal = P.parseImageSpec({ MasterSize: 2048, ThumbSize: 256, JpegQuality: 0.92 });
    ok('either casing is read', pascal.masterSize === 2048 && pascal.thumbSize === 256);
}

serverImage = { masterSize: 1024, thumbSize: 256, jpegQuality: 0.9, maxBytes: 4194304 };
await P.fetchAvatarLibrary({ getUrl: u => '/' + u }, 'token');
const spec = typeof P.imageSpec === 'function' ? P.imageSpec() : null;
ok('the avatar list carries the setting to the picker', !!spec && spec.masterSize === 1024 && spec.thumbSize === 256);

console.log();
console.log('── And the picture is rendered at them ─────────────────────────');

canvases.length = 0;
const img = { width: 3000, height: 2000 };
const out = P.renderCrop(img, 300, { zoom: 1, x: 0, y: 0 }, spec ? spec.masterSize : 512, false);
ok('the crop is drawn at the chosen size', canvases.length === 1 && canvases[0].width === 1024);
ok('and encoded at the chosen quality',
    canvases.length === 1 && canvases[0].encoded.length === 1 && canvases[0].encoded[0][1] === 0.9);
ok('as a JPEG', typeof out === 'string' && out.indexOf('data:image/jpeg') === 0);

// The save handler is a closure inside the crop dialog, so it is checked where it reads
// its sizes: from the spec, not from the fixed constants that used to decide everything.
const dialog = src.slice(src.indexOf('showCropDialog: function'), src.indexOf('fetchAvatarLibrary: function'));
ok('the crop dialog renders the full picture at the spec size',
    dialog.indexOf('this.renderCrop(img, VIEW, crop, spec.masterSize, preferPng)') !== -1);
ok('and the thumbnail at the spec thumbnail size',
    dialog.indexOf('this._cropCanvas(img, VIEW, crop, spec.thumbSize)') !== -1);

console.log();
console.log('── The dashboard: four levels, stored ids unchanged ────────────');

// The stored ids are values in saved configs. "high" has always meant 1024 px and "maximum"
// 2048 px; the words an administrator reads (Low, Medium, High, Maximum) moved up one place
// when a fourth level was added, and renaming the ids to match would silently change what a
// server saved. So this pins each id to the size it has always had, and each label to the size
// its id really has.
const PINNED = {
    standard: { px: 512, mb: 2, word: 'Low', key: 'qualityStandard' },
    high: { px: 1024, mb: 4, word: 'Medium', key: 'qualityHigh' },
    maximum: { px: 2048, mb: 8, word: 'High', key: 'qualityMaximum' },
    ultra: { px: 4096, mb: 16, word: 'Maximum', key: 'qualityUltra' }
};
const html = L.readDashboard();
const dscript = L.dashboardScript(html);
let specs = null, normalize = null;
try {
    specs = new Function('return ' + /const IMAGE_QUALITY_SPECS = (\{[\s\S]*?\n\s*\});/.exec(dscript)[1].replace(/;$/, ''))();
    normalize = new Function(L.extractFunction(dscript, 'normalizeImageQuality') + '; return normalizeImageQuality;')();
} catch (e) { /* reported below */ }
ok('the dashboard has the spec table and the normaliser', !!specs && !!normalize);
Object.keys(PINNED).forEach(id => {
    const p = PINNED[id];
    ok(id + ' renders at ' + p.px + ' px and takes up to ' + p.mb + ' MB',
        !!specs && !!specs[id] && specs[id].master === p.px && specs[id].mb === p.mb);
    ok(id + ' is a level the dashboard keeps', !!normalize && normalize(id) === id);
    const option = new RegExp('<option value="' + id + '" data-i18n="admin\\.avatars\\.' + p.key + '">([^<]*)</option>').exec(html);
    ok(id + ' is labelled ' + p.word + ' with its real size',
        !!option && option[1] === p.word + ' — ' + p.px + ' px, up to ' + p.mb + ' MB', option ? option[1] : 'no option');
});
ok('an unknown level is the safest one, not the default', !!normalize && normalize('enormous') === 'standard' && normalize('') === 'standard');
ok('and the select offers exactly these four, in size order',
    JSON.stringify((/<select is="emby-select" id="profileImageQuality">([\s\S]*?)<\/select>/.exec(html) || ['', ''])[1]
        .match(/value="([a-z]+)"/g)) === JSON.stringify(['value="standard"', 'value="high"', 'value="maximum"', 'value="ultra"']));
ok('the client accepts a 4096 px / 0.92 answer from the server', (() => {
    const s = P.parseImageSpec({ masterSize: 4096, thumbSize: 256, jpegQuality: 0.92 });
    return s.masterSize === 4096 && s.thumbSize === 256 && s.jpegQuality === 0.92;
})());

const wordsOf = (label) => label.replace(/\s*—.*$/, '');
fs.readdirSync(L.i18nDir()).filter(f => /\.json$/.test(f)).forEach(f => {
    const catalog = JSON.parse(fs.readFileSync(require('path').join(L.i18nDir(), f), 'utf8'));
    Object.keys(PINNED).forEach(id => {
        const p = PINNED[id];
        const label = catalog['admin.avatars.' + p.key] || '';
        ok(f + ': ' + id + ' shows ' + p.px + ' px and ' + p.mb + ' MB',
            new RegExp('\\b' + p.px + ' px\\b').test(label) && new RegExp('\\b' + p.mb + ' (MB|Mo)\\b').test(label), label);
    });
    const words = Object.keys(PINNED).map(id => wordsOf(catalog['admin.avatars.' + PINNED[id].key] || ''));
    ok(f + ': four different words', words.every(Boolean) && new Set(words).size === 4, words.join(', '));
});

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
})();
