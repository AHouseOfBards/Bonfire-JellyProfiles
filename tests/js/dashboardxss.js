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
function dashboardFns(names, entry) {
    let src;
    try {
        src = names.map(n => L.extractFunction(script, n)).join('\n');
    } catch (e) {
        return null;
    }
    return new Function('arg1', 'arg2', 'let auditEntries = arg2 || [];\n' + src + '\nreturn ' + entry + ';');
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
    const rows = dashboardFns(['escapeHtml', 'sessionRowsHtml'], 'sessionRowsHtml(arg1)');
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
    const render = dashboardFns(['escapeHtml', 'renderOrphans'], 'renderOrphans(arg1, arg2)');
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

console.log('\n── The import summary ──────────────────────────────────────────');
{
    const summary = dashboardFns(['importSummary'], 'importSummary(arg1)');
    ok('the summary is built by a function a test can run', summary !== null);
    if (summary) {
        const text = summary({ mappings: 1, devices: 0, bonfires: 0, droppedMappings: [EVIL] });
        ok('it names the skipped profiles', text.indexOf(EVIL) !== -1);
    }
    ok('and the page escapes that text before it becomes markup',
       /innerHTML\s*=\s*'<span class="jpf-dim">'\s*\+\s*escapeHtml\(importSummary\(res\)\)/.test(script));
}

console.log('\n── escapeHtml itself ───────────────────────────────────────────');
{
    const esc = dashboardFns(['escapeHtml'], 'escapeHtml(arg1)');
    ok('a number is escaped rather than thrown on', (() => { try { return esc(0) === '0'; } catch (e) { return false; } })());
    ok('null is empty', esc(null) === '');
}

// ── The client ───────────────────────────────────────────────────────────────

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
    const client = fs.readFileSync(L.profilesPath(), 'utf8');
    ok('no server error message is interpolated raw', client.indexOf('{ message: err.message }') === -1);
}

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) process.exit(1);
