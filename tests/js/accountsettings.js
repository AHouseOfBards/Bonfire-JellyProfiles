// Settings in five sections, and Settings → Your account (1.7.2).
//
// Runs the real showSettingsMenu, showAccountSettings and bindDeviceTools against a stub
// DOM and a recording fetch, then reads back what was drawn and what was sent. Red
// against 1.7.1, which had three Settings entries, no account page, and the device tools
// inside each profile's form.
//
//   node tests/js/accountsettings.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const { PP, content, posts, settle } = env;
let respond = () => ({ status: 200, body: {} });
env.setRespond((url, body) => respond(url, body));

// The calls this page makes into the rest of the plugin, recorded rather than run.
const calls = { settings: 0, account: 0, grid: [], alerts: [], confirms: [], selects: [] };
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.fetchAndRenderProfiles = function () { calls.grid.push({ manage: this.isManageMode }); };
PP.showAlert = (title, body) => calls.alerts.push(body);
PP.showConfirmDialog = (title, msg, yes) => { calls.confirms.push(msg); yes(); };
PP.showPromptDialog = (title, label, initial, cb) => cb('Living room TV');
PP.showSelectDialog = (title, msg, options, cb) => { calls.selects.push(options); cb(options[0].id); };
PP.handleSessionExpired = () => {};

const realSettings = PP.showSettingsMenu;
const realAccount = PP.showAccountSettings;
const DEVICES = [
    { deviceId: 'tv-1', deviceName: 'TV', client: 'Jellyfin Web', lastSeen: '2026-10-01T00:00:00Z' },
    { deviceId: 'tv-2', deviceName: 'TV (domain)', client: 'Jellyfin Web', lastSeen: '2026-10-02T00:00:00Z' }
];

function freshAccount() {
    PP.masterPin = '2468';
    PP.currentProfiles = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true,
          askPinToOpen: true, bypassPinOnLocalNetwork: false, seenOnLocalNetwork: true, lockoutMinutes: 5, isAdministrator: false },
        { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false }
    ];
    PP.fetchSharedFormData = () => Promise.resolve({ devices: DEVICES });
    PP._sharedForm = null;
}

