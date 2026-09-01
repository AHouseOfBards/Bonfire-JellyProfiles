/*
 * The settings page must save only the settings it owns.
 *
 * It used to GET the entire PluginConfiguration through Jellyfin's generic plugin API,
 * change six fields on the copy in the browser, and PUT the whole document back. Every
 * profile mapping, known device, Bonfire group, avatar library entry and the emergency
 * disable hash rode along. Anything that changed between the GET and the PUT — a profile
 * someone created while the administrator had the page open — was overwritten with the
 * state from before it existed. No error, nothing in the log, and the window is as wide
 * as the page has been open.
 *
 * This harness runs the real saveConfiguration out of the dashboard against a stubbed
 * ApiClient and looks at what it actually sends. Reading the function and checking it
 * mentions the right URL would pass just as happily against a version that also still
 * called getPluginConfiguration.
 *
 * Point it at an older dashboard to watch it fail (argv[3], because argv[2] is the
 * client script every other harness takes):
 *
 *     node tests/js/adminsave.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const script = L.dashboardScript(L.readDashboard());
const source = L.extractFunction(script, 'saveConfiguration');

// ── the stub server ──────────────────────────────────────────────────────────
// Holds a configuration that already contains a profile the administrator has never
// seen — created after the settings page was opened. If saving can lose it, it will.
const serverConfig = {
    MaxProfilesPerUser: 5,
    RequireMasterPinForCreation: true,
    DisallowCustomAvatarUploads: false,
    DefaultAskOnStartup: true,
    DefaultSwitcherLocation: 'button',
    IndexInjectionMode: 'middleware',
    Mappings: [{ ProfileUserId: 'created-while-the-page-was-open' }],
    KnownDevices: [{ DeviceId: 'living-room-tv' }],
    BonfireGroups: [{ OwnerUserId: 'someone' }],
    AvatarLibrary: [{ Id: 'avatar-1' }],
    PanicCodeHash: 'a-hash-nobody-can-retype'
};

async function run(fieldValues) {
    const calls = { getPluginConfiguration: 0, updatePluginConfiguration: 0, ajax: [] };

    const fields = Object.assign({
        maxProfiles: '5',
        requireMasterPin: true,
        disallowCustomAvatars: false,
        defaultAskOnStartup: true,
        defaultSwitcherLocation: 'button',
        indexInjectionMode: 'middleware'
    }, fieldValues || {});

    // One fake element per id. `value` and `checked` both present, so whichever the
    // function reads works, and writes back (the clamp does) are visible afterwards.
    const elements = {};
    Object.keys(fields).forEach(function (id) {
        const v = fields[id];
        elements[id] = typeof v === 'boolean'
            ? { checked: v, value: String(v) }
            : { value: v, checked: false };
    });

    const page = {
        querySelector: function (sel) {
            const el = elements[sel.replace('#', '')];
            if (!el) throw new Error('the page has no ' + sel);
            return el;
        }
    };

    const ApiClient = {
        getUrl: function (u) { return '/' + u; },
        ajax: function (opts) {
            calls.ajax.push(opts);
            return Promise.resolve({});
        },
        getPluginConfiguration: function () {
            calls.getPluginConfiguration++;
            return Promise.resolve(JSON.parse(JSON.stringify(serverConfig)));
        },
        updatePluginConfiguration: function (id, cfg) {
            calls.updatePluginConfiguration++;
            calls.pushedConfig = cfg;
            return Promise.resolve({});
        }
    };

    const Dashboard = {
        showLoadingMsg: function () {},
        hideLoadingMsg: function () {},
        alert: function (o) { calls.alert = o; },
        processPluginConfigurationUpdateResult: function () {}
    };

    // eslint-disable-next-line no-new-func
    const factory = new Function('ApiClient', 'Dashboard', 'pluginId',
        source + '; return saveConfiguration;');
    factory(ApiClient, Dashboard, 'b1462fca-774b-4b13-8d02-e2d4f2bc18b9')(page);

    // The old version did its work inside `getPluginConfiguration().then(...)`, so its
    // PUT lands a microtask later than the call that starts it. Drain the queue before
    // looking, or the round-trip this harness exists to catch goes uncounted.
    for (let i = 0; i < 5; i++) await new Promise(function (r) { setImmediate(r); });

    return { calls: calls, elements: elements };
}

/** The JSON body of the one request a run made, or {} if it made none. */
function sent(result) {
    const req = result.calls.ajax[0];
    if (!req || typeof req.data !== 'string') return {};
    try { return JSON.parse(req.data); } catch (e) { return {}; }
}

