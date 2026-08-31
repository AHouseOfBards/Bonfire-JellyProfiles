/*
 * The shared profile-form renderers (P5-9 to P5-15).
 *
 * Add Profile and Edit Profile were two functions copied from each other and then
 * edited apart — 213 identical normalised lines, ten runs of six or more. The copies
 * had already drifted, and the duplicated *comments* were the tell: the same sentence
 * about sending null rather than an empty array sat verbatim in both.
 *
 * Merging them is only safe if the two modes still render what they each rendered
 * before, and no harness looked at that. This one does: it calls each renderer in both
 * modes and checks the differences that are supposed to exist, and the sameness that
 * is supposed to exist. A merge that quietly gave the create form the edit form's
 * behaviour would pass every other harness in the suite.
 *
 * Against a pre-merge build this fails structurally — the renderers do not exist there.
 * Say so rather than calling it a bisect. What it does catch is the next edit to one
 * mode that was meant to apply to both.
 *
 *   node tests/js/profileform.js [path/to/profiles.js]
 */
'use strict';

const fs = require('fs');
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

const src = L.readClientBundle(fs.readFileSync(L.profilesPath(), 'utf8'));
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function build() {
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0', languages: ['en'] },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
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
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    return sandbox.__PROFILES;
}

const P = build();

const DEVICES = [
    { deviceId: 'aaa', deviceName: 'Living room TV', client: 'Tizen', lastSeen: '2026-08-01T10:00:00Z' },
    { DeviceId: 'bbb', DeviceName: 'Phone', Client: 'Android', LastSeen: '2026-08-20T10:00:00Z' },
    { deviceId: 'ccc', deviceName: 'Never seen', client: 'Web', lastSeen: '0001-01-01T00:00:00Z' },
];
const PROFILE = {
    allowedDeviceIds: ['aaa'],
    blockedTags: ['horror'],
    allowedTags: ['kids'],
};

console.log();
console.log('── the renderers exist and are shared ─────────────────────────');

for (const fn of ['renderDeviceDropdown', 'renderRatingSelect', 'renderTagSection']) {
    ok(fn + ' exists', typeof P[fn] === 'function');
}
if (typeof P.renderDeviceDropdown !== 'function') {
    console.log();
    console.log('  This build predates the form merge. The renderers are absent, so the');
    console.log('  assertions below are unrunnable — a missing method, not a bisect.');
    console.log();
    console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

console.log();
console.log('── devices: what differs between the modes, and what must not ──');

const dCreate = P.renderDeviceDropdown('create', DEVICES, null);
const dEdit = P.renderDeviceDropdown('edit', DEVICES, PROFILE);

// Both forms can be in the document at once, so create prefixes its ids. Duplicate
// ids would make getElementById a coin toss.
ok('create prefixes its element ids', dCreate.indexOf('id="create-devices-dropdown-trigger"') !== -1);
ok('edit does not', dEdit.indexOf('id="devices-dropdown-trigger"') !== -1);
ok('and the two never collide',
    dCreate.indexOf('id="devices-dropdown-trigger"') === -1);

ok('create uses its own checkbox class', dCreate.indexOf('class="create-device-checkbox"') !== -1);
ok('edit uses the unprefixed one', dEdit.indexOf('class="device-checkbox"') !== -1);

// Only edit has an existing profile to read selections from.
ok('edit ticks the device the profile already allows', /value="aaa"[^>]*checked/.test(dEdit));
ok('and leaves the others alone', !/value="bbb"[^>]*checked/.test(dEdit));
ok('create ticks nothing', dCreate.indexOf('checked') === -1);

// A device cannot be stale for a profile that does not exist yet.
ok('edit offers Forget', dEdit.indexOf('device-delete-btn') !== -1);
ok('create does not', dCreate.indexOf('device-delete-btn') === -1);

// Both spellings, because the list can arrive as the plugin's camelCase JSON or
// straight off a Jellyfin DTO.
ok('a PascalCase device is rendered too', dEdit.indexOf('Phone') !== -1);
ok('and so is its client', dEdit.indexOf('Android') !== -1);

// Year 1 is an unset DateTime. Rendering "01/01/0001" reads as a bug.
ok('a never-seen device does not show year 1',
    dEdit.indexOf('0001') === -1 && dCreate.indexOf('0001') === -1);

// The parts that are supposed to be identical.
for (const shared of ['devices-dropdown-container', 'devices-dropdown-list', 'form-hint']) {
    ok('both modes render ' + shared,
        dCreate.indexOf(shared) !== -1 && dEdit.indexOf(shared) !== -1);
}

const empty = P.renderDeviceDropdown('create', [], null);
ok('an empty device list still renders the container', empty.indexOf('devices-dropdown-list') !== -1);
ok('and says so rather than rendering nothing', empty.length > 200);

console.log();
console.log('── rating: five options, and the right one preselected ────────');

const rCreate = P.renderRatingSelect('create', null);
const rEdit = P.renderRatingSelect('edit', 14);

// Counted, because an option added to one copy and not the other is exactly the drift
// this merge removes — and there is only one copy now to count.
ok('five options in create', (rCreate.match(/<option/g) || []).length === 5);
ok('five options in edit', (rEdit.match(/<option/g) || []).length === 5);
ok('create preselects "no restrictions"', /<option value="" selected/.test(rCreate));
ok('edit preselects the profile rating', /<option value="14" selected/.test(rEdit));
ok('and only that one', (rEdit.match(/selected/g) || []).length === 1);
ok('ids are prefixed per mode',
    rCreate.indexOf('id="create-rating-select"') !== -1
    && rEdit.indexOf('id="edit-rating-select"') !== -1);
// null and undefined both mean "unset"; a profile that has never had a rating set
// arrives as undefined, and treating that as "no option selected" would silently
// clear the rating on the next save.
ok('an undefined rating selects "no restrictions" too',
    /<option value="" selected/.test(P.renderRatingSelect('edit', undefined)));

console.log();
console.log('── tags: prefixes, and the existing values ────────────────────');

const tCreate = P.renderTagSection('create', ['horror', 'kids'], null);
const tEdit = P.renderTagSection('edit', ['horror', 'kids'], PROFILE);

ok('create starts with no blocked tags', tCreate.indexOf('data-tag="horror"') === -1);
ok('edit shows the profile blocked tag', tEdit.indexOf('data-tag="horror"') !== -1);
ok('and the allowed one', tEdit.indexOf('data-tag="kids"') !== -1);
ok('both editors share one suggestions list per mode',
    (tCreate.match(/create-tag-suggestions/g) || []).length >= 3);
ok('and the modes do not share ids',
    tCreate.indexOf('edit-tag-suggestions') === -1);
// An allow-list is subtractive: naming one tag hides everything without it. That
// warning existing in only one of two copies is the drift this merge removes.
ok('the allowed-tags warning is present in both',
    tCreate.indexOf('form-hint-warn') !== -1 && tEdit.indexOf('form-hint-warn') !== -1);

console.log();
console.log('── the duplication is actually gone ───────────────────────────');

const raw = fs.readFileSync(L.profilesPath(), 'utf8');
function countOutsideRenderers(needle) {
    return raw.split(needle).length - 1;
}
// One definition each, in the renderer, plus the English string table.
ok('the device markup exists once', countOutsideRenderers('devices-dropdown-container') === 1);
ok('the rating options exist once', countOutsideRenderers("'profileForm.ratingPG13'") === 2);
ok('the allowed-tags hint exists once',
    countOutsideRenderers("t('profileForm.allowedTagsHint')") === 1);

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
