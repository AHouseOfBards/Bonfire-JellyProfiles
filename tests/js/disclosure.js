// Progressive disclosure, held by a ledger (P2-601 to P2-610).
//
// The household screens keep what is rarely needed under a closed "More" fold, and the
// admin tabs keep theirs under a closed "Advanced" one. Each of those was an owner decision
// about one control at a time, and nothing stopped the next edit putting a rare control back
// in the default view, or leaving a fold open. This enumerates every control the first open of
// each screen draws and says which side of the fold it is on.
//
//   node tests/js/disclosure.js [path/to/profiles.js [path/to/profilesDashboard.html]]
//
// The LEDGER is the whole contract, written down from the markup as it stood: for each
// screen, the controls in the default view (`basic`) and the controls under the fold
// (`more`). It is compared pairwise and as a multiset, in both directions:
//   - a ledgered control that is on the other side of the fold, or gone;
//   - a control the screen draws that the ledger does not know, so a new control has to be
//     placed on purpose, here, in review;
//   - a fold that is open on first open, which puts everything under it in the default view.
// Moving a control is a deliberate act: change the ledger in the same commit. The ledger is
// not computed from the markup; a harness that derived its expectation from the thing it
// checks could not go red.
//
// A control is an input, select, textarea or button. One without an id is named by its
// first class, with its input type for an input.

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

// ── The ledger ──────────────────────────────────────────────────────────────────────────
const HOUSEHOLD = {
    // Add: the name, picture, PIN, rating, libraries and which devices are the basics.
    add: {
        basic: ['.create-device-checkbox[checkbox]', '.library-checkbox[checkbox]', 'create-cancel-btn', 'create-change-picture', 'create-clear-profile-image-btn', 'create-kids-preset', 'create-name-input', 'create-pin-input', 'create-profile-image-file', 'create-rating-select', 'create-select-all-libraries', 'create-submit-btn', 'create-transparent-toggle'],
        more: ['.profiles-btn[button]', '.profiles-btn[button]', '.tag-input[text]', '.tag-input[text]', 'create-local-bypass-checkbox', 'create-lockout-select', 'create-theme-select']
    },
    // Edit, for a sub-profile. The two .profiles-btn under More are the tag Add buttons.
    editProfile: {
        basic: ['.device-checkbox[checkbox]', '.libart-mode', '.library-checkbox[checkbox]', '.profiles-btn[button]', 'edit-cancel-btn', 'edit-change-picture', 'edit-clear-profile-image-btn', 'edit-delete-btn', 'edit-name-input', 'edit-pin-input', 'edit-profile-image-file', 'edit-rating-select', 'edit-select-all-libraries', 'edit-submit-btn', 'edit-transparent-toggle'],
        more: ['.profiles-btn[button]', '.profiles-btn[button]', '.tag-input[text]', '.tag-input[text]', 'edit-libart-toggle', 'edit-local-bypass-checkbox', 'edit-lockout-select', 'edit-theme-select']
    },
    // Edit, for the main account: no limits to set, so nothing is under a fold.
    editMain: {
        basic: ['edit-cancel-btn', 'edit-change-picture', 'edit-clear-profile-image-btn', 'edit-name-input', 'edit-profile-image-file', 'edit-submit-btn', 'edit-theme-select', 'edit-transparent-toggle'],
        more: []
    },
    // Your account: the PIN and devices are the basics; when it is asked, the home network
    // and auto-lock are More.
    account: {
        basic: ['account-back-btn', 'account-manage-devices', 'account-pin-clear', 'account-pin-input', 'account-pin-save'],
        more: ['account-ask-pin', 'account-lan-bypass', 'account-layout-select', 'account-lockout-select']
    },
    // Your account, Email section, once an address is confirmed: which mails are sent is More.
    accountEmail: {
        basic: ['account-email-change', 'account-email-remove'],
        more: ['account-email-asked', 'account-email-device', 'account-email-limit', 'account-email-lockout']
    },
    // Sharing, in its three states.
    sharingHost: {
        basic: ['bonfire-back-btn', 'bonfire-delete-btn', 'bonfire-hide-mine-checkbox', 'bonfire-hide-others-checkbox', 'bonfire-join-btn', 'bonfire-join-input', 'bonfire-newcode-btn'],
        more: ['bonfire-lan-bypass-checkbox']
    },
    sharingMember: {
        basic: ['bonfire-back-btn', 'bonfire-generate-btn', 'bonfire-hide-mine-checkbox', 'bonfire-hide-others-checkbox', 'bonfire-leave-btn'],
        more: ['bonfire-lan-bypass-checkbox']
    },
    sharingNone: {
        basic: ['bonfire-back-btn', 'bonfire-generate-btn', 'bonfire-join-btn', 'bonfire-join-input'],
        more: []
    }
};

