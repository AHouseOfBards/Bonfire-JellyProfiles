/*
 * The Dashboard -> Users retry is not forgotten inside its wait (r2-26-02).
 *
 * checkRoute clears _usersRetryDue before calling syncHiddenProfiles. When the address
 * changed during the wait after a failed read, syncHiddenProfiles hit the wait check and
 * returned without raising the flag again, so nothing ever asked a second time and profile
 * accounts stayed visible on the Users page.
 *
 *   node tests/js/hiddenretry.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const vm = require('vm');
const lib = require('./_lib');

let bad = 0, pass = 0;
function ok(name, cond) { if (!cond) { bad++; console.log('FAIL ' + name); } else { pass++; } }

const src = fs.readFileSync(lib.profilesPath(), 'utf8').replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');
const store = { getItem() { return null; }, setItem() {}, removeItem() {} };
let fetched = 0;
const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: store, sessionStorage: store,
    navigator: { userAgent: 'Mozilla/5.0' },
    setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
    fetch() { fetched++; return new Promise(function () {}); },
    ApiClient: { accessToken() { return 'tok'; }, getUrl(p) { return '/' + p; } },
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

// Inside the wait, as checkRoute leaves it: flag cleared, wait not over.
p._usersRetryDue = false;
p._usersMappingsRetryAt = Date.now() + 60000;
p.syncHiddenProfiles('#/dashboard/users');
ok('inside the wait: no request is sent', fetched === 0);
ok('inside the wait: the retry stays due', p._usersRetryDue === true);

// Another page entirely must not raise it.
p._usersRetryDue = false;
p.syncHiddenProfiles('#/home');
ok('off the Users page: the retry is not raised', p._usersRetryDue === false);

console.log(bad ? bad + ' failed' : 'ok: a retry cleared by checkRoute is raised again inside its wait (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
