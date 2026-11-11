// Deleting a profile whose account is kept (R-1134 follow-up).
//
// An admin can make an account into a profile with "keep the account when the profile is
// deleted". DeleteProfile then releases the account instead of deleting it, but the
// confirmation still said "and its underlying user account ... irreversible". It now says
// the account is kept and that an admin sets its password in Dashboard -> Users.
//
// Runs the real showEditProfileModal against a stub DOM, clicks Delete, and reads the
// confirmation. Also holds the server to sending the flag, and only to the household's own
// view of its profiles. Red against the build before the fix.
//
//   node tests/js/keptaccountdelete.js [path/to/profiles.js]

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const BASE = { profileUserId: 'kid', profileName: 'Kid', isMaster: false, hasPin: false,
    lockoutMinutes: 20, bypassPinOnLocalNetwork: false, allowedDeviceIds: [], enabledFolders: null,
    blockedTags: [], allowedTags: [], avatarColor: '#1F77B4', theme: null };

async function confirmFor(profile) {
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
    PP.currentProfiles = [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, hasPin: true }, profile];
    PP.fetchSharedFormData = () => Promise.resolve({
        libraries: [], devices: [], libraryTags: [],
        avatarLibrary: { allowCustomUploads: true, avatars: [] }, ratings: null
    });
    const asked = [];
    PP.showConfirmDialog = (title, msg) => { asked.push(msg); };   // never confirms
    PP.showEditProfileModal(Object.assign({}, profile));
    await env.settle();
    const btn = env.content.querySelector('#edit-delete-btn');
    if (btn) btn.click();
    await env.settle();
    return asked[0];
}

(async () => {
    console.log('\n── Delete confirmation ─────────────────────────────────────────');

    const normal = await confirmFor(Object.assign({}, BASE));
    ok('a normal profile still says its account is deleted', !!normal && /and its account\?/.test(normal), normal);

    const kept = await confirmFor(Object.assign({}, BASE, { keepAccountOnDelete: true }));
    ok('a kept account says it is kept', !!kept && /kept/i.test(kept), kept);
    ok('and does not say the account is deleted', !!kept && !/underlying user account|irreversible/i.test(kept), kept);
    ok('and sends the admin to Dashboard -> Users for its password', !!kept && /Dashboard/.test(kept) && /Users/.test(kept), kept);
    ok('with the profile name', !!kept && /Kid/.test(kept), kept);

    console.log('\n── Every language has the new line ─────────────────────────────');
    const dir = path.join(__dirname, '..', '..', 'Web', 'i18n');
    for (const f of fs.readdirSync(dir).filter(n => /\.json$/.test(n))) {
        const j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
        ok(f + ' has profileForm.deleteConfirmKept', typeof j['profileForm.deleteConfirmKept'] === 'string'
            && /\{name\}/.test(j['profileForm.deleteConfirmKept']));
    }

    console.log('\n── The list sends the flag, to the owner only ──────────────────');
    const ctl = fs.readFileSync(path.join(__dirname, '..', '..', 'Controllers', 'ProfilesController.cs'), 'utf8');
    ok('GetProfiles sends KeepAccountOnDelete only when ownerView',
        /KeepAccountOnDelete\s*=\s*ownerView\s*&&\s*m\.KeepAccountOnDelete/.test(ctl));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
