/*
 * A settings page that could not read the settings must not be able to overwrite them.
 *
 * loadConfiguration's catch only logged. The form stayed at its defaults, the Save button
 * stayed live, and Save posts every setting on the page, so one failed read followed by
 * one click put the defaults over the real configuration. Nothing told the administrator
 * the read had failed, and nothing in the log said a save had overwritten anything.
 *
 * Runs the real loadConfiguration and saveConfiguration against a stubbed ApiClient and
 * page. Point it at an older dashboard to watch it fail (argv[3], because argv[2] is the
 * client script every other harness takes):
 *
 *     node tests/js/adminload.js "" /path/to/old/profilesDashboard.html
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
const loadSrc = L.extractFunction(script, 'loadConfiguration');
// Every function loadConfiguration calls on the page is a loader or a renderer; they are
// stubbed, because what is under test is what it does with the result of the read.
const stubbed = {};
(loadSrc.match(/\b(?:load|render|update)[A-Za-z]*\(/g) || []).forEach(function (n) {
    const name = n.slice(0, -1);
    if (name !== 'loadConfiguration') stubbed[name] = true;
});

const source = L.dashboardI18n(script)
    + L.extractFunction(script, 'normalizeImageQuality') + '\n'
    + L.extractFunction(script, 'normalizeTheme') + '\n'
    // Loading clears the "unsaved changes" note; it is not what this harness is about.
    + (script.indexOf('function setUnsaved') !== -1 ? L.extractFunction(script, 'setUnsaved') + '\n' : '')
    + (script.indexOf('function showConfigLoadError') !== -1 ? L.extractFunction(script, 'showConfigLoadError') + '\n' : '')
    // Save reads the trusted-address rows (2.0); before they're drawn it sends what was loaded.
    + (script.indexOf('function collectTrusted') !== -1 ? L.extractFunction(script, 'collectTrusted') + '\n' : '')
    + loadSrc + '\n'
    + L.extractFunction(script, 'saveConfiguration');

function element() {
    const classes = {};
    return {
        value: '', checked: false, disabled: false, textContent: '', dataset: {}, style: {},
        classList: {
            add: function (c) { classes[c] = true; },
            remove: function (c) { delete classes[c]; },
            toggle: function (c, on) { if (on === undefined ? !classes[c] : on) classes[c] = true; else delete classes[c]; },
            contains: function (c) { return !!classes[c]; }
        },
        hidden: false,
        setAttribute: function () {}, getAttribute: function () { return null; },
        addEventListener: function () {}
    };
}

function makePage() {
    const els = {};
    return {
        els: els,
        dataset: {},
        querySelector: function (sel) { return els[sel] || (els[sel] = element()); },
        querySelectorAll: function () { return []; }
    };
}

async function drain() { for (let i = 0; i < 6; i++) await new Promise(function (r) { setImmediate(r); }); }

async function scenario(readOutcome) {
    const calls = { ajax: [], alerts: [], loaders: [] };
    const page = makePage();
    const ApiClient = {
        getUrl: function (u) { return '/' + u; },
        getPluginConfiguration: function () {
            return readOutcome.ok ? Promise.resolve(readOutcome.config) : Promise.reject(new Error('offline'));
        },
        ajax: function (o) { calls.ajax.push(o); return Promise.resolve({}); }
    };
    const Dashboard = {
        showLoadingMsg: function () {}, hideLoadingMsg: function () {},
        alert: function (o) { calls.alerts.push(o); }
    };
    const names = Object.keys(stubbed);
    const stubs = names.map(function (n) { return n === 'updateInjectionModeHelp' ? function () {} : function () { calls.loaders.push(n); }; });
    const factory = new Function('ApiClient', 'Dashboard', 'pluginId', 'noteSavedSettings', 'loadAvatarLibrary', 'field',
        names.join(',') + (names.length ? ',' : '') + 'console',
        // eslint-disable-next-line no-useless-concat
        source + '; return { load: loadConfiguration, save: saveConfiguration };');
    const quiet = { error: function () {}, log: console.log };
    const api = factory.apply(null, [ApiClient, Dashboard, 'guid', function () {}, function () {}, function (o, n) { return o[n]; }]
        .concat(stubs).concat([quiet]));
    return { api: api, page: page, calls: calls, ApiClient: ApiClient };
}

(async function main() {
    console.log('\n── A read that failed ──────────────────────────────────────────');
    const failed = await scenario({ ok: false });
    failed.api.load(failed.page);
    await drain();
    failed.api.save(failed.page);
    await drain();
    ok('Save sends nothing after the read failed (' + failed.calls.ajax.length + ' requests)', failed.calls.ajax.length === 0);
    const banner = failed.page.els['#configLoadError'];
    ok('the load-failure banner is shown', !!banner && !banner.classList.contains('jpf-hidden'));

    console.log('\n── Retrying ────────────────────────────────────────────────────');
    failed.ApiClient.getPluginConfiguration = function () { return Promise.resolve({ MaxProfilesPerUser: 7 }); };
    failed.api.load(failed.page);
    await drain();
    ok('a retry that works hides the banner', !!banner && banner.classList.contains('jpf-hidden'));
    failed.api.save(failed.page);
    await drain();
    ok('and Save goes through (' + failed.calls.ajax.length + ' request)', failed.calls.ajax.length === 1);

    console.log('\n── A read that worked ──────────────────────────────────────────');
    const good = await scenario({ ok: true, config: { MaxProfilesPerUser: 3 } });
    good.api.load(good.page);
    await drain();
    good.api.save(good.page);
    await drain();
    ok('Save sends the settings (' + good.calls.ajax.length + ' request)', good.calls.ajax.length === 1);
    ok('the banner stays hidden', !good.page.els['#configLoadError']
        || good.page.els['#configLoadError'].classList.contains('jpf-hidden'));

    console.log('\n── A save made before any read ─────────────────────────────────');
    const never = await scenario({ ok: true, config: {} });
    never.api.save(never.page);
    await drain();
    ok('Save sends nothing before a read has succeeded (' + never.calls.ajax.length + ' requests)', never.calls.ajax.length === 0);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
