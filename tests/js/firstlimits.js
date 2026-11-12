// The first child, added with a viewing limit, on an account with no main PIN yet (P2-712).
//
// The server refuses to switch limits on while the main account has no PIN, so saving them
// right after the create failed, and the rules were lost with an error. The household
// has to end up with the child, the limits and a main PIN, in one pass and with no error.
//
// Runs the real showAddProfileModal against the stub DOM and a server that behaves like
// the real one: /limits is refused until /account has set a PIN.
//
//   node tests/js/firstlimits.js [path/to/profiles.js]

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

const server = { pinSet: false, limits: null };
env.setRespond((url, body) => {
    if (url === 'plugins/profiles/create') return { status: 200, body: { profileUserId: 'new' } };
    if (url === 'plugins/profiles/account') { server.pinSet = true; return { status: 200, body: {} }; }
    if (url === 'plugins/profiles/limits/new' && body) {
        if (!server.pinSet) return { status: 400, body: 'Set a PIN for your account first.' };
        server.limits = body;
        return { status: 200, body: { profileId: 'new', limited: true, rules: body } };
    }
    return { status: 200, body: {} };
});

const alerts = [];
let refreshed = 0;
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
// Dismissed at once: what matters is that it was shown and that the list follows it.
PP.showAlert = (title, message, onClose) => { alerts.push(title + ': ' + message); if (onClose) onClose(); };
PP.fetchAndRenderProfiles = () => { refreshed++; };
PP.masterPin = '';
PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
PP.fetchSharedFormData = () => Promise.resolve({
    libraries: [], devices: [], libraryTags: [], avatarLibrary: { allowCustomUploads: true, avatars: [] }, ratings: null
});
// The stub gives every id a plain node; the editor needs a slot that holds markup.
let slotNode = null;
const realInit = PP.initViewingEditor;
PP.initViewingEditor = function (slot, profile, creating) {
    slotNode = D.makeContent();
    return realInit.call(this, slotNode, profile, creating);
};
const noPinAccount =() => [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: false }];

async function addKid(limit) {
    PP.masterPin = '';
    PP.currentProfiles = noPinAccount();
    alerts.length = 0; refreshed = 0; server.pinSet = false; server.limits = null;
    PP.showAddProfileModal();
    await settle();
    content.querySelector('#create-name-input').value = 'Kid';
    if (limit) {
        const daily = slotNode.querySelector('#viewing-daily');
        daily.value = '60';
        daily.fire('change');
    }
    posts.length = 0;
    content.querySelector('#create-submit-btn').click();
    await settle();
}

(async () => {
    console.log('\n── First child with a limit, no main PIN yet ───────────────────');
    await addKid(true);
    ok('the profile is created', posts.some(p => p.url === 'plugins/profiles/create'));
    const pinField = content.querySelector('#jpf-main-pin-new');
    ok('the main PIN is asked for, before anything fails', !!pinField);
    ok('no error was shown while asking', alerts.length === 0, alerts.join(' | '));
    if (pinField) {
        pinField.value = '2468';
        content.querySelector('#main-pin-save').click();
        await settle();
    }
    const calls = posts.map(p => p.url);
    const setAt = calls.indexOf('plugins/profiles/account');
    const limitsAt = calls.indexOf('plugins/profiles/limits/new');
    ok('the PIN is set', setAt !== -1, calls.join(','));
    ok('then the limits are saved, after it', limitsAt !== -1 && limitsAt > setAt, calls.join(','));
    ok('the limits reached the server', !!server.limits && server.limits.dailyMinutes === 60);
    ok('and carried the new main PIN', !!server.limits && server.limits.masterPin === '2468');
    ok('no error at any point', alerts.length === 0, alerts.join(' | '));
    ok('the list is refreshed at the end', refreshed >= 1);

    console.log('\n── Not now ─────────────────────────────────────────────────────');
    await addKid(true);
    const skip = content.querySelector('#main-pin-skip');
    ok('there is a Not now', !!skip);
    if (skip) { skip.click(); await settle(); }
    ok('no limits call is made without a PIN', posts.every(p => p.url !== 'plugins/profiles/limits/new'));
    ok('the household is told the limit is off', alerts.length === 1, alerts.join(' | '));
    ok('and the list is refreshed', refreshed >= 1);

    console.log('\n── No limit set ────────────────────────────────────────────────');
    await addKid(false);
    ok('the PIN is still offered', !!content.querySelector('#jpf-main-pin-new'));
    ok('without any limits call', posts.every(p => p.url !== 'plugins/profiles/limits/new'));
    ok('and no alert', alerts.length === 0, alerts.join(' | '));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
