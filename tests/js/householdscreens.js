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

    // ── P2-104, P2-113: the picture chooser and the artwork note ────────────────────
    console.log('\n── Form clutter ────────────────────────────────────────────────');
    const lib = { avatars: [{ id: 'a', url: '/a.png', thumbUrl: '/a.png', displayName: 'A' }], allowCustomUploads: true };
    const fresh = PP.renderAvatarPicker('create', lib, null, '#e07a5f', { enabled: false });
    ok('a new profile\'s picture chooser starts closed',
        /aria-expanded="false"/.test(fresh) && !/class="picture-sources is-open"/.test(fresh));
    const expl = (EN.match(/'profileForm\.artworkExplainer': (["'])([\s\S]*?)\1,\n/) || [])[2] || '';
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

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
