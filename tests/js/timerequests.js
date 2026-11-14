// Requests for more time on the picker (2.0): a row above the tiles when the main
// account's household has any, opening one asks for the main PIN, and then more time or no.
//
// Runs the real renderOverlayContent and the real row code against the stub DOM and a
// recording fetch, and reads back what was drawn and what was sent. Against 5b126c9, which
// has no row, no route and no answer dialog, it fails.
//
//   node tests/js/timerequests.js [path/to/profiles.js]

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
const { PP, posts, settle, sandbox } = env;

// What the server answers to the household's pending requests.
const server = { requests: [] };
env.setRespond(url => {
    if (url === 'plugins/profiles/time-requests') return { status: 200, body: { requests: server.requests } };
    return { status: 200, body: {} };
});

// Rows are recorded as they are appended: the stub's appendChild does nothing.
sandbox.document.createElement = tag => D.makeNode({}, { tagName: String(tag).toUpperCase(), className: '', innerHTML: '' });
function makeBox() {
    const box = D.makeNode({}, { children: [], style: { display: 'none' }, innerHTML: '' });
    box.appendChild = c => { box.children.push(c); return c; };
    return box;
}

PP.drawLogo = () => {};
PP.attachOverlayInteractions = () => {};
PP.fitGateToScreen = () => {};
PP.showAnnouncements = () => {};
PP._syncMasterFromProfiles = () => {};
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'master-token' });
PP.handleSessionExpired = () => {};
let refreshed = 0;
PP.fetchAndRenderProfiles = () => { refreshed++; };

const MAIN = { profileUserId: 'main', profileName: 'Parent', isMaster: true, isBonfire: false, masterUserId: 'main',
    hasPin: true, requiresPin: true, maxSubProfiles: 5, avatarColor: '#336699', avatarInitial: 'P' };
const asked = (id, name, askedUtc) => ({ profileUserId: id, profileName: name, isMaster: false, isBonfire: false,
    masterUserId: 'main', requiresPin: false, avatarColor: '#993366', avatarInitial: name.charAt(0),
    viewing: PP.normalizeViewing ? PP.normalizeViewing({ limited: true, allowed: false, reason: 'limit', askedUtc: askedUtc, timeZone: 'Europe/London' }) : null });
const quiet = { profileUserId: 'teen', profileName: 'Teen', isMaster: false, isBonfire: false, masterUserId: 'main', requiresPin: false,
    avatarColor: '#993366', avatarInitial: 'T',
    viewing: PP.normalizeViewing ? PP.normalizeViewing({ limited: true, allowed: true, binding: 'limit', minutesLeft: 40 }) : null };
const request = (id, name, left) => ({ profileId: id, profileName: name, askedUtc: '2030-01-09T19:00:00Z', timeZone: 'Europe/London', grantMinutesLeft: left });

async function draw(profiles, manage) {
    const overlay = D.makeContent();
    const box = makeBox();
    const find = overlay.querySelector;
    overlay.querySelector = sel => (sel === '#profiles-time-requests' ? (/id="profiles-time-requests"/.test(overlay.innerHTML) ? box : null) : find(sel));
    posts.length = 0;
    PP.isManageMode = !!manage;
    PP.renderOverlayContent(overlay, profiles);
    await settle();
    return { overlay, box, asks: posts.filter(p => p.url === 'plugins/profiles/time-requests') };
}

