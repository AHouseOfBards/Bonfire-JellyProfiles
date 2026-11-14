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
const src = ['escapeHtml', 'orphanReasonText', 'renderOrphans'].map(n => L.extractFunction(script, n)).join('\n');

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

// Unused background images (2.0) are part of the same scan. The button said "Remove this
// record" when only an image was found, the confirmation never mentioned images though the
// server reports them, and the Status tab left them out of its count, so the two tabs
// disagreed. Run with the page's real English, since the question is what an administrator
// reads.
const english = L.dashboardI18n(script);
const statusSrc = L.extractFunction(script, 'loadOrphanStatus');

function scan(data, applied) {
    let clickApply = null;
    const apply = { addEventListener: (ev, fn) => { clickApply = fn; } };
    const box = { innerHTML: '' };
    const page = { querySelector: s => (s === '#orphansResult' ? box : s === '#applyOrphansBtn' ? apply : null) };
    const fn = new Function('Dashboard', 'ApiClient', 'loadMappings', 'loadOrphanStatus', 'page', 'data',
        english + src + '\nrenderOrphans(page, data);');
    fn({ showLoadingMsg() {}, hideLoadingMsg() {} },
        { getUrl: u => u, ajax: () => ({ then: okFn => okFn(applied) }) },
        () => {}, () => {}, page, data);
    const before = box.innerHTML;
    if (clickApply) clickApply();
    return { before, after: box.innerHTML };
}

const none = { orphans: [], deadGroups: [], deadMembers: [] };
const oneImage = scan(Object.assign({}, none, { unusedImages: 1 }),
    { removedMappings: 0, removedGroups: 0, removedMembers: 0, removedImages: 1 });
const button = (oneImage.before.match(/<button[^>]*><span>([^<]*)</) || [])[1] || '';
ok('an image found alone is listed', /theme image/.test(oneImage.before), oneImage.before);
ok('and the button does not call it a record', button !== '' && !/record/i.test(button), button);
ok('the confirmation says how many images went', /image[^.]*: 1\b/.test(oneImage.after), oneImage.after);

const mixed = scan({ orphans: [{ profileName: 'Ana', reason: 'gone' }], deadGroups: [], deadMembers: [], unusedImages: 2 },
    { removedMappings: 1, removedGroups: 0, removedMembers: 0, removedImages: 2 });
const mixedButton = (mixed.before.match(/<button[^>]*><span>([^<]*)</) || [])[1] || '';
ok('a record and two images make one button for all three', /3/.test(mixedButton) && !/record/i.test(mixedButton), mixedButton);
ok('and the confirmation counts both', /: 1\b/.test(mixed.after) && /image[^.]*: 2\b/.test(mixed.after), mixed.after);

function statusOf(data) {
    let items = null;
    const fn = new Function('ApiClient', 'setStatus', 'page', english + statusSrc + '\nloadOrphanStatus(page);');
    fn({ getUrl: u => u, ajax: () => ({ then: okFn => okFn(data) }) }, (p, part, list) => { items = list; }, {});
    return items || [];
}
const imgStatus = statusOf(Object.assign({}, none, { unusedImages: 2 }));
ok('the Status tab counts unused images too', imgStatus.length > 0 && imgStatus.some(i => /2/.test(i.text) && /image/.test(i.text)),
    JSON.stringify(imgStatus));
ok('and sends them to the same scan', imgStatus.every(i => i.tab === 'households' && i.anchor === 'jpfOrphans'), JSON.stringify(imgStatus));
const bothStatus = statusOf({ orphans: [{}], deadGroups: [], deadMembers: [], unusedImages: 1 });
ok('a record and an image are both on the Status tab', bothStatus.length === 2, JSON.stringify(bothStatus));
ok('nothing found is nothing on Status', statusOf(none).length === 0);

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
