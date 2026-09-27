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
console.log(pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
})();