// The admin tabs: `basic` is outside any closed fold, `more` is inside one (Advanced, or a
// FAQ item that is closed until clicked). Every tab in the markup must be listed.
const ADMIN = {
    status: { basic: ['bonfireOn', 'onboardingAskNo', 'onboardingAskYes'], more: [] },
    households: { basic: ['.raised[button]', 'maxProfiles', 'scanOrphansBtn'], more: [] },
    devices: { basic: ['refreshSessionsBtn'], more: [] },
    security: {
        basic: ['.raised[button]', 'enablePinResetByEmail', 'requireMainPin'],
        more: ['panicCodeInput', 'pinGuessesPerDay', 'savePanicCode', 'trustedAdd']
    },
    look: {
        basic: ['.raised[button]', 'allowProfileThemes', 'avatarFolderBtn', 'avatarFolderInput', 'avatarUploadBtn', 'avatarUploadInput', 'defaultAskOnStartup', 'defaultSwitcherLocation', 'defaultTheme', 'disallowCustomAvatars'],
        more: ['.jpf-te-mode[button]', '.jpf-te-mode[button]', '.raised[button]', 'avatarScanBtn', 'avatarScanPath', 'enableStarterAvatars', 'fontChoose', 'fontFallback', 'fontFamily', 'fontFile', 'fontRights', 'fontUpload', 'logoAlt', 'logoChoose', 'logoCorners', 'logoFile', 'logoRemove', 'logoSave', 'logoText', 'profileImageQuality', 'richThemeMotion', 'teAccent', 'teAvatar', 'teBase', 'teBg1', 'teBg2', 'teButton', 'teCancel', 'teFont', 'teId', 'teName', 'tePattern', 'teSave', 'teText', 'themeImport', 'themeImportFile', 'themeNew',
            // 2.0: the four layout choices and the preview's Expand, inside the editor's fold.
            'teAlign', 'teExpand', 'teLogo', 'teProfiles', 'teSize']
    },
    setup: {
        basic: ['.raised[button]', 'defaultTimeZone', 'onboardingEnabled', 'onboardingShowAgain'],
        more: []
    },
    clients: {
        basic: ['.jpfProxyClient[checkbox]', '.raised[button]', 'enableClientPinLogin', 'enableClientProfileList', 'skipQuickConnectOnKnownDevices'],
        more: ['clientPinLoginFromAnywhere']
    },
    messages: {
        basic: ['annCancel', 'annMessage', 'annSave', 'annSeverity', 'emailFrom', 'emailHost', 'emailPassword', 'emailPort', 'emailSave', 'emailSecurity', 'emailTest', 'emailTestTo', 'emailUsername', 'mntClose', 'mntMessage', 'mntOpen', 'mntReopen', 'mntStop', 'mntUntil'],
        more: ['annEnds', 'annStarts']
    },
    activity: { basic: ['auditClear', 'auditDelete', 'auditDownload', 'auditFilter', 'auditFrom', 'auditTo'], more: [] },
    troubleshoot: {
        basic: ['copyReportBtn', 'reportText'],
        more: ['exportConfigBtn', 'faqOpenClientScript', 'importConfigBtn', 'importConfigFile']
    }
};

