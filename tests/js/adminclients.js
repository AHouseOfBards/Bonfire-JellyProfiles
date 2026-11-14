/*
 * The admin tab for other Jellyfin client apps is "Clients", and the page can be opened on
 * a tab and a setting from the link:
 *
 *     /web/#/configurationpage?name=Bonfire&tab=clients&setting=pinLoginFromAnywhere
 *
 * Another plugin builds that link, so the two parameters are API. Runs the real initTabs
 * against a stubbed page. Red against the dashboard before the rename:
 *
 *     node tests/js/adminclients.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);

console.log('\n-- The tab name -------------------------------------------------');
const tabNames = [];
const re = /<button[^>]*class="jpf-tab"[^>]*data-tab="(\w+)"[^>]*data-i18n="([^"]+)"[^>]*>([^<]*)</g;
let m;
const labels = {};
while ((m = re.exec(html))) { tabNames.push(m[1]); labels[m[1]] = m[3]; }
ok('the tab is data-tab="clients"', tabNames.indexOf('clients') >= 0);
ok('there is no "apps" tab', tabNames.indexOf('apps') < 0);
ok('its label reads Clients', labels.clients === 'Clients', labels.clients);
ok('its panel is data-panel="clients"', /data-panel="clients"/.test(html) && !/data-panel="apps"/.test(html));
ok('the tab and the panel point at each other',
   /id="jpfTabClients"[^>]*aria-controls="jpfPanelClients"/.test(html)
   && /id="jpfPanelClients"[^>]*aria-labelledby="jpfTabClients"/.test(html));
ok('the English key for the label is admin.tab.clients', /data-i18n="admin\.tab\.clients"/.test(html));

console.log('\n-- A page to run initTabs on -------------------------------------');
const NAMES = ['status', 'households', 'security', 'look', 'setup', 'clients', 'messages'];
function pageWith(names, hash, settingIds) {
    const focused = [], scrolled = [], queries = [];
    function tabEl(name) {
        return {
            attrs: { 'data-tab': name, 'data-panel': name }, hidden: false, classList: { toggle() {} },
            getAttribute(k) { return this.attrs[k]; }, setAttribute(k, v) { this.attrs[k] = v; },
            addEventListener() {}, dataset: {}
        };
    }
    const tabEls = names.map(tabEl), panelEls = names.map(tabEl);
    const fold = { open: false };
    const control = {
        id: 'clientPinLoginFromAnywhere',
        closest(sel) { return sel === 'details.jpf-advanced' ? fold : null; },
        scrollIntoView() { scrolled.push('control'); }, focus() { focused.push('control'); }
    };
    return {
        dataset: {}, tabEls, panelEls, fold, focused, scrolled, queries,
        querySelectorAll(sel) { return sel === '.jpf-tab' ? tabEls : sel === '.jpf-tab-panel' ? panelEls : []; },
        querySelector(sel) { queries.push(sel); return sel === '#clientPinLoginFromAnywhere' ? control : null; },
        shownPanel() { return panelEls.filter(p => !p.hidden).map(p => p.attrs['data-panel']).join(); }
    };
}
function open(stored, hash) {
    const store = { 'bonfire-admin-tab': stored };
    const localStorage = { getItem: k => (k in store ? store[k] : null), setItem: (k, v) => { store[k] = v; } };
    const location = { hash: hash || '' };
    const src = L.extractFunction(script, 'initTabs') + '; return initTabs;';
    const openAdv = script.indexOf('function openAdvancedFor') !== -1 ? L.extractFunction(script, 'openAdvancedFor') : '';
    const initTabs = new Function('localStorage', 'location', openAdv + src)(localStorage, location);
    const page = pageWith(NAMES);
    initTabs(page);
    return { page, panel: page.shownPanel(), stored: store['bonfire-admin-tab'] };
}
const link = '#/configurationpage?name=Bonfire';

console.log('\n-- A stored "apps" -----------------------------------------------');
ok('a browser that last had Apps open lands on Clients', open('apps').panel === 'clients');
ok('and remembers Clients from then on', open('apps').stored === 'clients');
ok('a stored tab that exists is still opened', open('setup').panel === 'setup');
ok('a stored name that never existed falls back to the first tab', open('nonsense').panel === 'status');

console.log('\n-- tab and setting from the link ---------------------------------');
ok('tab=clients opens Clients', open('', link + '&tab=clients').panel === 'clients');
ok('a known tab in the link wins over the stored one', open('look', link + '&tab=clients').panel === 'clients');
ok('without tab, the stored tab stands', open('look', link).panel === 'look');
ok('tab alone does not focus anything', open('', link + '&tab=clients').page.focused.length === 0);
{
    const r = open('look', link + '&tab=clients&setting=pinLoginFromAnywhere');
    ok('setting=pinLoginFromAnywhere focuses its control', r.page.focused.length === 1);
    ok('and scrolls it into view', r.page.scrolled.length === 1);
    ok('and opens the Advanced fold it sits in', r.page.fold.open === true);
    ok('on the Clients panel', r.panel === 'clients');
}
ok('the tab parameter can come second', open('', '#/configurationpage?setting=pinLoginFromAnywhere&name=Bonfire&tab=clients').panel === 'clients');

console.log('\n-- Hostile or unknown values do nothing ---------------------------');
const hostile = [
    ['a selector as the tab', '&tab=' + encodeURIComponent('clients"],body[x="')],
    ['a script as the tab', '&tab=' + encodeURIComponent('<script>alert(1)</script>')],
    ['an inherited name as the tab', '&tab=constructor'],
    ['an unknown tab', '&tab=nonsense'],
    ['an empty tab', '&tab=']
];
hostile.forEach(function (h) {
    const r = open('look', link + h[1]);
    ok(h[0] + ' leaves the stored tab open', r.panel === 'look', r.panel);
});
const hostileSettings = [
    ['a selector as the setting', '&setting=' + encodeURIComponent('#clientPinLoginFromAnywhere, body')],
    ['a script as the setting', '&setting=' + encodeURIComponent('<img src=x onerror=alert(1)>')],
    ['an inherited name as the setting', '&setting=constructor'],
    ['__proto__ as the setting', '&setting=__proto__'],
    ['an unknown setting', '&setting=nonsense'],
    ['a bad escape', '&setting=%E0%A4%A']
];
hostileSettings.forEach(function (h) {
    let r = null, threw = false;
    try { r = open('look', link + '&tab=clients' + h[1]); } catch (e) { threw = true; }
    ok(h[0] + ' focuses nothing and does not throw', !threw && r.page.focused.length === 0 && r.page.scrolled.length === 0);
    ok(h[0] + ' queries nothing built from the link',
       !threw && r.page.queries.every(q => /^#[A-Za-z]+$/.test(q) && q === '#clientPinLoginFromAnywhere'));
});
ok('a bad escape in the tab does not throw', (function () { try { open('', link + '&tab=%E0%A4%A'); return true; } catch (e) { return false; } })());

console.log('\n-- pass ' + pass + ', fail ' + fails.length);
process.exit(fails.length ? 1 : 0);
