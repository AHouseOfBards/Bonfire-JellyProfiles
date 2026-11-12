// The profile forms as Basics plus a closed "More settings", and the Kids preset (1.7.2).
//
// Runs the real showAddProfileModal and showEditProfileModal against a stub DOM, reads
// back where each control was drawn, then submits and reads what was sent. The point of
// the second half: a setting tucked into a closed <details> must still be sent with its
// value, or every new profile would silently lose its auto-lock and home-network choice.
//
// Red against 1.7.1, which drew every control in four open sections and had no preset.
//
//   node tests/js/profilebasics.js [path/to/profiles.js]

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
PP.masterPin = '2468';
PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
PP.currentProfiles = [
    { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true, seenOnLocalNetwork: true },
    { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false }
];
PP.fetchSharedFormData = () => Promise.resolve({
    libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
    devices: [{ deviceId: 'tv-1', deviceName: 'TV', client: 'Jellyfin Web' }],
    libraryTags: [],
    avatarLibrary: { allowCustomUploads: true, avatars: [] },
    ratings: null
});

/** Splits drawn markup into what is outside the More panel and what is inside it. */
function halves(html) {
    const open = html.indexOf('<details class="jpf-more"');
    const close = html.indexOf('</details>', open);
    return {
        hasMore: open !== -1 && close !== -1,
        openTag: open === -1 ? '' : html.slice(open, html.indexOf('>', open) + 1),
        basics: open === -1 ? html : html.slice(0, open) + html.slice(close),
        more: open === -1 ? '' : html.slice(open, close)
    };
}
const has = (part, id) => part.indexOf('id="' + id + '"') !== -1;

