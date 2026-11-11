/*
 * Round 1 sweep, batch 10: small honesty fixes on the admin page.
 *
 *   R-1252  Re-check says "Still not fixed" / "installed and up to date" when a restart is pending
 *   R-1253  importing a file that holds `null` threw an uncaught TypeError
 *   R-1257  avatar Remove asked nothing
 *   R-1258  Post announcement could be pressed twice
 *   R-1259  an avatar refusal lost the server's reason; the display name was not trimmed
 *   R-1264  Save: the server's reason was dropped, and a throw after saving reported failure
 *
 * Runs the real functions against stubs. Red against the dashboard before the fix:
 *
 *     node tests/js/adminsweep10.js "" /path/to/old/profilesDashboard.html
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
const has = n => new RegExp('function ' + n + '\\s*\\(').test(script);
const fn = n => L.extractFunction(script, n) + '\n';
// A throw in a success handler is now an unhandled rejection, which a browser logs. Here
// that is the behaviour under test, not a crash.
process.on('unhandledRejection', () => {});
const tick = () => new Promise(r => setTimeout(r, 0));

const T = 'const t = function (k) { return k; };\n';
const baseGlobals = T + fn('field');

function el() {
    const listeners = {};
    const cls = new Set();
    return {
        value: '', textContent: '', innerHTML: '', disabled: false, style: {}, dataset: {},
        classList: { add: x => cls.add(x), remove: x => cls.delete(x), toggle: (x, on) => { if (on) cls.add(x); else cls.delete(x); }, contains: x => cls.has(x) },
        addEventListener: (n, f) => { listeners[n] = f; },
        fire: (n) => listeners[n] && listeners[n](),
        querySelector: () => null, querySelectorAll: () => [],
        closest: () => null
    };
}
function pageOf(map) {
    const cache = {};
    return {
        dataset: {},
        querySelector: sel => (map && map[sel]) || (cache[sel] = cache[sel] || el()),
        querySelectorAll: sel => (map && map[sel + '*']) || []
    };
}

(async function () {
    // ── R-1252 ──────────────────────────────────────────────────────────────
    console.log('\n-- Re-check with a restart pending (R-1252) --');
    {
        const src = baseGlobals + fn('retryInjection') + 'return retryInjection;';
        async function run(result) {
            const status = el(), button = el();
            const alerts = [];
            const page = pageOf({ '.retryInjectionBtn*': [button], '.retryInjectionStatus*': [status] });
            const retry = new Function('ApiClient', 'Dashboard', 'renderInjectionStatus', 'console', src)(
                { getUrl: u => u, ajax: () => Promise.resolve(result) },
                { alert: a => alerts.push(a) }, () => {}, console);
            retry(page);
            await tick(); await tick(); await tick();
            return { status: status.textContent, alerts, enabled: !button.disabled };
        }
        const pending = await run({ injectionSucceeded: false, mechanism: { restartRequired: true } });
        ok('a pending restart does not say "still not fixed"', pending.status === '', JSON.stringify(pending.status));
        ok('and does not claim it is installed and up to date', pending.alerts.length === 0, JSON.stringify(pending.alerts));
        ok('the button is usable again', pending.enabled);
        const up = await run({ injectionSucceeded: true, mechanism: { restartRequired: true } });
        ok('a restart pending on a good install does not claim it is up to date', up.alerts.length === 0 && up.status === '', JSON.stringify(up.alerts));
        const bad = await run({ injectionSucceeded: false, failureReason: 'x' });
        ok('a real failure still says so', bad.status === 'admin.banner.stillBroken' && bad.alerts.length === 0);
        const good = await run({ injectionSucceeded: true });
        ok('a real fix still says so', good.status === '' && good.alerts.length === 1);
    }

    // ── R-1253 ──────────────────────────────────────────────────────────────
    console.log('\n-- Import of a file holding null (R-1253) --');
    {
        async function run(text) {
            const box = el();
            const fileInput = el(); fileInput.files = [{}];
            const page = pageOf({ '#importConfigFile': fileInput, '#importConfigResult': box });
            // bindBackup finds its message box by id; give it whichever it asks for.
            const orig = page.querySelector;
            page.querySelector = sel => (sel === '#importConfigBtn' || sel === '#exportConfigBtn') ? el() : orig(sel);
            const posted = [];
            function FileReader() { this.readAsText = () => { this.result = text; this.onload(); }; }
            let thrown = null;
            const bind = new Function('ApiClient', 'Dashboard', 'FileReader', 'askFirst', 'importSummary', 'loadConfiguration', 'loadMappings', 'escapeHtml', 'URL', 'Blob', 'document', 'setTimeout',
                T + fn('bindBackup') + 'return bindBackup;')(
                { getUrl: u => u, ajax: a => { posted.push(a); return Promise.resolve({}); }, getJSON: () => Promise.resolve({}) },
                { showLoadingMsg() {}, hideLoadingMsg() {} }, FileReader, () => new Promise(() => {}), () => '', () => {}, () => {}, s => String(s), URL, Blob, {}, setTimeout);
            try { bind(page); fileInput.fire('change'); } catch (e) { thrown = e; }
            return { thrown, html: Object.keys(page).length && (page.querySelector('#importConfigResult').innerHTML), any: posted.length };
        }
        const r = await run('null');
        ok('a file holding null is refused without throwing', r.thrown === null, r.thrown && r.thrown.message);
        const r2 = await run('"text"');
        ok('a file holding a bare string is refused without throwing', r2.thrown === null, r2.thrown && r2.thrown.message);
    }

    // ── R-1257 ──────────────────────────────────────────────────────────────
    console.log('\n-- Avatar Remove asks first (R-1257) --');
    {
        const calls = [];
        let answer = false;
        const Dashboard = { confirm: (m, ti, cb) => cb(answer), alert() {} };
        const ApiClient = { getUrl: u => u, ajax: a => { calls.push(a); return Promise.resolve(); } };
        const del = new Function('ApiClient', 'Dashboard', 'loadAvatarLibrary', 'console',
            T + fn('askFirst') + fn('deleteAvatar') + 'return deleteAvatar;')(ApiClient, Dashboard, () => {}, console);
        del(pageOf(), 'a1'); await tick();
        ok('declining sends nothing', calls.length === 0, calls.length + ' calls');
        answer = true; calls.length = 0;
        del(pageOf(), 'a1'); await tick();
        ok('confirming removes it', calls.length === 1 && calls[0].type === 'DELETE');
    }

    // ── R-1258 ──────────────────────────────────────────────────────────────
    console.log('\n-- Post announcement in flight (R-1258) --');
    {
        const save = el();
        const msg = el(); msg.value = 'hello';
        const sev = el(); sev.value = 'info';
        const page = pageOf({ '#annSave': save, '#annMessage': msg, '#annSeverity': sev });
        let finish;
        const ApiClient = { getUrl: u => u, ajax: () => new Promise(r => { finish = r; }) };
        const f = new Function('ApiClient', 'resetAnnouncementForm', 'showAnnouncementResult', 'loadAnnouncements', 'localInputToIso', 'errorText', 'openAdvancedFor',
            T + fn('saveAnnouncement') + 'return saveAnnouncement;')(ApiClient, () => {}, () => {}, () => {}, () => null, () => Promise.resolve(''), () => {});
        f(page);
        ok('the button is disabled while the post is in flight', save.disabled === true);
        finish({}); await tick(); await tick();
        ok('and enabled again afterwards', save.disabled === false);
    }

    // ── R-1259 ──────────────────────────────────────────────────────────────
    console.log('\n-- Avatar upload: name and refusal (R-1259) --');
    {
        async function run(file, rejection) {
            const sent = [];
            function FileReader() { this.readAsDataURL = () => { this.onload({ target: { result: 'data:x' } }); }; }
            function Image() { Object.defineProperty(this, 'src', { set: () => { this.onload(); } }); }
            const up = new Function('ApiClient', 'FileReader', 'Image', 'rejectAvatarFile', 'currentImageSpec', 'hasTransparency', 'squareToDataUrl', 'console',
                T + fn('refusalCode') + fn('refusalMessage') + fn('uploadOneAvatar') + 'return uploadOneAvatar;')(
                { getUrl: u => u, ajax: a => { sent.push(a); return rejection ? Promise.reject(rejection) : Promise.resolve(); } },
                FileReader, Image, () => null, () => ({ master: 1, thumb: 1, jpeg: 1, mb: 5 }), () => false, () => 'data:img', { error() {} });
            let error = null;
            try { await up(file); } catch (e) { error = e; }
            return { sent, error };
        }
        const named = await run({ name: '  Fox  .png' });
        ok('the display name is trimmed', named.sent.length === 1 && JSON.parse(named.sent[0].data).displayName === 'Fox', named.sent[0] && named.sent[0].data);
        const blank = await run({ name: '   .png' });
        ok('a blank name falls back to Avatar', blank.sent.length === 1 && JSON.parse(blank.sent[0].data).displayName === 'Avatar');
        const refused = await run({ name: 'a.png' }, { status: 400, headers: { get: h => h === 'X-Bonfire-Error' ? 'image_invalid' : null } });
        ok('the server\'s own reason is shown when it gave one', refused.error && refused.error.message === 'admin.err.imageInvalid', refused.error && refused.error.message);
        const plain = await run({ name: 'a.png' }, { status: 500, headers: { get: () => null } });
        ok('with none, the general sentence is', plain.error && plain.error.message === 'admin.avatars.rejected');
    }

    // ── R-1264 ──────────────────────────────────────────────────────────────
    console.log('\n-- Save: the reason, and a throw after saving (R-1264) --');
    {
        const src = fn('refusalCode') + fn('refusalMessage') + fn('saveConfiguration');
        const reasonKey = /admin\.err\.forbidden/.test(src);
        // Drive saveConfiguration the way adminsave.js does is heavy; read what it does with
        // a failure and with a throw in its success handler by running it on a minimal form.
        const page = (function () {
            const map = {};
            return {
                _configLoaded: true, dataset: {},
                querySelector: sel => {
                    if (!map[sel]) map[sel] = Object.assign(el(), { value: '5', checked: false });
                    return map[sel];
                },
                querySelectorAll: () => []
            };
        })();
        async function run(rejection, throwInSuccess) {
            const alerts = [];
            let result;
            const ApiClient = { getUrl: u => u, ajax: () => rejection ? Promise.reject(rejection) : Promise.resolve({}) };
            const Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert: a => alerts.push(a) };
            const stub = n => (script.indexOf('function ' + n) !== -1 ? fn(n) : '');
            const f = new Function('ApiClient', 'Dashboard', 'console', 'window', 'localStorage', 'collectTrusted2', 'noteSavedSettings', 'loadAvatarLibrary', 'setUnsaved2',
                L.dashboardI18n(script) + fn('normalizeImageQuality') + fn('normalizeTheme') + stub('setUnsaved') + stub('collectTrusted')
                + fn('refusalCode') + fn('refusalMessage') + fn('saveConfiguration') + 'return saveConfiguration;');
            const save = f(ApiClient, Dashboard, { error() {} }, { dispatchEvent() {} }, { setItem() {} }, null, () => { if (throwInSuccess) throw new Error('boom'); }, () => {}, null);
            try { save(page); } catch (e) { return { alerts, threw: e.message }; }
            await tick(); await tick(); await tick();
            return { alerts };
        }
        let r;
        try {
            r = await run({ status: 400, headers: { get: h => h === 'X-Bonfire-Error' ? 'forbidden' : null } });
            const m = r.alerts[0] && r.alerts[0].message;
            ok('a refusal the server explained is shown as that', m && /Couldn't save/.test(m) === false && m !== undefined, JSON.stringify(m) + (r.threw ? ' threw ' + r.threw : ''));
            r = await run(null, true);
            ok('a throw after a successful save does not report failure',
                !r.alerts.some(a => a && /Couldn't save/.test(a.message)), JSON.stringify(r.alerts));
        } catch (e) {
            ok('saveConfiguration ran under the stub', false, e.message);
        }
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
