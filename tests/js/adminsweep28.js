/*
 * Three small dashboard faults from the round 1 sweep, run against the real functions.
 *
 *   R-1266  a mail port that is not a number saved as 587 while the box still showed what
 *           was typed. It is refused now, with a sentence.
 *   R-1267  Save on the theme editor had no in-flight guard, so a double click saved twice;
 *           and loadTimeZones threw on an empty answer.
 *
 *     node tests/js/adminsweep28.js "" /path/to/old/profilesDashboard.html
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
const names = ['saveEmail', 'saveThemeDraft', 'loadTimeZones'];
const missing = names.filter(n => !new RegExp('function ' + n + '\\s*\\(').test(script));
if (missing.length) { console.log('missing ' + missing.join(',')); process.exit(1); }

const source = L.dashboardI18n(script) + '\n' + names.map(n => L.extractFunction(script, n)).join('\n');

function el(init) { return Object.assign({ value: '', textContent: '', options: [], className: '' }, init || {}); }

function thenable(run) {
    // A promise that is settled by hand, so a second click can land while the first is out.
    let resolve;
    const p = new Promise(r => { resolve = r; });
    run && run(resolve);
    return { p: p, resolve: resolve };
}

(async function () {
    console.log('\n── Mail port (R-1266) ──────────────────────────────────────────');
    function emailWorld(portValue) {
        const els = { '#emailPort': el({ value: portValue }), '#emailHost': el({ value: 'smtp.example.com' }),
            '#emailSecurity': el({ value: 'starttls' }), '#emailUsername': el(), '#emailFrom': el(), '#emailPassword': el() };
        const posts = [];
        const shown = [];
        const page = { querySelector: s => els[s] || el() };
        const run = new Function('page', 'ApiClient', 'showEmailResult', 'renderEmail', 'errorText', 'showOutcome',
            source + '; return { saveEmail };');
        const api = run(page, { getUrl: u => '/' + u, ajax: o => { posts.push(JSON.parse(o.data)); return Promise.resolve({}); } },
            (p, msg, good) => shown.push({ msg: msg, good: good }), () => {}, () => Promise.resolve(''), () => {});
        return { api, page, posts, shown };
    }
    let w = emailWorld('abc');
    let saved = await w.api.saveEmail(w.page);
    ok('a port that is not a number is not sent', w.posts.length === 0 && saved === null, JSON.stringify(w.posts));
    ok('and the form says why', w.shown.length === 1 && w.shown[0].good === false && /1 to 65535/.test(w.shown[0].msg), JSON.stringify(w.shown));
    w = emailWorld('70000');
    await w.api.saveEmail(w.page);
    ok('a port above 65535 is not sent', w.posts.length === 0, JSON.stringify(w.posts));
    w = emailWorld('0');
    await w.api.saveEmail(w.page);
    ok('port 0 is not sent', w.posts.length === 0, JSON.stringify(w.posts));
    w = emailWorld('2525');
    await w.api.saveEmail(w.page);
    ok('a good port is sent as typed', w.posts.length === 1 && w.posts[0].port === 2525, JSON.stringify(w.posts));

    console.log('\n── Theme Save in flight (R-1267) ───────────────────────────────');
    const ed = { id: 'x' };
    const pending = [];
    const themePage = { _themeEditor: ed, _themes: { data: {} } };
    const run = new Function('page', 'ApiClient', 'localThemeProblems', 'showThemeProblems', 'showThemeStatus', 'themeDraftForSave',
        'closeThemeEditor', 'getThemePath', 'loadThemes', 'refreshClientThemes', 'readThemeError',
        source + '; return { saveThemeDraft };');
    let puts = 0;
    const api = run(themePage,
        { getUrl: u => '/' + u, ajax: () => { puts++; const t = thenable(); pending.push(t); return t.p; } },
        () => [], () => {}, () => {}, e => ({ id: 'x' }), () => {}, () => '', () => {}, () => {},
        () => Promise.resolve({ message: 'no' }));
    api.saveThemeDraft(themePage);
    api.saveThemeDraft(themePage);
    ok('a second click while the first save is out sends nothing', puts === 1, 'puts=' + puts);
    pending[0].resolve({});
    await new Promise(r => setTimeout(r, 0));

    // A failed save must let the next click through.
    const ed2 = { id: 'y' };
    const page2 = { _themeEditor: ed2, _themes: { data: {} } };
    let puts2 = 0;
    const run2 = new Function('page', 'ApiClient', 'localThemeProblems', 'showThemeProblems', 'showThemeStatus', 'themeDraftForSave',
        'closeThemeEditor', 'getThemePath', 'loadThemes', 'refreshClientThemes', 'readThemeError',
        source + '; return { saveThemeDraft };');
    const api2 = run2(page2,
        { getUrl: u => '/' + u, ajax: () => { puts2++; return Promise.reject(new Error('x')); } },
        () => [], () => {}, () => {}, e => ({ id: 'y' }), () => {}, () => '', () => {}, () => {},
        () => Promise.resolve({ message: 'no' }));
    api2.saveThemeDraft(page2);
    await new Promise(r => setTimeout(r, 0));
    api2.saveThemeDraft(page2);
    ok('after a failed save the button works again', puts2 === 2, 'puts=' + puts2);

    console.log('\n── Time zones (R-1267) ─────────────────────────────────────────');
    const select = { options: [{}], remove() {}, appendChild() {}, value: '' };
    const tzEls = { '#defaultTimeZone': select, '#defaultTimeZoneHint': el() };
    const tzPage = { querySelector: s => tzEls[s] || el() };
    let threw = null;
    const runTz = new Function('page', 'ApiClient', 'renderGettingStarted', 'document', 'field',
        source + '; return { loadTimeZones };');
    const tz = runTz(tzPage, { getUrl: u => '/' + u, ajax: () => Promise.resolve(null) }, () => {},
        { createElement: () => ({}) }, (o, n) => o[n] !== undefined ? o[n] : o[n.charAt(0).toUpperCase() + n.slice(1)]);
    try { tz.loadTimeZones(tzPage, ''); } catch (e) { threw = e; }
    // The rejection, if any, is inside the promise chain: wait for it.
    let unhandled = null;
    process.once('unhandledRejection', e => { unhandled = e; });
    await new Promise(r => setTimeout(r, 20));
    ok('an empty answer does not throw', !threw && !unhandled, String(threw || unhandled));

    console.log('');
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
})();