(async () => {
    // ── 1. The Kids preset, on its own ──────────────────────────────────────────────
    console.log('\n── The Kids preset ─────────────────────────────────────────────');

    const from = typeof PP.kidsRatingFrom === 'function' ? PP.kidsRatingFrom.bind(PP) : null;
    ok('there is a Kids preset', !!from && typeof PP.bindKidsPreset === 'function');
    if (from) {
        ok('the four US ratings: PG (10)', from(['', '6', '10', '14', '17']) === '10');
        ok('a country with no 10: the highest under it', from(['', '4', '8', '12', '15', '18']) === '8');
        ok('nothing at or under 10: the lowest offered', from(['', '12', '16', '18']) === '12');
        ok('no ratings at all: none', from(['']) === null);
        ok('a limit set elsewhere, offered as its own option, counts like any other',
            from(['', '6', '10', '14', '17', '9']) === '10');
    }

    // ── 2. The create form ──────────────────────────────────────────────────────────
    console.log('\n── Create: Basics, then More ───────────────────────────────────');

    PP.showAddProfileModal();
    await settle();
    let h = halves(content.innerHTML);
    ok('there is a More settings panel', h.hasMore);
    ok('and it starts closed', h.hasMore && !/\bopen\b/.test(h.openTag), h.openTag);
    for (const id of ['create-name-input', 'create-image-upload-preview', 'create-color-group',
                      'jpf-create-pin-input', 'create-kids-preset', 'create-rating-select']) {
        ok('Basics has ' + id, has(h.basics, id) && !has(h.more, id));
    }
    ok('Basics has the libraries', /class="library-checkbox"/.test(h.basics));
    for (const id of ['create-lockout-select', 'create-local-bypass-checkbox', 'create-theme-select']) {
        ok('More has ' + id, has(h.more, id) && !has(h.basics, id));
    }
    // Moved out of More by the owner's decision (P2-605): keeping a child to one device is
    // the common parental task.
    ok('Basics has the allowed devices, More does not',
        has(h.basics, 'create-devices-dropdown-trigger') && !has(h.more, 'create-devices-dropdown-trigger'));
    ok('More has the tags', /create-blocked-tags/.test(h.more) && !/create-blocked-tags/.test(h.basics));
    ok('No background is part of the picture picker, before the colours',
        h.basics.indexOf('id="create-transparent-row"') > h.basics.indexOf('id="create-image-upload-preview"')
        && h.basics.indexOf('id="create-transparent-row"') < h.basics.indexOf('id="create-color-group"'));

    // Tick Kids, then save without ever opening More.
    const kids = content.querySelector('#create-kids-preset');
    const rating = content.querySelector('#create-rating-select');
    if (kids && rating) {
        kids.checked = true;
        kids.fire('change');
        ok('ticking Kids sets the rating to 10', rating.value === '10', rating.value);
        kids.checked = false;
        kids.fire('change');
        ok('unticking puts back what was there', rating.value === '', rating.value);
        kids.checked = true;
        kids.fire('change');
        rating.value = '14';
        rating.fire('change');
        ok('changing the rating by hand unticks it', kids.checked === false);
        kids.checked = true;
        kids.fire('change');
    }

    const name = content.querySelector('#create-name-input');
    if (name) name.value = 'Kid two';
    posts.length = 0;
    respond = () => ({ status: 200, body: { profileUserId: 'new' } });
    const createBtn = content.querySelector('#create-submit-btn');
    if (createBtn) createBtn.click();
    await settle();
    const created = posts.filter(p => p.url === 'plugins/profiles/create')[0];
    ok('Create posts', !!created);
    if (created) {
        ok('with the Kids rating', created.body.maxParentalRating === '10', String(created.body.maxParentalRating));
        ok('and More\'s values, never opened: five-minute auto-lock', created.body.lockoutMinutes === 5);
        ok('home network off', created.body.bypassPinOnLocalNetwork === false);
        ok('any device', Array.isArray(created.body.allowedDeviceIds) && created.body.allowedDeviceIds.length === 0);
    }

    // ── 3. The edit form ────────────────────────────────────────────────────────────
    console.log('\n── Edit: the same split; the main account has no More ─────────');

    respond = (url) => /^Users\//.test(url)
        ? { status: 200, body: { Policy: { MaxParentalRating: 14, EnableAllFolders: true } } }
        : { status: 200, body: {} };
    PP.showEditProfileModal({ profileUserId: 'kid', profileName: 'Kid', isMaster: false, hasPin: true,
        lockoutMinutes: 20, bypassPinOnLocalNetwork: true, allowedDeviceIds: [], enabledFolders: null,
        blockedTags: [], allowedTags: [], avatarColor: '#1F77B4', theme: null });
    await settle();
    h = halves(content.innerHTML);
    ok('a profile\'s form has the panel, closed', h.hasMore && !/\bopen\b/.test(h.openTag));
    for (const id of ['edit-name-input', 'jpf-edit-pin-input', 'edit-rating-select']) {
        ok('Basics has ' + id, has(h.basics, id) && !has(h.more, id));
    }
    for (const id of ['edit-lockout-select', 'edit-local-bypass-checkbox', 'edit-theme-select', 'edit-libart-toggle', 'edit-libart-hint']) {
        ok('More has ' + id, has(h.more, id) && !has(h.basics, id));
    }
    ok('Basics has the allowed devices, More does not (P2-605)',
        has(h.basics, 'devices-dropdown-trigger') && !has(h.more, 'devices-dropdown-trigger'));
    ok('the stored rating is selected', (content.querySelector('#edit-rating-select') || {}).value === '14');

    posts.length = 0;
    respond = () => ({ status: 200, body: {} });
    const saveBtn = content.querySelector('#edit-submit-btn');
    if (saveBtn) saveBtn.click();
    await settle();
    const updated = posts.filter(p => p.url === 'plugins/profiles/update')[0];
    ok('Save posts', !!updated);
    if (updated) {
        ok('with More\'s stored values intact: 20 minutes', updated.body.lockoutMinutes === 20);
        ok('and home network on', updated.body.bypassPinOnLocalNetwork === true);
    }

    PP.showEditProfileModal({ profileUserId: 'main', profileName: 'Bard', isMaster: true, hasPin: true,
        avatarColor: '#1F77B4', theme: null });
    await settle();
    h = halves(content.innerHTML);
    ok('the main account\'s form has no More', !h.hasMore);
    ok('nor a PIN field: that is Your account\'s', !has(h.basics, 'jpf-edit-pin-input'));
    ok('but keeps its theme', has(h.basics, 'edit-theme-select'));

    posts.length = 0;
    const mainSave = content.querySelector('#edit-submit-btn');
    if (mainSave) mainSave.click();
    await settle();
    const mainUpdate = posts.filter(p => p.url === 'plugins/profiles/update')[0];
    ok('saving it leaves the PIN, home network and auto-lock alone',
        !!mainUpdate && mainUpdate.body.pin === null && mainUpdate.body.bypassPinOnLocalNetwork === null
        && mainUpdate.body.lockoutMinutes === undefined && !('askPinToOpen' in mainUpdate.body),
        mainUpdate ? JSON.stringify({ pin: mainUpdate.body.pin, lan: mainUpdate.body.bypassPinOnLocalNetwork, lock: mainUpdate.body.lockoutMinutes }) : 'no post');

    // ── 4. The panel on a television ────────────────────────────────────────────────
    console.log('\n── The panel on a television ───────────────────────────────────');
    const css = L.extractCss(SRC);
    ok('its summary takes focus', /<summary class="jpf-more-summary" tabindex="0">/.test(SRC));
    ok('and shows it', /\.jpf-more > summary:focus\s*\{[^}]*outline:/.test(css));
    ok('the browser\'s own marker is hidden', /\.jpf-more > summary::-webkit-details-marker\s*\{\s*display: none;/.test(css));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
