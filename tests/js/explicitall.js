// "No rating limit" and "every library", said on purpose (R-1144).
//
// /update now reads a left-out maxParentalRating or enabledFolders as "keep it". The forms
// used to send null for both "No restrictions" and "no library ticked", which the server
// read as "none" and "every library". With null meaning "keep", those two choices would
// silently stop working, so the forms must send explicit values: maxParentalRating ""
// and allLibraries true. The main account's form has neither control and must keep
// sending null, which leaves its own policy alone.
//
// Runs the real showEditProfileModal and showAddProfileModal against a stub DOM and reads
// what was sent.
//
//   node tests/js/explicitall.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();
const LIB = '11111111-1111-1111-1111-111111111111';

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const KID = { profileUserId: 'kid', profileName: 'Kid', isMaster: false, hasPin: true,
    lockoutMinutes: 20, bypassPinOnLocalNetwork: false, allowedDeviceIds: [], enabledFolders: [],
    allLibraries: true, blockedTags: [], allowedTags: [], avatarColor: '#1F77B4', theme: null };
const MAIN = { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true };

function setup(policy) {
    const env = D.loadPlugin(SRC);
    const { PP } = env;
    env.setRespond((url) => /^Users\//.test(url)
        ? { status: 200, body: { Policy: policy } }
        : { status: 200, body: { profileUserId: 'new' } });
    PP.beginNavigation = () => 1;
    PP.navIsCurrent = () => true;
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    PP.fetchAndRenderProfiles = () => {};
    PP.showAlert = () => {};
    PP.masterPin = '2468';
    PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
    PP.currentProfiles = [MAIN, KID];
    PP.fetchSharedFormData = () => Promise.resolve({
        libraries: [{ Id: LIB, Name: 'Movies' }],
        devices: [], libraryTags: [],
        avatarLibrary: { allowCustomUploads: true, avatars: [] },
        ratings: null
    });
    return env;
}

function tick(c, on) {
    c.querySelectorAll('.library-checkbox').forEach(cb => { if (cb.value === LIB) cb.checked = on; });
}

async function edit(profile, policy, steps) {
    const env = setup(policy);
    env.PP.showEditProfileModal(Object.assign({}, profile));
    await env.settle();
    const c = env.content;
    if (steps) await steps(c);
    env.posts.length = 0;
    const btn = c.querySelector('#edit-submit-btn');
    if (btn) btn.click();
    await env.settle();
    return env.posts.filter(p => p.url === 'plugins/profiles/update')[0];
}

async function create(steps) {
    const env = setup({ EnableAllFolders: true });
    env.PP.showAddProfileModal();
    await env.settle();
    const c = env.content;
    const name = c.querySelector('#create-name-input');
    if (name) name.value = 'Kid two';
    if (steps) await steps(c);
    env.posts.length = 0;
    const btn = c.querySelector('#create-submit-btn');
    if (btn) btn.click();
    await env.settle();
    return env.posts.filter(p => p.url === 'plugins/profiles/create')[0];
}

const show = s => (s ? JSON.stringify({ r: s.body.maxParentalRating, f: s.body.enabledFolders, a: s.body.allLibraries }) : 'no post');

(async () => {
    console.log('\n── Edit a profile ──────────────────────────────────────────────');

    // R-1222: a refused Users/{id} read has no Policy; read as {} it ticked every library
    // and saved them as a fixed list. A refusal must show the error and draw no form.
    for (const status of [500, 403]) {
        const env = setup({});
        const alerts = [];
        env.PP.showAlert = (title, body) => alerts.push(body);
        env.setRespond((url) => /^Users\//.test(url)
            ? { status: status, body: {} }
            : { status: 200, body: {} });
        env.PP.showEditProfileModal(Object.assign({}, KID));
        await env.settle();
        ok('a ' + status + ' on the profile\'s own read draws no edit form', !env.content.querySelector('#edit-submit-btn'));
        ok('and says so', alerts.length === 1);
    }
    // R-1215: Add had no .catch, so a failed form-options read left the button doing nothing.
    {
        const env = setup({});
        const alerts = [];
        let grids = 0;
        env.PP.showAlert = (title, body) => alerts.push(body);
        env.PP.fetchAndRenderProfiles = () => { grids++; };
        env.PP.fetchSharedFormData = () => Promise.reject(new Error('x'));
        env.PP.showAddProfileModal();
        await env.settle();
        ok('Add Profile says so when the shared read fails', alerts.length === 1);
        ok('and goes back to the profiles', grids === 1);
    }
    {
        const env = setup({});
        let expired = 0;
        env.PP.handleSessionExpired = () => { expired++; };
        env.PP.showAlert = () => {};
        env.setRespond((url) => /^Users\//.test(url) ? { status: 401, body: {} } : { status: 200, body: {} });
        env.PP.showEditProfileModal(Object.assign({}, KID));
        await env.settle();
        ok('a 401 on it signs out instead of an error', expired === 1);
    }

    let sent = await edit(KID, { MaxParentalRating: null, EnableAllFolders: true }, c => tick(c, false));
    ok('Save posts', !!sent);
    ok('"No restrictions" is sent as "" (null now keeps the old limit)', !!sent && sent.body.maxParentalRating === '', show(sent));
    ok('no library ticked is sent as allLibraries true', !!sent && sent.body.allLibraries === true, show(sent));
    ok('and with no list beside it', !!sent && sent.body.enabledFolders == null, show(sent));

    sent = await edit(KID, { MaxParentalRating: 14, EnableAllFolders: true }, c => tick(c, true));
    ok('a rating is sent as its score', !!sent && sent.body.maxParentalRating === '14', show(sent));
    ok('a ticked library is sent as the list', !!sent && Array.isArray(sent.body.enabledFolders)
        && sent.body.enabledFolders.length === 1 && sent.body.enabledFolders[0] === LIB, show(sent));
    ok('and without allLibraries', !!sent && sent.body.allLibraries !== true, show(sent));

    sent = await edit(MAIN, { MaxParentalRating: null, EnableAllFolders: true });
    ok('the main account sends no rating (keeps its own)', !!sent && sent.body.maxParentalRating == null, show(sent));
    ok('and no libraries either way', !!sent && sent.body.enabledFolders == null && sent.body.allLibraries !== true, show(sent));

    console.log('\n── Create a profile ────────────────────────────────────────────');

    sent = await create(c => tick(c, false));
    ok('Create posts', !!sent);
    ok('no library ticked is sent as allLibraries true', !!sent && sent.body.allLibraries === true, show(sent));
    ok('"No restrictions" is sent as ""', !!sent && sent.body.maxParentalRating === '', show(sent));

    sent = await create(c => tick(c, true));
    ok('a ticked library is sent as the list, without allLibraries', !!sent && Array.isArray(sent.body.enabledFolders)
        && sent.body.enabledFolders[0] === LIB && sent.body.allLibraries !== true, show(sent));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
