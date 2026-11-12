/*
 * The admin page's tabs and their Advanced folds (P2-601, P2-303, P2-304, P2-610).
 *
 * Reads the markup panel by panel and runs the real initTabs, openAdvancedFor and
 * showOutcome. Red against the dashboard at f3a9680, which had one Experience tab of about
 * fifty decisions, a Messages tab that held Email, and an Advanced fold in Households that
 * held one field.
 *
 *     node tests/js/admintabs.js "" /path/to/old/profilesDashboard.html
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

// ── the markup, one panel at a time ──────────────────────────────────────────
const panels = {};
const starts = [];
const re = /<div class="jpf-tab-panel"[^>]*data-panel="(\w+)"/g;
let m;
while ((m = re.exec(html))) starts.push({ name: m[1], at: m.index });
starts.forEach((s, i) => { panels[s.name] = html.slice(s.at, i + 1 < starts.length ? starts[i + 1].at : html.indexOf('<script', s.at)); });
const tabs = (html.match(/data-tab="(\w+)"/g) || []).map(x => x.slice(10, -1));

console.log('\n── The strip ───────────────────────────────────────────────────');
ok('Experience is gone and Look and Setup are in its place',
   tabs.indexOf('experience') === -1 && tabs.indexOf('look') !== -1 && tabs.indexOf('setup') !== -1, tabs.join(','));
ok('Look comes before Setup, Setup before Clients', tabs.indexOf('look') < tabs.indexOf('setup') && tabs.indexOf('setup') < tabs.indexOf('clients'));
ok('every tab has a panel and every panel a tab',
   tabs.length === Object.keys(panels).length && tabs.every(n => panels[n]), tabs.join(',') + ' / ' + Object.keys(panels).join(','));

console.log('\n── Where things live ───────────────────────────────────────────');
const has = (tab, id) => !!panels[tab] && panels[tab].indexOf('id="' + id + '"') !== -1;
ok('Setup holds the time zone and first-time setup, and no longer Email (moved to Notices)',
   !has('setup', 'jpfEmail') && !has('setup', 'emailHost') && has('setup', 'defaultTimeZone') && has('setup', 'jpfOnboarding'));
ok('Look holds the switcher, theme and avatar library',
   has('look', 'defaultAskOnStartup') && has('look', 'defaultTheme') && has('look', 'jpfAvatars'));
ok('Notices (the messages panel) keeps Announcements and Maintenance, and now holds Email',
   has('messages', 'annMessage') && has('messages', 'jpfMaintenance') && has('messages', 'jpfEmail') && has('messages', 'emailHost'));
ok('Skip Quick Connect stays in Clients', has('clients', 'skipQuickConnectOnKnownDevices'));
ok('no setting was dropped: every control id the page saves is still in exactly one panel',
   ['maxProfiles', 'pinGuessesPerDay', 'requireMainPin', 'enablePinResetByEmail', 'disallowCustomAvatars', 'enableStarterAvatars',
    'defaultAskOnStartup', 'defaultSwitcherLocation', 'defaultTheme', 'allowProfileThemes', 'richThemeMotion', 'defaultTimeZone',
    'enableClientPinLogin', 'enableClientProfileList', 'clientPinLoginFromAnywhere', 'skipQuickConnectOnKnownDevices',
    'profileImageQuality', 'onboardingEnabled', 'fontUpload', 'themeImport', 'themeNew'].every(id =>
        Object.keys(panels).filter(n => has(n, id)).length === 1));

// The Moonfin picker is beta, and says so where it is switched on and where it is described.
// It stays off by default (an empty ProxyClients list; tests/cs/proxyswitcher).
const pickerBlock = (panels.clients || '').slice((panels.clients || '').indexOf('data-i18n="admin.apps.pickerTitle"'));
ok('the Clients tab marks the profile picker as beta, in one short line about clients supporting profiles themselves',
   /data-i18n="admin\.apps\.pickerTitle">[^<]*\(beta\)</.test(pickerBlock)
   && /data-i18n="admin\.apps\.pickerBeta">[^<]*clients support profiles themselves[^<]*</.test(pickerBlock));
const fs = require('fs'), path = require('path');
const readme = fs.readFileSync(path.join(L.ROOT, 'README.md'), 'utf8');
const clients = fs.readFileSync(path.join(L.ROOT, 'docs', 'clients.md'), 'utf8');
ok('README names the picker as beta and says it may change when native support arrives',
   /Profile picker \(beta\)/.test(readme) && /may change when native support arrives/.test(readme));
ok('docs/clients.md does too, on the picker\'s own section',
   /## Profile picker \(beta\)[\s\S]*?may change when native support arrives/.test(clients.replace(/\s+/g, ' ')));
ok('and its intro calls the picker beta', /Moonfin gets a profile picker \(beta\)/.test(clients.replace(/\s+/g, ' ')));

console.log('\n── Advanced ────────────────────────────────────────────────────');
const folds = n => (panels[n].match(/<details class="jpf-advanced[^"]*"/g) || []).length;
const summaries = n => (panels[n].match(/<details class="jpf-advanced[\s\S]*?<summary([^>]*)>([^<]*)</g) || []);
tabs.forEach(n => ok(n + ' has at most one Advanced fold (' + folds(n) + ')', folds(n) <= 1));
ok('Households has none: its one field moved', folds('households') === 0);
ok('every fold is the same pattern: closed, named "Advanced", from the one key',
   tabs.every(n => summaries(n).every(s => /data-i18n="admin\.advanced\.title">Advanced</.test(s)))
   && !/<details class="jpf-advanced[^>]*\sopen/.test(html));
const advanced = (tab) => {
    const p = panels[tab] || '';
    const i = p.indexOf('<details class="jpf-advanced');
    return i === -1 ? '' : p.slice(i, p.indexOf('</details>', i));
};
const inFold = (tab, id) => advanced(tab).indexOf('id="' + id + '"') !== -1;
ok('PIN tries a day is in Security\'s Advanced', inFold('security', 'pinGuessesPerDay'));
ok('the emergency disable code is in Security\'s Advanced, and the PINs are not',
   ['jpfEmergency', 'panicCodeInput', 'savePanicCode', 'panicCodeResult'].every(id => inFold('security', id))
   && !inFold('security', 'requireMainPin') && !inFold('security', 'enablePinResetByEmail'));
ok('the starter avatars, picture quality and server-folder copy are in Look\'s Advanced',
   ['enableStarterAvatars', 'profileImageQuality', 'avatarScanPath', 'avatarScanBtn'].every(id => inFold('look', id))
   && !inFold('look', 'disallowCustomAvatars') && !inFold('look', 'avatarUploadBtn'));
// Owner, 1.9.0.4 eye test: the "Where profiles are managed" note moves from Status to
// Troubleshoot, with its wording and its keys.
ok('the "Where profiles are managed" note is on Troubleshoot, not Status',
   (panels.troubleshoot || '').indexOf('data-i18n="admin.status.whereTitle"') !== -1
   && (panels.troubleshoot || '').indexOf('data-i18n="admin.status.whereBody"') !== -1
   && (panels.status || '').indexOf('admin.status.where') === -1);
// Owner, 1.9.0.4 eye test: "Save code" then the page Save, stacked after the emergency
// section, read as a duplicate, and the page Save sat after a section it does not save.
// The PIN settings' Save now follows the last PIN setting directly, and the code's own
// button is a secondary one inside its block, so the tab has one primary Save.
const secP = panels.security || '';
const secSave = secP.indexOf('jpf-save-settings');
ok('Security\'s Save follows the last PIN setting, with the emergency code before them both',
   secSave > secP.indexOf('id="trustedBox"') && secP.indexOf('id="trustedBox"') > secP.indexOf('id="savePanicCode"')
   && secP.slice(secP.indexOf('id="trustedBox"'), secSave).indexOf('savePanicCode') === -1);
ok('Security has one primary button, its Save',
   (secP.match(/class="[^"]*button-submit[^"]*"/g) || []).length === 1
   && !/id="savePanicCode"[^>]*button-submit|button-submit[^>]*id="savePanicCode"/.test(secP));
ok('a refused emergency code opens its fold',
   /const show = function \(msg, ok\) \{[^}]*if \(!ok\) openAdvancedFor\(result\);/.test(script));
ok('the theme editor and fonts are in Look\'s Advanced',
   inFold('look', 'themeEditor') && inFold('look', 'themesFonts') && inFold('look', 'fontUpload'));
ok('richer theme motion is in Look\'s Advanced, not in the open', inFold('look', 'richThemeMotion'));
ok('theme import and new theme are still on the page', has('look', 'themeImport') && has('look', 'themeNew'));
ok('announcement dates are in Messages\' Advanced, and the message is not',
   inFold('messages', 'annStarts') && inFold('messages', 'annEnds') && !inFold('messages', 'annMessage'));
ok('the switcher and the default theme are not hidden in a fold', !inFold('look', 'defaultTheme') && !inFold('look', 'defaultAskOnStartup'));

console.log('\n── A stored "experience" ───────────────────────────────────────');
function pageWith(names) {
    const shown = [];
    const el = (name) => ({
        attrs: { 'data-tab': name, 'data-panel': name }, hidden: false, classList: { toggle() {} },
        getAttribute(k) { return this.attrs[k]; }, setAttribute(k, v) { this.attrs[k] = v; },
        addEventListener() {}, dataset: {}
    });
    const tabEls = names.map(el), panelEls = names.map(el);
    return {
        dataset: {}, tabEls, panelEls,
        querySelectorAll(sel) { return sel === '.jpf-tab' ? tabEls : sel === '.jpf-tab-panel' ? panelEls : []; },
        shownPanel() { return panelEls.filter(p => !p.hidden).map(p => p.attrs['data-panel']).join(); }
    };
}
function open(stored, names) {
    const store = { 'bonfire-admin-tab': stored };
    const localStorage = { getItem: k => (k in store ? store[k] : null), setItem: (k, v) => { store[k] = v; } };
    const initTabs = new Function('localStorage', L.extractFunction(script, 'initTabs') + '; return initTabs;')(localStorage);
    const page = pageWith(names);
    initTabs(page);
    return { panel: page.shownPanel(), stored: store['bonfire-admin-tab'] };
}
const names = tabs.length ? tabs : ['status', 'look', 'setup'];
ok('a browser that last had Experience open lands on Look', open('experience', names).panel === 'look');
ok('and remembers Look from then on', open('experience', names).stored === 'look');
ok('a tab that exists is still opened', open('setup', names).panel === 'setup');
ok('a name that never existed still falls back to the first tab', open('nonsense', names).panel === names[0]);

console.log('\n── A failure opens its fold ────────────────────────────────────');
function node(fold) {
    const n = { textContent: '', style: {}, _c: new Set(['jpf-hidden']),
        classList: { remove: c => n._c.delete(c), add: c => n._c.add(c), contains: c => n._c.has(c) },
        closest: sel => (fold && sel === 'details.jpf-advanced' ? fold : null) };
    return n;
}
let api = null;
try {
    api = new Function(L.dashboardI18n(script) + L.extractFunction(script, 'openAdvancedFor') + L.extractFunction(script, 'paintOutcome') + L.extractFunction(script, 'showOutcome')
        + '; return { showOutcome, openAdvancedFor };')();
} catch (e) { api = null; }
ok('the page has the functions that do it', api !== null);
if (api) {
    let fold = { open: false };
    api.showOutcome(node(fold), 'That did not work.', false);
    ok('an error inside a closed fold opens it', fold.open === true);
    fold = { open: false };
    api.showOutcome(node(fold), 'Saved.', true);
    ok('a success does not', fold.open === false);
    const bare = node(null);
    api.showOutcome(bare, 'No fold here.', false);
    ok('a message outside any fold is shown as before', bare.textContent === 'No fold here.' && !bare.classList.contains('jpf-hidden'));
}
const themeStatus = /function showThemeStatus[\s\S]*?\n {12}\}/.exec(script);
ok('theme and font messages go through the same door', !!themeStatus && /showOutcome\(/.test(themeStatus[0]));
ok('a failed announcement post opens the dates fold when it sent dates',
   /openAdvancedFor\(page\.querySelector\('#annStarts'\)\)/.test(script));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
