/*
 * "Turn on Bonfire" on the dashboard's Status tab (2.0).
 *
 *   - The toggle is at the top of the Status tab, labelled "Turn on Bonfire", with no
 *     hint line under it (decided: an explanation was rejected).
 *   - While off, the Status list starts with "Bonfire is off.", above every level; on, it
 *     carries nothing. A configuration from before 2.0 has no BonfireOn and is on.
 *   - Getting started has no item for turning it on: the switch is right there.
 *   - Changing the toggle saves just { bonfireOn } through admin/settings at once, and a
 *     failed save puts the toggle back to what the server has. It is not one of the
 *     fields the page's Save sends, so it never marks the page unsaved.
 *
 * Runs the real functions out of the dashboard. Red against 14c94bb, which has none of it.
 *
 *   node tests/js/adminonoff.js [unused] [path/to/profilesDashboard.html]
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function finish() {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
}

const html = L.readDashboard();
const script = L.dashboardScript(html);

console.log('\n── The toggle ──────────────────────────────────────────────────');
const panel = (/<div class="jpf-tab-panel" id="jpfPanelStatus"[\s\S]*?<!-- \/status -->/.exec(html) || [''])[0];
const box = /<input[^>]*id="bonfireOn"[^>]*>/.exec(panel);
ok('the Status tab has the toggle', !!box && /type="checkbox"/.test(box[0]));
const before = box ? panel.slice(0, box.index) : '';
ok('at the top: before the summary, the list and Getting started',
   !!box && !/id="statusSummary"|id="statusChecks"|id="gettingStartedBlock"/.test(before));
const label = /<input[^>]*id="bonfireOn"[^>]*>\s*<span data-i18n="([^"]+)">([^<]*)<\/span>\s*<\/label>\s*<\/div>/.exec(panel);
ok('labelled "Turn on Bonfire", through a key', !!label && label[2] === 'Turn on Bonfire' && label[1] === 'admin.bonfireOn',
   label ? label[1] + ' ' + label[2] : 'no label');
const afterLabel = label ? panel.slice(label.index + label[0].length) : '';
ok('with no hint line under it', !!label && /^\s*<div class="fieldDescription jpf-lede" id="statusSummary"/.test(afterLabel),
   afterLabel.trim().slice(0, 80));
const savedIds = (/const SAVED_FIELD_IDS = \[([\s\S]*?)\]/.exec(script) || [])[1];
ok('it is not a field the page\'s Save sends', savedIds !== undefined && !/'bonfireOn'/.test(savedIds));

let api;
try {
    const order = /const STATUS_ORDER = [^;]+;/.exec(script)[0];
    const source = L.dashboardI18n(script) + order + '\n'
        + ['field', 'isUtcZone', 'gettingStartedItems', 'renderBonfireOn', 'bindBonfireOn']
            .map(n => L.extractFunction(script, n)).join('\n');
    // eslint-disable-next-line no-new-func
    api = new Function('ApiClient', 'Dashboard', 'setStatus', 'renderGettingStarted',
        source + '; return { STATUS_ORDER, gettingStartedItems, renderBonfireOn, bindBonfireOn, t };');
} catch (e) {
    ok('the dashboard has renderBonfireOn and bindBonfireOn', false, e.message);
    finish();
}

const posted = [];
let answer = () => Promise.resolve({});
const ApiClient = { getUrl: u => '/' + u, ajax: o => { posted.push({ url: o.url, body: JSON.parse(o.data) }); return answer(); } };
const alerts = [];
const Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert: a => alerts.push(a) };
const status = {};
let gsDrawn = 0;
const A = api(ApiClient, Dashboard, (page, part, items) => { status[part] = items; }, () => { gsDrawn++; });

function makePage(config) {
    const toggle = { checked: false, dataset: {}, listeners: {}, addEventListener(t, f) { this.listeners[t] = f; } };
    return { toggle, _config: config, querySelector: sel => (sel === '#bonfireOn' ? toggle : null) };
}

console.log('\n── Status ──────────────────────────────────────────────────────');
ok('the off line has a place in the Status order', A.STATUS_ORDER.indexOf('off') !== -1);
let page = makePage({ BonfireOn: false });
A.renderBonfireOn(page);
ok('off: the toggle reads off', page.toggle.checked === false);
ok('and the Status list says "Bonfire is off."', (status.off || []).length === 1 && status.off[0].text === 'Bonfire is off.',
   JSON.stringify(status.off));
ok('through a key', A.t('admin.status.bonfireOff') === 'Bonfire is off.');
ok('and Getting started is drawn again', gsDrawn === 1);
page = makePage({ BonfireOn: true });
A.renderBonfireOn(page);
ok('on: the toggle reads on, and the line is gone', page.toggle.checked === true && (status.off || []).length === 0);
page = makePage({});
A.renderBonfireOn(page);
ok('a configuration from before 2.0 reads on', page.toggle.checked === true && (status.off || []).length === 0);

console.log('\n── Getting started ─────────────────────────────────────────────');
// The switch is at the top of the tab and Status says so first, so there is no item for it.
const items = c => A.gettingStartedItems({ _config: c, _mappings: { subProfiles: [] } });
[{ BonfireOn: false }, { BonfireOn: true }, {}, { BonfireOn: false, DefaultTimeZone: '' }].forEach(function (c) {
    const list = items(c);
    ok('no "Turn on Bonfire." item for ' + JSON.stringify(c), !list.some(i => i.key === 'bonfireOn' || /Turn on Bonfire/.test(i.text)),
       list.map(i => i.key).join());
});
ok('and no key for it in the strings', A.t('admin.gs.bonfireOn') === 'admin.gs.bonfireOn');

console.log('\n── Status order ────────────────────────────────────────────────');
{
    const order = /const STATUS_ORDER = [^;]+;/.exec(script)[0];
    const rank = /const LEVEL_RANK = [^;]+;/.exec(script)[0];
    const src = L.dashboardI18n(script) + order + '\n' + rank + '\n'
        + ['levelLabel', 'renderStatus'].map(n => L.extractFunction(script, n)).join('\n');
    const kit = new Function('document', 'goTo', src + '; return { renderStatus };');
    const node = () => ({ children: [], attrs: {}, text: '',
        appendChild(c) { this.children.push(c); }, setAttribute(k, v) { this.attrs[k] = v; },
        addEventListener() {}, set textContent(v) { this.text = v; if (v === '') this.children = []; }, get textContent() { return this.text; } });
    const draw = function (parts) {
        const list = node(), summary = node();
        const pg = { _status: parts, querySelector: s => (s === '#statusChecks' ? list : s === '#statusSummary' ? summary : null) };
        kit({ createElement: node }, () => {}).renderStatus(pg);
        return { texts: list.children.map(r => r.children[1].textContent), summary: summary.textContent };
    };
    const off = [{ level: 'info', text: 'Bonfire is off.' }];
    let out = draw({ off, pins: [{ level: 'warn', text: 'WARN' }], script: [{ level: 'bad', text: 'BAD' }] });
    ok('off, with a bad and a warn item: "Bonfire is off." is the first row', out.texts[0] === 'Bonfire is off.', out.texts.join(' | '));
    ok('the others keep their order below it', out.texts.slice(1).join() === 'BAD,WARN', out.texts.join(' | '));
    ok('and it is not counted as needing attention', /^2 things|2/.test(out.summary) && !/3/.test(out.summary), out.summary);
    out = draw({ off, avatars: [{ level: 'ok', text: 'OK' }] });
    ok('off with only an ok item: still first', out.texts[0] === 'Bonfire is off.', out.texts.join(' | '));
    out = draw({ off: [], pins: [{ level: 'warn', text: 'WARN' }], script: [{ level: 'bad', text: 'BAD' }] });
    ok('on: no "Bonfire is off." row, and the order is by level', out.texts.join() === 'BAD,WARN', out.texts.join(' | '));
}

// A backup from before 2.0 has no BonfireOn, so restoring one onto a new install (which
// starts off) left Bonfire off, and the result said only what it imported.
console.log('\n── An import that leaves Bonfire off ───────────────────────────');
{
    const summary = new Function(L.dashboardI18n(script) + L.extractFunction(script, 'importSummary') + '\nreturn importSummary;')();
    const base = { mappings: 2, devices: 1, bonfires: 0, droppedMappings: [] };
    const off = summary(Object.assign({ bonfireOn: false }, base));
    ok('the import result says Bonfire is off', /Bonfire is off/.test(off) && /Status/.test(off), off);
    ok('and nothing when it is on', !/Bonfire is off/.test(summary(Object.assign({ bonfireOn: true }, base))));
    ok('or when an older server does not say', !/Bonfire is off/.test(summary(base)));
}

console.log('\n── Saving ──────────────────────────────────────────────────────');
const settle = () => new Promise(r => setImmediate(r));
(async function () {
    page = makePage({ BonfireOn: false });
    A.bindBonfireOn(page);
    A.bindBonfireOn(page);
    ok('bound once', typeof page.toggle.listeners.change === 'function' && page.toggle.dataset.bound === '1');
    page.toggle.checked = true;
    page.toggle.listeners.change();
    await settle(); await settle();
    ok('turning it on sends only bonfireOn: true to admin/settings',
       posted.length === 1 && posted[0].url === '/plugins/profiles/admin/settings'
       && JSON.stringify(posted[0].body) === '{"bonfireOn":true}', JSON.stringify(posted));
    ok('and the page now has it on', page._config.BonfireOn === true && page.toggle.checked === true && status.off.length === 0);

    answer = () => Promise.reject(new Error('500'));
    page.toggle.checked = false;
    page.toggle.listeners.change();
    await settle(); await settle();
    ok('a failed save puts the toggle back to on', page.toggle.checked === true && page._config.BonfireOn === true);
    ok('and says so', alerts.length === 1);
    finish();
})();
