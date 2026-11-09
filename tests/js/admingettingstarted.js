/*
 * The dashboard's first-time setup section and the Status tab's getting-started list
 * (1.7.2).
 *
 * Runs the real loadOnboarding and renderGettingStarted out of the
 * dashboard against stub elements, and reads back what they set and drew. Red against
 * the dashboard at cd9160c, which had neither.
 *
 *     node tests/js/admingettingstarted.js "" /path/to/old/profilesDashboard.html
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
const has = name => new RegExp('function ' + name + '\\s*\\(').test(script);
if (!['loadOnboarding', 'renderGettingStarted', 'gettingStartedItems'].every(has)) {
    ok('this dashboard has first-time setup and the getting-started list', false);
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
const source = L.dashboardI18n(script)
    + L.extractFunction(script, 'field') + '\n'
    + L.extractFunction(script, 'loadOnboarding') + '\n'
    + (has('isUtcZone') ? L.extractFunction(script, 'isUtcZone') + '\n' : '')
    + L.extractFunction(script, 'gettingStartedItems') + '\n'
    + L.extractFunction(script, 'renderGettingStarted') + '\n'
    // Only on a dashboard that asks; an older one fails the assertions below instead of here.
    + (has('onboardingAskDue') ? L.extractFunction(script, 'onboardingAskDue') + '\n' : '')
    + (has('answerOnboardingAsk') ? L.extractFunction(script, 'answerOnboardingAsk') + '\n' : '');

// ── stub elements ────────────────────────────────────────────────────────────
function el(attrs) {
    const node = {
        attrs: attrs || {}, children: [], textContent: '', value: '', checked: false, disabled: false,
        listeners: {},
        _classes: new Set(),
        classList: {
            toggle(c, on) { if (on) node._classes.add(c); else node._classes.delete(c); },
            contains(c) { return node._classes.has(c); }
        },
        getAttribute: k => (k in node.attrs ? node.attrs[k] : null),
        setAttribute: (k, v) => { node.attrs[k] = String(v); },
        appendChild: c => { node.children.push(c); },
        addEventListener: (t, fn) => { node.listeners[t] = fn; }
    };
    Object.defineProperty(node, 'textContent', {
        get: () => node._text || '',
        set: v => { node._text = v; if (v === '') node.children = []; }
    });
    return node;
}

const STEPS = ['welcome', 'mainPin', 'firstProfile', 'switcher', 'theme'];
function makePage() {
    const ids = {
        onboardingEnabled: el(), onboardingWelcome: el(),
        gettingStartedBlock: el(), gettingStarted: el(), onboardingAsk: el()
    };
    const shows = STEPS.map(s => el({ 'data-step': s }));
    // No "can't skip" boxes: a page that still looked for them would throw here.
    return {
        ids, shows,
        querySelector(sel) {
            if (sel.charAt(0) === '#') return ids[sel.slice(1)] || null;
            throw new Error('no ' + sel);
        },
        querySelectorAll(sel) {
            if (sel === '.jpfObShow') return shows;
            throw new Error('no ' + sel);
        }
    };
}

const gone = [];
const document = { createElement: () => el() };
const goTo = (page, tab, anchor) => gone.push(tab + '#' + (anchor || ''));
const posted = [];
const ApiClient = {
    getUrl: u => '/' + u,
    ajax: o => { posted.push(JSON.parse(o.data)); return Promise.resolve({}); }
};
const Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert() {} };
// eslint-disable-next-line no-new-func
const api = new Function('document', 'goTo', 'ApiClient', 'Dashboard',
    source + '; return { loadOnboarding, renderGettingStarted,'
    + ' onboardingAskDue: typeof onboardingAskDue === "function" ? onboardingAskDue : null,'
    + ' answerOnboardingAsk: typeof answerOnboardingAsk === "function" ? answerOnboardingAsk : null };')(document, goTo, ApiClient, Dashboard);

console.log('\n── First-time setup, as loaded ─────────────────────────────────');

let page = makePage();
api.loadOnboarding(page, {});
ok('a configuration that never chose shows every step', page.shows.every(b => b.checked));
ok('and is off', !page.ids.onboardingEnabled.checked);

page = makePage();
// A stored RequiredSteps from an older version rides along and is ignored.
api.loadOnboarding(page, { Enabled: true, StepsChosen: true, Steps: ['welcome', 'theme'],
                           RequiredSteps: ['welcome', 'mainPin'], WelcomeText: 'Hello' });
ok('a chosen set shows only those steps',
   page.shows.map(b => b.checked).join() === 'true,false,false,false,true');
ok('on, with its welcome', page.ids.onboardingEnabled.checked && page.ids.onboardingWelcome.value === 'Hello');

page = makePage();
api.loadOnboarding(page, { StepsChosen: true, Steps: [] });
ok('an empty choice shows nothing (not "everything")', page.shows.every(b => !b.checked));

console.log('\n── Getting started ─────────────────────────────────────────────');

page = makePage();
page._config = { RequireMainPin: false, EnableStarterAvatars: false, AvatarLibrary: [],
                 EnableClientPinLogin: false, EnableClientProfileList: false, ProxyClients: [],
                 Onboarding: { Enabled: false } };
api.renderGettingStarted(page);
const rows = () => page.ids.gettingStarted.children;
const row = key => rows().filter(r => r.getAttribute('data-gs') === key)[0];
ok('what is not known yet is left out: no profile list', !row('profiles'));
ok('a fresh install lists the main PIN as to do', row('pins') && row('pins').getAttribute('data-level') === 'info');
ok('the list is shown', !page.ids.gettingStartedBlock.classList.contains('jpf-hidden'));
ok('the main PIN opens where it is fixed', row('pins').children.length === 3);
row('pins').children[2].listeners.click();
ok('and goes to Security at the PINs', gone.pop() === 'security#jpfPins');

page._mappings = { subProfiles: [] };
page._panicConfigured = false;
api.renderGettingStarted(page);
ok('once known, no profiles is a to-do with nothing to open (households make them)',
   row('profiles') && row('profiles').getAttribute('data-level') === 'info' && row('profiles').children.length === 2);

// P2-301, P2-302: only what a new install needs. Each of these was a to-do that was never one.
ok('no emergency code, though none is set (it bypasses the picker; Security says so)', !row('emergency'));
ok('no avatars item, which was done on every install', !row('avatars'));
ok('no first-time setup item: it is asked once instead', !row('onboarding'));
ok('no apps item: a choice, not a chore', !row('apps'));
ok('exactly the profile and the PIN', rows().length === 2, rows().map(r => r.getAttribute('data-gs')).join());

// The time zone item: only a server on UTC with no default chosen is asked.
['Europe/London', 'America/New_York', '', undefined].forEach(function (zone) {
    page._serverZone = zone;
    api.renderGettingStarted(page);
    ok('a server on ' + JSON.stringify(zone) + ' is not asked for a zone', !row('zone') && rows().length === 2);
});
['UTC', 'Etc/UTC'].forEach(function (zone) {
    page._serverZone = zone;
    page._config.DefaultTimeZone = '';
    api.renderGettingStarted(page);
    ok(zone + ' with no default: a to-do that opens the viewing hours',
       row('zone') && row('zone').getAttribute('data-level') === 'info' && rows().length === 3);
    row('zone') && row('zone').children[2] && row('zone').children[2].listeners.click();
    ok('and goes to Setup at the zone', gone.pop() === 'setup#jpfViewing');
    page._config.DefaultTimeZone = 'Europe/Paris';
    api.renderGettingStarted(page);
    ok(zone + ' with a default chosen: ticked', row('zone') && row('zone').getAttribute('data-level') === 'ok');
});
page._serverZone = undefined;
page._config.DefaultTimeZone = '';

page._config.RequireMainPin = true;
page._mappings = { SubProfiles: [{}] };
api.renderGettingStarted(page);
ok('all done: the list goes away', page.ids.gettingStartedBlock.classList.contains('jpf-hidden'));

console.log('\n── The first-time setup question ───────────────────────────────');

const askShown = p => !p.ids.onboardingAsk.classList.contains('jpf-hidden');
function fresh(over) {
    const p = makePage();
    p._config = { RequireMainPin: false, Onboarding: Object.assign({}, over || {}) };
    p._mappings = { subProfiles: [] };
    return p;
}
page = fresh();
api.renderGettingStarted(page);
ok('a new install is asked', askShown(page));
page = fresh(); page._mappings = null;
api.renderGettingStarted(page);
ok('not before the profile list is known', !askShown(page));
page = fresh({ Asked: true });
api.renderGettingStarted(page);
ok('not again once answered', !askShown(page));
page = fresh({ Enabled: true });
api.renderGettingStarted(page);
ok('not when it is already on', !askShown(page));
page = fresh({ StepsChosen: true });
api.renderGettingStarted(page);
ok('not when the administrator has already chosen steps', !askShown(page));
page = fresh(); page._mappings = { subProfiles: [{}] };
api.renderGettingStarted(page);
ok('not on an install that already has profiles', !askShown(page));

page = fresh();
api.renderGettingStarted(page);
api.answerOnboardingAsk && api.answerOnboardingAsk(page, true);
ok('yes turns it on and records the answer', posted.length === 1 && posted[0].onboardingAsked === true && posted[0].onboardingEnabled === true,
   JSON.stringify(posted));
const settle = () => new Promise(r => setImmediate(r));
(async function () {
    await settle(); await settle();
    ok('and the question goes away, with the box ticked', !askShown(page) && page.ids.onboardingEnabled.checked === true);
    posted.length = 0;
    page = fresh();
    api.renderGettingStarted(page);
    api.answerOnboardingAsk && api.answerOnboardingAsk(page, false);
    ok('no records the answer and turns nothing on', posted.length === 1 && posted[0].onboardingAsked === true && !('onboardingEnabled' in posted[0]),
       JSON.stringify(posted));
    await settle(); await settle();
    ok('and the question goes away, the box left off', !askShown(page) && !page.ids.onboardingEnabled.checked);
    finish();
})();

function finish() {
console.log('\n── Wiring ──────────────────────────────────────────────────────');
ok('the Status tab carries the list', /id="gettingStartedBlock"/.test(html) && /id="gettingStarted"/.test(html));
ok('Setup carries the first-time setup section', /id="jpfOnboarding"/.test(html));
ok('the Status tab carries the question, with both answers', /id="onboardingAsk"/.test(html)
   && /id="onboardingAskYes"/.test(html) && /id="onboardingAskNo"/.test(html));
ok('saving, loading and the two status loads all redraw it',
   /noteSavedSettings\(page, sent\)/.test(script)
   && (script.match(/renderGettingStarted\(page\)/g) || []).length >= 4);

    console.log('');
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
}
