/*
 * R-1235: the per-account profile limit "Set" button.
 *
 * An empty or non-numeric box parsed to NaN, JSON.stringify turned that into null, and the
 * server reads null as "remove the override", so Set on an empty box silently reset the
 * limit. A number outside 1 to 20 went to the server as typed and came back as a 400 the
 * page showed as "couldn't update". Both are now refused in the page, with the range.
 *
 *     node tests/js/adminlimit.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  - ' + detail : '')); }
}

const script = L.dashboardScript(L.readDashboard());
const has = /function saveLimitFromBox\s*\(/.test(script);

function run(value) {
    const sent = [], alerts = [];
    const input = { value: value };
    const page = { querySelector: () => input };
    const src = 'const t = function (k) { return k; };\n' +
        'const setProfileLimit = function (p, id, n) { sent.push([id, n]); };\n' +
        (has ? L.extractFunction(script, 'saveLimitFromBox') : 'const saveLimitFromBox = null;') + '\nreturn saveLimitFromBox;';
    const Dashboard = { alert: a => alerts.push(a) };
    const f = new Function('sent', 'Dashboard', src)(sent, Dashboard);
    if (f) f(page, 'u1');
    return { sent: sent, alerts: alerts };
}

ok('saveLimitFromBox exists', has);
for (const bad of ['', '  ', 'abc', '0', '21', '-3', '1e9']) {
    const r = run(bad);
    ok('"' + bad + '" is not sent', has && r.sent.length === 0, JSON.stringify(r.sent));
    ok('"' + bad + '" tells the admin the range', has && r.alerts.length === 1 && /limitRange/.test(r.alerts[0].message));
}
for (const good of ['1', '5', '20']) {
    const r = run(good);
    ok('"' + good + '" is sent as a number', has && r.sent.length === 1 && r.sent[0][1] === Number(good) && r.alerts.length === 0);
}
ok('the Set handler goes through it', /save-limit-btn[\s\S]{0,300}saveLimitFromBox\(/.test(script));
ok('Reset still sends null', /clear-limit-btn[\s\S]{0,300}setProfileLimit\(page, userId, null\)/.test(script));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