// ── Reading a screen ────────────────────────────────────────────────────────────────────
/**
 * Every control in `html`, each with whether it sits inside a closed <details>. A real tag
 * walk with a stack, not a slice between two markers: the first version of this check cut
 * the markup at the first </details>, which ends a fold early whenever one is nested.
 * Anything in a <summary> is the fold's own label and always shows.
 */
function controlsOf(html) {
    html = html.replace(/<!--[\s\S]*?-->/g, '').replace(/<script[\s\S]*?<\/script>/g, '').replace(/<style[\s\S]*?<\/style>/g, '');
    const VOID = /^(input|br|img|hr|meta|link|source)$/;
    const stack = [];
    const out = [];
    const re = /<(\/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*?)(\/?)>/g;
    let m;
    while ((m = re.exec(html))) {
        const closing = m[1] === '/', tag = m[2].toLowerCase(), attrs = m[3];
        if (closing) {
            for (let i = stack.length - 1; i >= 0; i--) if (stack[i].tag === tag) { stack.length = i; break; }
            continue;
        }
        if (/^(input|select|textarea|button)$/.test(tag)) {
            const id = (/\bid="([^"]*)"/.exec(attrs) || [])[1];
            const cls = ((/\bclass="([^"]*)"/.exec(attrs) || [])[1] || '').split(/\s+/)[0];
            const type = (/\btype="(\w+)"/.exec(attrs) || [])[1];
            const inSummary = stack.some(s => s.tag === 'summary');
            const inClosedFold = !inSummary && stack.some(s => s.tag === 'details' && !s.open);
            const hidden = type === 'hidden';
            if (!hidden) out.push({
                name: id || ('.' + cls + (type ? '[' + type + ']' : '')),
                fold: inClosedFold
            });
        }
        if (!VOID.test(tag) && !m[4]) stack.push({ tag: tag, open: /\bopen\b/.test(attrs.replace(/"[^"]*"/g, '')) });
    }
    return out;
}

/** The folds drawn in `html` that are open: every control under one is in the default view. */
function openFolds(html) {
    html = html.replace(/<!--[\s\S]*?-->/g, '').replace(/<script[\s\S]*?<\/script>/g, '');
    return (html.match(/<details\b[^>]*>/g) || []).filter(t => /\bopen\b/.test(t.replace(/"[^"]*"/g, '')));
}

/** Multiset difference: what is in `a` and not in `b`, counting repeats. */
function minus(a, b) {
    const left = b.slice();
    return a.filter(x => { const i = left.indexOf(x); if (i === -1) return true; left.splice(i, 1); return false; });
}

/**
 * Checks one screen against its ledger entry. Pairwise: each ledgered control is named when
 * it is on the wrong side, missing, or unexpected, so the report says which member failed.
 */
function check(label, html, expected) {
    const found = controlsOf(html);
    const basic = found.filter(c => !c.fold).map(c => c.name);
    const more = found.filter(c => c.fold).map(c => c.name);
    const wrongDefault = minus(expected.more, more).filter(n => basic.indexOf(n) !== -1);
    const wrongFold = minus(expected.basic, basic).filter(n => more.indexOf(n) !== -1);
    ok(label + ': nothing meant for More is in the default view',
        wrongDefault.length === 0, wrongDefault.join(', '));
    ok(label + ': nothing meant for the default view is under a fold',
        wrongFold.length === 0, wrongFold.join(', '));
    const gone = minus(expected.basic.concat(expected.more), basic.concat(more));
    ok(label + ': every ledgered control is still drawn', gone.length === 0, gone.join(', '));
    const extra = minus(basic.concat(more), expected.basic.concat(expected.more));
    ok(label + ': and no control the ledger does not know (' + extra.join(', ') + ')', extra.length === 0,
        'place it in the ledger: ' + extra.join(', '));
    const open = openFolds(html);
    ok(label + ': every fold starts closed', open.length === 0, open.join(' '));
}

(async () => {
    // ── The household screens ───────────────────────────────────────────────────────────
    console.log('\n-- Household screens, first open ------------------------------');
    const env = D.loadPlugin(L.readProfiles());
    const { PP, content, settle } = env;
    PP.beginNavigation = () => 1;
    PP.navIsCurrent = () => true;
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    PP.fetchAndRenderProfiles = () => {};
    PP.showAlert = () => {};
    PP.masterPin = '2468';
    PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
    PP.currentProfiles = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true, askPinToOpen: true, seenOnLocalNetwork: true },
        { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false, seenOnLocalNetwork: true, avatarInitial: 'K', avatarColor: '#E50914' }
    ];
    PP.fetchSharedFormData = () => Promise.resolve({
        libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
        devices: [{ deviceId: 'tv-1', deviceName: 'TV', client: 'Jellyfin Web' }],
        libraryTags: [], avatarLibrary: { allowCustomUploads: true, avatars: [] }, ratings: null
    });
    env.setRespond((url) => {
        if (/Users\//.test(url)) return { status: 200, body: { Policy: {} } };
        if (/account\/email/.test(url)) return { status: 200, body: { available: true, address: 'a@b.co', pinResetEnabled: true } };
        return { status: 200, body: {} };
    });

    PP.showAddProfileModal(); await settle();
    check('Add', content.innerHTML, HOUSEHOLD.add);
    PP.showEditProfileModal(PP.currentProfiles[1]); await settle();
    check('Edit (a profile)', content.innerHTML, HOUSEHOLD.editProfile);
    PP.showEditProfileModal(PP.currentProfiles[0]); await settle();
    check('Edit (the main account)', content.innerHTML, HOUSEHOLD.editMain);
    PP.showAccountSettings(); await settle();
    check('Your account', content.innerHTML, HOUSEHOLD.account);
    const slot = D.makeContent();
    PP._bindAccountEmail(slot); await settle();
    check('Your account, Email', slot.innerHTML, HOUSEHOLD.accountEmail);

    PP.loadBonfireStatus = () => {};
    const api = { getUrl: p => p };
    PP.renderBonfireStatus(content, content, { isOwner: true, isMember: false, ownedCode: 'ABC123', ownedMembers: [] }, api, 'tok');
    check('Sharing (hosting)', content.innerHTML, HOUSEHOLD.sharingHost);
    PP.renderBonfireStatus(content, content, { isOwner: false, isMember: true, joinedCode: 'ABC123', memberOf: 'Pat' }, api, 'tok');
    check('Sharing (a member)', content.innerHTML, HOUSEHOLD.sharingMember);
    PP.renderBonfireStatus(content, content, { isOwner: false, isMember: false }, api, 'tok');
    check('Sharing (neither)', content.innerHTML, HOUSEHOLD.sharingNone);

    // ── The admin tabs ──────────────────────────────────────────────────────────────────
    console.log('\n-- Admin tabs, first open --------------------------------------');
    const dash = L.readDashboard();
    const marks = [];
    dash.replace(/<div class="jpf-tab-panel"[^>]*data-panel="(\w+)"[^>]*>/g, (m, p, off) => { marks.push({ p: p, off: off }); return m; });
    const panels = {};
    marks.forEach((mk, i) => { panels[mk.p] = dash.slice(mk.off, i + 1 < marks.length ? marks[i + 1].off : dash.length); });
    ok('every tab in the markup has a ledger entry, and every entry a tab',
        minus(Object.keys(panels), Object.keys(ADMIN)).length === 0 && minus(Object.keys(ADMIN), Object.keys(panels)).length === 0,
        Object.keys(panels).join(', '));
    Object.keys(ADMIN).forEach(tab => {
        if (panels[tab] === undefined) return;
        check('Admin ' + tab, panels[tab], ADMIN[tab]);
    });

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
