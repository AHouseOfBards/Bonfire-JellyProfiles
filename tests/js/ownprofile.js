// A profile's own name and picture, for the person using it (P2-713).
//
// The server route (POST /me, tests/cs/selfedit) existed with no screen in front of it. This
// runs the real client code against a stub DOM: where the screen is offered and to whom, what
// it sends, and that it never carries anything the route is not for.
//
//   node tests/js/ownprofile.js [path/to/profiles.js]
//
// Red against a build without the screen: every section fails (the functions are not there).

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const { PP, content, settle, posts } = env;
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.handleSessionExpired = () => {};
PP.clearManagedDocumentListeners = () => {};
PP.readMasterState = () => ({ masterUserId: 'main', token: 'main-token' });

const KID = { profileUserId: 'kid-id', profileName: 'Kid', avatarColor: '#E50914', avatarInitial: 'K', profileImage: null, transparentAvatar: false };
const MAIN = { profileUserId: 'main', profileName: 'Dad', isMaster: true, avatarColor: '#E50914', avatarInitial: 'D' };
const LINKED = { profileUserId: 'gran', profileName: 'Gran', isLinked: true, isBonfire: true, avatarColor: '#E50914', avatarInitial: 'G' };
const profiles = [MAIN, KID, LINKED];

let library = { allowCustomUploads: true, avatars: [{ id: 'a1', displayName: 'Fox', url: '/a1', thumbUrl: '/a1' }] };
env.setRespond((url) => {
    if (/plugins\/profiles\/avatars$/.test(url)) {
        return { status: 200, body: { allowCustomUploads: library.allowCustomUploads, avatars: library.avatars } };
    }
    if (/plugins\/profiles\/list$/.test(url)) return { status: 200, body: [] };
    return { status: 200, body: {} };
});

// Focusing a text box on a phone raises the keyboard before anyone asked for it.
const formStart = SRC.indexOf('showOwnProfileForm: function');
const formSource = SRC.slice(formStart, SRC.indexOf('showAddProfileModal: function', formStart));
ok('the name box is focused on a TV only',
    /_focusFirstOnTv\(content, '#self-name-input'\)/.test(formSource) && !/\.focus\(\)/.test(formSource));

