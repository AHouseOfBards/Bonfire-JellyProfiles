// "Skip the PIN on my home network" is only offered where it can apply.
//
// "Local" is the server's own network as Jellyfin sees it. A household that watches from
// its own house is never local, so for it the option does nothing. The server remembers
// which households have been seen on its network (seenOnLocalNetwork in /list); the
// screens draw the option only for those, or where it is already on so it can be turned off.
//
// Runs the real showAccountSettings, showEditProfileModal and showAddProfileModal against
// a stub DOM and asks each case on its own, never as a total:
//
//   never seen, off   -> hidden
//   never seen, on    -> shown
//   seen, off         -> shown
//   seen, on          -> shown
//
// for Your account and a profile's form, and for the create form (always off) in the two
// states that can differ. Red against 2fb341b, which drew the option for everyone. Also
// checks the forms still save with the option absent, and that a household with the
// option drawn still sends its choice.
//
//   node tests/js/homeskip.js [path/to/profiles.js]

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

PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.fetchAndRenderProfiles = () => {};
PP.showAlert = () => {};
PP.handleSessionExpired = () => {};
PP.masterPin = '2468';
PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
PP.fetchSharedFormData = () => Promise.resolve({
    libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
    devices: [{ deviceId: 'tv-1', deviceName: 'TV', client: 'Jellyfin Web' }],
    libraryTags: [],
    avatarLibrary: { allowCustomUploads: true, avatars: [] },
    ratings: null
});
PP._sharedForm = null;

const CASES = [
    { seen: false, on: false, shown: false, name: 'never seen on the server\'s network, off' },
    { seen: false, on: true,  shown: true,  name: 'never seen, but already on' },
    { seen: true,  on: false, shown: true,  name: 'seen, off' },
    { seen: true,  on: true,  shown: true,  name: 'seen, on' }
];

function household(seen, on) {
    PP.currentProfiles = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true,
          askPinToOpen: true, bypassPinOnLocalNetwork: on, seenOnLocalNetwork: seen, lockoutMinutes: 5 },
        { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false,
          bypassPinOnLocalNetwork: on, seenOnLocalNetwork: seen }
    ];
}

(async () => {
    console.log('\n── Your account ────────────────────────────────────────────────');
    for (const c of CASES) {
        household(c.seen, c.on);
        PP.showAccountSettings();
        await settle();
        const box = content.querySelector('#account-lan-bypass');
        ok('Your account, ' + c.name + ': ' + (c.shown ? 'shown' : 'hidden'), (!!box) === c.shown);
        if (box && c.shown) ok('Your account, ' + c.name + ': ticked as stored', box.checked === c.on);
        ok('Your account, ' + c.name + ': its siblings are still drawn',
            !!content.querySelector('#account-ask-pin') && !!content.querySelector('#account-lockout-select'));
    }

    console.log('\n── A profile\'s form ────────────────────────────────────────────');
    respond = (url) => /^Users\//.test(url)
        ? { status: 200, body: { Policy: { MaxParentalRating: 14, EnableAllFolders: true } } }
        : { status: 200, body: {} };
    for (const c of CASES) {
        household(c.seen, c.on);
        PP.showEditProfileModal({ profileUserId: 'kid', profileName: 'Kid', isMaster: false, hasPin: true,
            lockoutMinutes: 20, bypassPinOnLocalNetwork: c.on, seenOnLocalNetwork: c.seen,
            allowedDeviceIds: [], enabledFolders: null, blockedTags: [], allowedTags: [],
            avatarColor: '#1F77B4', theme: null });
        await settle();
        const box = content.querySelector('#edit-local-bypass-checkbox');
        ok('Edit, ' + c.name + ': ' + (c.shown ? 'shown' : 'hidden'), (!!box) === c.shown);
        if (box && c.shown) ok('Edit, ' + c.name + ': ticked as stored', box.checked === c.on);
        ok('Edit, ' + c.name + ': the auto-lock beside it is still drawn', !!content.querySelector('#edit-lockout-select'));

        posts.length = 0;
        respond = () => ({ status: 200, body: {} });
        const save = content.querySelector('#edit-submit-btn');
        if (save) save.click();
        await settle();
        const sent = posts.filter(p => p.url === 'plugins/profiles/update')[0];
        ok('Edit, ' + c.name + ': Save works, and ' + (c.shown ? 'sends the choice' : 'leaves it alone'),
            !!sent && (c.shown ? sent.body.bypassPinOnLocalNetwork === c.on : sent.body.bypassPinOnLocalNetwork === null),
            sent ? String(sent.body.bypassPinOnLocalNetwork) : 'no post');
        respond = (url) => /^Users\//.test(url)
            ? { status: 200, body: { Policy: { MaxParentalRating: 14, EnableAllFolders: true } } }
            : { status: 200, body: {} };
    }

    console.log('\n── The create form ─────────────────────────────────────────────');
    for (const c of CASES.filter(x => !x.on)) {
        household(c.seen, false);
        PP.showAddProfileModal();
        await settle();
        const box = content.querySelector('#create-local-bypass-checkbox');
        ok('Create, ' + c.name + ': ' + (c.shown ? 'shown' : 'hidden'), (!!box) === c.shown);

        const name = content.querySelector('#create-name-input');
        if (name) name.value = 'New one';
        posts.length = 0;
        respond = () => ({ status: 200, body: { profileUserId: 'new' } });
        const create = content.querySelector('#create-submit-btn');
        if (create) create.click();
        await settle();
        const made = posts.filter(p => p.url === 'plugins/profiles/create')[0];
        ok('Create, ' + c.name + ': it can still be saved, with the option off',
            !!made && made.body.bypassPinOnLocalNetwork === false, made ? String(made.body.bypassPinOnLocalNetwork) : 'no post');
    }

    console.log('\n── The flag travels with each profile ──────────────────────────');
    ok('the client keeps seenOnLocalNetwork from /list', /seenOnLocalNetwork: pick\(p, 'seenOnLocalNetwork', false\)/.test(SRC));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
