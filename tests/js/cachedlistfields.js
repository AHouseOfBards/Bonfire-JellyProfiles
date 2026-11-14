/*
 * The cached profile list holds what its reader needs and nothing else (r2-27-02b).
 *
 * jellyfin_profiles_cached_list lives in localStorage, which outlives the session on a
 * shared browser. Its only reader is getCachedActiveProfile, which builds the header
 * bubble. The list used to be the whole normalized profile: device ids, tags, library
 * lists. Each write site must now go through _cacheableProfiles, and that function must
 * emit exactly the fields the reader reads, no more and no fewer.
 *
 *   node tests/js/cachedlistfields.js [path-to-profiles.js]
 */
'use strict';
const fs = require('fs');
const vm = require('vm');
const lib = require('./_lib');

let src = fs.readFileSync(lib.profilesPath(), 'utf8');
let bad = 0, pass = 0;
function ok(name, cond) { if (!cond) { bad++; console.log('FAIL ' + name); } else { pass++; } }

// Every write site, pairwise.
const re = /localStorage\.setItem\('jellyfin_profiles_cached_list',\s*([^;]*)\);/g;
let m, sites = 0;
while ((m = re.exec(src))) {
    sites++;
    ok('write site ' + sites + ' stores only the trimmed list: ' + m[1],
        /this\._cacheableProfiles\(/.test(m[1]));
}
ok('there are write sites', sites >= 2);

// The reader's fields, taken from the reader.
const rd = src.indexOf('getCachedActiveProfile: function');
// Ends at the no-list fallback. Anchored on code, not on its comment, which was reworded.
const body = src.slice(rd, src.indexOf('const apiClient = haveClient ? ApiClient : null;', rd));
const readerFields = {};
body.replace(/\bprofile\.(\w+)/g, function (_, f) { readerFields[f] = true; return ''; });
readerFields.profileUserId = true;
ok('reader fields found', Object.keys(readerFields).length >= 5);

src = src.replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');
const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
    sessionStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
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
sandbox.window = sandbox;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
const plugin = sandbox.__PROFILES;

if (typeof plugin._cacheableProfiles !== 'function') {
    ok('_cacheableProfiles exists', false);
} else {
    const norm = plugin.normalizeProfiles([{
        profileUserId: 'a', profileName: 'Kid', avatarColor: '#123456', avatarInitial: 'K',
        profileImage: 'x.png', transparentAvatar: true, allowedTags: ['t'], blockedTags: ['u'],
        deviceIds: ['dev1'], allowedLibraries: ['l']
    }]);
    const out = plugin._cacheableProfiles(norm)[0];
    const keys = Object.keys(out).sort().join(',');
    ok('keys are exactly the reader\'s: ' + keys, keys === Object.keys(readerFields).sort().join(','));
    ok('values survive', out.profileName === 'Kid' && out.avatarColor === '#123456' && out.transparentAvatar === true);
    ok('no device ids or tags in the output', JSON.stringify(out).indexOf('dev1') < 0 && JSON.stringify(out).indexOf('"t"') < 0);
}
console.log(bad ? bad + ' failed' : 'ok: the cached list holds only what the header bubble reads (' + pass + ' passed, 0 failed)');
process.exit(bad ? 1 : 0);
