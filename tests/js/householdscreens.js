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
const { PP, content, settle } = env;
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

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
