/*
 * r2-35-01: loadMappings redrew the trusted-address rows from page._trusted (the saved list)
 * without reading the form first, so a PIN reset, a limit or an orphan removal on Households
 * threw away addresses typed and not yet saved, and Save then wrote the old list.
 *
 * Runs the real collectTrusted and loadMappings against a stub page whose rows are "drawn".
 * Red against the dashboard before the fix:
 *
 *     node tests/js/adminr2y35.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const script = L.dashboardScript(L.readDashboard());
const source = L.dashboardI18n(script) + '\n'
    + ['escapeHtml', 'namedAria', 'field', 'normId', 'collectTrusted', 'loadMappings'].map(n => L.extractFunction(script, n)).join('\n');

function element() {
    return {
        value: '', checked: false, textContent: '', innerHTML: '', dataset: {}, style: {},
        classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
        setAttribute() {}, getAttribute() { return null; }, addEventListener() {}, insertAdjacentHTML() {}
    };
}

async function drain() { for (let i = 0; i < 6; i++) await new Promise(r => setImmediate(r)); }

(async function main() {
    console.log('\n-- Unsaved trusted-address edits survive a Households reload (r2-35-01) --');
    const container = element();
    // One row on screen, edited: the saved list says 10.0.0.1 for nobody, the form says
    // 192.168.1.5 for aaaa.
    const row = {
        querySelector: sel => (sel === '.jpf-trust-addr' ? { value: ' 192.168.1.5 ' } : null),
        querySelectorAll: sel => (sel === '.jpf-trust-acct' ? [{ checked: true, value: 'aaaa' }, { checked: false, value: 'bbbb' }] : [])
    };
    const page = {
        dataset: {}, _trusted: [{ address: '10.0.0.1', userIds: [] }], _trustedDrawn: true,
        querySelector: sel => (sel === '#masterUsersList' ? container : element()),
        querySelectorAll: sel => (sel === '#trustedList .jpf-trust-row' ? [row] : [])
    };
    const seen = [];
    const renderTrusted = p => seen.push(JSON.parse(JSON.stringify(p._trusted)));
    const noop = () => {};
    const names = ['renderTrusted', 'renderGettingStarted', 'renderInjectionStatus', 'setStatus', 'pinStatusRows', 'askFirst',
        'bindStandaloneForms', 'bindAccountExtras', 'paintOutcome', 'errorText', 'Dashboard', 'console'];
    const stubs = [renderTrusted, noop, noop, noop, () => [], () => Promise.resolve(), noop, noop, noop,
        (e, f) => Promise.resolve(f), { hideLoadingMsg: noop, showLoadingMsg: noop }, { error: noop, log: noop }];
    const ApiClient = { getUrl: u => '/' + u, getJSON: () => Promise.resolve({ MasterUsers: [], SubProfiles: [] }) };
    const load = new Function('ApiClient', names.join(','), source + '; return loadMappings;').apply(null, [ApiClient].concat(stubs));
    load(page);
    await drain();
    ok('the redraw was given the rows as typed', seen.length === 1 && seen[0].length === 1 && seen[0][0].address === '192.168.1.5'
        && seen[0][0].userIds.join() === 'aaaa', JSON.stringify(seen));
    ok('the saved list is no longer what the page holds', page._trusted[0].address === '192.168.1.5', JSON.stringify(page._trusted));

    // Before any row is drawn the saved list is the truth, and must be left alone.
    const fresh = Object.assign({}, page, { _trusted: [{ address: '10.0.0.1', userIds: ['x'] }], _trustedDrawn: false });
    seen.length = 0;
    load(fresh);
    await drain();
    ok('before the rows are drawn the saved list is used', seen.length === 1 && seen[0][0].address === '10.0.0.1', JSON.stringify(seen));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
