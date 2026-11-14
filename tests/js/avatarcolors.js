/*
 * A new profile's color.
 *
 * Every profile used to start the same blue, and the defaults disagreed besides: #00A4DC
 * in the client and the server's constant, #1F77B4 in a stored row, in a request that left
 * the field out, and in the API docs. Now:
 *
 *   - the client and the server offer one palette, in one order;
 *   - a new profile starts on the first color nobody in the household has, in the form
 *     and on the server alike;
 *   - nothing still names the old blue.
 *
 *   node tests/js/avatarcolors.js [path/to/profiles.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name + (detail ? '  — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const SRC = process.argv[2] || L.profilesPath();
const js = fs.readFileSync(SRC, 'utf8');
const cs = fs.readFileSync(path.join(L.ROOT, 'Controllers', 'ProfilesBaseController.cs'), 'utf8');

const hexes = s => (s.match(/#[0-9A-Fa-f]{6}/g) || []).map(h => h.toUpperCase());
const jsBlock = /const AVATAR_PALETTE = \[([^\]]+)\]/.exec(js);
const csBlock = /AvatarPalette =\s*\{([^}]+)\}/.exec(cs);
ok('the client has one palette', !!jsBlock);
ok('the server has the palette', !!csBlock);
if (jsBlock && csBlock) {
    ok('they are the same colours in the same order', hexes(jsBlock[1]).join() === hexes(csBlock[1]).join(),
       hexes(jsBlock[1]).join() + ' vs ' + hexes(csBlock[1]).join());
}

// The client's choice, run from the script itself.
const raw = js.replace('ProfilesPlugin.init();', 'globalThis.__P = ProfilesPlugin;');
const box = {
    console: { log() {}, warn() {}, error() {} }, setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0,
    document: { documentElement: { classList: { add() {}, remove() {}, contains: () => false } }, getElementById: () => null,
                querySelector: () => null, querySelectorAll: () => [], addEventListener() {},
                createElement: () => ({ style: {}, classList: { add() {} }, appendChild() {}, setAttribute() {} }),
                head: { appendChild() {} }, body: { appendChild() {} } },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} }, sessionStorage: { getItem: () => null, setItem() {} },
    navigator: { userAgent: '' }, location: { href: '', hash: '', pathname: '/' }, fetch: () => new Promise(() => {}),
    MutationObserver: function () { this.observe = () => {}; }, ApiClient: { getUrl: p => p },
};
box.window = box; box.addEventListener = () => {}; box.globalThis = box;
vm.createContext(box);
vm.runInContext(raw, box);
const P = box.__P;
ok('the client can pick a colour', typeof P.nextAvatarColor === 'function');
if (typeof P.nextAvatarColor === 'function') {
    P.currentProfiles = [{ isMaster: true, avatarColor: '#00A4DC' }];
    ok('the first profile skips the main account\'s colour', P.nextAvatarColor() === '#E50914', P.nextAvatarColor());
    P.currentProfiles.push({ avatarColor: '#e50914' }, { avatarColor: '#22C55E' });
    ok('and later ones skip every colour in use, whatever its case', P.nextAvatarColor() === '#EAB308', P.nextAvatarColor());
    P.currentProfiles.push({ isBonfire: true, avatarColor: '#EAB308' });
    ok('another household\'s colours don\'t count', P.nextAvatarColor() === '#EAB308', P.nextAvatarColor());
    ok('the create form starts on it', /const startColor = this\.nextAvatarColor\(\);/.test(js)
       && /let selectedColor = startColor;/.test(js));
}

// Nothing still promises the old default.
const files = ['Configuration/PluginConfiguration.cs', 'Models/CreateProfileRequest.cs', 'Models/UpdateProfileRequest.cs',
               'docs/developer-api.md', 'Controllers/ProfilesController.cs'];
files.forEach(f => {
    const s = fs.readFileSync(path.join(L.ROOT, f), 'utf8');
    ok(f + ' no longer names #1F77B4', !/1F77B4/i.test(s));
});

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
