/*
 * "Reopen automatically at this time" on the Maintenance form.
 *
 * Runs the real renderMaintenance and saveMaintenance against stub elements. The server does
 * the reopening (tests/cs/maintenance); what the page must do is send the tick, show the
 * state back, never default it on, and refuse a tick with no time before it closes anything.
 * Red against the dashboard at f3a9680, which has no such box.
 *
 *     node tests/js/adminmaintreopen.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);

console.log('\n── The box ─────────────────────────────────────────────────────');
const box = /<input[^>]*id="mntReopen"[^>]*>/.exec(html);
ok('there is a Reopen automatically tick box', !!box);
ok('it is off by default', !!box && !/\schecked/.test(box[0]));
ok('it sits with Back by, before Stop what people are watching',
   !!box && html.indexOf('id="mntUntil"') < html.indexOf('id="mntReopen"') && html.indexOf('id="mntReopen"') < html.indexOf('id="mntStop"'));
ok('and carries its words through the translations, with no hint repeating what the label says', /data-i18n="admin\.mnt\.reopen"/.test(html) && !/data-i18n="admin\.mnt\.reopenHint"/.test(html));

if (!/function renderMaintenance\s*\(/.test(script) || !box) {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

const source = L.dashboardI18n(script) + '\n'
    + L.extractFunction(script, 'field') + '\n'
    + L.extractFunction(script, 'renderMaintenance') + '\n'
    + L.extractFunction(script, 'saveMaintenance') + '\n';

function el(init) {
    const e = Object.assign({ value: '', checked: false, textContent: '', className: '', _hidden: false }, init || {});
    e.classList = { toggle(c, on) { if (c === 'jpf-hidden') e._hidden = !!on; }, contains(c) { return c === 'jpf-hidden' && e._hidden; }, add() {}, remove() {} };
    return e;
}

function world() {
    const els = {};
    ['#mntStatus', '#mntMessage', '#mntUntil', '#mntStop', '#mntReopen', '#mntClose', '#mntOpen'].forEach(s => { els[s] = el(); });
    const posts = [];
    const shown = [];
    const page = { querySelector: s => els[s] || null };
    const run = new Function('page', 'ApiClient', 'isoToLocalInput', 'localInputToIso', 'formatWhen', 'setStatus', 'showMaintenanceResult', 'errorText',
        source + '; return { renderMaintenance, saveMaintenance };');
    const api = run(page,
        { getUrl: u => '/' + u, ajax: o => { posts.push(JSON.parse(o.data)); return { then: f => { f({}); return { then() {} }; } }; } },
        v => (v ? String(v).replace('Z', '') : ''), v => (v ? v + 'Z' : null), v => 'WHEN ' + v, () => {},
        (p, msg, good) => shown.push({ msg: msg, good: good }), () => ({ then() {} }));
    return { els, posts, shown, page, api };
}

console.log('\n── Showing what is saved ───────────────────────────────────────');
let w = world();
w.api.renderMaintenance(w.page, { enabled: true, untilUtc: '2026-11-12T03:00:00Z', reopenAutomatically: true, startedUtc: '2026-11-11T20:00:00Z', message: 'x' });
ok('a closure set to reopen shows the tick', w.els['#mntReopen'].checked === true);
ok('and says it reopens automatically, not "back by"', /reopens automatically/i.test(w.els['#mntStatus'].textContent) && !/Back by/.test(w.els['#mntStatus'].textContent),
   w.els['#mntStatus'].textContent);
w = world();
w.api.renderMaintenance(w.page, { enabled: true, untilUtc: '2026-11-12T03:00:00Z', reopenAutomatically: false, startedUtc: '2026-11-11T20:00:00Z', message: 'x' });
ok('a closure with only a time says back by', /Back by/.test(w.els['#mntStatus'].textContent) && w.els['#mntReopen'].checked === false,
   w.els['#mntStatus'].textContent);
w = world();
w.els['#mntReopen'].checked = true;
w.api.renderMaintenance(w.page, { enabled: false });
ok('an open server clears the tick', w.els['#mntReopen'].checked === false);

console.log('\n── Sending it ──────────────────────────────────────────────────');
w = world();
w.els['#mntUntil'].value = '2026-11-12T03:00';
w.els['#mntReopen'].checked = true;
w.api.saveMaintenance(w.page, true);
ok('closing with the tick and a time sends it', w.posts.length === 1 && w.posts[0].reopenAutomatically === true && !!w.posts[0].untilUtc, JSON.stringify(w.posts));
w = world();
w.els['#mntUntil'].value = '2026-11-12T03:00';
w.api.saveMaintenance(w.page, true);
ok('without the tick it sends false: a time alone is only shown', w.posts.length === 1 && w.posts[0].reopenAutomatically === false, JSON.stringify(w.posts));
w = world();
w.els['#mntReopen'].checked = true;
w.api.saveMaintenance(w.page, true);
ok('the tick with no time sends nothing, and says why', w.posts.length === 0 && w.shown.length === 1 && w.shown[0].good === false, JSON.stringify(w.shown));
w = world();
w.els['#mntUntil'].value = '2026-11-12T03:00';
w.els['#mntReopen'].checked = true;
w.api.saveMaintenance(w.page, false);
ok('reopening by hand sends no tick', w.posts.length === 1 && w.posts[0].reopenAutomatically === false && w.posts[0].enabled === false, JSON.stringify(w.posts));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
