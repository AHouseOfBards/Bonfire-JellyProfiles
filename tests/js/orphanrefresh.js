/*
 * R-1239. Removing leftover records on the Households tab changed the records but left
 * the Status tab still saying "N leftover records point at deleted accounts" until the
 * page was reloaded. A successful cleanup now asks the Status check again.
 *
 *   node tests/js/orphanrefresh.js [path/to/profilesDashboard.html]
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  - ' + detail : '')); }
}

const script = L.dashboardScript(fs.readFileSync(process.argv[2] || L.dashboardPath(), 'utf8'));
const src = ['escapeHtml', 'renderOrphans'].map(n => L.extractFunction(script, n)).join('\n');

function run(ajaxOutcome) {
    const calls = { status: 0, mappings: 0 };
    let clickApply = null;
    const apply = { addEventListener: (ev, fn) => { clickApply = fn; } };
    const box = { innerHTML: '' };
    const page = { querySelector: s => (s === '#orphansResult' ? box : s === '#applyOrphansBtn' ? apply : null) };
    const thenable = {
        then: (okFn, errFn) => { (ajaxOutcome === 'ok' ? okFn({ removedMappings: 1, removedGroups: 0, removedMembers: 0 }) : errFn()); }
    };
    const fn = new Function('t', 'Dashboard', 'ApiClient', 'loadMappings', 'loadOrphanStatus', 'page',
        src + '\nrenderOrphans(page, { orphans: [{ profileName: "a", reason: "b" }], deadGroups: [], deadMembers: [] });');
    fn(k => k, { showLoadingMsg() {}, hideLoadingMsg() {} },
        { getUrl: u => u, ajax: () => thenable },
        () => { calls.mappings++; }, () => { calls.status++; }, page);
    if (clickApply) clickApply();
    return calls;
}

const good = run('ok');
ok('the Households list is reloaded after a cleanup', good.mappings === 1, JSON.stringify(good));
ok('the Status check is asked again after a cleanup', good.status === 1, JSON.stringify(good));
const bad = run('fail');
ok('a failed cleanup changes neither', bad.status === 0 && bad.mappings === 0, JSON.stringify(bad));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
