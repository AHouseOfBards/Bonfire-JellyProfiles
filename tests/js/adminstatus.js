/*
 * The Status tab and the report on the settings page.
 *
 *   - Status lists what each loader reported, worst first, and counts only what needs
 *     attention. A part reported again replaces itself rather than adding to the list.
 *   - A "fix" link opens the tab it names, opening Advanced if the target is inside it.
 *   - Text goes in as text: nothing reported can become markup.
 *   - The report names no account, profile, id or hash.
 *
 *   node tests/js/adminstatus.js [path/to/profilesDashboard.html]
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name + (detail ? '  — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const html = fs.readFileSync(process.argv[2] || L.dashboardPath(), 'utf8');
const script = L.dashboardScript(html);

let pieces;
try {
    const consts = ['STATUS_ORDER', 'LEVEL_RANK'].map(name => {
        const m = new RegExp('const ' + name + ' = [^;]+;').exec(script);
        if (!m) throw new Error('no ' + name);
        return m[0];
    });
    // The chips' words are looked up when drawn, in the page's language.
    pieces = L.dashboardI18n(script) + consts.join('\n') + '\n'
        + ['field', 'levelLabel', 'setStatus', 'renderStatus', 'goTo', 'buildReport'].map(n => L.extractFunction(script, n)).join('\n');
} catch (e) {
    ok('the Status code is there to test', false, e.message);
    finish();
}

// Just enough DOM: elements that keep their children and text.
function el(tag) {
    return {
        tag, children: [], className: '', attrs: {}, listeners: {}, _text: '',
        set textContent(v) { this._text = v; this.children = []; },
        get textContent() { return this._text + this.children.map(c => c.textContent).join(' '); },
        appendChild(c) { this.children.push(c); return c; },
        setAttribute(k, v) { this.attrs[k] = v; },
        addEventListener(t, f) { this.listeners[t] = f; },
        get innerHTML() { throw new Error('innerHTML read'); },
        set innerHTML(v) { this.usedInnerHTML = true; }
    };
}
const list = el('div'), summary = el('div');
const anchor = { scrolled: false, opened: null, scrollIntoView() { this.scrolled = true; },
                 closest: () => ({ set open(v) { anchor.opened = v; } }) };
const page = {
    dataset: { pluginVersion: '1.7.2.0-beta' },
    querySelector: sel => sel === '#statusChecks' ? list : sel === '#statusSummary' ? summary
        : sel === '#jpfClientScript' ? anchor : null,
    _shown: null,
    _showTab(n) { this._shown = n; },
};
const sandbox = { document: { createElement: el }, ApiClient: null, navigator: { userAgent: 'UA' } };
const fn = new Function('document', 'ApiClient', 'navigator',
    pieces + '\nreturn { setStatus, buildReport };')(sandbox.document,
    { getJSON: () => Promise.resolve({ Version: '10.11.5' }), getUrl: p => p }, sandbox.navigator);

console.log('\n── Status ──────────────────────────────────────────────────────');
fn.setStatus(page, 'script', [{ level: 'ok', text: 'Script fine.' }]);
ok('all well says so', summary.textContent === 'Nothing needs your attention.', summary.textContent);

fn.setStatus(page, 'orphans', [{ level: 'info', text: '2 leftover records.', tab: 'households', fix: 'Clean up' }]);
fn.setStatus(page, 'maintenance', [{ level: 'warn', text: 'Closed.', tab: 'messages', fix: 'Reopen' }]);
fn.setStatus(page, 'panic', [{ level: 'bad', text: 'Switched off.' }]);
const levels = list.children.map(c => c.attrs['data-level']);
ok('worst first', levels.join(',') === 'bad,warn,info,ok', levels.join(','));
ok('only problems and warnings are counted', summary.textContent === '2 things need your attention.', summary.textContent);

fn.setStatus(page, 'maintenance', []);
ok('a part reported again replaces itself', list.children.length === 3 && summary.textContent === '1 thing needs your attention.',
   list.children.length + ' / ' + summary.textContent);

fn.setStatus(page, 'avatars', [{ level: 'info', text: '<img src=x onerror=alert(1)>' }]);
const row = list.children.find(c => c.textContent.indexOf('<img') !== -1);
ok('text stays text', row && !row.usedInnerHTML && list.usedInnerHTML !== true);

const withFix = list.children.find(c => c.children.length === 3);
ok('a row with somewhere to go has a button', !!withFix);
fn.setStatus(page, 'script', [{ level: 'bad', text: 'Script broken.', tab: 'troubleshoot', anchor: 'jpfClientScript', fix: 'Check it' }]);
const scriptRow = list.children.find(c => c.textContent.indexOf('Script broken.') !== -1);
scriptRow.children[2].listeners.click();
ok('it opens that tab', page._shown === 'troubleshoot');
ok('and the section, opening Advanced around it', anchor.scrolled && anchor.opened === true);

console.log('\n── The report ──────────────────────────────────────────────────');
page._config = { MaxProfilesPerUser: 5, RequireMainPin: true, PanicCodeHash: 'SECRETHASH', ProxyClients: ['moonfin'] };
page._mappings = {
    injectionSucceeded: true,
    failureReason: 'Access to /home/robin/jellyfin/index.html denied',
    mechanism: { mode: 'middleware', middlewareRegistered: true, middlewareServed: 3 },
    masterUsers: [{ profileName: 'Robin', profileUserId: '8e3cdfa579a84bb9bd9a0e96b7dc974a', requiresPin: false }],
    subProfiles: [{ profileName: 'Kiddo', profileUserId: 'a90f11cb42a1432d94bb97cc2d42ef8b' }],
};
fn.buildReport(page).then(text => {
    ok('it has the versions', text.indexOf('Bonfire 1.7.2.0-beta') !== -1 && text.indexOf('Jellyfin 10.11.5') !== -1);
    ok('and the counts', /Main accounts: 1, profiles: 1, main accounts without a PIN: 1/.test(text));
    ['Robin', 'Kiddo', '8e3cdfa5', 'a90f11cb', 'SECRETHASH', '/home/robin'].forEach(s =>
        ok('it never contains ' + s, text.indexOf(s) === -1));
    finish();
});

function finish() {
    console.log('');
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
}
