/*
 * "Turn on Bonfire" on the dashboard's Status tab (2.0).
 *
 *   - The toggle is at the top of the Status tab, labelled "Turn on Bonfire", with no
 *     hint line under it (decided: an explanation was rejected).
 *   - While off, the Status list carries "Bonfire is off."; on, it carries nothing.
 *   - Getting started ends with "Turn on Bonfire.", done when on. A configuration from
 *     before 2.0 has no BonfireOn and is on.
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
const items = c => A.gettingStartedItems({ _config: c, _mappings: { subProfiles: [] } });
let list = items({ BonfireOn: false });
const last = list[list.length - 1];
ok('"Turn on Bonfire." is the last item', !!last && last.key === 'bonfireOn' && last.text === 'Turn on Bonfire.',
   list.map(i => i.key).join());
ok('to do while off', last && last.done === false);
list = items({ BonfireOn: true });
ok('done when on', list[list.length - 1].done === true);
list = items({});
ok('done on a configuration from before 2.0', list[list.length - 1].done === true);
list = items({ BonfireOn: false, DefaultTimeZone: '' });
ok('still last when the zone item is there too', list[list.length - 1].key === 'bonfireOn');

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
