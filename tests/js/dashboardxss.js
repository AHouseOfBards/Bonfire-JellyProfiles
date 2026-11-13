/*
 * Nothing the server hands back may be rendered as markup.
 *
 * The admin dashboard runs with the administrator's token. Its audit log printed the
 * device and client name of every profile switch straight into innerHTML, and those two
 * values are copied from the Authorization header of whoever switched, URL-decoded on the
 * way in. Any account on the server can switch into itself, so any account could name its
 * device <img src=x onerror=…> and have that script run as the administrator the next time
 * the Bonfire settings page was opened. The audit log loads with the page, so the
 * administrator did not have to click anything. The sessions list and the orphan list had
 * the same shape.
 *
 * This runs the real render functions against hostile values and looks at what they
 * produce. Reading the source for "escapeHtml" would pass a version that escapes one field
 * and forgets the next, which is how this happened.
 *
 * Point it at older files to watch it fail (argv[2] is the client script, argv[3] the
 * dashboard):
 *
 *     node tests/js/dashboardxss.js /old/profiles.js /old/profilesDashboard.html
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

// What WebUtility.UrlDecode makes of Device="%3Cimg%20src%3Dx%20onerror%3Dalert(1)%3E".
const EVIL = '<img src=x onerror=alert(1)>';
const EVIL_ATTR = '" onmouseover="alert(1)';

/** True when the payload survived as markup rather than text. */
function live(html) {
    return html.indexOf('<img src=x') !== -1 || html.indexOf('" onmouseover="') !== -1;
}

// ── The dashboard ────────────────────────────────────────────────────────────

const script = L.dashboardScript(L.readDashboard());

/** Builds one callable out of the named dashboard functions, or null if any is missing. */
function dashboardFns(names, entry, pre) {
    let src;
    try {
        src = L.dashboardI18n(script) + names.map(n => L.extractFunction(script, n)).join('\n');
    } catch (e) {
        return null;
    }
    return new Function('arg1', 'arg2', 'let auditEntries = arg2 || [];\n' + (pre || '') + src + '\nreturn ' + entry + ';');
}

/** A stand-in element: records the markup written to it, ignores everything else. */
function fakeEl() {
    const el = {
        innerHTML: '', textContent: '', value: '', style: {}, dataset: {}, checked: false,
        classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
        addEventListener() {}, setAttribute() {}, getAttribute: () => null,
        appendChild() {}, remove() {}, focus() {},
        insertAdjacentHTML(where, h) { el.innerHTML += h; },
        querySelector: () => fakeEl(), querySelectorAll: () => []
    };
    return el;
}

function fakePage(extra) {
    const els = Object.assign({
        '#auditLogsList': { innerHTML: '' },
        '#auditCount': { textContent: '' },
        '#auditFilter': { value: '' },
        '#auditFrom': { value: '' },
        '#auditTo': { value: '' },
        '#orphansResult': { innerHTML: '', querySelector: () => null },
    }, extra || {});
    return { els, querySelector: s => els[s] || null };
}

console.log('\n── The audit log, which loads with the page ────────────────────');
{
    const render = dashboardFns(
        ['escapeHtml', 'auditField', 'auditMatches', 'auditVisible', 'renderAuditLogs'],
        'renderAuditLogs(arg1)');
    const fields = ['DeviceName', 'Client', 'MasterUsername', 'TargetUsername', 'IpAddress'];
    fields.forEach(field => {
        const entry = { Timestamp: '2026-09-27T05:00:00Z', MasterUsername: 'a', TargetUsername: 'b',
                        DeviceName: 'tv', Client: 'web', IpAddress: '1.2.3.4' };
        entry[field] = EVIL;
        const page = fakePage();
        render(page, [entry]);
        const html = page.els['#auditLogsList'].innerHTML;
        ok(field + ' is rendered as text', html.indexOf('&lt;img src=x') !== -1 && !live(html));
    });
}

console.log('\n── The signed-in sessions list ─────────────────────────────────');
{
    const rows = dashboardFns(['escapeHtml', 'namedAria', 'sessionRowsHtml'], 'sessionRowsHtml(arg1)');
    ok('the rows are built by a function a test can run', rows !== null);
    if (rows) {
        const hostile = {
            isSubProfile: true, username: EVIL, masterUsername: EVIL, deviceName: EVIL,
            client: EVIL, nowPlaying: EVIL, lastActivity: '2026-09-27T05:00:00Z',
            userId: EVIL_ATTR, deviceId: EVIL_ATTR
        };
        const html = rows([hostile]);
        ok('no field of a session is rendered as markup', !live(html));
        ok('and the values are still shown', html.split('&lt;img src=x').length - 1 >= 5);
        ok('a device id cannot leave its data attribute', html.indexOf('data-device="&quot; onmouseover') !== -1);
    }
}

