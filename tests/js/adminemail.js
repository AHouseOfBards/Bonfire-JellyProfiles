/*
 * The settings page's Email section (1.7.2), and the PIN-reset switch it governs on the
 * Security tab.
 *
 * Runs the real renderEmail, saveEmail and testEmail out of the dashboard against a stub
 * page and ApiClient, and checks what is drawn and what is sent:
 *
 *  - the stored password is never put back in the form, and a blank field keeps it;
 *  - "Save and send test" saves first, and does not test settings the server refused;
 *  - the switch can't be turned on until email works, but one already on stays usable,
 *    since the server keeps it while email is broken;
 *  - "set up but untested" reaches the Status tab;
 *  - account names and addresses are drawn as text.
 *
 * Red against 3417506, which has no Email section.
 *
 *     node tests/js/adminemail.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const html = process.argv[3] ? require('fs').readFileSync(process.argv[3], 'utf8') : L.readDashboard();
const script = L.dashboardScript(html);
const required = ['renderEmail', 'loadEmail', 'showEmailResult', 'saveEmail', 'testEmail'];
// What showEmailResult calls, on a dashboard that has them; an older one is judged on its own.
const names = required.concat(['showOutcome', 'paintOutcome', 'openAdvancedFor']
    .filter(n => new RegExp('function ' + n + '\\s*\\(').test(script)));
const missing = required.filter(n => !new RegExp('function ' + n + '\\s*\\(').test(script));
ok('this page has an Email section', missing.length === 0 && html.indexOf('id="jpfEmail"') !== -1, missing.join(','));
if (missing.length) {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

const source = L.dashboardI18n(script)
    + L.extractFunction(script, 'field') + '\n'
    + L.extractFunction(script, 'askFirst') + '\n'
    + names.map(n => L.extractFunction(script, n)).join('\n');

// ── A page made of one stub per id ───────────────────────────────────────────
function el(extra) {
    const classes = new Set();
    const node = Object.assign({
        value: '', textContent: '', checked: false, disabled: false, className: '', style: {}, dataset: {},
        children: [], listeners: {},
        classList: {
            toggle: (c, on) => { if (on === undefined ? !classes.has(c) : on) classes.add(c); else classes.delete(c); },
            add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c)
        },
        appendChild: c => { node.children.push(c); return c; },
        setAttribute: () => {},
        addEventListener: (t, fn) => { node.listeners[t] = fn; }
    }, extra || {});
    Object.defineProperty(node, 'innerHTML', { set: () => { throw new Error('innerHTML written'); } });
    // textContent = '' clears children, as in a browser.
    let text = '';
    Object.defineProperty(node, 'textContent', {
        get: () => text,
        set: v => { text = String(v); if (v === '') node.children.length = 0; }
    });
    return node;
}

function makeWorld(state, opts) {
    opts = opts || {};
    const ids = ['emailHost', 'emailPort', 'emailSecurity', 'emailUsername', 'emailPassword', 'emailPasswordHint',
        'emailFrom', 'emailFromName', 'emailStatus', 'enablePinResetByEmail', 'pinResetNeedsEmail', 'emailAccounts',
        'emailResult', 'emailTestTo'];
    const els = {};
    ids.forEach(id => { els[id] = el(); });
    const page = { querySelector: sel => { const e = els[sel.slice(1)]; if (!e) throw new Error('no ' + sel); return e; } };
    const calls = [];
    const statuses = {};
    const confirms = [];
    const ApiClient = {
        getUrl: u => '/' + u,
        getJSON: () => Promise.resolve(state),
        ajax: o => {
            calls.push({ url: o.url, body: o.data ? JSON.parse(o.data) : null });
            const refuse = opts.refuse && opts.refuse(o.url);
            if (refuse) return Promise.reject({ status: 400, text: () => Promise.resolve(refuse) });
            if (o.url === '/plugins/profiles/admin/email/test') return Promise.resolve(Object.assign({}, state, { verified: true }));
            return Promise.resolve(state);
        }
    };
    // Upstream's shape: a callback, and nothing returned. This stub used to return a promise,
    // which no version of jellyfin-web does, and so passed a button whose OK did nothing (#44).
    const Dashboard = { confirm: (msg, title, callback) => { confirms.push(msg); Promise.resolve().then(() => callback(true)); } };
    const document = { createElement: () => el() };
    const setStatus = (p, part, items) => { statuses[part] = items; };
    const errorText = (err, fallback) => err && err.text ? err.text() : Promise.resolve(fallback);
    const fn = new Function('page', 'ApiClient', 'Dashboard', 'document', 'setStatus', 'errorText',
        source + '\nreturn { renderEmail, loadEmail, saveEmail, testEmail };');
    const api = fn(page, ApiClient, Dashboard, document, setStatus, errorText);
    return { els, api, calls, statuses, confirms, page };
}
const settle = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };

(async () => {
    console.log('\n── Drawing ─────────────────────────────────────────────────────');

    const working = {
        host: 'smtp.example.com', port: 587, security: 'starttls', username: 'u', hasPassword: true,
        fromAddress: 'bonfire@example.com', fromName: 'Bonfire', configured: true, verified: true, pinResetByEmail: false,
        accounts: [{ userId: 'u1', name: '<b>Bard</b>', address: 'bard@example.com' }]
    };
    let w = makeWorld(working);
    w.els.emailPassword.value = 'typed earlier';
    w.api.renderEmail(w.page, working);
    ok('the form shows the saved settings', w.els.emailHost.value === 'smtp.example.com' && w.els.emailPort.value === 587
       && w.els.emailFrom.value === 'bonfire@example.com');
    ok('the password field is emptied, never filled', w.els.emailPassword.value === '');
    ok('and says one is saved', w.els.emailPasswordHint.textContent === 'Saved. Leave blank to keep it.');
    ok('working: says so, and nothing on the Status tab', /jpf-note-ok/.test(w.els.emailStatus.className)
       && w.statuses.email.length === 0);
    ok('the PIN-reset switch can be turned on', !w.els.enablePinResetByEmail.disabled
       && w.els.pinResetNeedsEmail.classList.contains('jpf-hidden'));
    const row = w.els.emailAccounts.children[0];
    ok('an address is listed with its account, as text', row && row.children[0].textContent === '<b>Bard</b>'
       && row.children[1].textContent === 'bard@example.com');
    row.children[2].children[0].listeners.click();
    await settle();
    ok('Remove asks first, then removes that account', w.confirms.length === 1 && w.confirms[0].indexOf('<b>Bard</b>') !== -1
       && w.calls.some(c => c.url === '/plugins/profiles/admin/account-email/remove' && c.body.userId === 'u1'));

    const untested = Object.assign({}, working, { verified: false, accounts: [] });
    w = makeWorld(untested);
    w.api.renderEmail(w.page, untested);
    ok('untested: a warning, also on the Status tab', /jpf-note-warn/.test(w.els.emailStatus.className)
       && w.statuses.email.length === 1 && w.statuses.email[0].level === 'warn' && w.statuses.email[0].tab === 'setup');
    ok('the switch is off and can not be turned on', !w.els.enablePinResetByEmail.checked && w.els.enablePinResetByEmail.disabled);
    ok('and says what it needs', !w.els.pinResetNeedsEmail.classList.contains('jpf-hidden')
       && w.els.pinResetNeedsEmail.textContent !== '');
    ok('no addresses: says so', w.els.emailAccounts.children.length === 1
       && w.els.emailAccounts.children[0].textContent === 'No main account has added one yet.');

    const brokenButOn = Object.assign({}, untested, { pinResetByEmail: true });
    w = makeWorld(brokenButOn);
    w.api.renderEmail(w.page, brokenButOn);
    ok('a switch already on stays on and usable while email is broken',
       w.els.enablePinResetByEmail.checked && !w.els.enablePinResetByEmail.disabled);

    const blank = { port: 587, security: 'starttls', configured: false, verified: false, accounts: [] };
    w = makeWorld(blank);
    w.api.renderEmail(w.page, blank);
    ok('not set up: says so, and nothing on the Status tab', w.els.emailStatus.textContent === "Email isn't set up."
       && w.statuses.email.length === 0 && w.els.emailPasswordHint.textContent === '');

    console.log('\n── Saving ──────────────────────────────────────────────────────');

    w = makeWorld(working);
    w.api.renderEmail(w.page, working);
    w.els.emailHost.value = ' smtp.other.example.com ';
    w.els.emailPort.value = 'abc';
    await w.api.saveEmail(w.page);
    let body = w.calls.pop().body;
    ok('a blank password is not sent, so the saved one is kept', body && !('password' in body));
    ok('fields are trimmed and a bad port falls back to 587', body.host === 'smtp.other.example.com' && body.port === 587);

    w.els.emailPassword.value = 'n3w pass';
    await w.api.saveEmail(w.page);
    body = w.calls.pop().body;
    ok('a typed password is sent as typed', body.password === 'n3w pass');

    console.log('\n── Testing ─────────────────────────────────────────────────────');

    w = makeWorld(untested);
    w.api.renderEmail(w.page, untested);
    w.api.testEmail(w.page);
    await settle();
    ok('no address to test: no request', w.calls.length === 0 && w.els.emailResult.textContent === 'Enter an address to send the test to.');

    w.els.emailTestTo.value = 'me@example.com';
    w.api.testEmail(w.page);
    await settle();
    ok('it saves, then tests', w.calls.length === 2 && w.calls[0].url === '/plugins/profiles/admin/email'
       && w.calls[1].url === '/plugins/profiles/admin/email/test' && w.calls[1].body.to === 'me@example.com',
       w.calls.map(c => c.url).join(','));
    ok('and says email is on', w.els.emailResult.textContent === 'Test sent to me@example.com. Email is on.'
       && /jpf-note-ok/.test(w.els.emailStatus.className));

    w = makeWorld(untested, { refuse: u => u === '/plugins/profiles/admin/email' ? "Port 465 isn't supported." : null });
    w.api.renderEmail(w.page, untested);
    w.els.emailTestTo.value = 'me@example.com';
    w.api.testEmail(w.page);
    await settle();
    ok("settings the server refused are not tested", w.calls.length === 1
       && w.els.emailResult.textContent === "Port 465 isn't supported.");

    w = makeWorld(untested, { refuse: u => u === '/plugins/profiles/admin/email/test' ? "The mail server didn't accept the username or password." : null });
    w.api.renderEmail(w.page, untested);
    w.els.emailTestTo.value = 'me@example.com';
    w.api.testEmail(w.page);
    await settle();
    ok("a failed test says the server's reason", w.els.emailResult.textContent === "The mail server didn't accept the username or password.");
    // P2-306: the reason was only on the Email section, which is not where an administrator is
    // looking. Status carries it, with a way back.
    ok("and Status says so too, with the reason and a way to the section",
       w.statuses.email && w.statuses.email.length === 1 && w.statuses.email[0].level === 'warn'
       && /didn't accept the username or password/.test(w.statuses.email[0].text)
       && w.statuses.email[0].tab === 'setup' && w.statuses.email[0].anchor === 'jpfEmail',
       JSON.stringify(w.statuses.email));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
