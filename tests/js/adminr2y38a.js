/*
 * r2-38-01, r2-38-02: after Save, Copy report showed the old value of every setting that
 * noteSavedSettings did not update, and read the emergency code line from PanicCodeHash,
 * which the configuration fetched at load carries stale.
 *
 * Runs the real noteSavedSettings and buildReport. Red against the dashboard before the fix:
 *
 *     node tests/js/adminr2y38a.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const script = L.dashboardScript(L.readDashboard());
const source = ['field', 'noteSavedSettings', 'buildReport'].map(n => L.extractFunction(script, n)).join('\n');

(async function main() {
    const ApiClient = { getUrl: u => u, getJSON: () => Promise.resolve({ Version: '10.11.0' }) };
    const navigator = { userAgent: 'test' };
    const make = new Function('ApiClient', 'navigator', 'renderGettingStarted', source + '; return { noteSavedSettings: noteSavedSettings, buildReport: buildReport };');
    const api = make(ApiClient, navigator, () => {});

    console.log('\n-- Copy report after Save (r2-38-01) --');
    const page = {
        dataset: {}, _mappings: {}, _status: { maintenance: [] },
        _config: {
            MaxProfilesPerUser: 5, PinGuessesPerDay: 20, SkipQuickConnectOnKnownDevices: false,
            DefaultAskOnStartup: true, DefaultSwitcherLocation: 'button', DefaultTheme: 'bonfire', AllowProfileThemes: true,
            PanicCodeHash: ''
        },
        _panicConfigured: true
    };
    const sent = {
        maxProfilesPerUser: 3, pinGuessesPerDay: 7, requireMainPin: true, enablePinResetByEmail: false, enableStarterAvatars: true,
        richThemeMotion: false, enableClientPinLogin: false, enableClientProfileList: false, clientPinLoginFromAnywhere: false,
        proxyClients: [], defaultTimeZone: '', trustedAddressEntries: [], onboardingEnabled: false,
        skipQuickConnectOnKnownDevices: true, defaultAskOnStartup: false, defaultSwitcherLocation: 'menu',
        defaultTheme: 'ember', allowProfileThemes: false
    };
    api.noteSavedSettings(page, sent);
    const report = await api.buildReport(page);
    const has = s => report.indexOf(s) !== -1;
    ok('profiles per account', has('Profiles per account: 3'), report);
    ok('PIN tries a day', has('PIN tries a day: 7'));
    ok('skip Quick Connect', has('skip Quick Connect: on'));
    ok('ask on startup and switch from', has('Ask on startup: off, switch from: menu'));
    ok('theme and profiles choose', has('Theme: ember, profiles choose: off'));

    console.log('\n-- The emergency code line (r2-38-02) --');
    ok('set, when the status route says so, whatever the loaded hash was', has('Emergency code: set'));
    page._panicConfigured = false;
    page._config.PanicCodeHash = 'stale';
    ok('not set, when the status route says so, whatever the loaded hash was', (await api.buildReport(page)).indexOf('Emergency code: not set') !== -1);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