console.log('\n── The orphan scan ─────────────────────────────────────────────');
{
    const render = dashboardFns(['escapeHtml', 'orphanReasonText', 'renderOrphans'], 'renderOrphans(arg1, arg2)');
    ok('renderOrphans can be run', render !== null);
    if (render) {
        // renderOrphans binds its apply button after rendering.
        const page = fakePage({ '#applyOrphansBtn': { addEventListener() {} } });
        let threw = null;
        try {
            // arg2 doubles as auditEntries in the wrapper; renderOrphans ignores that.
            render.call(null, page, {
                orphans: [{ profileName: EVIL, reason: EVIL }],
                deadGroups: [{ code: EVIL }],
                deadMembers: []
            });
        } catch (e) { threw = e; }
        const html = page.els['#orphansResult'].innerHTML;
        ok('it renders without throwing', threw === null);
        ok('profile names, reasons and codes are text', html.length > 0 && !live(html));
    }
}

// R-1280: the household list, the avatar grid and the theme rows are built from names the
// server hands back — a profile's name, an avatar's file name, a theme's name — and had no
// hostile-value run. The household and avatar renderers live inside a fetch callback, so the
// real function is run against a fake ApiClient that answers with the payload.
console.log('\n── The household list ──────────────────────────────────────────');
{
    // Stubs only for what is not markup: the status rows, the first-run checklist, the
    // trusted-address editor and the injection status. Every HTML builder is the real one.
    const pre = 'var __result = null, __ajax = null;\n'
        + 'var Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert() {}, confirm() {} };\n'
        + 'var ApiClient = { getUrl: function (u) { return "/" + u; },\n'
        + '  serverVersion: function () { return "10.11.0"; },\n'
        + '  getJSON: function () { return { then: function (f) { f(__result); return { catch: function () {} }; } }; } };\n'
        + 'function setStatus() {} function pinStatusRows() { return []; } function renderGettingStarted() {}\n'
        + 'function renderTrusted() {} function renderInjectionStatus() {}\n'
        + 'function bindStandaloneForms() {} function bindAccountExtras() {}\n'
        + 'function pluginAsset(u) { return "/" + u; }\n';
    const make = dashboardFns(['escapeHtml', 'namedAria', 'field', 'normId', 'collectTrusted', 'loadMappings'],
        '(function (res, page) { __result = res; loadMappings(page); })', pre);
    ok('loadMappings can be run against a fake server', make !== null);
    if (make) {
        const run = make();
        const master = { profileName: EVIL, profileUserId: EVIL_ATTR, requiresPin: true,
                         maxProfiles: 5, limitOverride: 3 };
        const sub = { profileName: EVIL, profileUserId: EVIL_ATTR, masterUserId: EVIL_ATTR,
                      requiresPin: true };
        const orphan = { profileName: EVIL, profileUserId: EVIL_ATTR, masterUserId: 'nobody',
                         requiresPin: true };
        const list = fakeEl();
        const page = fakeEl();
        page.dataset = {};
        page.querySelector = s => s === '#masterUsersList' ? list : fakeEl();
        let threw = null;
        try { run({ MasterUsers: [master], SubProfiles: [sub, orphan] }, page); }
        catch (e) { threw = e; }
        ok('it renders without throwing' + (threw ? ' — ' + threw.message : ''), threw === null);
        ok('the list has rows, with the hostile name shown as text',
           list.innerHTML.indexOf('&lt;img src=x') !== -1);
        ok('no part of the household list is markup (names, ids, reasons)', !live(list.innerHTML));
    }
}

console.log('\n── The avatar library ──────────────────────────────────────────');
{
    const pre = 'var ApiClient = { getUrl: function (u) { return "/" + u; },\n'
        + '  ajax: function () { return { then: function (f) { f(__data); return { catch: function () {} }; } }; } };\n'
        + 'var __data = null; function pluginAsset(u) { return "/" + u; }\n';
    const make = dashboardFns(['escapeHtml', 'namedAria', 'field', 'loadAvatarLibrary'],
        '(function (d, page) { __data = d; loadAvatarLibrary(page); })', pre);
    ok('loadAvatarLibrary can be run against a fake server', make !== null);
    if (make) {
        const grid = fakeEl();
        const page = fakeEl();
        page.querySelector = s => s === '#avatarLibraryGrid' ? grid : fakeEl();
        let threw = null;
        try {
            make()({ avatars: { avatars: [{ id: EVIL_ATTR, displayName: EVIL, thumbUrl: EVIL_ATTR, isStarter: false }] } },
                   page);
        } catch (e) { threw = e; }
        ok('it renders without throwing' + (threw ? ' — ' + threw.message : ''), threw === null);
        ok('the avatar name is shown as text', grid.innerHTML.indexOf('&lt;img src=x') !== -1);
        ok('no part of the avatar grid is markup (name, id, thumbnail)', !live(grid.innerHTML));
    }
}

