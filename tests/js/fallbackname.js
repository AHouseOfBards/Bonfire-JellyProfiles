/*
 * getCachedActiveProfile before anything is cached (r2-26-04).
 *
 * Its last resort returned the bare English word 'Profiles' as the name, which the
 * profile page printed untranslated, and it called ApiClient with no guard, so a page
 * where the client had not loaded threw. The name is now empty (the caller falls back
 * through t()) and a missing ApiClient is survived.
 *
 *   node tests/js/fallbackname.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const vm = require('vm');
const lib = require('./_lib');

let bad = 0, pass = 0;
function ok(name, cond) { if (!cond) { bad++; console.log('FAIL ' + name); } else { pass++; } }

function make(apiClient) {
    const src = fs.readFileSync(lib.profilesPath(), 'utf8').replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');
    const store = { getItem() { return null; }, setItem() {}, removeItem() {} };
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: store, sessionStorage: store,
        navigator: { userAgent: 'Mozilla/5.0' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false }); },
        document: {
            addEventListener() {}, removeEventListener() {}, querySelector() { return null; },
            querySelectorAll() { return []; }, getElementById() { return null; },
            createElement() { return { style: {}, classList: { add() {}, remove() {} }, appendChild() {} }; },
            head: { appendChild() {} }, body: { classList: { add() {}, remove() {} } },
            documentElement: { style: { cssText: '', removeProperty() {} }, classList: { add() {}, remove() {} } }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise
    };
    if (apiClient) sandbox.ApiClient = apiClient;
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    return sandbox.__PROFILES;
}

let info = null;
try { info = make(null).getCachedActiveProfile(); } catch (e) { ok('no ApiClient: does not throw (' + e.message + ')', false); }
if (info) ok('no ApiClient: the name is empty, not English', info.name === '');

info = null;
try { info = make({ getCurrentUserId() { return null; } }).getCachedActiveProfile(); } catch (e) { ok('empty client: does not throw', false); }
if (info) ok('empty client: the name is empty, not English', info.name === '');

info = null;
try { info = make({ getCurrentUserId() { return null; }, _currentUser: { Name: 'maria' } }).getCachedActiveProfile(); } catch (e) { ok('named user: does not throw', false); }
if (info) ok('named user: still uses the account name', info.name === 'maria' && info.initial === 'M');

console.log(bad ? bad + ' failed' : 'ok: the fallback name is empty and a missing ApiClient is survived (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