// Wrapped rather than using top-level await: run.sh discovers tests/js/*.js and runs
// them as CommonJS, where Node will not accept it.
main();
async function main() {

console.log('\n── The whole configuration is no longer round-tripped ─────────');

const base = await run();

ok('saveConfiguration never reads the whole plugin configuration '
   + '(getPluginConfiguration called ' + base.calls.getPluginConfiguration + ' times)',
   base.calls.getPluginConfiguration === 0);

ok('and never writes the whole plugin configuration back '
   + '(updatePluginConfiguration called ' + base.calls.updatePluginConfiguration + ' times)',
   base.calls.updatePluginConfiguration === 0);

ok('it makes exactly one request (' + base.calls.ajax.length + ')', base.calls.ajax.length === 1);

const req = base.calls.ajax[0] || {};
ok('a POST', req.type === 'POST');
ok('to admin/settings (' + (req.url || 'nothing') + ')',
   typeof req.url === 'string' && req.url.indexOf('admin/settings') !== -1);
ok('as JSON', req.contentType === 'application/json');

let body = null;
try { body = JSON.parse(req.data); } catch (e) { /* reported below */ }
ok('with a parseable body', body !== null && typeof body === 'object');

console.log('\n── It sends the six settings and nothing else ─────────────────');

const expected = [
    'maxProfilesPerUser',
    'requireMasterPinForCreation',
    'disallowCustomAvatarUploads',
    'defaultAskOnStartup',
    'defaultSwitcherLocation',
    'indexInjectionMode'
];

if (body) {
    // Enumerated both ways rather than compared as a count. A count says something is
    // wrong without saying which field, and a missing key and an extra key cancel out.
    expected.forEach(function (k) {
        ok('sends ' + k, Object.prototype.hasOwnProperty.call(body, k));
    });
    Object.keys(body).forEach(function (k) {
        ok(k + ' is one of the six this page owns', expected.indexOf(k) !== -1);
    });

    // The named collections are the ones that were being silently reverted. Listed
    // explicitly, because this is the actual bug and it deserves its own line.
    ['Mappings', 'KnownDevices', 'BonfireGroups', 'AvatarLibrary', 'PanicCodeHash']
        .forEach(function (k) {
            ok('does not carry ' + k + ' — nothing there can be lost by saving settings',
               !Object.prototype.hasOwnProperty.call(body, k));
        });
}

console.log('\n── The profile limit is clamped, not passed through ───────────');

// parseInt('-4', 10) is -4, which is truthy, so the old `|| 5` never caught a negative.
const negative = await run({ maxProfiles: '-4' });
const negBody = sent(negative);
ok('a negative limit is clamped to 1, not sent as-is (sent ' + negBody.maxProfilesPerUser + ')',
   negBody.maxProfilesPerUser === 1);

const huge = await run({ maxProfiles: '2000000000' });
const hugeBody = sent(huge);
ok('an absurd limit is clamped to 20 (sent ' + hugeBody.maxProfilesPerUser + ')',
   hugeBody.maxProfilesPerUser === 20);

const empty = await run({ maxProfiles: '' });
const emptyBody = sent(empty);
ok('an empty box falls back to 5 (sent ' + emptyBody.maxProfilesPerUser + ')',
   emptyBody.maxProfilesPerUser === 5);

ok('the clamped value is written back to the box, so it shows what was saved '
   + '(box reads ' + negative.elements.maxProfiles.value + ')',
   String(negative.elements.maxProfiles.value) === '1');

console.log('\n── The other five are read from the page ──────────────────────');

const flipped = await run({
    requireMasterPin: false,
    disallowCustomAvatars: true,
    defaultAskOnStartup: false,
    defaultSwitcherLocation: 'menu',
    indexInjectionMode: 'both'
});
const flippedBody = sent(flipped);
ok('requireMasterPinForCreation follows the checkbox', flippedBody.requireMasterPinForCreation === false);
ok('disallowCustomAvatarUploads follows the checkbox', flippedBody.disallowCustomAvatarUploads === true);
ok('defaultAskOnStartup follows the checkbox', flippedBody.defaultAskOnStartup === false);
ok('defaultSwitcherLocation follows the select', flippedBody.defaultSwitcherLocation === 'menu');
ok('indexInjectionMode follows the select', flippedBody.indexInjectionMode === 'both');

// Anything unrecognised in the location select becomes 'button' rather than being sent
// on: the server rejects what it does not know, and this is a two-option select.
const odd = await run({ defaultSwitcherLocation: 'somewhere-else' });
ok('an unknown switcher location is sent as button',
   sent(odd).defaultSwitcherLocation === 'button');

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');

}
