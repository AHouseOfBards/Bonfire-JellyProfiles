/*
 * The Authorization header when there is no token (R-1249).
 *
 * getAuthHeaders interpolated the token blindly, so a null masterToken sent
 * Token="null" and an undefined one Token="undefined". The server treats that as a bad
 * token, not as none. With no token the field is left out.
 *
 *     node tests/js/authheader.js /path/to/old/profiles.js
 */
'use strict';

const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  ' + detail : '')); }
}

const INIT = 'ProfilesPlugin.init();';
let src = L.readProfiles();
if (src.split(INIT).length - 1 !== 1) { console.error('could not find the single init() call'); process.exit(1); }
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function makeEl() {
    return { style: {}, textContent: '', addEventListener() {}, removeEventListener() {},
             setAttribute() {}, getAttribute: () => null, appendChild() {}, remove() {}, focus() {} };
}
const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', language: 'en' },
    setTimeout() { return 1; }, clearTimeout() {}, setInterval() {}, clearInterval() {},
    fetch: () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) }),
    ApiClient: { appName: () => 'Web', deviceName: () => 'Dev', deviceId: () => 'id1', appVersion: () => '1' },
    document: {
        addEventListener() {}, removeEventListener() {},
        querySelector() { return null; }, querySelectorAll() { return []; },
        getElementById: () => null, createElement: makeEl,
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

ok('a token is sent', /Token="abc"/.test(P.getAuthHeaders('abc').Authorization));
[null, undefined, ''].forEach(function (v) {
    const h = P.getAuthHeaders(v).Authorization;
    ok('no Token field for ' + JSON.stringify(v), !/Token=/.test(h), h);
    ok('and no "null" or "undefined" text for ' + JSON.stringify(v), !/null|undefined/.test(h), h);
    ok('the device is still named for ' + JSON.stringify(v), /DeviceId="id1"/.test(h), h);
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
