// Other accounts on a household's screen (issue #39, 1.7.2), in the web client: the tile
// on the gate, what choosing it in Manage Profiles does, and the section in Settings →
// Sharing that adds, removes and leaves.
//
// Runs the real code against the stub DOM and a recording fetch, then reads back what was
// drawn and what was sent. Red against 77b0d04, whose client has none of it.
//
//   node tests/js/accountlinks.js [path/to/profiles.js]

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
const { PP, posts, settle } = env;

const has = ['_bindAccountLinks', 'showAddedAccount'].filter(n => typeof PP[n] === 'function');
ok('this build has added accounts in the client', has.length === 2, has.join(','));
if (has.length !== 2) {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

// ── The server ───────────────────────────────────────────────────────────────
const server = {
    links: {
        added: [{ userId: 'gran', name: 'Gran <3', hasPin: true }, { userId: 'uncle', name: 'Uncle', hasPin: false }],
        shownIn: [{ userId: 'smiths', name: 'Smiths' }]
    },
    refuse: {}
};
env.setRespond((url, body) => {
    if (server.refuse[url]) { const r = server.refuse[url]; delete server.refuse[url]; return { status: r[0], body: r[1], headers: r[2] ? { 'X-Bonfire-Error': r[2] } : undefined }; }
    if (url === 'plugins/profiles/accounts/links') return { status: 200, body: server.links };
    if (url === 'plugins/profiles/accounts/links/add') {
        return { status: 200, body: Object.assign({}, server.links, { added: server.links.added.concat([{ userId: 'new', name: body.username, hasPin: false }]) }) };
    }
    if (url === 'plugins/profiles/accounts/links/remove') {
        return { status: 200, body: { added: server.links.added.filter(a => a.userId !== body.userId), shownIn: body.householdId ? [] : server.links.shownIn } };
    }
    return { status: 200, body: {} };
});

PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.handleSessionExpired = () => {};
let refreshed = 0;
PP.fetchAndRenderProfiles = () => { refreshed++; };
PP.masterPin = '1234';

let dialog = null;
PP.showActionsDialog = (title, message, actions) => { dialog = { title, message, actions }; };
const alerts = [];
PP.showAlert = (title, message) => { alerts.push({ title, message }); };

const sent = url => posts.filter(p => p.url === url);
const last = url => sent(url).pop();

(async () => {
    // ── What /list says ─────────────────────────────────────────────────────
    console.log('\n── The tile from /list ─────────────────────────────────────────');

    const rows = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, masterUserId: 'main' },
        { profileUserId: 'kid', profileName: 'Kid', isMaster: false, masterUserId: 'main' },
        { profileUserId: 'gran', profileName: 'Gran', isMaster: true, isBonfire: true, isLinked: true, masterUserId: 'gran', requiresPin: true },
        { profileUserId: 'smithmain', profileName: 'Smith', isMaster: true, isBonfire: true, masterUserId: 'smiths' }
    ];
    const profiles = PP.normalizeProfiles(rows);
    ok('isLinked is read from /list', profiles[2].isLinked === true && profiles[3].isLinked === false && profiles[0].isLinked === false);
    ok('PascalCase reads the same', PP.normalizeProfiles([{ ProfileUserId: 'g', IsLinked: true }])[0].isLinked === true);

    const overlay = D.makeContent();
    const realAttach = PP.attachOverlayInteractions;
    PP.attachOverlayInteractions = () => {};
    PP.isManageMode = false;
    let drawn = true;
    try { PP.renderOverlayContent(overlay, profiles); } catch (e) { drawn = e.message; }
    PP.attachOverlayInteractions = realAttach;
    ok('the gate draws in the stub', drawn === true, String(drawn));
    const html = overlay.innerHTML;

    // The card for an id, cut from the start of its tag to the start of the next card.
    const cardOf = id => {
        const at = html.indexOf('data-id="' + id + '"');
        if (at === -1) return '';
        const next = html.indexOf('profile-card', at + 20);
        return html.slice(at, next === -1 ? undefined : next);
    };
    const sectionOf = id => {
        const at = html.indexOf('data-id="' + id + '"');
        return html.slice(0, at).split('profiles-home-section').length;
    };
    ok('the added account has a tile', cardOf('gran') !== '');
    ok('marked as an added account, not a Bonfire profile', cardOf('gran').indexOf('title="Added account"') !== -1
       && cardOf('gran').indexOf('title="Bonfire Profile"') === -1);
    ok('with the link mark, not the fire', /material-icons[^>]*>link</.test(cardOf('gran')));
    ok('no crown on any tile', cardOf('gran').indexOf('crown') === -1 && cardOf('main').indexOf('crown') === -1,
       cardOf('gran').slice(0, 400));
    ok("it sits with this household's tiles", sectionOf('gran') === sectionOf('kid') && sectionOf('gran') !== sectionOf('smithmain'),
       [sectionOf('main'), sectionOf('kid'), sectionOf('gran'), sectionOf('smithmain')].join(','));
    ok('the Bonfire profile is still a Bonfire profile', cardOf('smithmain').indexOf('title="Bonfire Profile"') !== -1);

    // ── Choosing it ─────────────────────────────────────────────────────────
    console.log('\n── Choosing it ─────────────────────────────────────────────────');

    const wire = profile => {
        const card = D.makeNode({ 'data-id': profile.profileUserId });
        PP.attachOverlayInteractions(D.makeNode({}, {
            querySelectorAll: sel => (sel.indexOf('.profile-card:not') === 0 ? [card] : [])
        }), [profile]);
        return card;
    };
    let opened = null;
    let pinFor = null;
    let edited = null;
    PP.executeProfileSwitch = id => { opened = id; };
    PP.promptPinEntry = id => { pinFor = id; };
    PP.showEditProfileModal = p => { edited = p; };

    PP.isManageMode = false;
    wire(profiles[2]).click();
    ok('on the gate, it opens like any tile — through its PIN', pinFor === 'gran' && opened === null && dialog === null);

    PP.isManageMode = true;
    wire(profiles[2]).click();
    ok("in Manage Profiles it isn't edited", edited === null);
    ok('it offers to take it off', dialog && dialog.title === 'Gran' && dialog.message === "Gran's own account, added to this screen."
       && dialog.actions.map(a => a.label).join('|') === 'Remove|Cancel', dialog && JSON.stringify(dialog.message));
    wire(profiles[1]).click();
    ok('a profile of this household is still edited', edited && edited.profileUserId === 'kid');

    dialog.actions = null;
    PP.showAddedAccount(profiles[2]);
    dialog.actions[0].run();
    await settle();
    const removed = last('plugins/profiles/accounts/links/remove');
    ok('removing posts its id with the main PIN', removed && removed.body.userId === 'gran' && removed.body.masterPin === '1234');
    ok('and redraws the gate', refreshed === 1);
    PP.isManageMode = false;

    // ── Settings → Sharing ──────────────────────────────────────────────────
    console.log('\n── Settings → Sharing ──────────────────────────────────────────');

    let slot = D.makeContent();
    PP._bindAccountLinks(slot);
    await settle();
    ok('read from GET accounts/links', !!last('plugins/profiles/accounts/links') && !last('plugins/profiles/accounts/links').init.method);
    ok('the added accounts are listed, names as text', slot.innerHTML.indexOf('Gran &lt;3') !== -1 && slot.innerHTML.indexOf('Gran <3') === -1);
    ok('each says whether it asks for a PIN', slot.innerHTML.indexOf('Opens with their PIN') !== -1 && slot.innerHTML.indexOf('Opens without a PIN') !== -1);
    ok('and the screens this account is on', slot.innerHTML.indexOf('Your account on other screens') !== -1
       && slot.querySelectorAll('.links-leave').length === 1);
    ok('one remove button each', slot.querySelectorAll('.links-remove').length === 2);

    let before = posts.length;
    slot.querySelector('#links-add').click();
    ok('both fields are needed, and nothing is sent without them', posts.length === before
       && slot.querySelector('#links-status').textContent === 'Enter their username and password.');

    slot.querySelector('#links-username').value = '  grandpa ';
    slot.querySelector('#links-password').value = 'secret';
    slot.querySelector('#links-add').click();
    await settle();
    const added = last('plugins/profiles/accounts/links/add');
    ok('adding sends the username, trimmed, the password and the main PIN', added && added.init.method === 'POST'
       && added.body.username === 'grandpa' && added.body.password === 'secret' && added.body.masterPin === '1234',
       JSON.stringify(added && added.body));
    ok('and redraws from the reply', slot.querySelectorAll('.links-remove').length === 3
       && slot.querySelector('#links-status').textContent === 'grandpa added.');

    server.refuse['plugins/profiles/accounts/links/add'] = [400, "That username or password isn't right.", 'credentials_wrong'];
    slot.querySelector('#links-username').value = 'grandpa';
    slot.querySelector('#links-password').value = 'wrong';
    slot.querySelector('#links-add').click();
    await settle();
    // Replaced behaviour: a refusal used to be shown in the server's English words; it is said by its X-Bonfire-Error code now.
    ok('a refusal is shown by its code', slot.querySelector('#links-status').textContent === "That username or password isn't right. Try again.");
    ok('and clears the password', slot.querySelector('#links-password').value === '');

    slot = D.makeContent();
    PP._bindAccountLinks(slot);
    await settle();
    dialog = null;
    slot.querySelectorAll('.links-remove')[0].click();
    // The stub hands back attributes as written, so the name is still escaped here; a
    // browser decodes it, and the dialog writes titles as text.
    ok('removing asks first, by name', dialog && dialog.title === 'Remove Gran &lt;3?', dialog && dialog.title);
    before = posts.length;
    dialog.actions[1].run && dialog.actions[1].run();
    ok('cancel sends nothing', posts.length === before);
    dialog.actions[0].run();
    await settle();
    ok('remove sends that account', last('plugins/profiles/accounts/links/remove').body.userId === 'gran');
    ok('and redraws without it', slot.querySelectorAll('.links-remove').length === 1 && slot.innerHTML.indexOf('Gran') === -1);

    dialog = null;
    slot.querySelectorAll('.links-leave')[0].click();
    ok('leaving another screen asks first', dialog && dialog.title === "Leave Smiths's screen?");
    dialog.actions[0].run();
    await settle();
    const left = last('plugins/profiles/accounts/links/remove');
    ok('and sends that household, not a user', left.body.householdId === 'smiths' && !left.body.userId);

    server.refuse['plugins/profiles/accounts/links'] = [403, 'Only the main account can do that.'];
    slot = D.makeContent();
    slot.innerHTML = 'placeholder';
    PP._bindAccountLinks(slot);
    await settle();
    ok('a refusal leaves the section out', slot.innerHTML === '');

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) { console.log('\n   FAILED: ' + fails.join('; ')); process.exit(1); }
})().catch(e => { console.log('  FAIL  threw: ' + (e && e.stack || e)); process.exit(1); });
