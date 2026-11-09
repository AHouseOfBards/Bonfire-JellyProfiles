// First-time setup in the web client (1.7.2).
//
// Runs the real loadSwitcherPrefs, maybeStartOnboarding, showProfileOverlay and every
// step against a stub DOM and a recording fetch, then reads back what was drawn and
// what was sent. Red against 870369d, which has no setup in the client at all.
//
//   node tests/js/onboarding.js [path/to/profiles.js]

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

if (typeof PP.maybeStartOnboarding !== 'function' || typeof PP.showOnboardingStep !== 'function') {
    ok('this build has first-time setup in the client', false);
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

const STEPS = [
    { id: 'welcome', required: false, done: false },
    { id: 'mainPin', required: true, done: false },
    { id: 'firstProfile', required: false, done: false },
    { id: 'email', required: true, done: false },      // a newer server's step
    { id: 'switcher', required: false, done: false },
    { id: 'theme', required: false, done: false }
];
let onboarding = { pending: true, generation: 1, welcomeText: 'Hi <b>all</b>\nPopcorn is in the kitchen.', steps: STEPS };
let prefs = { askOnStartup: true, switcherLocation: 'button', masterUserId: 'main', onboardingPending: false,
              theme: 'bonfire', defaultTheme: 'bonfire', allowProfileThemes: true };

env.setRespond((url, body) => {
    if (url === 'plugins/profiles/preferences' && !body) return { status: 200, body: prefs };
    if (url === 'plugins/profiles/preferences') return { status: 200, body: body };
    if (url === 'plugins/profiles/onboarding') return { status: 200, body: onboarding };
    if (url === 'plugins/profiles/theme') return { status: 200, body: { theme: body.theme || 'bonfire', stored: body.theme } };
    if (url === 'plugins/profiles/create' && body.profileName === 'Taken') return { status: 400, body: 'A user with this name already exists.', headers: { 'X-Bonfire-Error': 'name_taken' } };
    return { status: 200, body: {} };
});

// What the overlay would do, recorded: the grid is drawn as one marker element.
const calls = { removed: 0, grids: 0 };
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.renderOverlayContent = function () { calls.grids++; content.innerHTML = '<div id="the-grid"></div>'; };
PP._revealPage = () => {};
PP.fetchSharedFormData = () => Promise.resolve({});
PP.fetchAndRenderProfiles = function () { this.showProfileOverlay(this.currentProfiles); };
PP.removeProfileOverlay = () => { calls.removed++; };
PP.handleSessionExpired = () => {};
PP.checkRoute = () => {};
PP.currentProfiles = [
    { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: false, theme: null }
];

const sent = url => posts.filter(p => p.url === url);
const id = sel => content.querySelector(sel);
const html = () => content.innerHTML;

(async () => {
    console.log('\n── Only when the server says so ────────────────────────────────');

    PP.loadSwitcherPrefs();
    await settle();
    ok('not pending: /onboarding is never asked', sent('plugins/profiles/onboarding').length === 0);

    prefs.onboardingPending = true;
    PP._switcherPrefsLoading = false;
    PP.loadSwitcherPrefs();
    await settle();
    ok('pending: /onboarding is asked once', sent('plugins/profiles/onboarding').length === 1);
    PP._switcherPrefsLoading = false;
    PP.loadSwitcherPrefs();
    await settle();
    ok('and not again on the same page', sent('plugins/profiles/onboarding').length === 1);

    console.log('\n── Welcome ─────────────────────────────────────────────────────');
    ok('it opens the overlay and replaces the grid before it is seen',
       calls.grids === 1 && !id('#the-grid') && html().indexOf('onboarding.welcomeTitle') === -1
       && html().indexOf('Welcome to Bonfire') !== -1);
    // The administrator's own welcome text was cut: the screen is always Bonfire's. A server
    // that still sends one (above) is ignored, not drawn.
    ok("a welcome text from the server is not drawn", html().indexOf('Popcorn') === -1 && html().indexOf('all</b>') === -1
       && html().indexOf('&lt;b&gt;all') === -1);
    ok('the welcome has no Skip', !id('#onboarding-skip') && !!id('#onboarding-next'));

    // A grid that arrives late must not paint over the step.
    PP.showProfileOverlay(PP.currentProfiles);
    ok('a late grid is replaced by the step it would have covered', !id('#the-grid') && html().indexOf('Welcome to Bonfire') !== -1);

    id('#onboarding-next').click();

    console.log('\n── Main PIN ────────────────────────────────────────────────────');
    ok('the PIN step is the account PIN screen', !!id('#main-pin-new'));
    ok('a step an older server marks required still has Not now', !!id('#main-pin-skip'));
    id('#main-pin-new').value = '1234';
    id('#main-pin-save').click();
    await settle();
    const acct = sent('plugins/profiles/account')[0];
    ok('Set PIN saves it to /account', acct && acct.body.pin === '1234');

    console.log('\n── First profile ───────────────────────────────────────────────');
    ok('then the first profile', !!id('#onboarding-profile-name'));
    ok('numbered among the steps after the welcome, unknown ones left out',
       html().indexOf('Step 2 of 4') !== -1, (/Step \d of \d/.exec(html()) || [''])[0]);
    ok('with Skip, as it is not required', !!id('#onboarding-skip'));
    id('#onboarding-next').click();
    await settle();
    ok('an empty name is refused without a request',
       sent('plugins/profiles/create').length === 0 && id('#onboarding-error').textContent !== '');
    id('#onboarding-profile-name').value = 'Taken';
    id('#onboarding-next').click();
    await settle();
    // Replaced behaviour: a refusal used to be shown in the server's English words; it is said by its X-Bonfire-Error code now.
    ok('the refusal is shown on the same step, by its code',
       !!id('#onboarding-profile-name') && id('#onboarding-error').textContent === 'That name is taken. Choose another.');
    id('#onboarding-profile-name').value = 'Kids';
    id('#onboarding-next').click();
    await settle();
    const created = sent('plugins/profiles/create').pop();
    ok('Create sends the name and the PIN set a step ago',
       created.body.profileName === 'Kids' && created.body.masterPin === '1234' && !!created.body.avatarColor);

    console.log('\n── Switcher ────────────────────────────────────────────────────');
    ok('then the switcher', !!id('#onboarding-ask'));
    const menuCard = content.querySelectorAll('.switcher-location-option').filter(c => c.getAttribute('data-location') === 'menu')[0];
    menuCard.click();
    id('#onboarding-ask').checked = false;
    id('#onboarding-next').click();
    await settle();
    const pref = sent('plugins/profiles/preferences').filter(p => p.body).pop();
    ok('Next saves both answers', pref && pref.body.askOnStartup === false && pref.body.switcherLocation === 'menu');
    ok('with the main PIN', pref && pref.init.headers && pref.init.headers['X-Bonfire-Master-Pin'] === '1234');

    console.log('\n── Theme ───────────────────────────────────────────────────────');
    ok('then the theme', content.querySelectorAll('.jpf-theme-option').length > 1);
    const harbor = content.querySelectorAll('.jpf-theme-option').filter(o => o.getAttribute('data-id') === 'harbor')[0];
    harbor.click();
    id('#onboarding-next').click();
    await settle();
    const theme = sent('plugins/profiles/theme').pop();
    ok("Next saves the main account's theme",
       theme && theme.body.profileId === 'main' && theme.body.theme === 'harbor' && theme.body.masterPin === '1234');

    console.log('\n── Done ────────────────────────────────────────────────────────');
    ok('the end is recorded on the server', sent('plugins/profiles/onboarding/done').length === 1);
    ok('the setup lets go of the overlay and of the PIN', PP._onboarding === null && PP.masterPin === null);
    ok('not asked on startup any more: straight into Jellyfin', calls.removed === 1);

    console.log('\n── What is already true is passed over ─────────────────────────');
    PP._onboardingChecked = false;
    onboarding = { pending: true, welcomeText: '', steps: [
        { id: 'mainPin', required: false, done: true },
        { id: 'firstProfile', required: true, done: false }
    ] };
    PP.maybeStartOnboarding();
    await settle();
    ok('a PIN already set skips the PIN step', !id('#main-pin-new') && !!id('#onboarding-profile-name'));
    // "Can't skip" was cut; a flag an older server still sends changes nothing.
    ok('a step an older server marks required still has Skip', !!id('#onboarding-skip'));
    ok('and a single numbered step shows no counter', html().indexOf('Step ') === -1);
    PP._onboarding = null;

    PP._onboardingChecked = false;
    onboarding = { pending: false, steps: STEPS };
    const before = calls.grids;
    PP.maybeStartOnboarding();
    await settle();
    ok('not pending in /onboarding: nothing opens', calls.grids === before && PP._onboarding == null);

    console.log('\n── Never from the route poll ───────────────────────────────────');
    const tick = /checkRoute: function[\s\S]*?\r?\n {8}\},\r?\n/.exec(SRC);
    ok('checkRoute does not start or fetch it', tick && !/maybeStartOnboarding|plugins\/profiles\/onboarding/.test(tick[0]));
    ok('and does not take its overlay down', tick && /!this\._onboarding/.test(tick[0]));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) { fails.forEach(f => console.log('   - ' + f)); process.exit(1); }
})().catch(e => { console.error(e); process.exit(1); });
