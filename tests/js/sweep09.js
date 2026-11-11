/*
 * Round 1 sweep, batch 9: client-script defects that a source-level check can pin.
 *
 *   R-1229  a value set through textContent must not be escapeHtml'd first, or the page
 *           shows the entity (&#039;) instead of the character.
 *
 *   node tests/js/sweep09.js [path/to/profiles.js]
 */
'use strict';
const L = require('./_lib');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

console.log('\n-- R-1229: textContent takes text, not markup --');
const lines = SRC.split(/\r?\n/);
const bad = [];
lines.forEach(function (ln, i) {
    // textContent / innerText / .value / .title assigned from escapeHtml(...) shows the entity.
    if (/\.(textContent|innerText|title|placeholder)\s*=\s*escapeHtml\(/.test(ln)) bad.push((i + 1) + ': ' + ln.trim());
});
ok('no textContent (or title, placeholder) is assigned an escapeHtml() result', bad.length === 0, bad.join(' | '));

console.log('\n-- R-1236: clearing the emergency code asks first, and a too-long code says so --');
const DASH = L.readDashboard().replace(/\r\n/g, '\n');
const fnAt = DASH.indexOf('function savePanicCode(');
const fnBody = fnAt === -1 ? '' : DASH.slice(fnAt, DASH.indexOf('// ── Status', fnAt));
ok('savePanicCode is found', fnBody.length > 0);
ok('an empty code (a clear) goes through askFirst', /if \(!code\) \{[^}]*askFirst\(/.test(fnBody));
ok('a code over 128 characters is refused with its own reason', /code\.length > 128/.test(fnBody) && /admin\.panic\.tooLong/.test(fnBody));

console.log('\n-- R-1228: every fetch sends a 401 to checked() or handleSessionExpired, or says why not --');
// Pairwise, one assertion per fetch (GET included): each must reach checked() or
// handleSessionExpired before the next fetch begins. A count would let one site cover for
// another. An exemption is matched against the start of the call, carries its reason, and
// fails if no fetch uses it, so a stale one cannot linger.
const EXEMPT = [
    ['/i18n/',                              'the language file is served to anyone; no session is involved'],
    ['plugins/profiles/themes?part=custom', 'the theme list is served to anyone; no session is involved'],
    ['plugins/profiles/panic-state',        'asked before sign-in, to decide whether to show the emergency screen'],
    ["plugins/profiles/panic'",             'the emergency code, entered with no session'],
    ['Sessions/Logout',                     'signing out; the session is ending anyway'],
    ['plugins/profiles/info',               'a public status read that works before sign-in'],
    ['plugins/profiles/onboarding/done',    'a marker whose loss only shows setup again'],
    ['plugins/profiles/admin/mappings',     'a Dashboard poll that hides profiles; a failure is retried and a reload on it would loop'],
    ["plugins/profiles/preferences'), { cache", 'the startup read; before sign-in a 401 is normal and falls back to a guess'],
    ["plugins/profiles/library-artwork'), { cache", 'the page-load read of the artwork cache; a failure keeps what is cached'],
    ['plugins/profiles/announcements',      'a notice banner; no banner is the safe reading, and nobody is waiting on it'],
    ["plugins/profiles/me'), { cache",      'decides whether a Forgot PIN link shows; none is the safe reading'],
    ["plugins/profiles/onboarding'), {",    'the page-load check for first-run setup; none is the safe reading'],
    ["fetch(url, { headers: this.getAuthHeaders(masterState.token) })", 'arming the lock timer; a failure falls back to the cached list and retries'],
    ["plugins/profiles/list'), Object.assign", 'the _requestList wrapper; each of its callers is checked below'],
    ['fetch(url, init).then(',              'the _fetchTimed wrapper; each of its callers is checked below']
];
const usedEx = {};
(function () {
    // The two wrappers count as fetches at their call sites, so a caller cannot hide behind them.
    const re = /(?:\bfetch|\._fetchTimed|\._requestList)\(/g; let m; const at = [];
    while ((m = re.exec(SRC))) {
        // Skip the word in a comment.
        const ls = SRC.lastIndexOf('\n', m.index) + 1;
        if (/\/\//.test(SRC.slice(ls, m.index))) continue;
        at.push(m.index);
    }
    at.forEach(function (p, i) {
        const head = SRC.slice(p, p + 400);
        const line = SRC.slice(0, p).split('\n').length;
        const label = head.replace(/\s+/g, ' ').slice(0, 70);
        const flat = head.replace(/\s+/g, ' ');
        const hit = EXEMPT.filter(function (x) { return flat.indexOf(x[0]) !== -1; })[0];
        if (hit) { usedEx[hit[0]] = true; return; }
        const win = SRC.slice(p, Math.min(at[i + 1] || SRC.length, p + 2500));
        ok('line ' + line + ' ' + label + ' reaches checked()', /\.checked\(|handleSessionExpired/.test(win));
    });
    EXEMPT.forEach(function (x) { ok('exemption "' + x[0] + '" is used by a fetch', usedEx[x[0]] === true, x[1]); });
}());

console.log('\n-- R-1230: the merge picker shows client and last seen, not the name alone --');
(function () {
    const at = SRC.indexOf("querySelectorAll('.device-merge-btn')");
    const body = at === -1 ? '' : SRC.slice(at, at + 1800);
    ok('the merge handler is found', body.length > 0);
    ok('each choice is given a label built from the row detail', /data-device-detail/.test(body) && /label:\s*detail/.test(body));
    ok('every device row carries its client and last seen as data-device-detail', /data-device-detail="\$\{escapeHtml\(t\('profileForm\.deviceLastSeen'/.test(SRC));
    ok('showSelectDialog draws the label when there is one', /escapeHtml\(o\.label \|\| o\.name\)/.test(SRC));
}());

console.log('\n-- R-1244: switcher entries built before the language arrived are rebuilt in it --');
(function () {
    const at = SRC.indexOf('_loadLocaleAndRelabel: function');
    const body = at === -1 ? '' : SRC.slice(at, at + 1200);
    ok('a loadLocale wrapper that relabels exists', body.length > 0);
    ['profiles-floating-bubble', 'profiles-user-menu-item', 'profiles-preferences-menu-item', 'profiles-userprofile-section']
        .forEach(function (id) { ok('the wrapper drops #' + id + ' so it is rebuilt in the new language', body.indexOf("'" + id + "'") !== -1); });
    ok('it only does so when the language changed', /activeLocale === before\) return/.test(body));
    const bare = SRC.split(/\r?\n/).filter(function (l) { return /_i18nReady\s*=\s*loadLocale\(\)/.test(l); });
    ok('startup and a live language change both go through it (no bare loadLocale assignment)', bare.length === 0, bare.join(' | '));
}());

console.log('\n-- R-1245: evaluating the script twice in one page starts it once --');
(function () {
    const vm = require('vm');
    const timers = [];
    const ctx = {
        console: { log() {}, warn() {}, error() {} },
        setTimeout: function (fn, ms) { timers.push(ms); return 1; },
        clearTimeout() {},
        setInterval: function () { timers.push('interval'); return 1; },
        clearInterval() {},
        navigator: { userAgent: 'x' },
        location: { hash: '', href: 'http://x/', pathname: '/' },
        localStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
        document: { documentElement: {}, addEventListener() {}, getElementById() { return null; }, querySelector() { return null; } }
    };
    ctx.window = ctx;
    vm.createContext(ctx);
    // No ApiClient in the page, so init() does one thing: schedule itself to try again.
    // That one timer is what a second copy would double.
    let threw = '';
    try { vm.runInContext(SRC, ctx); vm.runInContext(SRC, ctx); } catch (e) { threw = e.message; }
    ok('two evaluations do not throw', threw === '', threw);
    ok('the first evaluation started init (one timer)', timers.length >= 1);
    ok('the second evaluation added nothing', timers.length === 1, 'timers: ' + JSON.stringify(timers));
}());

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