(async () => {
    // ── 1. Five sections ────────────────────────────────────────────────────────────
    console.log('\n── Settings: five sections, in order ───────────────────────────');

    PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
    PP.showSettingsMenu = realSettings;
    PP.showSettingsMenu();
    const order = ['settings-profiles', 'settings-account', 'settings-switcher-style',
                   'settings-your-bonfire', 'settings-theme'];
    const at = order.map(id => content.innerHTML.indexOf('id="' + id + '"'));
    ok('Profiles, Your account, Switcher, Sharing, Look', at.every(i => i !== -1)
        && at.every((v, i) => i === 0 || v > at[i - 1]), JSON.stringify(at));
    ok('their titles are the five names',
        ['settings.profilesTitle', 'settings.accountTitle', 'settings.switcherTitle',
         'settings.sharingTitle', 'settings.lookTitle'].every(k => new RegExp("t\\('" + k.replace('.', '\\.') + "'\\)").test(SRC)));

    PP.isManageMode = false;
    const profilesEntry = content.querySelector('#settings-profiles');
    if (profilesEntry) profilesEntry.click();
    ok('Profiles opens the manage grid, with the PIN already typed',
        calls.grid.length === 1 && calls.grid[0].manage === true, JSON.stringify(calls.grid));

    // Stubbed before the menu is drawn: the menu binds the function it is given.
    let opened = false;
    PP.showAccountSettings = () => { opened = true; };
    PP.showSettingsMenu();
    const accountEntry = content.querySelector('#settings-account');
    if (accountEntry) accountEntry.click();
    ok('Your account opens the account page', opened);
    PP.showAccountSettings = realAccount;

    // ── 2. The account page ──────────────────────────────────────────────────────────
    console.log('\n── Your account: what it shows ─────────────────────────────────');

    if (typeof realAccount !== 'function') {
        ok('this build has an account page', false);
    } else {
        PP.showSettingsMenu = () => { calls.settings++; };
        freshAccount();
        PP.showAccountSettings();
        await settle();
        const html = content.innerHTML;
        ok('a PIN field, with Save and Clear for a PIN that is set',
            /id="account-pin-input"/.test(html) && /id="account-pin-save"/.test(html) && /id="account-pin-clear"/.test(html));
        ok('ask-for-the-PIN, ticked as stored', content.querySelector('#account-ask-pin').checked === true);
        ok('home network, unticked as stored', content.querySelector('#account-lan-bypass').checked === false);
        ok('auto-lock, on the stored five minutes', content.querySelector('#account-lockout-select').value === '5');
        // Changed with P2-603: the account page shows a count and a button, and the list
        // with its tools is the Manage devices screen. These two used to read the list off
        // the account page itself.
        ok('a device count and a Manage devices button, not the list',
            /id="account-manage-devices"/.test(html) && /id="account-device-count"/.test(html)
            && content.querySelectorAll('.device-dropdown-item').length === 0);
        PP.showAccountDevices();
        await settle();
        ok('every device, each with rename, merge and forget',
            content.querySelectorAll('.device-dropdown-item').length === 2
            && content.querySelectorAll('.device-rename-btn').length === 2
            && content.querySelectorAll('.device-merge-btn').length === 2
            && content.querySelectorAll('.device-delete-btn').length === 2);
        ok('and Tidy up duplicates', /id="devices-tidy-btn"/.test(content.innerHTML));
        PP.showAccountSettings();
        await settle();
        ok('a remote\'s Back key leaves it',
            /#account-back-btn/.test((/_dismissSurface: function[\s\S]*?\n        \},/.exec(SRC) || [''])[0])
            && /id="account-back-btn"/.test(html));

        // ── 3. Saving ─────────────────────────────────────────────────────────────
        console.log('\n── Your account: each control saves alone ──────────────────────');

        const accountPosts = () => posts.filter(p => p.url === 'plugins/profiles/account');
        posts.length = 0;
        respond = (url, body) => ({ status: 200, body: { hasPin: true, askPinToOpen: body.askPinToOpen } });
        const ask = content.querySelector('#account-ask-pin');
        ask.checked = false;
        ask.fire('change');
        await settle();
        let sent = accountPosts();
        ok('unticking ask-for-the-PIN posts only that, with the main PIN',
            sent.length === 1 && JSON.stringify(sent[0].body) === JSON.stringify({ masterPin: '2468', askPinToOpen: false }),
            JSON.stringify(sent.map(p => p.body)));
        ok('and the page remembers it', PP.currentProfiles[0].askPinToOpen === false);

        posts.length = 0;
        respond = () => ({ status: 400, body: 'Invalid Master PIN.', headers: { 'X-Bonfire-Error': 'pin_wrong' } });
        const lan = content.querySelector('#account-lan-bypass');
        lan.checked = true;
        lan.fire('change');
        await settle();
        ok('a refusal puts the box back', lan.checked === false);
        // Replaced behaviour: a refusal used to be shown in the server's English words; it is said by its X-Bonfire-Error code now.
        ok('and says why, by its code', content.querySelector('#account-status').textContent === "That PIN isn't right. Try again.",
            content.querySelector('#account-status').textContent);

        posts.length = 0;
        respond = () => ({ status: 200, body: { lockoutMinutes: 10 } });
        const lock = content.querySelector('#account-lockout-select');
        lock.value = '10';
        lock.fire('change');
        await settle();
        sent = accountPosts();
        ok('auto-lock posts the minutes as a number', sent.length === 1 && sent[0].body.lockoutMinutes === 10,
            JSON.stringify(sent.map(p => p.body)));

        posts.length = 0;
        const pinIn = content.querySelector('#account-pin-input');
        pinIn.value = '12';
        content.querySelector('#account-pin-save').click();
        await settle();
        ok('a PIN that is not 4-8 digits is not sent', accountPosts().length === 0);

        respond = () => ({ status: 200, body: { hasPin: true } });
        pinIn.value = '1357';
        content.querySelector('#account-pin-save').click();
        await settle();
        sent = accountPosts();
        ok('a new PIN is sent with the current one',
            sent.length === 1 && sent[0].body.pin === '1357' && sent[0].body.masterPin === '2468',
            JSON.stringify(sent.map(p => p.body)));
        ok('and is the one sent from now on', PP.masterPin === '1357');
        ok('and the page says it, on the status line now on screen (R-1224)', content.querySelector('#account-status').textContent === 'PIN saved.',
            content.querySelector('#account-status').textContent);

        posts.length = 0;
        respond = () => ({ status: 200, body: { hasPin: false } });
        content.querySelector('#account-pin-clear').click();
        await settle();
        sent = accountPosts();
        ok('Clear sends an empty PIN', sent.length === 1 && sent[0].body.pin === '' && sent[0].body.masterPin === '1357');
        ok('and nothing is sent as the PIN after it', PP.masterPin === null);
        ok('and says it was removed, on the status line now on screen (R-1224)', content.querySelector('#account-status').textContent === 'PIN removed.',
            content.querySelector('#account-status').textContent);
        ok('the page redraws with no Clear button', !/id="account-pin-clear"/.test(content.innerHTML));

        // ── 4. Devices ────────────────────────────────────────────────────────────
        console.log('\n── Your account: the device tools ──────────────────────────────');

        freshAccount();
        PP.showAccountDevices();
        await settle();
        let redraws = 0;
        PP.showAccountDevices = () => { redraws++; };
        respond = () => ({ status: 200, body: {} });
        const dev = (path) => posts.filter(p => p.url === 'plugins/profiles/devices/' + path);

        posts.length = 0;
        content.querySelectorAll('.device-delete-btn')[0].click();
        await settle();
        ok('Forget asks, then posts the device', calls.confirms.length >= 1
            && dev('delete').length === 1 && dev('delete')[0].body.deviceId === 'tv-1');
        ok('and redraws the page from the server', redraws === 1);

        posts.length = 0;
        content.querySelectorAll('.device-rename-btn')[1].click();
        await settle();
        ok('Rename posts the new name', dev('rename').length === 1
            && JSON.stringify(dev('rename')[0].body) === JSON.stringify({ deviceId: 'tv-2', deviceName: 'Living room TV' }));

        posts.length = 0;
        content.querySelectorAll('.device-merge-btn')[1].click();
        await settle();
        const offered = calls.selects[calls.selects.length - 1] || [];
        ok('Merge offers every other device, not itself',
            offered.length === 1 && offered[0].id === 'tv-1' && offered[0].name === 'TV', JSON.stringify(offered));
        ok('and posts the pair', dev('merge').length === 1
            && JSON.stringify(dev('merge')[0].body) === JSON.stringify({ fromDeviceId: 'tv-2', intoDeviceId: 'tv-1' }));

        posts.length = 0;
        respond = (url) => url === 'plugins/profiles/devices/merge-duplicates'
            ? { status: 200, body: { groups: [{ keep: 'tv-1', merge: ['tv-2'] }] } }
            : { status: 200, body: {} };
        content.querySelector('#devices-tidy-btn').click();
        await settle();
        ok('Tidy looks first, then merges once asked',
            dev('merge-duplicates').length === 1 && dev('merge-duplicates?apply=true').length === 1
            && dev('merge-duplicates?apply=true')[0].init.body === undefined);
        ok('and every device call carries the main PIN in its header',
            posts.filter(p => /devices\//.test(p.url)).every(p => p.init.headers && p.init.headers['X-Bonfire-Master-Pin'] === '2468'));

        PP.showAccountSettings = realAccount;
        // The device list is its own screen now, so go back to the account page first.
        content.querySelector('#account-back-btn').click();
        await settle();
        const before = calls.settings;
        content.querySelector('#account-back-btn').click();
        ok('Done goes back to Settings', calls.settings === before + 1);
    }

    // Every Settings page goes back to Settings. Sharing went to the profile grid while it
    // was still loading, and to Settings once loaded.
    {
        PP.showSettingsMenu = () => { calls.settings++; };
        PP.loadBonfireStatus = () => {};
        PP.showBonfireModal();
        const before = calls.settings;
        const back = content.querySelector('#bonfire-back-btn');
        if (back) back.click();
        ok('Sharing\'s Back, while loading, goes to Settings', calls.settings === before + 1 && calls.grid.length === 1,
            'settings ' + (calls.settings - before) + ', grid ' + calls.grid.length);
    }

    // R-1217: Sharing's focus timer (250 ms) ran even when the user had already left for
    // another screen, and took focus on it. It must honour its navigation ticket.
    {
        PP.showSettingsMenu = () => { calls.settings++; };
        PP.loadBonfireStatus = () => {};
        const timers = [];
        const realTimeout = env.sandbox.setTimeout;
        env.sandbox.setTimeout = (fn) => { timers.push(fn); return 0; };
        PP.showBonfireModal();
        env.sandbox.setTimeout = realTimeout;
        const asked = [];
        const realQuery = content.querySelector;
        content.querySelector = function (sel) { asked.push(sel); return realQuery.call(content, sel); };
        PP.navIsCurrent = () => false;
        timers.forEach(fn => fn());
        content.querySelector = realQuery;
        PP.navIsCurrent = () => true;
        ok('Sharing\'s focus timer does nothing once the screen has moved on (R-1217)',
            timers.length > 0 && !asked.some(s => /^input, button/.test(s)), asked.join(' | '));
    }

    // ── 5. What left the profile form ────────────────────────────────────────────────
    console.log('\n── The profile form only ticks devices ────────────────────────');

    const edit = (/showEditProfileModal: function[\s\S]*?\n        showBonfireModal: function/.exec(SRC) || [''])[0];
    ok('no device tools are wired in the profile form',
        !!edit && !/device-delete-btn|device-rename-btn|device-merge-btn|devices-tidy-btn/.test(edit));
    ok('the main account\'s form no longer carries its PIN settings',
        !!edit && /mainSecurityMoved/.test(edit) && !/edit-ask-pin-checkbox/.test(SRC));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