(async () => {
    console.log('\n── The row ──────────────────────────────────────────────────────');

    ok('this build draws requests for more time', typeof PP.showTimeRequests === 'function' && typeof PP.answerTimeRequest === 'function');

    server.requests = [request('kid', 'Kid', 240), request('kid2', 'Sam', 240)];
    let r = await draw([MAIN, asked('kid', 'Kid', '2030-01-09T19:00:00Z'), asked('kid2', 'Sam', '2030-01-09T19:05:00Z'), quiet]);
    ok('the picker has a box for it above the tiles', /id="profiles-time-requests"/.test(r.overlay.innerHTML)
       && r.overlay.innerHTML.indexOf('id="profiles-time-requests"') < r.overlay.innerHTML.indexOf('class="profiles-home-section"'));
    ok('it asks the server once, as the main account', r.asks.length === 1
       && /Token="master-token"/.test(((r.asks[0].init || {}).headers || {}).Authorization || ''), r.asks.length + ' asks');
    ok('a row for each request', r.box.children.length === 2, r.box.children.length + ' rows');
    ok('and the row is shown', r.box.style.display === '');
    const row = r.box.children[0] || D.makeNode({}, { className: '', innerHTML: '' });
    ok('a row is a real button, so Enter opens it', row.tagName === 'BUTTON' && row.attrs.type === 'button' || row.type === 'button', String(row.tagName));
    ok('which a remote can reach', /\bfocusable\b/.test(row.className));
    ok('it says who asked', /Kid asked for more time\./.test(row.innerHTML), row.innerHTML.slice(0, 200));
    ok('and when', /profiles-time-request-when/.test(row.innerHTML));

    // Hostile names stay text.
    server.requests = [request('kid', '<img src=x onerror=alert(1)>', 240)];
    r = await draw([MAIN, asked('kid', '<img src=x onerror=alert(1)>', '2030-01-09T19:00:00Z')]);
    ok('a name is escaped', r.box.children[0] && r.box.children[0].innerHTML.indexOf('<img src=x') === -1, r.box.children[0] && r.box.children[0].innerHTML.slice(0, 120));

    // Nobody waiting: no call, no row, nothing shown.
    server.requests = [];
    r = await draw([MAIN, quiet]);
    ok('with nobody asking it does not even ask the server', r.asks.length === 0);
    ok('and shows nothing', r.box.children.length === 0 && r.box.style.display === 'none');

    // The list said someone asked, the server says no longer (answered on another device).
    r = await draw([MAIN, asked('kid', 'Kid', '2030-01-09T19:00:00Z')]);
    ok('a request answered elsewhere leaves the row hidden', r.asks.length === 1 && r.box.children.length === 0 && r.box.style.display === 'none');

    // Manage Profiles is not the picker.
    server.requests = [request('kid', 'Kid', 240)];
    r = await draw([MAIN, asked('kid', 'Kid', '2030-01-09T19:00:00Z')], true);
    ok('Manage Profiles has no row and makes no call', !/profiles-time-requests/.test(r.overlay.innerHTML) && r.asks.length === 0);

    // Someone else's household is never the row's business: the main account is not this device's.
    PP.readMasterState = () => ({ masterUserId: 'someone-else', token: 'other-token' });
    r = await draw([MAIN, asked('kid', 'Kid', '2030-01-09T19:00:00Z')]);
    ok('a device whose main account is another one asks for nothing', r.asks.length === 0 && r.box.children.length === 0);
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'master-token' });
    // A guest household on the same screen: its requests are not drawn.
    const guest = { profileUserId: 'gkid', profileName: 'Guestkid', isMaster: false, isBonfire: true, masterUserId: 'guestmain', requiresPin: false,
        avatarColor: '#111111', avatarInitial: 'G', viewing: PP.normalizeViewing ? PP.normalizeViewing({ limited: true, allowed: false, askedUtc: '2030-01-09T19:00:00Z' }) : null };
    r = await draw([MAIN, quiet, guest]);
    ok('a linked household\'s request does not make the picker ask', r.asks.length === 0);

    // The server failing is the safe reading.
    env.setRespond(url => (url === 'plugins/profiles/time-requests' ? { status: 500, body: {} } : { status: 200, body: {} }));
    r = await draw([MAIN, asked('kid', 'Kid', '2030-01-09T19:00:00Z')]);
    ok('a server error shows nothing and does not throw', r.box.children.length === 0 && r.box.style.display === 'none');
    env.setRespond(url => (url === 'plugins/profiles/time-requests' ? { status: 200, body: { requests: server.requests } } : { status: 200, body: {} }));

    console.log('\n── Opening one ──────────────────────────────────────────────────');

    server.requests = [request('kid', 'Kid', 240)];
    r = await draw([MAIN, asked('kid', 'Kid', '2030-01-09T19:00:00Z')]);
    let pinAsked = null;
    PP.promptMasterPinEntry = (action, cb) => { pinAsked = { action, cb }; };
    let dialog = null;
    PP.showActionsDialog = (title, message, actions) => { dialog = { title, message, actions }; };
    PP.masterPin = null;
    posts.length = 0;
    refreshed = 0;
    if (r.box.children[0]) r.box.children[0].click();
    ok('opening it asks for the main PIN', !!pinAsked);
    ok('and nothing is given or refused before it is typed', posts.filter(p => /grant/.test(p.url)).length === 0 && !dialog);

    if (pinAsked) { PP.masterPin = '1234'; pinAsked.cb(); }
    ok('then it asks how much', dialog && dialog.title === 'More time for Kid' && dialog.message === 'How long?', dialog && dialog.title);
    const labels = dialog ? dialog.actions.map(a => a.label) : [];
    ok('15 minutes, 30, 1 hour, 2 hours, no, and cancel', labels.join('|') === '15 min|30 min|1 h|2 h|Decline|Cancel', labels.join('|'));
    ok('Cancel has nothing to run, so Back closes the dialog', dialog && dialog.actions[dialog.actions.length - 1].run === undefined
       && dialog.actions.slice(0, -1).every(a => typeof a.run === 'function'));
    ok('and the picker is drawn again behind it', refreshed >= 1);

    if (dialog) dialog.actions[1].run();
    await settle();
    const grant = posts.filter(p => p.url === 'plugins/profiles/limits/kid/grant').pop();
    ok('30 minutes is sent with the main PIN', grant && grant.body && grant.body.minutes === 30 && grant.body.masterPin === '1234', JSON.stringify(grant && grant.body));
    ok('as the main account', grant && /Token="master-token"/.test(((grant.init || {}).headers || {}).Authorization || ''));
    ok('and the PIN is dropped once it is used', PP.masterPin === null);

    // No.
    PP.masterPin = '1234';
    dialog = null;
    PP.answerTimeRequest(request('kid', 'Kid', 240), () => {});
    posts.length = 0;
    dialog.actions[dialog.actions.length - 2].run();
    await settle();
    const no = posts.filter(p => /grant$/.test(p.url)).pop();
    ok('Decline sends 0 minutes', no && no.body.minutes === 0 && no.body.masterPin === '1234', JSON.stringify(no && no.body));

    // Less than the list's top amount is left.
    PP.masterPin = '1234';
    PP.answerTimeRequest(request('kid', 'Kid', 100), () => {});
    ok('with 100 minutes left, nothing over 100 is offered, and 100 is', dialog.actions.map(a => a.label).join('|') === '15 min|30 min|1 h|1 h 40 min|Decline|Cancel',
       dialog.actions.map(a => a.label).join('|'));
    PP.answerTimeRequest(request('kid', 'Kid', 10), () => {});
    ok('with 10 left, only 10', dialog.actions.map(a => a.label).join('|') === '10 min|Decline|Cancel', dialog.actions.map(a => a.label).join('|'));

    // At the cap: plainly, and only no.
    PP.answerTimeRequest(request('kid', 'Kid', 0), () => {});
    ok('at the daily cap it offers only no, and cancel', dialog.actions.map(a => a.label).join('|') === 'Decline|Cancel', dialog.actions.map(a => a.label).join('|'));
    ok('and says why, plainly', /4 hours/.test(dialog.message) && /Kid/.test(dialog.message), dialog.message);

    // The server refusing a grant (cap reached since the screen was drawn) is shown.
    let alerted = null;
    PP.showAlert = (title, message, done) => { alerted = { title, message }; if (done) done(); };
    env.setRespond((url) => (/grant$/.test(url)
        ? { status: 400, body: "That's past the 4 hours of extra time allowed in a day.", headers: { 'X-Bonfire-Error': 'grant_cap' } }
        : { status: 200, body: {} }));
    PP.masterPin = '1234';
    PP.answerTimeRequest(request('kid', 'Kid', 240), () => {});
    dialog.actions[0].run();
    await settle();
    ok('a refusal from the server is shown, in words, not as "couldn\'t save"', alerted && /4 hours/.test(alerted.message), JSON.stringify(alerted));

    console.log('\n── Where it is allowed to cost ──────────────────────────────────');

    const src = SRC.replace(/checkRoute\s*:\s*function/, 'function checkRoute');
    const checkRoute = L.extractFunction(src, 'checkRoute');
    ok('checkRoute never asks for requests', checkRoute.indexOf('showTimeRequests') === -1 && checkRoute.indexOf('time-requests') === -1);
    const css = L.extractCss().replace(/\/\*[\s\S]*?\*\//g, '');
    const rules =(css.match(/[^{}]*\{[^}]*\}/g) || []).filter(x => /profiles-time-request/.test(x.split('{')[0]));
    ok('the row is styled', rules.length >= 5, rules.length + ' rules');
    ok('with the theme\'s own tokens, no fixed colours', rules.every(x => !/#[0-9a-f]{3,8}\b|rgba?\(/i.test(x)));
    ok('and none the old Chromium cannot read: no grid, no gap', rules.every(x => !/\bgrid\b|[^-]gap\s*:/.test(x)));
    ok('focus shows on a keyboard and on a television', rules.some(x => /:focus/.test(x.split('{')[0]) && !/jpf-tv/.test(x.split('{')[0]))
       && rules.some(x => /\.jpf-tv[^{]*:focus/.test(x.split('{')[0])));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('  ERROR ' + (e && e.stack || e)); console.log('\n' + pass + ' passed, ' + (fails.length + 1) + ' failed'); process.exit(1); });
