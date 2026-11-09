// "Clear PIN" and then a new PIN in the same box (R-1214).
//
// Clear PIN empties the edit form's PIN field and leaves it on screen. Save checked the
// clear before the field, so a parent who cleared and then typed a new PIN sent pin ""
// — an unprotected profile — and was told the save had worked.
//
// Runs the real showEditProfileModal against a stub DOM, clicks Clear PIN, types, saves,
// and reads what was sent.
//
//   node tests/js/clearpin.js [path/to/profiles.js]

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

const KID = { profileUserId: 'kid', profileName: 'Kid', isMaster: false, hasPin: true,
    lockoutMinutes: 20, bypassPinOnLocalNetwork: false, allowedDeviceIds: [], enabledFolders: null,
    blockedTags: [], allowedTags: [], avatarColor: '#1F77B4', theme: null };

function setup() {
    const env = D.loadPlugin(SRC);
    const { PP } = env;
    env.setRespond((url) => /^Users\//.test(url)
        ? { status: 200, body: { Policy: { MaxParentalRating: 14, EnableAllFolders: true } } }
        : { status: 200, body: {} });
    PP.beginNavigation = () => 1;
    PP.navIsCurrent = () => true;
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    PP.fetchAndRenderProfiles = () => {};
    PP.showAlert = () => {};
    PP.masterPin = '2468';
    PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
    PP.currentProfiles = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true },
        KID
    ];
    PP.fetchSharedFormData = () => Promise.resolve({
        libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
        devices: [], libraryTags: [],
        avatarLibrary: { allowCustomUploads: true, avatars: [] },
        ratings: null
    });
    return env;
}

async function save(env, steps) {
    env.PP.showEditProfileModal(Object.assign({}, KID));
    await env.settle();
    const c = env.content;
    await steps(c);
    env.posts.length = 0;
    const btn = c.querySelector('#edit-submit-btn');
    if (btn) btn.click();
    await env.settle();
    return env.posts.filter(p => p.url === 'plugins/profiles/update')[0];
}

(async () => {
    console.log('\n── Clear PIN, then type a new one ──────────────────────────────');

    let env = setup();
    let sent = await save(env, async (c) => {
        c.querySelector('#edit-clear-pin-btn').click();
        const input = c.querySelector('#edit-pin-input');
        input.value = '1234';
        input.fire('input');
    });
    ok('Save posts', !!sent);
    ok('with the PIN typed after the clear ("1234")', !!sent && sent.body.pin === '1234',
       sent ? JSON.stringify(sent.body.pin) : 'no post');

    // A clear on its own still clears.
    env = setup();
    sent = await save(env, async (c) => { c.querySelector('#edit-clear-pin-btn').click(); });
    ok('Clear PIN alone still sends pin "" (removes it)', !!sent && sent.body.pin === '',
       sent ? JSON.stringify(sent.body.pin) : 'no post');

    // A short PIN typed after a clear is refused, not quietly turned into "no PIN".
    env = setup();
    sent = await save(env, async (c) => {
        c.querySelector('#edit-clear-pin-btn').click();
        c.querySelector('#edit-pin-input').value = '12';
    });
    ok('a 2-digit PIN after a clear is refused, nothing sent', !sent,
       sent ? JSON.stringify(sent.body.pin) : '');

    // Untouched: the PIN is left as it is.
    env = setup();
    sent = await save(env, async () => {});
    ok('an untouched PIN field sends null (leave it alone)', !!sent && sent.body.pin === null,
       sent ? JSON.stringify(sent.body.pin) : 'no post');

    // Issue #44. The main PIN typed to open Manage Profiles is sent with every change, so
    // changing it from the edit form left the old one in memory: each later save in the
    // same visit was refused as an incorrect main PIN, and each one counted as a wrong try.
    console.log('\n── The main account changing its own PIN ───────────────────────');

    const MAIN = Object.assign({}, KID, { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false });
    const saveMain = async (env, steps) => {
        env.PP.showEditProfileModal(Object.assign({}, MAIN));
        await env.settle();
        await steps(env.content);
        env.content.querySelector('#edit-submit-btn').click();
        await env.settle();
    };

    env = setup();
    await saveMain(env, async (c) => {
        const input = c.querySelector('#edit-pin-input');
        input.value = '9753';
        input.fire('input');
    });
    ok('the new main PIN is what the next save sends', env.PP.masterPin === '9753', JSON.stringify(env.PP.masterPin));

    env = setup();
    await saveMain(env, async (c) => { c.querySelector('#edit-clear-pin-btn').click(); });
    ok('a cleared main PIN is forgotten', env.PP.masterPin === null, JSON.stringify(env.PP.masterPin));

    env = setup();
    await saveMain(env, async () => {});
    ok('a save that leaves the PIN alone keeps it', env.PP.masterPin === '2468', JSON.stringify(env.PP.masterPin));

    env = setup();
    env.setRespond((url) => url === 'plugins/profiles/update' ? { status: 400, body: 'No' }
        : /^Users\//.test(url) ? { status: 200, body: { Policy: { MaxParentalRating: 14, EnableAllFolders: true } } }
        : { status: 200, body: {} });
    await saveMain(env, async (c) => {
        const input = c.querySelector('#edit-pin-input');
        input.value = '9753';
        input.fire('input');
    });
    ok('a refused save keeps the PIN that still works', env.PP.masterPin === '2468', JSON.stringify(env.PP.masterPin));

    // A profile's PIN is not the main PIN.
    env = setup();
    sent = await save(env, async (c) => {
        const input = c.querySelector('#edit-pin-input');
        input.value = '1234';
        input.fire('input');
    });
    ok('changing a profile\'s PIN leaves the main PIN alone', env.PP.masterPin === '2468', JSON.stringify(env.PP.masterPin));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
