/*
 * Round 1 sweep, batch 10: small honesty fixes on the admin page.
 *
 *   R-1252  Re-check says "Still not fixed" / "installed and up to date" when a restart is pending
 *   R-1253  importing a file that holds `null` threw an uncaught TypeError
 *   R-1257  avatar Remove asked nothing
 *   R-1258  Post announcement could be pressed twice
 *   R-1259  an avatar refusal lost the server's reason; the display name was not trimmed
 *   R-1264  Save: the server's reason was dropped, and a throw after saving reported failure
 *   R-1254  household buttons and the limit box carried no account name for a screen reader
 *   R-1255  open households folded after every action; import loaded mappings twice;
 *           "Invalid Date" in the audit list; the audit download said nothing of IP addresses
 *   R-1260  repeated Sign out / Edit / Delete / Remove buttons carried no item name
 *   R-1261  Simple theme view showed an empty font box and a blank pattern
 *   R-1262  a deleted default-theme selection fell back to Bonfire without a word
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
// A function the old code lacks: absent, so the section runs and its assertions go red
// instead of the whole harness stopping.
const opt = n => (has(n) ? fn(n) : '');
// A throw in a success handler is now an unhandled rejection, which a browser logs. Here
// that is the behaviour under test, not a crash.
process.on('unhandledRejection', () => {});
// Set on the last line. Anything that ends the process earlier, however it gets there, is a
// harness that did not finish and must not exit 0.
let reachedEnd = false;
process.on('exit', code => {
    if (!reachedEnd && code === 0) {
        console.log('FAIL  the harness stopped before its end');
        process.exitCode = 1;
    }
});
const tick = () => new Promise(r => setTimeout(r, 0));

const T = 'const t = function (k) { return k; };\n';
// Like T, but shows the values it was given, so a label that names someone can be read.
const TV = 'const t = function (k, v) { return v ? k + "|" + Object.keys(v).map(function (x) { return v[x]; }).join("|") : k; };\n';
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
        ok('a blank name falls back to the translated default name', blank.sent.length === 1 && JSON.parse(blank.sent[0].data).displayName === 'admin.avatars.defaultName');
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

    // ── R-1254, R-1255, R-1260 ──────────────────────────────────────────────
    // Plumbing for the sections below: a small document, and the household list drawn by
    // the real loadMappings.
    function fakeEl() {
        const e = el();
        e.attrs = {}; e.children = [];
        e.setAttribute = (k, v) => { e.attrs[k] = String(v); };
        e.getAttribute = k => (k in e.attrs ? e.attrs[k] : null);
        e.appendChild = c => { e.children.push(c); return c; };
        e.insertBefore = c => { e.children.unshift(c); return c; };
        e.click = () => e.fire('click');
        return e;
    }
    const fakeDoc = { createElement: () => fakeEl() };
    const tagsOf = (html, re) => (html.match(/<(button|input)\b[^>]*>/g) || []).filter(x => re.test(x));
    const labelOf = tag => { const m = /aria-label="([^"]*)"/.exec(tag); return m ? m[1] : null; };

    console.log('\n-- Household buttons say whose they are (R-1254) --');
    const data = {
        masterUsers: [
            { profileUserId: 'aaaa', profileName: 'Alice', requiresPin: true, maxProfiles: 5, limitOverride: 3 },
            { profileUserId: 'bbbb', profileName: 'Bob', requiresPin: true, maxProfiles: 5 }
        ],
        subProfiles: [
            { profileUserId: 'cccc', masterUserId: 'aaaa', profileName: 'Kid One', requiresPin: true },
            { profileUserId: 'dddd', masterUserId: 'bbbb', profileName: 'Kid Two', requiresPin: true }
        ]
    };
    async function drawHouseholds(openMasters) {
        const container = fakeEl();
        const toggles = ['aaaa', 'bbbb'].map(id => {
            const b = fakeEl(); b.attrs['data-master'] = id;
            b.attrs['aria-expanded'] = openMasters.indexOf(id) !== -1 ? 'true' : 'false';
            return b;
        });
        const panels = {};
        const page = {
            dataset: {}, _mappings: null,
            querySelector: sel => {
                if (sel === '#masterUsersList') return container;
                const m = /^\.sub-profile-panel\[data-master="(.*)"\]$/.exec(sel);
                if (m) return panels[m[1]] || (panels[m[1]] = Object.assign(fakeEl(), { style: { display: 'none' } }));
                return fakeEl();
            },
            querySelectorAll: sel => sel === '.sub-profile-toggle' ? toggles : []
        };
        const calls = [];
        const src = TV + fn('field') + fn('escapeHtml') + fn('normId') + opt('namedAria') + fn('collectTrusted') + fn('loadMappings') + 'return loadMappings;';
        const load = new Function('ApiClient', 'renderTrusted', 'renderGettingStarted', 'renderInjectionStatus', 'setStatus', 'pinStatusRows',
            'askFirst', 'Dashboard', 'loadConfiguration', 'document', 'window', 'bindStandaloneForms', 'bindPinPrompts', 'bindAccountExtras',
            src)(
            { getUrl: u => u, getJSON: () => Promise.resolve(data), serverVersion: () => '10.11.0' },
            () => {}, () => {}, () => {}, () => {}, () => [], () => new Promise(() => {}), { alert() {}, hideLoadingMsg() {} }, () => {}, fakeDoc, {}, () => {}, () => {}, () => {});
        load(page); await tick(); await tick();
        return { html: container.innerHTML, toggles, panels };
    }
    {
        const r = await drawHouseholds([]);
        const buttons = {
            'Reset PIN': tagsOf(r.html, /reset-pin-btn/),
            'Set': tagsOf(r.html, /save-limit-btn/),
            'Reset': tagsOf(r.html, /clear-limit-btn/),
            'Make own account': tagsOf(r.html, /standalone-btn/),
            'the limit box': tagsOf(r.html, /user-limit-input/)
        };
        // Four Reset PIN (2 mains, 2 profiles), two Set, one Reset, two Make own account, two boxes.
        const want = { 'Reset PIN': 4, 'Set': 2, 'Reset': 1, 'Make own account': 2, 'the limit box': 2 };
        Object.keys(buttons).forEach(k => {
            ok(k + ': found ' + want[k], buttons[k].length === want[k], buttons[k].length + ' found');
            ok(k + ': every one carries its own name',
                buttons[k].length > 0 && buttons[k].every(tag => /Alice|Bob|Kid One|Kid Two/.test(labelOf(tag) || '')),
                JSON.stringify(buttons[k].map(labelOf)));
            ok(k + ': no two share a label', new Set(buttons[k].map(labelOf)).size === buttons[k].length, JSON.stringify(buttons[k].map(labelOf)));
        });
    }

    console.log('\n-- Other repeated buttons say which item (R-1260) --');
    {
        const src = TV + fn('field') + fn('escapeHtml') + opt('namedAria');
        const mk = (names, body) => new Function('t2', src + body);
        const sessions = mk(0, fn('sessionRowsHtml') + 'return sessionRowsHtml;')()([
            { userId: 'u1', deviceId: 'd1', username: 'Alice', deviceName: 'Living room TV', client: 'x' },
            { userId: 'u2', deviceId: 'd2', username: 'Bob', deviceName: 'Phone', client: 'x' }]);
        const so = tagsOf(sessions, /jpf-signout-btn/);
        ok('sign out: each names the person and the device',
            so.length === 2 && /Alice/.test(labelOf(so[0]) || '') && /Living room TV/.test(labelOf(so[0]) || '') && /Bob/.test(labelOf(so[1]) || ''),
            JSON.stringify(so.map(labelOf)));

        const themeData = { custom: [{ id: 'one', theme: { name: { text: 'First' } } }, { id: 'two', theme: { name: { text: 'Second' } } }], 'default': 'bonfire',
            fonts: [{ file: 'a.woff2', family: 'Fraunces', weightMin: 400, weightMax: 400, fallback: 'serif', usedBy: [] }, { file: 'b.woff2', family: 'Lora', weightMin: 400, weightMax: 400, fallback: 'serif', usedBy: [] }] };
        const themeFns = fn('getThemePath') + fn('themeDisplayName') + fn('customThemeRowsHtml') + fn('fontRowsHtml') + fn('themeFontCssName');
        const rows = mk(0, themeFns + 'return [customThemeRowsHtml, fontRowsHtml];')();
        const th = tagsOf(rows[0](themeData), /jpf-te-act/);
        ok('theme Edit / Duplicate / Export / Delete name the theme',
            th.length === 8 && th.every(x => /First|Second/.test(labelOf(x) || '')), JSON.stringify(th.map(labelOf)));
        ok('no two theme buttons share a label', new Set(th.map(labelOf)).size === th.length);
        const fd = tagsOf(rows[1](themeData), /jpf-te-font-delete/);
        ok('font Delete names the font', fd.length === 2 && /Fraunces/.test(labelOf(fd[0]) || '') && /Lora/.test(labelOf(fd[1]) || ''), JSON.stringify(fd.map(labelOf)));

        // Announcements are built node by node.
        const list = fakeEl();
        const page = { dataset: {}, querySelector: () => list, querySelectorAll: () => [] };
        const mkAnn = new Function('document', 'ApiClient', 'announcementSchedule', 'isoToLocalInput', 'openAdvancedFor', 'askFirst', 'loadAnnouncements',
            TV + fn('field') + fn('escapeHtml') + opt('namedAria') + fn('renderAnnouncements') + 'return renderAnnouncements;')(
            fakeDoc, { getUrl: u => u, ajax: () => Promise.resolve({}) }, () => '', () => '', () => {}, () => new Promise(() => {}), () => {});
        mkAnn(page, [{ id: 'a1', message: 'Maintenance tonight', severity: 'info', showing: true }, { id: 'a2', message: 'New profiles', severity: 'info', showing: true }]);
        const labels = [];
        (function walk(n) { (n.children || []).forEach(c => { if (c.attrs && c.attrs['aria-label']) labels.push(c.attrs['aria-label']); walk(c); }); })(list);
        ok('announcement Edit and Delete name the announcement',
            labels.length === 4 && labels.filter(x => /Maintenance/.test(x)).length === 2 && labels.filter(x => /New profiles/.test(x)).length === 2, JSON.stringify(labels));
    }

    console.log('\n-- Things the list does after an action (R-1255) --');
    {
        const r = await drawHouseholds(['bbbb']);
        ok('a household that was open is open again after the list is redrawn',
            r.toggles[1].attrs['aria-expanded'] === 'true' && r.panels.bbbb && r.panels.bbbb.style.display === 'block',
            JSON.stringify(r.toggles.map(b => b.attrs['aria-expanded'])));
        ok('one that was closed stays closed', r.toggles[0].attrs['aria-expanded'] === 'false');

        // Import: loadConfiguration already loads the mappings, so a second call loads them twice.
        const box = el();
        const fileInput = el(); fileInput.files = [{}];
        const page = pageOf({ '#importConfigFile': fileInput, '#backupResult': box });
        const loads = { cfg: 0, map: 0 };
        function FileReader() { this.readAsText = () => { this.result = '{"Mappings":[]}'; this.onload(); }; }
        const bind = new Function('ApiClient', 'Dashboard', 'FileReader', 'askFirst', 'importSummary', 'importResultHtml', 'loadConfiguration', 'loadMappings', 'escapeHtml', 'URL', 'Blob', 'document', 'setTimeout',
            T + fn('bindBackup') + 'return bindBackup;')(
            { getUrl: u => u, ajax: () => Promise.resolve({}), getJSON: () => Promise.resolve({}) },
            { showLoadingMsg() {}, hideLoadingMsg() {} }, FileReader, () => Promise.resolve(), () => '', () => '', () => { loads.cfg++; }, () => { loads.map++; }, s => String(s), URL, Blob, {}, setTimeout);
        bind(page); fileInput.fire('change'); await tick(); await tick(); await tick();
        ok('an import reloads the configuration once', loads.cfg === 1, JSON.stringify(loads));
        ok('and does not load the mappings a second time itself', loads.map === 0, JSON.stringify(loads));

        // The audit list.
        const logs = [{ Timestamp: 'not a date', MasterUsername: 'Alice', TargetUsername: 'Kid' }, { Timestamp: '2026-01-02T03:04:05Z', MasterUsername: 'Bob', TargetUsername: 'Kid' }];
        const list = el();
        const ap = { querySelector: s => s === '#auditLogsList' ? list : (s === '#auditCount' ? el() : Object.assign(el(), { value: '' })) };
        const draw = new Function('auditEntries', 'auditField', 'auditVisible', 'escapeHtml',
            T + fn('renderAuditLogs') + 'return renderAuditLogs;')(
            logs, (l, k) => l[k], () => ({ filtered: false, rows: logs }), s => String(s));
        draw(ap);
        ok('an entry with no readable time does not say "Invalid Date"', list.innerHTML.indexOf('Invalid Date') === -1, list.innerHTML.slice(0, 200));
        ok('a good time is still shown', /2026|1\/2|2\/1/.test(list.innerHTML));
        ok('the audit download says it holds IP addresses', /id="auditDownload"[\s\S]{0,400}<\/div>\s*<div class="fieldDescription" data-i18n="admin\.audit\.exportNote"/.test(L.readDashboard()),
            'no note after the audit controls');
    }

    console.log('\n-- Simple theme view and the default select (R-1261, R-1262) --');
    {
        const mkSel = opts => {
            const s = fakeEl();
            s.options = (opts || []).map(v => ({ value: v, textContent: v }));
            s.appendChild = c => { s.options.push(c); return c; };
            s.insertBefore = c => { s.options.unshift(c); return c; };
            return s;
        };
        function fill(draftFont, draftPattern) {
            const sels = { '#teFont': mkSel(['Fraunces']), '#tePattern': mkSel(['', 'leaves']), '#teAvatar': mkSel(), '#teButton': mkSel(), '#teAccent': mkSel(), '#teBg1': mkSel(), '#teBg2': mkSel(), '#teText': mkSel() };
            const page = { _themeEditor: { view: 'simple', draft: {} }, querySelector: s => sels[s] || mkSel(), querySelectorAll: () => [] };
            // fillThemeEditor also draws the photo box and the swatches, and tests a pattern
            // name against PATTERN_FILE: the real setSwatch and PATTERN_FILE, and a photo sync
            // that does nothing (this section is about the font and pattern boxes).
            const f = new Function('document', 'simpleThemeValues', 'themeFonts', 'syncThemePhoto',
                T + 'const PATTERN_FILE = /^[0-9a-f]{32}\\.(png|webp)$/;\n' + fn('setSwatch') + fn('fillThemeEditor') + 'return fillThemeEditor;')(
                { createElement: () => ({}) }, () => ({ accent: '#000000', bg1: '#000000', bg2: '#000000', text: '#ffffff', font: draftFont, avatar: 'circle', button: 'pill', pattern: draftPattern }), () => [], () => {});
            f(page);
            return sels;
        }
        const noFont = fill('', '');
        ok('no font of its own: the box says so, not blank',
            noFont['#teFont'].options.some(o => o.value === '' && o.textContent === 'admin.themes.fontUnset'), JSON.stringify(noFont['#teFont'].options.map(o => o.textContent)));
        const odd = fill('Fraunces', 'confetti');
        ok('a pattern the list does not carry is shown by name, not as None',
            odd['#tePattern'].options.some(o => o.value === 'confetti') && odd['#tePattern'].value === 'confetti', odd['#tePattern'].value);
        const known = fill('Fraunces', 'leaves');
        ok('a listed pattern adds nothing', known['#tePattern'].options.length === 2);

        // Default theme select.
        function drawSelect(chosen) {
            const select = fakeEl(); select.value = chosen; select.options = [];
            select.appendChild = c => { select.options.push(c); return c; };
            Object.defineProperty(select, 'innerHTML', { get: () => '', set: () => { select.options = []; } });
            const unsaved = [];
            const page = { _themes: { data: { builtIn: [{ id: 'bonfire' }], custom: [] } }, _themeStaticOptions: [{ value: 'bonfire' }], querySelector: () => select };
            const f = new Function('document', 'themeOfferedIds', 'themeDisplayName', 'normalizeTheme', 'setUnsaved',
                T + fn('renderDefaultThemeOptions') + 'return renderDefaultThemeOptions;')(
                fakeDoc, () => ['bonfire'], () => '', (v, o) => (o.indexOf(v) !== -1 ? v : 'bonfire'), (p, on) => unsaved.push(on));
            f(page);              // first draw
            const first = unsaved.length;
            select.value = 'mine'; // the administrator picks a custom theme, which is then deleted
            f(page);
            return { first, unsaved };
        }
        const d = drawSelect('bonfire');
        ok('a selection that is deleted marks the page unsaved', d.unsaved.length === 1 && d.unsaved[0] === true, JSON.stringify(d));
        ok('a first draw does not', d.first === 0);
    }

    reachedEnd = true;
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => {
    // The handler above swallows rejections on purpose, and this one was swallowed with it:
    // a section threw, the rest never ran, and the harness exited 0 with no totals.
    console.log('FAIL  the harness stopped before its end: ' + (e && e.stack || e));
    process.exit(1);
});