(async () => {
    console.log('\n-- Who is offered it ------------------------------------------');
    ok('the client has the screen', typeof PP.showOwnProfileForm === 'function' && typeof PP.ownProfileOf === 'function');
    if (typeof PP.ownProfileOf !== 'function') {
        console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }

    PP.isManageMode = false;
    PP._resumeState = { token: 'kid-token', userId: 'KID-ID', info: '{"name":"Kid"}' };
    const own = PP.ownProfileOf(profiles);
    ok('a sub-profile in use is found, whatever the id\'s case', !!own && own.profileUserId === 'kid-id');

    PP._resumeState = { token: 'main-token', userId: 'main' };
    ok('the main account is not offered it: it has the PIN and /update', PP.ownProfileOf(profiles) === null);
    PP._resumeState = { token: 'x', userId: 'gran' };
    ok('nor is an added account', PP.ownProfileOf(profiles) === null);
    PP._resumeState = { closeOnly: true };
    ok('nor is anyone when the picker was opened with nobody in', PP.ownProfileOf(profiles) === null);
    PP._resumeState = null;
    ok('nor the startup gate', PP.ownProfileOf(profiles) === null);
    PP._resumeState = { token: 'kid-token', userId: 'kid-id' };
    PP.isManageMode = true;
    ok('nor Manage Profiles, which is the main account\'s', PP.ownProfileOf(profiles) === null);
    PP.isManageMode = false;

    // The gate itself: the button is in the markup for the profile and for nobody else.
    const overlay = D.makeContent();
    PP.drawLogo = () => {};
    PP.attachOverlayInteractions = () => {};
    PP.fitGateToScreen = () => {};
    PP.showAnnouncements = () => {};
    PP._syncMasterFromProfiles = () => {};
    PP.renderOverlayContent(overlay, profiles);
    ok('the gate has a Name and picture button while a profile is in use', /id="profiles-self-btn"/.test(overlay.innerHTML));
    PP._resumeState = { token: 'main-token', userId: 'main' };
    PP.renderOverlayContent(overlay, profiles);
    ok('and not while the main account is', !/id="profiles-self-btn"/.test(overlay.innerHTML));

    console.log('\n-- The screen -------------------------------------------------');
    PP._resumeState = { token: 'kid-token', userId: 'kid-id', info: '{"name":"Kid"}' };
    PP.showOwnProfileForm(KID);
    await settle();
    const html = content.innerHTML;
    ok('it asks for a name, with the current one in it', /id="self-name-input"[^>]*value="Kid"/.test(html));
    ok('a picture, from this server and from this device', /id="self-picture-sources"/.test(html) && /id="self-profile-image-file"/.test(html));
    ok('and a colour', /id="self-color-group"/.test(html));
    // Enumerated: every field the screen draws, matched against what the route takes.
    const fields = (html.match(/<(input|select|textarea)\b[^>]*>/g) || [])
        .map(tag => (/\bid="([^"]*)"/.exec(tag) || [])[1] || tag);
    ok('and no other field: no PIN, rating, library, limit, device or theme (' + fields.join(', ') + ')',
        fields.length === 3 && fields.every(f => ['self-name-input', 'self-profile-image-file', 'self-transparent-toggle'].indexOf(f) !== -1));
    ok('Save and Cancel', /id="self-submit-btn"/.test(html) && /id="self-cancel-btn"/.test(html));

    console.log('\n-- What it sends ----------------------------------------------');
    // Rename only.
    posts.length = 0;
    content.querySelector('#self-name-input').value = 'Kiddo';
    content.querySelector('#self-submit-btn').click();
    await settle();
    const me = posts.filter(p => /plugins\/profiles\/me$/.test(p.url) && p.init.method === 'POST');
    ok('one POST to /me', me.length === 1, JSON.stringify(posts.map(p => p.url)));
    const sent = me[0] ? me[0].body : {};
    ok('as the profile, with its own token and not the main account\'s',
        me[0] && me[0].init.headers && /kid-token/.test(JSON.stringify(me[0].init.headers)) && !/main-token/.test(JSON.stringify(me[0].init.headers)));
    ok('the new name', sent.profileName === 'Kiddo');
    ok('the colour', typeof sent.avatarColor === 'string');
    ok('no picture, since none changed', sent.profileImage === undefined && sent.avatarLibraryId === undefined);
    ok('no profile id, PIN, rating, library, device or theme field',
        ['profileId', 'masterPin', 'pin', 'maxParentalRating', 'enabledFolders', 'allowedDeviceIds', 'theme', 'blockedTags']
            .every(k => !(k in sent)), Object.keys(sent).join(','));
    ok('and no /update', !posts.some(p => /plugins\/profiles\/update/.test(p.url)));
    ok('the profile list is read again afterwards, so the grid shows the change', posts.some(p => /plugins\/profiles\/list$/.test(p.url)));
    ok('and the remembered header marker is dropped, so it is rebuilt from that list', PP._resumeState.info === null);

    // Unchanged name: not sent.
    PP._resumeState = { token: 'kid-token', userId: 'kid-id' };
    PP.showOwnProfileForm(KID);
    await settle();
    posts.length = 0;
    content.querySelector('#self-submit-btn').click();
    await settle();
    const same = (posts.filter(p => /plugins\/profiles\/me$/.test(p.url))[0] || {}).body || {};
    ok('an untouched name is not sent', same.profileName === undefined, JSON.stringify(same));

    // An empty name is refused here, before anything is sent.
    PP._resumeState = { token: 'kid-token', userId: 'kid-id' };
    PP.showOwnProfileForm(KID);
    await settle();
    posts.length = 0;
    content.querySelector('#self-name-input').value = '   ';
    content.querySelector('#self-submit-btn').click();
    await settle();
    ok('an empty name sends nothing', !posts.some(p => /profiles\/me$/.test(p.url)));

    // A picture from the library, on a server limited to it.
    library = { allowCustomUploads: false, avatars: [{ id: 'a1', displayName: 'Fox', url: '/a1', thumbUrl: '/a1' }] };
    PP._resumeState = { token: 'kid-token', userId: 'kid-id' };
    const realPicker = PP.initAvatarPicker;
    PP.initAvatarPicker = () => ({ get: () => ({ image: '/plugins/profiles/avatars/a1', thumb: null, libraryId: 'a1', transparent: false }) });
    PP.showOwnProfileForm(KID);
    await settle();
    const limited = content.innerHTML;
    ok('with custom uploads off there is no upload control', !/id="self-profile-image-file"/.test(limited) && /avatar-library-grid/.test(limited));
    posts.length = 0;
    content.querySelector('#self-submit-btn').click();
    await settle();
    const lib = (posts.filter(p => /profiles\/me$/.test(p.url))[0] || {}).body || {};
    ok('a library choice goes as an id for the server to copy, never as image data', lib.avatarLibraryId === 'a1' && lib.profileImage === undefined, JSON.stringify(lib));

    // A refusal is shown and the screen stays.
    env.setRespond((url) => /profiles\/me$/.test(url) ? { status: 400, body: 'A profile with this name already exists.', headers: { 'X-Bonfire-Error': 'name_taken' } }
        : { status: 200, body: /list$/.test(url) ? [] : { allowCustomUploads: true, avatars: [] } });
    PP.initAvatarPicker = realPicker;
    PP._resumeState = { token: 'kid-token', userId: 'kid-id' };
    PP.showOwnProfileForm(KID);
    await settle();
    content.querySelector('#self-name-input').value = 'Dad';
    content.querySelector('#self-submit-btn').click();
    await settle();
    const err = content.querySelector('#self-error');
    // Replaced behaviour: a refusal used to be shown in the server's English words; it is said by its X-Bonfire-Error code now.
    ok('a refusal is shown in the screen, by its code', err.textContent === 'That name is taken. Choose another.' && err.style.display === 'block', err.textContent);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();