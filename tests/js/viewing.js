// Viewing hours in the web client (1.7.2): the line on a limited profile's tile, what
// choosing an out-of-time profile does, the editor on a profile's form, the household's
// time zone in Your account, and the two new email notices.
//
// Runs the real code against the stub DOM and a recording fetch, then reads back what was
// drawn and what was sent. Red against de05710, whose client has none of it.
//
//   node tests/js/viewing.js [path/to/profiles.js]

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

const has = ['normalizeViewing', 'viewingLine', 'showTimeUp', 'initViewingEditor', '_bindAccountZone']
    .filter(n => typeof PP[n] === 'function');
ok('this build has viewing hours in the client', has.length === 5, has.join(','));
if (has.length !== 5) {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

// ── The server ───────────────────────────────────────────────────────────────
const MON_FRI = 62;
const server = {
    limits: {
        profileId: 'kid', limited: true, allowed: true, reason: '', binding: 'limit', minutesLeft: 45,
        untilUtc: '2030-01-09T16:00:00Z', nextStartUtc: null, usedMinutes: 15, limitMinutes: 60,
        askedUtc: null, timeZone: 'Europe/London',
        rules: { hoursEnabled: true, windows: [{ days: MON_FRI, start: '15:00', end: '20:00' }], dailyMinutes: 60, weekendMinutes: null }
    },
    zones: { zones: ['America/New_York', 'Europe/London', 'Europe/Paris'], server: 'Europe/London', default: '', household: 'Europe/Paris', effective: 'Europe/Paris' },
    email: { available: true, address: 'parent@example.com', pendingAddress: '', notifyLockout: true, notifyNewDevice: true, pinResetEnabled: true },
    refuse: {}
};
env.setRespond((url, body) => {
    if (server.refuse[url]) { const r = server.refuse[url]; delete server.refuse[url]; return { status: r[0], body: r[1] }; }
    if (url === 'plugins/profiles/limits/kid' && !body) return { status: 200, body: server.limits };
    if (url === 'plugins/profiles/limits/kid') return { status: 200, body: Object.assign({}, server.limits, { rules: body }) };
    if (url === 'plugins/profiles/limits/kid/grant' || url === 'plugins/profiles/limits/kid/ask') {
        return { status: 200, body: Object.assign({}, server.limits, { rules: null }) };
    }
    if (url === 'plugins/profiles/timezones') return { status: 200, body: server.zones };
    if (url === 'plugins/profiles/account/email' && !body) return { status: 200, body: server.email };
    if (url === 'plugins/profiles/account/email/notifications') return { status: 200, body: Object.assign({}, server.email, body) };
    return { status: 200, body: {} };
});

PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.handleSessionExpired = () => {};
PP.fetchAndRenderProfiles = () => {};
PP.masterPin = '1234';

const sent = url => posts.filter(p => p.url === url);
const last = url => sent(url).pop();

(async () => {
    // ── What the server says, as the client reads it ────────────────────────
    console.log('\n── The state from the server ───────────────────────────────────');

    ok('a profile with no limits has no state', PP.normalizeViewing(null) === null
       && PP.normalizeViewing({ limited: false, allowed: true }) === null);
    const pascal = PP.normalizeViewing({ Limited: true, Allowed: false, Reason: 'limit', TimeZone: 'Europe/London' });
    ok('PascalCase from an older serializer reads the same', pascal && pascal.allowed === false && pascal.reason === 'limit'
       && pascal.timeZone === 'Europe/London');
    const listed = PP.normalizeProfiles([{ profileUserId: 'kid', profileName: 'Kid', viewing: server.limits }])[0];
    ok('/list carries it onto the profile', listed.viewing && listed.viewing.minutesLeft === 45);

    // ── The line on the tile ─────────────────────────────────────────────────
    console.log('\n── The line on the tile ────────────────────────────────────────');

    const line = v => PP.viewingLine(PP.normalizeViewing(Object.assign({ limited: true }, v)));
    ok('nothing for a profile with no limits', PP.viewingLine(null) === '');
    ok('the daily limit: minutes left', line({ allowed: true, binding: 'limit', minutesLeft: 45 }) === '45 min left');
    ok('the hours: when it ends', /^Until /.test(line({ allowed: true, binding: 'hours', untilUtc: '2030-01-09T20:00:00Z', timeZone: 'Europe/London' })));
    ok('nothing to say when no limit ends today', line({ allowed: true, binding: '' }) === '');
    ok('outside its hours: when it opens', /^From /.test(line({ allowed: false, reason: 'hours', nextStartUtc: '2030-01-10T15:00:00Z', timeZone: 'Europe/London' })));
    ok("limit spent: time's up", line({ allowed: false, reason: 'limit', nextStartUtc: '2030-01-10T15:00:00Z' }) === "Time's up");
    ok('asked, and nobody has answered', line({ allowed: false, reason: 'limit', askedUtc: '2030-01-09T19:00:00Z' }) === 'Asked for more time');

    // The household's clock, not the device's. 20:00 in London and 20:00 in Tokyo are
    // different instants and must read the same; 15:00 in New York must not.
    const london = PP.viewingTime('2030-01-09T20:00:00Z', 'Europe/London');
    const tokyo = PP.viewingTime('2030-01-09T11:00:00Z', 'Asia/Tokyo');
    const newYork = PP.viewingTime('2030-01-09T20:00:00Z', 'America/New_York');
    ok('times are read in the household zone', london !== '' && london === tokyo && london !== newYork, [london, tokyo, newYork].join(' | '));
    ok('a zone the browser does not know falls back to the device clock', PP.viewingTime('2030-01-09T20:00:00Z', 'Not/AZone') !== '');

    // ── Choosing a profile that is out of time ──────────────────────────────
    console.log('\n── Choosing a profile that is out of time ──────────────────────');

    let dialog = null;
    let alerts = [];
    let opened = 0;
    PP.showActionsDialog = (title, message, actions) => { dialog = { title, message, actions }; };
    PP.showAlert = (title, message, done) => { alerts.push({ title, message }); if (done) done(); };
    const kid = { profileUserId: 'kid', profileName: 'Kid', isMaster: false, requiresPin: false,
        viewing: PP.normalizeViewing({ limited: true, allowed: false, reason: 'limit' }) };

    // The card's own click handler, from the real attachOverlayInteractions.
    const card = D.makeNode({ 'data-id': 'kid' });
    const overlay = D.makeNode({}, {
        querySelectorAll: sel => (sel.indexOf('.profile-card:not') === 0 ? [card] : [])
    });
    PP.executeProfileSwitch = () => { opened++; };
    PP.isManageMode = false;
    let wired = true;
    try { PP.attachOverlayInteractions(overlay, [kid]); } catch (e) { wired = e.message; }
    ok('the gate wires its cards in the stub', wired === true, String(wired));
    card.click();
    ok('choosing it says so, instead of opening', dialog && opened === 0 && dialog.message === "Kid has used today's time. You can still browse.",
       dialog && dialog.message);
    const labels = dialog ? dialog.actions.map(a => a.label) : [];
    // Four labels a child can tell apart: "Give more time" sat beside "Ask for more time" and
    // read as the same thing, and "Browse" beside "Cancel" did not say what it did (P2-208).
    ok('ask, give time, browse, cancel', labels.join('|') === 'Ask for more time|Parent: add time|Browse without playing|Cancel', labels.join('|'));

    dialog.actions[2].run();
    ok('browse opens it anyway', opened === 1);

    const allowedKid = Object.assign({}, kid, { viewing: PP.normalizeViewing({ limited: true, allowed: true, binding: 'limit', minutesLeft: 5 }) });
    dialog = null;
    const card2 = D.makeNode({ 'data-id': 'kid' });
    PP.attachOverlayInteractions(D.makeNode({}, { querySelectorAll: sel => (sel.indexOf('.profile-card:not') === 0 ? [card2] : []) }), [allowedKid]);
    card2.click();
    ok('a profile with time left opens at once', dialog === null && opened === 2);

    PP.showTimeUp(kid, () => {});
    dialog.actions[0].run();
    await settle();
    const ask = last('plugins/profiles/limits/kid/ask');
    ok('asking posts to /ask', !!ask && ask.init.method === 'POST');
    ok('and says it was asked', alerts.length && alerts[alerts.length - 1].title === 'Asked');

    server.refuse['plugins/profiles/limits/kid/ask'] = [429, 'You already asked. Try again in a few minutes.'];
    PP.showTimeUp(kid, () => {});
    dialog.actions[0].run();
    await settle();
    ok('asking twice is told to wait', alerts[alerts.length - 1].message === 'You already asked. Try again in a few minutes.');

    const outside = Object.assign({}, kid, { viewing: PP.normalizeViewing({ limited: true, allowed: false, reason: 'hours', nextStartUtc: '2030-01-10T15:00:00Z', timeZone: 'Europe/London' }) });
    PP.showTimeUp(outside, () => {});
    ok('outside its hours, it says when it can watch', dialog.message.indexOf('Kid can watch again at ') === 0, dialog.message);

    let pinAsked = 0;
    let picked = null;
    PP.currentProfiles = [{ profileUserId: 'main', isMaster: true, hasPin: true }, kid];
    PP.withMainPin = (profiles, then) => { pinAsked++; then(); };
    PP.showSelectDialog = (title, body, options, choose) => { picked = options.map(o => o.id); choose('30'); };
    PP.showTimeUp(kid, () => {});
    dialog.actions[1].run();
    await settle();
    const grant = last('plugins/profiles/limits/kid/grant');
    ok('giving time asks for the main PIN first', pinAsked === 1);
    ok('offers 15, 30, 60 and 120 minutes', picked && picked.join(',') === '15,30,60,120');
    ok('and grants with the PIN', grant && grant.body.minutes === 30 && grant.body.masterPin === '1234');
    ok('the PIN is forgotten after, outside Manage Profiles', PP.masterPin === null);
    PP.masterPin = '1234';

    // ── The editor on a profile's form ──────────────────────────────────────
    console.log('\n── The editor on a profile\'s form ──────────────────────────────');

    let before = posts.length;
    let slot = D.makeContent();
    PP.initViewingEditor(slot, { profileUserId: 'main', profileName: 'Bard', isMaster: true });
    await settle();
    ok('nothing for a main account, and no request', slot.innerHTML === '' && posts.length === before);

    slot = D.makeContent();
    const q = sel => slot.querySelector(sel);
    let editor = PP.initViewingEditor(slot, { profileUserId: 'kid', profileName: 'Kid', isMaster: false });
    await settle();
    ok('drawn from GET /limits', slot.innerHTML.indexOf('Viewing hours') !== -1);
    ok('weekdays ticked, the weekend not', [1, 2, 3, 4, 5].every(d => q('#viewing-day-' + d).checked)
       && !q('#viewing-day-0').checked && !q('#viewing-day-6').checked);
    ok('with their hours', q('#viewing-from-3').value === '15:00' && q('#viewing-to-3').value === '20:00');
    ok('midnight can end a window', q('#viewing-to-3').options.some(o => o.value === '24:00')
       && !q('#viewing-from-3').options.some(o => o.value === '24:00'));
    ok('the daily limit is chosen', q('#viewing-daily').value === '60');
    ok('the weekend follows weekdays', q('#viewing-weekend').value === '');
    ok('where it stands now', slot.innerHTML.indexOf('45 min left · 15 of 60 min used today') !== -1);

    before = posts.length;
    await editor.save();
    ok('saving the form unchanged sends nothing', posts.length === before);

    q('#viewing-daily').value = '90';
    q('#viewing-day-6').checked = true;
    q('#viewing-from-6').value = '09:00';
    q('#viewing-to-6').value = '24:00';
    await editor.save();
    let saved = last('plugins/profiles/limits/kid');
    ok('a change is saved with the main PIN', saved && saved.body.masterPin === '1234' && saved.body.dailyMinutes === 90);
    const byDays = saved ? saved.body.windows.reduce((m, w) => { m[w.days] = w.start + '-' + w.end; return m; }, {}) : {};
    ok('days with the same hours share one window', byDays[MON_FRI] === '15:00-20:00' && byDays[64] === '09:00-24:00'
       && saved.body.windows.length === 2, JSON.stringify(saved && saved.body.windows));
    ok('with the language for its messages', saved && 'language' in saved.body);

    ok('no "say no" without a request', !slot.querySelectorAll('.viewing-grant').some(b => b.getAttribute('data-minutes') === '0'));
    const thirty = slot.querySelectorAll('.viewing-grant').filter(b => b.getAttribute('data-minutes') === '30')[0];
    q('#viewing-daily').value = '120';
    thirty.click();
    await settle();
    const given = last('plugins/profiles/limits/kid/grant');
    ok('more time is given at once, with the PIN', given && given.body.minutes === 30 && given.body.masterPin === '1234');
    ok('and says so', q('#viewing-grant-status').textContent === 'Given 30 min.');
    ok('without losing what was being edited', q('#viewing-daily').value === '120');

    server.limits = Object.assign({}, server.limits, { allowed: false, reason: 'limit', askedUtc: '2030-01-09T19:00:00Z' });
    slot = D.makeContent();
    editor = PP.initViewingEditor(slot, { profileUserId: 'kid', profileName: 'Kid', isMaster: false });
    await settle();
    ok('a request waiting is shown', slot.innerHTML.indexOf('Kid asked for more time.') !== -1);
    const no = slot.querySelectorAll('.viewing-grant').filter(b => b.getAttribute('data-minutes') === '0')[0];
    ok('with a way to say no', !!no);
    if (no) no.click();
    await settle();
    ok('which grants nothing', last('plugins/profiles/limits/kid/grant').body.minutes === 0
       && slot.querySelector('#viewing-grant-status').textContent === 'Told no.');

    server.refuse['plugins/profiles/limits/kid'] = [404, ''];
    slot = D.makeContent();
    slot.innerHTML = 'placeholder';
    editor = PP.initViewingEditor(slot, { profileUserId: 'kid', profileName: 'Kid', isMaster: false });
    await settle();
    before = posts.length;
    await editor.save();
    ok('a server without limits shows nothing and saves nothing', slot.innerHTML === '' && posts.length === before);

    // ── Your account ────────────────────────────────────────────────────────
    console.log('\n── Your account ────────────────────────────────────────────────');

    slot = D.makeContent();
    let savedAccount = null;
    const said = [];
    PP._bindAccountZone(slot, body => { savedAccount = body; return Promise.resolve(true); }, m => said.push(m));
    await settle();
    const zone = slot.querySelector('#account-zone-select');
    ok('the household zone is listed and chosen', zone && zone.value === 'Europe/Paris');
    ok('the first choice follows the server, and names it', zone && zone.options[0].value === ''
       && slot.innerHTML.indexOf('Server default (Europe/London)') !== -1);
    ok('zones read with spaces', slot.innerHTML.indexOf('>America/New York<') !== -1);
    zone.value = 'America/New_York';
    zone.fire('change');
    await settle();
    ok('choosing one saves it on the account', savedAccount && savedAccount.timeZone === 'America/New_York');

    slot = D.makeContent();
    PP._bindAccountEmail(slot);
    await settle();
    ok('asking for time mails by default', slot.querySelector('#account-email-asked').checked);
    ok('a spent limit does not, until asked', !slot.querySelector('#account-email-limit').checked);
    const limitBox = slot.querySelector('#account-email-limit');
    limitBox.checked = true;
    limitBox.fire('change');
    await settle();
    const n = last('plugins/profiles/account/email/notifications');
    ok('turning it on saves that one notice', n && n.body.notifyLimitReached === true && Object.keys(n.body).filter(k => k !== 'masterPin' && k !== 'language').length === 1,
       JSON.stringify(n && n.body));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) { console.log('\n   FAILED: ' + fails.join('; ')); process.exit(1); }
})().catch(e => { console.log('  FAIL  threw: ' + (e && e.stack || e)); process.exit(1); });