console.log('\n── The theme rows ──────────────────────────────────────────────');
{
    const build = (fn, a, b) => {
        const f = dashboardFns(['escapeHtml', 'namedAria', 'themePreviewCardHtml', 'themeShortName', 'getThemePath',
                                'themeDisplayName', 'themeFontCssName', fn], fn + '(arg1, arg2)');
        return f ? f(a, b) : null;
    };
    // The shapes the server sends: a built-in is { id, hidden }, a custom theme carries its
    // own name in theme.name.text, a font is { family, file, usedBy, weights, fallback }.
    const data = {
        'default': EVIL_ATTR,
        builtIn: [{ id: EVIL_ATTR, hidden: false }, { id: EVIL, hidden: true }],
        builtInNames: { [EVIL_ATTR]: EVIL, [EVIL]: EVIL },
        custom: [{ id: EVIL_ATTR, problems: [{ code: EVIL }], theme: { name: { text: EVIL } } }],
        fonts: [{ family: EVIL, file: EVIL_ATTR, usedBy: [EVIL_ATTR], weightMin: 400, weightMax: 700,
                  fallback: 'serif' }]
    };
    const previews = { [EVIL_ATTR]: EVIL_ATTR, [EVIL]: EVIL };
    [['builtInThemeRowsHtml', data, previews], ['customThemeRowsHtml', data],
     ['fontRowsHtml', data]].forEach(args => {
        let html = null, threw = null;
        try { html = build(args[0], args[1], args[2]); } catch (e) { threw = e; }
        ok(args[0] + ' runs' + (threw ? ' — ' + threw.message : ''), threw === null && typeof html === 'string');
        if (typeof html === 'string') {
            ok(args[0] + ' shows the hostile name as text', html.indexOf('&lt;img src=x') !== -1
               || html.indexOf('&quot; onmouseover') !== -1);
            ok(args[0] + ' renders nothing as markup', !live(html));
        }
    });
}

console.log('\n── The import summary ──────────────────────────────────────────');
{
    const summary = dashboardFns(['importSummary'], 'importSummary(arg1)');
    ok('the summary is built by a function a test can run', summary !== null);
    if (summary) {
        const text = summary({ mappings: 1, devices: 0, bonfires: 0, droppedMappings: [EVIL] });
        ok('it names the skipped profiles', text.indexOf(EVIL) !== -1);
        // The export leaves the emergency code out. The summary says which of the two
        // happened: the server kept its own, or there is none and it needs setting.
        const base = { mappings: 1, devices: 0, bonfires: 0, droppedMappings: [] };
        const kept = summary(Object.assign({}, base, { emergencyCodeIncluded: false, emergencyCodeSet: true }));
        const unset = summary(Object.assign({}, base, { emergencyCodeIncluded: false, emergencyCodeSet: false }));
        const had = summary(Object.assign({}, base, { emergencyCodeIncluded: true, emergencyCodeSet: true }));
        ok('a file without the emergency code says the server kept its own',
           /emergency code/i.test(kept) && /kept/i.test(kept), kept);
        ok('and with none on the server, says to set it', /emergency code/i.test(unset) && /set it/i.test(unset), unset);
        ok('a file with the code says nothing about it', !/emergency code/i.test(had), had);
    }
    // R-1280: was a regex over the source for one spelling of the call. The markup the page
    // writes is built by a function now, and this runs it.
    const markup = dashboardFns(['escapeHtml', 'importSummary', 'importResultHtml'], 'importResultHtml(arg1)');
    ok('the markup under the import button is built by a function a test can run', markup !== null);
    if (markup) {
        const html = markup({ mappings: 1, devices: 0, bonfires: 0, droppedMappings: [EVIL] });
        ok('and the skipped profile is text in it, not markup', html.indexOf('&lt;img src=x') !== -1 && !live(html));
    }
    ok('and the page writes that function\'s result, nothing else, to the box',
       script.indexOf('box.innerHTML = importResultHtml(res);') !== -1);
}

console.log('\n── escapeHtml itself ───────────────────────────────────────────');
{
    const esc = dashboardFns(['escapeHtml'], 'escapeHtml(arg1)');
    ok('a number is escaped rather than thrown on', (() => { try { return esc(0) === '0'; } catch (e) { return false; } })());
    ok('null is empty', esc(null) === '');
}

// ── The client ───────────────────────────────────────────────────────────────

