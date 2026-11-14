/*
 * What a failed request says on screen.
 *
 * Two things went wrong together. Alerts appended the raw message of whatever was thrown
 * ("Error saving profile: Failed to fetch", "Unexpected token < in JSON"): English written
 * for a developer, and untranslated. And the Bonfire status loader called res.json() on any
 * response that was not a 401, so a 500 page was parsed as JSON and the parse error became
 * the reason shown.
 *
 * The loader is run for real against a 500 whose body is not JSON. The wording is read
 * from EN_STRINGS key by key, not summed, so a failure names the key.
 *
 *     node tests/js/errorwording.js /path/to/old/profiles.js
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

let src = fs.readFileSync(L.profilesPath(), 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const shipped = src;
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

function makeEl() {
    const el = { style: {}, attrs: {}, textContent: '', addEventListener() {}, removeEventListener() {},
                 setAttribute: (k, v) => { el.attrs[k] = v; }, getAttribute: () => null,
                 appendChild() {}, remove() {}, focus() {} };
    return el;
}

function run(res, shared) {
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', language: 'en' },
        setTimeout() { return 1; }, clearTimeout() {}, setInterval() {}, clearInterval() {},
        // In the shared-form run only the library list refuses: since R-1228 the other
        // reads report a 401 too, and this asks about the library list alone.
        fetch: (url) => (shared && String(url).indexOf('plugins/profiles/form-options') === -1)
            ? Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([]) })
            : Promise.resolve(res),
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector() { return null; }, querySelectorAll() { return []; },
            getElementById: () => null, createElement: makeEl,
            head: { appendChild() {} },
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
    let expired = 0;
    plugin.handleSessionExpired = () => { expired++; };
    plugin.navIsCurrent = () => true;
    if (shared) {
        // R-1177: the library list is read like the others, so a refusal is a refusal.
        let rejected = '';
        return plugin.fetchSharedFormData({ getUrl: u => '/' + u }, { token: 't', masterUserId: 'm' })
            .catch(e => { rejected = String(e && e.message); })
            .then(() => ({ expired, rejected }));
    }
    let html = '';
    const container = { set innerHTML(v) { html = v; }, get innerHTML() { return html; } };
    const content = { querySelector: () => container };
    plugin.loadBonfireStatus(content, { getUrl: u => '/' + u }, 'token');
    return new Promise(r => setImmediate(r)).then(() => new Promise(r => setImmediate(r)))
        .then(() => ({ html: html }));
}

(async () => {
    console.log('\n-- The status loader checks the response before reading it --');
    let jsonCalls = 0;
    const bad = await run({
        ok: false, status: 500,
        json: () => { jsonCalls++; return Promise.reject(new SyntaxError('Unexpected token < in JSON at position 0')); }
    });
    ok('a 500 is not parsed as JSON', jsonCalls === 0);
    ok('and the parse error is not shown', bad.html.indexOf('Unexpected token') === -1);
    ok('a sentence is shown instead', /Couldn't load Bonfire/.test(bad.html));

    console.log('\n-- The form data is checked before it is read (R-1177) --');
    const refused = await run({
        ok: false, status: 401,
        json: () => Promise.reject(new SyntaxError('Unexpected end of JSON input'))
    }, true);
    ok('a 401 on the library list is a session that has ended', refused.expired === 1, 'expired ' + refused.expired);
    ok('and the parse error is not what is reported', refused.rejected.indexOf('JSON') === -1, refused.rejected);

    console.log('\n-- No string takes a raw message --');
    const block = shipped.slice(shipped.indexOf('const EN_STRINGS'));
    const end = block.indexOf('\n    };');
    const keys = block.slice(0, end).split(/\r?\n/).filter(l => /^\s*'[\w.]+':/.test(l));
    ok('EN_STRINGS was found', keys.length > 300);
    keys.filter(l => /\{message\}/.test(l)).forEach(l => ok(l.trim().split(':')[0] + ' has no {message}', false));
    ok('no string carries {message}', !keys.some(l => /\{message\}/.test(l)));
    const callSites = shipped.split(/\r?\n/).filter(l => /\{\s*message:\s*escapeHtml\(err\.message\)/.test(l));
    ok('no call site passes err.message into a string', callSites.length === 0);

    console.log('\n-- Words a household does not use --');
    const banned = [['errors.validationError', /Validation/], ['errors.unauthorized', /.*/],
                    ['bonfire.proxyHint', /.*/], ['panic.dialogBody', /gate|Bonfire/],
                    ['profileForm.noPinSetHint', /switcher|reach/], ['switcher.notSavedBody', /store/]];
    banned.forEach(b => {
        const line = keys.find(l => l.indexOf("'" + b[0] + "'") !== -1);
        ok(b[0] + ' is free of developer wording', !line || !b[1].test(line.slice(line.indexOf(':') + 1)));
    });

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
