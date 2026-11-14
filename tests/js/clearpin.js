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
        const input = c.querySelector('#jpf-edit-pin-input');
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
        c.querySelector('#jpf-edit-pin-input').value = '12';
    });
    ok('a 2-digit PIN after a clear is refused, nothing sent', !sent,
       sent ? JSON.stringify(sent.body.pin) : '');

    // Untouched: the PIN is left as it is.
    env = setup();
    sent = await save(env, async () => {});
    ok('an untouched PIN field sends null (leave it alone)', !!sent && sent.body.pin === null,
       sent ? JSON.stringify(sent.body.pin) : 'no post');

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
