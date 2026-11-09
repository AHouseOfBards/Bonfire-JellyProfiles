/*
 * The dashboard's first-time setup section and the Status tab's getting-started list
 * (1.7.2).
 *
 * Runs the real loadOnboarding, syncOnboardingRows and renderGettingStarted out of the
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
if (!['loadOnboarding', 'syncOnboardingRows', 'renderGettingStarted', 'gettingStartedItems'].every(has)) {
    ok('this dashboard has first-time setup and the getting-started list', false);
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
const source = L.dashboardI18n(script)
    + L.extractFunction(script, 'field') + '\n'
    + L.extractFunction(script, 'loadOnboarding') + '\n'
    + L.extractFunction(script, 'syncOnboardingRows') + '\n'
    + L.extractFunction(script, 'gettingStartedItems') + '\n'
    + L.extractFunction(script, 'renderGettingStarted') + '\n';

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
        gettingStartedBlock: el(), gettingStarted: el()
    };
    const shows = STEPS.map(s => el({ 'data-step': s }));
    const reqs = STEPS.map(s => el({ 'data-step': s }));
    return {
        ids, shows, reqs,
        querySelector(sel) {
            if (sel.charAt(0) === '#') return ids[sel.slice(1)] || null;
            const m = /^\.jpfObRequired\[data-step="(\w+)"\]$/.exec(sel);
            if (m) return reqs[STEPS.indexOf(m[1])] || null;
            throw new Error('no ' + sel);
        },
        querySelectorAll(sel) {
            if (sel === '.jpfObShow') return shows;
            if (sel === '.jpfObRequired') return reqs;
            throw new Error('no ' + sel);
        }
    };
}

const gone = [];
const document = { createElement: () => el() };
const goTo = (page, tab, anchor) => gone.push(tab + '#' + (anchor || ''));
// eslint-disable-next-line no-new-func
const api = new Function('document', 'goTo',
    source + '; return { loadOnboarding, syncOnboardingRows, renderGettingStarted };')(document, goTo);

console.log('\n── First-time setup, as loaded ─────────────────────────────────');

let page = makePage();
api.loadOnboarding(page, {});
ok('a configuration that never chose shows every step', page.shows.every(b => b.checked));
ok('and requires none, and is off', page.reqs.every(b => !b.checked && !b.disabled) && !page.ids.onboardingEnabled.checked);

page = makePage();
api.loadOnboarding(page, { Enabled: true, StepsChosen: true, Steps: ['welcome', 'theme'],
                           RequiredSteps: ['welcome', 'mainPin'], WelcomeText: 'Hello' });
ok('a chosen set shows only those steps',
   page.shows.map(b => b.checked).join() === 'true,false,false,false,true');
ok("a required step that isn't shown is unticked and greyed",
   page.reqs[1].checked === false && page.reqs[1].disabled === true);
ok('a required step that is shown stays ticked', page.reqs[0].checked === true && page.reqs[0].disabled === false);
ok('on, with its welcome', page.ids.onboardingEnabled.checked && page.ids.onboardingWelcome.value === 'Hello');

page = makePage();
api.loadOnboarding(page, { StepsChosen: true, Steps: [] });
ok('an empty choice shows nothing (not "everything")', page.shows.every(b => !b.checked));

page.shows[2].checked = true;
api.syncOnboardingRows(page);
ok('ticking Show makes Can\'t skip available again', page.reqs[2].disabled === false);

console.log('\n── Getting started ─────────────────────────────────────────────');

page = makePage();
page._config = { RequireMainPin: false, EnableStarterAvatars: false, AvatarLibrary: [],
                 EnableClientPinLogin: false, EnableClientProfileList: false, ProxyClients: [],
                 Onboarding: { Enabled: false } };
api.renderGettingStarted(page);
const rows = () => page.ids.gettingStarted.children;
const row = key => rows().filter(r => r.getAttribute('data-gs') === key)[0];
ok('what is not known yet is left out: no profile list, no emergency status',
   !row('profiles') && !row('emergency'));
ok('a fresh install lists the rest as to do',
   ['pins', 'avatars', 'onboarding', 'apps'].every(k => row(k) && row(k).getAttribute('data-level') === 'info'));
ok('the list is shown', !page.ids.gettingStartedBlock.classList.contains('jpf-hidden'));
ok('each to-do opens where it is fixed', row('onboarding').children.length === 3);
row('onboarding').children[2].listeners.click();
ok('First-time setup opens Setup at its section', gone.pop() === 'setup#jpfOnboarding');
row('apps').children[2].listeners.click();
ok('Apps opens the Apps tab', gone.pop() === 'apps#');

page._mappings = { subProfiles: [] };
page._panicConfigured = false;
api.renderGettingStarted(page);
ok('once known, no profiles is a to-do with nothing to open (households make them)',
   row('profiles') && row('profiles').getAttribute('data-level') === 'info' && row('profiles').children.length === 2);
ok('and the emergency code joins the list', row('emergency') && row('emergency').getAttribute('data-level') === 'info');

page._config.EnableStarterAvatars = true;
page._config.Onboarding = { Enabled: true };
page._config.ProxyClients = ['moonfin'];
api.renderGettingStarted(page);
ok('it ticks itself from the settings: avatars, setup, apps',
   ['avatars', 'onboarding', 'apps'].every(k => row(k).getAttribute('data-level') === 'ok'));
ok('a ticked row has nothing to open', row('avatars').children.length === 2);
ok('a starter-less library with pictures counts too', (function () {
    page._config.EnableStarterAvatars = false;
    page._config.AvatarLibrary = [{ Id: 'a' }];
    api.renderGettingStarted(page);
    return row('avatars').getAttribute('data-level') === 'ok';
})());

page._config.RequireMainPin = true;
page._mappings = { SubProfiles: [{}] };
page._panicConfigured = true;
api.renderGettingStarted(page);
ok('all done: the list goes away', page.ids.gettingStartedBlock.classList.contains('jpf-hidden'));

console.log('\n── Wiring ──────────────────────────────────────────────────────');
ok('the Status tab carries the list', /id="gettingStartedBlock"/.test(html) && /id="gettingStarted"/.test(html));
ok('Setup carries the section the list points at', /id="jpfOnboarding"/.test(html));
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
