/*
 * Saving a profile must never loosen a restriction the household did not touch.
 *
 * Four ways it did, each silent, each a save for some unrelated reason (a new avatar,
 * a rename) taking a limit off a child's profile:
 *
 *   1. A rating set in Dashboard -> Users that is not one of the form's five choices
 *      (PG-13 is 13; the form's "PG-13 / TV-14" is 14) had no option to preselect, so
 *      the select showed "No Restrictions" and the save cleared it.
 *   2. The same save dropped the rating's sub-score (TV-PG vs TV-PG-V) every time.
 *   3. If the device list could not be read, the form drew no device boxes and saved
 *      the profile's allowed devices as [] — "any device".
 *   4. A whitelisted device id in a different case from the device's record drew an
 *      unticked box (the server compares ids ignoring case, the form did not), and the
 *      save dropped it.
 *
 * And two on the switch path:
 *
 *   5. Every switch into a sub-profile wrote IsDisabled = false, so an account turned
 *      off in Dashboard -> Users — or by the delete route's own fallback — came back on
 *      the next time somebody picked it. AuthenticateDirect does not check IsDisabled.
 *   6. A profile with no stored library list and "all libraries" on its account was
 *      migrated from its master's EnabledFolders, which is empty for an all-libraries
 *      master, so it was saved with no libraries at all.
 *
 *   node tests/js/restrictions.js [profiles.js] [ProfilesController.cs] [ProfilePolicySync.cs]
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
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

// ── The client, executed ────────────────────────────────────────────────────

const src = L.readClientBundle(fs.readFileSync(L.profilesPath(), 'utf8'));
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    navigator: { userAgent: 'Mozilla/5.0', languages: ['en'] },
    setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
    fetch() { return Promise.resolve({ ok: false, json: () => Promise.reject(new Error('no')) }); },
    location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
    document: {
        addEventListener() {}, removeEventListener() {},
        querySelector: () => null, querySelectorAll: () => [],
        getElementById: () => null,
        createElement: () => ({ style: {}, classList: { add() {}, remove() {}, contains: () => false, toggle: () => false } }),
        head: { appendChild() {} },
        body: { classList: { add() {}, remove() {}, contains: () => false } },
        documentElement: { style: {}, classList: { add() {}, remove() {}, contains: () => false, toggle: () => false } },
    },
    JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set,
    Uint8ClampedArray,
};
sandbox.ApiClient = { appName: 'Jellyfin Web', deviceName: 'Test', deviceId: 'dev', appVersion: '10.11.0' };
sandbox.window = sandbox;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
const P = sandbox.__PROFILES;

function selectedValues(html) {
    const out = [];
    const re = /<option value="([^"]*)"\s*selected/g;
    let m;
    while ((m = re.exec(html)) !== null) out.push(m[1]);
    return out;
}

console.log('── 1. a rating the form has no choice for ──────────────────────');
{
    const html = P.renderRatingSelect('edit', 13);
    ok('a limit of 13 is preselected as itself, not as "No Restrictions"',
        JSON.stringify(selectedValues(html)) === '["13"]', 'selected: ' + JSON.stringify(selectedValues(html)));

    const listed = P.renderRatingSelect('edit', 14);
    ok('a listed limit is preselected as before', JSON.stringify(selectedValues(listed)) === '["14"]');
    ok('and gets no extra option', (listed.match(/<option/g) || []).length === 5);

    const none = P.renderRatingSelect('edit', null);
    ok('no limit still preselects "No Restrictions"', JSON.stringify(selectedValues(none)) === '[""]');
    ok('and gets no extra option either', (none.match(/<option/g) || []).length === 5);
}

console.log('── 3. the device list could not be read ────────────────────────');
function sharedWithDevices(deviceResponse) {
    P._sharedForm = null;
    sandbox.fetch = (url) => {
        if (String(url).indexOf('profiles/devices') !== -1) return Promise.resolve(deviceResponse);
        return Promise.resolve({ ok: true, json: () => Promise.resolve(String(url).indexOf('Filters2') !== -1 ? { Tags: [] } : []) });
    };
    const apiClient = { getUrl: (p) => p };
    return P.fetchSharedFormData(apiClient, { token: 't', masterUserId: 'm' })
        .then(shared => shared.devices, err => 'threw: ' + err.message);
}

const pending = [
    sharedWithDevices({ ok: false, status: 500, json: () => Promise.reject(new SyntaxError('Unexpected token')) })
        .then(d => ok('a failed device request is null, not an empty list', d === null, 'got ' + JSON.stringify(d))),
    sharedWithDevices({ ok: true, status: 200, json: () => Promise.resolve({ message: 'Plugin configuration missing.' }) })
        .then(d => ok('a device response that is not a list is null too', d === null, 'got ' + JSON.stringify(d))),
    sharedWithDevices({ ok: true, status: 200, json: () => Promise.resolve([{ deviceId: 'a' }]) })
        .then(d => ok('a good device list still comes through', Array.isArray(d) && d.length === 1)),
];

{
    const fn = L.extractFunction(src.replace(/showEditProfileModal:\s*function/, 'function showEditProfileModal'), 'showEditProfileModal');
    const guarded = /if\s*\(\s*devices\s*!==\s*null\s*\)\s*\{\s*checkedDevices\s*=\s*\[\]/.test(fn);
    const unguarded = /^\s*checkedDevices\s*=\s*\[\];/m.test(fn.replace(/if\s*\(\s*devices\s*!==\s*null\s*\)\s*\{\s*checkedDevices\s*=\s*\[\];/, ''));
    ok('the edit form only rebuilds the device list when it has one to rebuild from', guarded && !unguarded);

    const unavailable = P.renderDeviceDropdown('edit', null, { allowedDeviceIds: ['abc'] });
    ok('and says so instead of "All Devices Allowed"', unavailable.indexOf('All Devices Allowed') === -1);
}

console.log('── 4. a whitelisted id in another case ─────────────────────────');
{
    const html = P.renderDeviceDropdown('edit', [{ deviceId: 'ABC-123', deviceName: 'TV' }], { allowedDeviceIds: ['abc-123'] });
    ok('the box for it is ticked', /value="ABC-123"\s*checked/.test(html));
    const plain = P.renderDeviceDropdown('edit', [{ deviceId: 'xyz', deviceName: 'TV' }], { allowedDeviceIds: ['abc-123'] });
    ok('a device not on the list is still unticked', !/value="xyz"\s*checked/.test(plain));
    const create = P.renderDeviceDropdown('create', [{ deviceId: 'abc-123', deviceName: 'TV' }], null);
    ok('nothing is ticked on a new profile', !/checked/.test(create));
}

// ── The server, read ────────────────────────────────────────────────────────
// These live inside two long controller actions with no seam to call, so they are read.
// Comments are stripped so the prose explaining a rule cannot satisfy it.

const CONTROLLER = process.argv[3] || path.join(L.ROOT, 'Controllers', 'ProfilesController.cs');
const SYNC = process.argv[4] || path.join(L.ROOT, 'Auth', 'ProfilePolicySync.cs');
const stripComments = s => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
const cs = stripComments(fs.readFileSync(CONTROLLER, 'utf8'));
// Where the switch's inheritance block lives since it was shared with every sign-in.
// Missing in older builds, where the block was still inside SwitchProfile.
const sync = fs.existsSync(SYNC) ? stripComments(fs.readFileSync(SYNC, 'utf8')) : '';

function method(name) {
    const at = cs.search(new RegExp('Task<ActionResult(?:<object>)?>\\s+' + name + '\\s*\\('));
    if (at === -1) return '';
    const open = cs.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < cs.length; i++) {
        if (cs[i] === '{') depth++;
        else if (cs[i] === '}' && --depth === 0) return cs.slice(at, i + 1);
    }
    return '';
}

console.log('── 5. a disabled account ───────────────────────────────────────');
{
    const sw = method('SwitchProfile');
    ok('SwitchProfile is findable', sw.length > 0);
    ok('the switch never turns an account back on', !/IsDisabled\s*=\s*false/.test(sw + sync));
    // [\s\S] rather than [^}]: the log line in between carries a "{ProfileId}" placeholder.
    const refuse = sw.search(/\.IsDisabled\s*==\s*true\s*\)\s*\{[\s\S]{0,400}?return\s+BadRequest/);
    const session = sw.indexOf('AuthenticateDirect');
    ok('and refuses a disabled one before a session is made', refuse !== -1 && session !== -1 && refuse < session);
}

console.log('── 6. a legacy all-libraries profile ───────────────────────────');
{
    const sw = method('SwitchProfile') + sync;
    const legacy = /legacyEnabled\s*=\s*childEnableAllFolders\s*\?\s*([^:]+):/.exec(sw);
    ok('the legacy branch is findable', !!legacy);
    // Either the controller's helper, or the value it computed once for the whole sync.
    const reachable = legacy && (/GetMasterAccessibleFolders\s*\(\s*masterPolicy\s*\)/.test(legacy[1])
        || (/^\s*masterAccessible\.ToList\(\)\s*$/.test(legacy[1])
            && (/var\s+masterAccessible\s*=\s*ProfilesBaseController\.MasterAccessibleFolders\(\s*libraryManager,\s*masterPolicy\s*\)/.test(sync)
                // 2.0: one pass over every profile reads it once (ProfilePolicySync.Pass).
                || (/var\s+masterAccessible\s*=\s*pass\.MasterReach\(\s*masterUser\.Id,\s*masterPolicy\s*\)/.test(sync)
                    && /HashSet<Guid>\s+MasterReach\([^)]*\)\s*\{[^}]*ProfilesBaseController\.MasterAccessibleFolders\(\s*masterPolicy\b/.test(sync)))));
    ok('it takes what the master can reach, not the master\'s EnabledFolders', !!reachable,
        legacy ? legacy[1].trim() : '');
}

console.log('── 7. "tick nothing" means every library, later ones too ───────');
{
    ok('the sync gives an every-library profile what the master can reach now',
        /if\s*\(\s*mapping\.AllLibraries\s*\)\s*\{\s*authorityFolders\s*=\s*masterAccessible\.ToList\(\);/.test(sync));
    const create = method('CreateProfileCore');
    const update = method('UpdateProfile');
    ok('creating with nothing ticked records it', /AllLibraries\s*=\s*request\.EnabledFolders\s*==\s*null/.test(create));
    // Since R-1144 the edit form says "every library" as allLibraries: true, and a
    // left-out list keeps what is stored, so the record follows the flag, not a null list.
    // tests/cs/partialupdate checks the stored record through the real route.
    ok('and so does saving with nothing ticked', /mappingEntry\.AllLibraries\s*=\s*request\.AllLibraries\s*==\s*true/.test(update));
    const form = L.extractFunction(src.replace(/showEditProfileModal:\s*function/, 'function showEditProfileModal'), 'showEditProfileModal');
    ok('the edit form shows such a profile with nothing ticked',
        /if\s*\(\s*profile\.allLibraries\s*\)\s*\{\s*isChecked\s*=\s*false;/.test(form));
}

console.log('── 2. the rating\'s sub-score on save ───────────────────────────');
{
    const up = method('UpdateProfile');
    ok('UpdateProfile is findable', up.length > 0);
    // Every write of a null sub-score must sit inside a branch that changed the score.
    const writes = [...up.matchAll(/MaxParentalSubRating\s*=\s*null/g)].map(m => m.index);
    const guarded = writes.every(i => {
        const before = up.slice(Math.max(0, i - 400), i);
        // R-1144 put "was a rating sent at all" in front of the same comparison.
        return /if\s*\(\s*(?:request\.MaxParentalRating\s*!=\s*null\s*&&\s*)?requestedRating\s*!=\s*targetPolicy\.MaxParentalRating\s*\)\s*\{[^}]*$/.test(before);
    });
    ok('the sub-score is only dropped when the score changes', writes.length > 0 && guarded,
        writes.length + ' write(s)');
}

Promise.all(pending).then(() => {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) {
        fails.forEach(f => console.log('  - ' + f));
        process.exit(1);
    }
});
