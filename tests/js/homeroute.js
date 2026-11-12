/*
 * isHomeRoute matches route segments, not substrings of the address (r2-25-01).
 *
 * A server hosted under /plugins-media/web/ has 'plugin' in every path, and the substring
 * test said "not home" for all of them, so the gate never opened there. Pairwise cases:
 * each row is one address and the answer it must get.
 *
 *   node tests/js/homeroute.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const vm = require('vm');
const lib = require('./_lib');

const src = fs.readFileSync(lib.profilesPath(), 'utf8').replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');
const store = { getItem() { return null; }, setItem() {}, removeItem() {} };
const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: store, sessionStorage: store,
    navigator: { userAgent: 'Mozilla/5.0' },
    setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
    fetch() { return new Promise(function () {}); },
    location: { hash: '', pathname: '/' },
    document: {
        addEventListener() {}, removeEventListener() {}, querySelector() { return null; },
        querySelectorAll() { return []; }, getElementById() { return null; },
        createElement() { return { style: {}, classList: { add() {}, remove() {} }, appendChild() {} }; },
        head: { appendChild() {} }, body: { classList: { add() {}, remove() {} } },
        documentElement: { style: { cssText: '', removeProperty() {} }, classList: { add() {}, remove() {} } }
    },
    JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise
};
sandbox.window = sandbox;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
const p = sandbox.__PROFILES;

const cases = [
    // [pathname, hash, expected]
    ['/plugins-media/web/', '', true],
    ['/plugins-media/web/index.html', '#/home.html', true],
    ['/plugins-media/web/home', '', true],
    ['/login-portal/web/', '#/', true],
    ['/web/', '#/configurationpage?name=Profiles', false],
    ['/web/', '#/dashboard/plugins', false],
    ['/web/', '#/login.html', false],
    ['/web/', '#/selectserver.html', false],
    ['/web/home', '', true],
    ['/web/configurationpage', '', false],
    ['/web/plugins', '', false],
    ['/plugins-media/web/login', '', false],
    ['/web/', '#/search.html?query=plugin', false],
    ['/web/', '#/home.html?context=login', true],
    ['/web/', '', true],
    ['/web/', '#/details?id=1', false]
];
let bad = 0;
cases.forEach(function (c) {
    sandbox.location.pathname = c[0];
    sandbox.location.hash = c[1];
    const got = p.isHomeRoute();
    if (got !== c[2]) { bad++; console.log('FAIL ' + c[0] + ' ' + c[1] + ' -> ' + got + ', want ' + c[2]); }
});
console.log(bad ? bad + ' failed' : 'ok: ' + cases.length + ' addresses classified by route segment');
process.exit(bad ? 1 : 0);
