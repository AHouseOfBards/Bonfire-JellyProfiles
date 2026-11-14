/*
 * r2-38-04: the Make-own-account, Link, Make-profile and PIN-prompts buttons stayed clickable
 * while their request ran, so a second click posted again (the link one twice).
 *
 * Runs the real bindStandaloneForms and bindAccountExtras against stub controls whose
 * requests stay open until the harness settles them. Red against the dashboard before the fix:
 *
 *     node tests/js/adminr2y38b.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const script = L.dashboardScript(L.readDashboard());
const has = n => new RegExp('function ' + n + '\\s*\\(').test(script);
const source = ['field', 'normId', 'bindStandaloneForms', 'bindAccountExtras'].concat(has('holdWhile') ? ['holdWhile'] : [])
    .map(n => L.extractFunction(script, n)).join('\n');

async function drain() { for (let i = 0; i < 6; i++) await new Promise(r => setImmediate(r)); }

function control(extra) {
    const c = Object.assign({
        value: 'x', checked: false, disabled: false, textContent: '',
        classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
        getAttribute: () => 'id', addEventListener(e, fn) { c.click = fn; }
    }, extra || {});
    return c;
}

(async function main() {
    const pending = [];
    const ApiClient = {
        getUrl: u => u,
        ajax: () => new Promise((res, rej) => pending.push({ res, rej }))
    };
    const noop = () => {};
    const stubs = {
        askFirst: () => Promise.resolve(), paintOutcome: noop, errorText: (e, f) => Promise.resolve(f),
        loadMappings: noop, t: k => k, Dashboard: { alert: noop }, page: null
    };
    const make = new Function('ApiClient', 'askFirst', 'paintOutcome', 'errorText', 'loadMappings', 't', 'Dashboard',
        source + '; return { bindStandaloneForms: bindStandaloneForms, bindAccountExtras: bindAccountExtras };');
    const api = make(ApiClient, stubs.askFirst, stubs.paintOutcome, stubs.errorText, stubs.loadMappings, stubs.t, stubs.Dashboard);

    // The standalone form.
    const go = control();
    const fields = { '.standalone-go': go, '.standalone-cancel': control(), '.standalone-password': control({ value: 'pw' }),
        '.standalone-username': control(), '.standalone-result': control() };
    const form = { getAttribute: () => 'p1', querySelector: sel => fields[sel] || control() };
    const sPage = { querySelectorAll: sel => (sel === '.standalone-form' ? [form] : []), querySelector: () => null };
    api.bindStandaloneForms(sPage);

    console.log('\n-- Buttons stay pressed while the request runs (r2-38-04) --');
    pending.length = 0;
    go.click();
    await drain();
    ok('standalone: one request, button disabled', pending.length === 1 && go.disabled === true, pending.length + ' ' + go.disabled);
    go.click();
    await drain();
    pending[0].rej(new Error('x'));
    await drain();
    ok('standalone: usable again when it ends', go.disabled === false);

    // The account extras.
    const link = control(), pin = control(), toProfile = control();
    const result = control();
    const box = {
        getAttribute: () => 'acct',
        querySelector: sel => ({
            '.extras-result': result, '.link-go': link, '.pin-prompts-go': pin, '.to-profile-go': toProfile,
            '.link-user': control({ value: 'u2' }), '.pin-prompt-ask': control({ checked: true }),
            '.to-profile-household': { value: 'h', options: [{ textContent: 'H' }], selectedIndex: 0 },
            '.to-profile-name': control(), '.to-profile-later': control({ value: 'keep' })
        })[sel] || null,
        querySelectorAll: () => []
    };
    const aPage = { _mappings: { MasterUsers: [] }, querySelectorAll: sel => (sel === '.jpf-account-extras' ? [box] : []) };
    api.bindAccountExtras(aPage);

    for (const [name, btn, ok2] of [['link', link], ['PIN prompts', pin], ['make profile', toProfile]]) {
        pending.length = 0;
        btn.click();
        await drain();
        btn.click();           // a second click, as a user would on a slow server
        await drain();
        const disabled = btn.disabled;
        // A disabled button is not clickable in a browser; model that.
        if (disabled && pending.length > 1) pending.length = 1;
        ok(name + ': disabled while in flight', disabled === true, String(disabled));
        pending.forEach(p => p.res({}));
        await drain();
        ok(name + ': usable again when it ends', btn.disabled === false);
        void ok2;
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