let pending = null;
console.log('\n── The Bonfire panel names other people ────────────────────────');
{
    let src = fs.readFileSync(L.profilesPath(), 'utf8');
    src = src.replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');

    const makeEl = () => ({
        style: {}, checked: false, textContent: '',
        addEventListener() {}, removeEventListener() {}, setAttribute() {}, getAttribute: () => null,
        appendChild() {}, remove() {}, focus() {}
    });
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0', language: 'en' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        requestAnimationFrame() {},
        fetch: () => Promise.resolve({ ok: true, text: () => Promise.resolve('') }),
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
            createElement: makeEl, head: { appendChild() {} },
            body: { classList: { add() {}, remove() {}, contains: () => false }, appendChild() {} },
            documentElement: { style: { cssText: '', removeProperty() {}, setProperty() {} },
                               classList: { add() {}, remove() {}, contains: () => false } }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);
    const plugin = sandbox.__PROFILES;
    plugin.getAuthHeaders = () => ({});

    function render(status) {
        let html = '';
        const container = {
            set innerHTML(v) { html = v; }, get innerHTML() { return html; },
            querySelector: () => makeEl(), querySelectorAll: () => []
        };
        try {
            plugin.renderBonfireStatus(container, {}, status, { getUrl: u => '/' + u }, 'token');
        } catch (e) { /* handlers wired after render are not what this is about */ }
        return html;
    }

    const owned = render({ isOwner: true, isMember: false, ownedCode: 'ABCDEF',
                           ownedMembers: [{ userId: EVIL_ATTR, username: EVIL }] });
    ok('a member name is text', owned.indexOf('&lt;img src=x') !== -1 && !live(owned));

    const joined = render({ isOwner: false, isMember: true, joinedOwnerName: EVIL, joinedOwnerId: 'x' });
    ok('the owner of a Bonfire you joined is text', joined.indexOf('&lt;img src=x') !== -1 && !live(joined));

    // showAlert renders its message as markup because translations carry tags. What goes
    // into it from a server response must therefore be escaped at the call site.
    //
    // R-1280: this used to look for one spelling of the mistake in the source, which a
    // different spelling walks past. The three screens that show a refusal from the time-limit
    // routes are driven instead: the server's answer is a hostile message, and what reaches
    // showAlert is what is checked.
    const alerts = [];
    // r2-24-03: the dialog now escapes what it is given, so what is checked is what the real
    // showAlert writes into the page, not what the caller handed it.
    const realAlert = plugin.showAlert;
    plugin.showAlert = function (title, message, onClose, asHtml) {
        let written = '';
        const el = { style: {}, classList: { add() {}, remove() {}, contains: () => false },
            set innerHTML(v) { written = v; }, get innerHTML() { return written; },
            addEventListener() {}, setAttribute() {}, getAttribute: () => null, appendChild() {}, remove() {}, focus() {},
            querySelector: () => ({ addEventListener() {}, focus() {}, setAttribute() {}, getAttribute: () => null, style: {} }),
            querySelectorAll: () => [] };
        const was = sandbox.document.createElement;
        sandbox.document.createElement = () => el;
        try { realAlert.call(plugin, title, message, onClose, asHtml); } finally { sandbox.document.createElement = was; }
        alerts.push(written);
    };
    plugin._limitsCall = function () { return Promise.reject(new Error(EVIL)); };
    const dialogs = [];
    plugin.showActionsDialog = function (title, body, actions) { dialogs.push(actions); };
    plugin.showSelectDialog = function (title, body, options, cb) { cb('15'); };
    const kid = { profileName: 'Kid', profileUserId: 'u1', profileId: 'u1', viewing: { reason: 'limit' } };
    const none = function () {};
    function drive(fn) {
        const before = dialogs.length;
        try { fn(); } catch (e) { /* only what reaches showAlert matters */ }
        dialogs.slice(before).forEach(function (actions) {
            actions.forEach(function (a) { try { if (a.run) a.run(); } catch (e) { /* ditto */ } });
        });
    }
    drive(() => plugin.showTimeUp(kid, none));
    drive(() => plugin.chooseMoreTime(kid, none));
    drive(() => plugin.answerTimeRequest({ profileName: 'Kid', profileId: 'u1', grantMinutesLeft: 60 }, none));
    pending = new Promise(r => setTimeout(r, 20)).then(() => {
        ok('the three time-limit screens each showed the refusal (' + alerts.length + ' alerts)',
           alerts.length >= 3);
        ok('a refusal from the server is shown as text, never as markup',
           alerts.length > 0 && alerts.every(a => !live(a)) && alerts.some(a => a.indexOf('&lt;img') !== -1));
    });
}

Promise.resolve(pending).then(function () {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exit(1);
});
