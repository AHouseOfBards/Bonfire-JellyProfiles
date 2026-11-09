// The household screens, reworked (P2-101 to 113, P2-602 to 607).
//
// Each section is one finding, drawn by the real render code against a stub DOM and read
// back from the markup. Every section was run against the build before its fix and is red
// there; the fix commit says by how much.
//
//   node tests/js/householdscreens.js [path/to/profiles.js]

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
const { PP, content, settle, posts } = env;
let respond = () => ({ status: 200, body: {} });
env.setRespond((url, body) => respond(url, body));
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.handleSessionExpired = () => {};

(async () => {
    // ── P2-102, P2-604: Sharing ─────────────────────────────────────────────────────
    console.log('\n── Sharing ─────────────────────────────────────────────────────');
    respond = (url) => {
        if (/accounts\/links$/.test(url)) {
            return { status: 200, body: { added: [{ userId: 'u1', name: 'grandma', hasPin: true }], shownIn: [{ householdId: 'h', userId: 'u2', name: 'Pat' }] } };
        }
        return { status: 200, body: {} };
    };
    PP._bindAccountLinks(content);
    await settle();
    const links = content.innerHTML;
    ok('Remove and Leave are quiet danger buttons, never secondary or primary',
        /class="profiles-btn btn-danger-quiet links-remove"/.test(links)
        && /class="profiles-btn btn-danger-quiet links-leave"/.test(links), links.slice(0, 80));

    const standalone = { isOwner: false, isMember: false };
    PP.renderBonfireStatus(content, content, standalone, { getUrl: p => p }, 'tok');
    ok('a standalone account is not offered the hide switches', !/bonfire-hide-mine-checkbox|bonfire-hide-others-checkbox/.test(content.innerHTML));
    PP.renderBonfireStatus(content, content, { isOwner: true, isMember: false, ownedCode: 'ABC123', ownedMembers: [] }, { getUrl: p => p }, 'tok');
    ok('a host is', /bonfire-hide-mine-checkbox/.test(content.innerHTML) && /bonfire-hide-others-checkbox/.test(content.innerHTML));

    const EN = (SRC.match(/const EN_STRINGS = \{[\s\S]*?\n    \};/) || [''])[0];
    ok('one word for the shared thing: no "group", "friends" or "sub-profiles" in the Sharing copy',
        !/'(bonfire|settings)\.[A-Za-z]+': "?'?[^\n]*\b(group|friend|sub-profile)/i.test(
            EN.split('\n').filter(l => /'(bonfire\.|settings\.yourBonfireBody)/.test(l) && !/bonfire\.(deleteGroup|leaveGroup)\w*'|bonfire\.failed/.test(l)).join('\n')));

    // A main account and its profiles are a household. "Bonfire" is the product, and the name
    // of an invite-code group ("Host a Bonfire", "Join a Bonfire"); it is not the word for
    // the household, so only the Sharing screen's own strings (bonfire.*, settings.yourBonfireBody)
    // may say "your Bonfire" or "X's Bonfire". Every other screen says household.
    const bonfireAsHousehold = EN.split(/\r?\n/).filter(l => {
        const m = /^\s*'([^']+)':\s*(.*)$/.exec(l);
        if (!m || /^(bonfire\.|settings\.yourBonfireBody$)/.test(m[1])) return false;
        return /\byour Bonfire\b|['’]s Bonfire\b|\bBonfire of\b/i.test(m[2]);
    }).map(l => l.trim().slice(0, 60));
    ok('no string outside Sharing calls a household a Bonfire', bonfireAsHousehold.length === 0, bonfireAsHousehold.join(' | '));
    ok('the picker heading is "Your household"', /'gate\.yourBonfire': 'Your household'/.test(EN));
    ok("and a linked one is \"{name}'s household\"", /'gate\.bonfireOf': "\{name\}'s household"/.test(EN));

    // ── P2-104, P2-113: the picture chooser and the artwork note ────────────────────
    console.log('\n── Form clutter ────────────────────────────────────────────────');
    const lib = { avatars: [{ id: 'a', url: '/a.png', thumbUrl: '/a.png', displayName: 'A' }], allowCustomUploads: true };
    const fresh = PP.renderAvatarPicker('create', lib, null, '#e07a5f', { enabled: false });
    ok('a new profile\'s picture chooser starts closed',
        /aria-expanded="false"/.test(fresh) && !/class="picture-sources is-open"/.test(fresh));
    const expl = (EN.match(/'profileForm\.artworkExplainer': (["'])([\s\S]*?)\1,\r?\n/) || [])[2] || '';
    ok('the artwork explainer is one short line, not a paragraph', expl.length > 0 && expl.length < 130, String(expl.length));

    // ── P2-103, P2-105, P2-602, P2-605: the child form ──────────────────────────────
    console.log('\n── Child form ──────────────────────────────────────────────────');
    PP.fetchSharedFormData = () => Promise.resolve({
        libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
        devices: [{ deviceId: 'tv-1', deviceName: 'TV', client: 'Jellyfin Web' }],
        libraryTags: [],
        avatarLibrary: { allowCustomUploads: true, avatars: [] },
        ratings: null
    });
    PP.showAddProfileModal();
    await settle();
    const add = content.innerHTML;
    const foldAt = add.indexOf('<details class="jpf-more"');
    const foldEnd = add.indexOf('</details>', foldAt);
    const outside = foldAt === -1 ? add : add.slice(0, foldAt) + add.slice(foldEnd);
    ok('Add has the viewing slot, outside More', /id="create-viewing-slot"/.test(outside));
    ok('allowed devices are outside More', /create-devices-dropdown-trigger/.test(outside));
    ok('the fold is labelled More', /jpf-more-title">More</.test(add.replace(/\s+/g, ' ').replace('> ', '>')) || /t\('profileForm.moreTitle'\)/.test(SRC)
        && /'profileForm\.moreTitle': 'More'/.test(SRC));

    const slot = D.makeContent();
    const editor = PP.initViewingEditor(slot, null, true);
    const sh = slot.innerHTML;
    ok('Add draws a daily limit and an hours checkbox', /id="viewing-daily"/.test(sh) && /id="viewing-hours"/.test(sh));
    ok('Add offers no "more time now"', !/viewing-grant/.test(sh));
    const days = slot.querySelector('#viewing-days');
    const hoursBox = slot.querySelector('#viewing-hours');
    ok('the seven rows are hidden until hours is ticked', !!days && days.style.display === 'none');
    if (hoursBox && days) {
        hoursBox.checked = true; hoursBox.fire('change');
        ok('and shown once it is', days.style.display === '');
    }
    if (hoursBox) { hoursBox.checked = false; hoursBox.fire('change'); }
    posts.length = 0;
    await editor.save('new-id');
    ok('an untouched form saves no rules', posts.length === 0);
    slot.querySelector('#viewing-daily').value = '60';
    posts.length = 0;
    await editor.save('new-id');
    const limitsPost = posts.filter(p => /limits\/new-id$/.test(p.url))[0];
    ok('a limit set on Add is saved against the new profile\'s id',
        !!limitsPost && limitsPost.body.dailyMinutes === 60, JSON.stringify(posts.map(p => p.url)));

    // ── P2-107, P2-603, P2-607: Your account ────────────────────────────────────────
    console.log('\n── Your account ────────────────────────────────────────────────');
    PP.masterPin = '2468';
    PP.currentProfiles = [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true,
        askPinToOpen: true, bypassPinOnLocalNetwork: false, lockoutMinutes: 5 }];
    PP.fetchSharedFormData = () => Promise.resolve({ devices: [{ deviceId: 'a', deviceName: 'TV' }, { deviceId: 'b', deviceName: 'Phone' }] });
    PP.showAccountSettings();
    await settle();
    const acct = content.innerHTML;
    const fAt = acct.indexOf('<details class="jpf-more');
    const fEnd = acct.indexOf('</details>', fAt);
    const inFold = fAt === -1 ? '' : acct.slice(fAt, fEnd);
    const outFold = fAt === -1 ? acct : acct.slice(0, fAt) + acct.slice(fEnd);
    ok('the PIN is the main PIN, and shows', /for="account-pin-input">Main PIN</.test(acct) && /id="account-pin-input"/.test(outFold));
    ok('ask-PIN, home network and auto-lock are in one fold, closed',
        fAt !== -1 && !/\bopen\b/.test(acct.slice(fAt, acct.indexOf('>', fAt)))
        && ['account-ask-pin', 'account-lan-bypass', 'account-lockout-select'].every(id => inFold.indexOf('id="' + id + '"') !== -1)
        && ['account-ask-pin', 'account-lan-bypass', 'account-lockout-select'].every(id => outFold.indexOf('id="' + id + '"') === -1));
    ok('devices are a count and a Manage devices button',
        /Known devices: 2/.test(acct) && /id="account-manage-devices"/.test(acct) && !/device-rename-btn/.test(acct));
    ok('the home-network option does not say LAN', !/\bLAN\b/.test((EN.match(/'profileForm\.bypassPinLan': [^\n]*/) || [''])[0]));
    const innerNode = { nodeType: 1, tagName: 'SELECT', parentNode: null };
    const fold = { nodeType: 1, tagName: 'DETAILS', open: false, parentNode: null };
    innerNode.parentNode = fold;
    PP.revealFold(innerNode);
    ok('revealFold opens the fold a field is in', fold.open === true);
    ok('Email off says so rather than vanishing', /Email is not set up/.test(EN));

    // ── P2-108 to P2-112: setup and the manage grid ─────────────────────────────────
    console.log('\n── First-time setup, manage grid ───────────────────────────────');
    PP._onboarding = {
        steps: ['welcome', 'mainPin', 'resetEmail', 'firstProfile', 'switcher'].map(id => ({ id: id, required: false, done: false, given: false })),
        welcome: '', index: 1, waiting: false
    };
    PP.promptSetMainPin([], false, () => {}, { onSkip: () => {} });
    ok('the PIN step says which step it is', /class="jpf-onboarding-step">Step 1 of 4</.test(content.innerHTML), content.innerHTML.slice(0, 120));
    PP._onboardingSwitcher(() => {}, () => {});
    ok('the switcher step says it can be changed later', /id="onboarding-switcher-later"/.test(content.innerHTML));
    const css = L.extractCss(SRC);
    ok('an email field is styled like the text fields (no white bar)',
        /\.bonfire-form-group input\[type="email"\]/.test(css));
    PP.isManageMode = true;
    ok('the manage grid says tiles are tapped and labels are status', /'gate\.manageHint': 'Tap a profile/.test(EN) && /jpf-manage-hint/.test(SRC));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
