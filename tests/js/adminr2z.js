/*
 * Dashboard fixes from the second review (r2-33-01, r2-34-02, r2-35-02, r2-35-03,
 * r2-35-04, r2-35-05, r2-35-06, r2-36-01, r2-36-02, r2-36-03). Each check runs the real
 * function from the page, or reads the markup it is about, and is red against the
 * dashboard before the fix:
 *
 *     node tests/js/adminr2z.js "" /path/to/old/profilesDashboard.html
 *
 * r2-34-03 (markup strings captured before any loader fills them) needed no change: the
 * only entry point is the page's pageshow, which applies the strings before showPage, and
 * no data-i18n element is written by JavaScript. The last check holds that.
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);
const has = n => script.indexOf('function ' + n + '(') !== -1;
const fn = n => L.extractFunction(script, n);

function element() {
    return {
        value: '', checked: false, textContent: '', innerHTML: '', dataset: {}, style: {},
        classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
        setAttribute() {}, getAttribute() { return null; }, addEventListener() {}, insertAdjacentHTML() {}
    };
}
async function drain() { for (let i = 0; i < 8; i++) await new Promise(r => setImmediate(r)); }

(async function main() {
    // ---- r2-33-01
    console.log('\n-- The notice title takes the theme colour (r2-33-01) --');
    const titles = html.match(/<[^>]*jpf-note-title[^>]*>/g) || [];
    ok('some notice titles were found', titles.length > 0);
    ok('none carries a fixed colour in its style', titles.every(t => !/style="[^"]*color:\s*#/i.test(t)), titles.join(' | '));

    // ---- r2-34-02
    console.log('\n-- Avatar upload accepts what its hint says (r2-34-02) --');
    const input = (html.match(/<input[^>]*id="avatarUploadInput"[^>]*>/) || [''])[0];
    const accept = ((input.match(/accept="([^"]*)"/) || [, ''])[1]).split(',').sort().join(',');
    ok('accept is exactly JPEG, PNG, WebP and GIF', accept === 'image/gif,image/jpeg,image/png,image/webp', accept);
    ok('the hint names the same four', /data-i18n="admin\.avatars\.uploadHint">JPEG, PNG, WebP or GIF\./.test(html));
    ok('the folder picker does not pick out avif or bmp', !/\.\(jpe\?g\|png\|webp\|gif\|[^)]*(bmp|avif)/.test(html)
        && !/\|(bmp|avif)\)\$/.test(html));

    // ---- r2-35-02
    console.log('\n-- Getting started is drawn once per loadMappings (r2-35-02) --');
    {
        const src = L.dashboardI18n(script) + '\n'
            + ['escapeHtml', 'namedAria', 'field', 'normId', 'collectTrusted', 'loadMappings'].map(fn).join('\n');
        const container = element();
        const page = {
            dataset: {}, _trusted: [], querySelector: sel => (sel === '#masterUsersList' ? container : element()),
            querySelectorAll: () => []
        };
        let drawn = 0;
        const noop = () => {};
        const names = ['renderTrusted', 'renderGettingStarted', 'renderInjectionStatus', 'setStatus', 'pinStatusRows', 'askFirst',
            'bindStandaloneForms', 'bindAccountExtras', 'paintOutcome', 'errorText', 'Dashboard', 'console'];
        const stubs = [noop, () => { drawn++; }, noop, noop, () => [], () => Promise.resolve(), noop, noop, noop,
            (e, f) => Promise.resolve(f), { hideLoadingMsg: noop, showLoadingMsg: noop }, { error: noop, log: noop }];
        const ApiClient = { getUrl: u => '/' + u, getJSON: () => Promise.resolve({ MasterUsers: [], SubProfiles: [] }) };
        const load = new Function('ApiClient', names.join(','), src + '; return loadMappings;').apply(null, [ApiClient].concat(stubs));
        load(page);
        await drain();
        ok('one call to renderGettingStarted', drawn === 1, 'drawn ' + drawn);
    }

    // ---- r2-35-03
    console.log('\n-- The orphan reason is shown in the page language (r2-35-03) --');
    {
        const src = L.dashboardI18n(script) + '\n'
            + (has('orphanReasonText') ? fn('orphanReasonText') + '\n' : '')
            + ['escapeHtml', 'renderOrphans'].map(fn).join('\n');
        const box = element();
        const page = { querySelector: sel => (sel === '#orphansResult' ? box : element()) };
        const make = new Function('ApiClient', 'Dashboard', 'loadOrphanStatus', 'askFirst', 'console', 'setAdmin',
            src + '; return { render: renderOrphans, tr: function (m) { adminStrings = m; } };');
        const api = make({ getUrl: u => u, ajax: () => Promise.resolve() }, {}, () => {}, () => Promise.resolve(), console);
        api.tr({ 'admin.orphans.reasonProfile': 'XX profile gone', 'admin.orphans.reasonMaster': 'XX main gone' });
        const serverEnglish = "the profile's Jellyfin user was deleted";
        api.render(page, { orphans: [{ profileName: 'Sam', reason: serverEnglish, reasonCode: 'profile_deleted' }],
            deadGroups: [], deadMembers: [], unusedImages: 0 });
        ok('a profile_deleted code shows its translation', box.innerHTML.indexOf('XX profile gone') !== -1, box.innerHTML);
        ok('and not the server English', box.innerHTML.indexOf('Jellyfin user was deleted') === -1, box.innerHTML);
        api.render(page, { orphans: [{ profileName: 'Sam', reason: 'the master account was deleted', reasonCode: 'master_deleted' }],
            deadGroups: [], deadMembers: [], unusedImages: 0 });
        ok('a master_deleted code shows its translation', box.innerHTML.indexOf('XX main gone') !== -1, box.innerHTML);
        api.render(page, { orphans: [{ profileName: 'Sam', reason: 'some newer reason' }], deadGroups: [], deadMembers: [], unusedImages: 0 });
        ok('a response with no code still shows something', box.innerHTML.indexOf('some newer reason') !== -1, box.innerHTML);
        const controller = require('fs').readFileSync(process.argv[4] || require('path').join(__dirname, '..', '..', 'Controllers', 'ProfilesController.cs'), 'utf8');
        ok('the server sends reasonCode for both reasons', /reasonCode\s*=[^;]*"profile_deleted"[^;]*"master_deleted"/.test(controller));
    }

    // ---- r2-35-04
    console.log('\n-- The default avatar name goes through t() (r2-35-04) --');
    ok('uploadOneAvatar has no literal fallback name', !/\|\|\s*'Avatar'/.test(fn('uploadOneAvatar')));

    // ---- r2-35-05
    console.log('\n-- t() fills placeholders in one pass (r2-35-05) --');
    {
        const make = new Function(L.dashboardI18n(script) + '; return t;');
        const t = make();
        ok('a value holding {b} is not filled again', t('x {a} {b}', { a: '{b}', b: 'Z' }) === 'x {b} Z', t('x {a} {b}', { a: '{b}', b: 'Z' }));
        ok('an ordinary fill still works', t('{n} of {m}', { n: 1, m: 2 }) === '1 of 2');
        ok('a token with no value stays as written', t('{n} {q}', { n: 1 }) === '1 {q}');
    }

    // ---- r2-35-06
    console.log('\n-- An image that decodes as 0x0 is refused before drawImage (r2-35-06) --');
    {
        let drew = 0;
        const document = { createElement: () => ({ width: 0, height: 0, getContext: () => ({ drawImage() { drew++; } }), toDataURL: () => 'data:' }) };
        const make = new Function('document', L.dashboardI18n(script) + '\n' + fn('squareToDataUrl') + '; return squareToDataUrl;');
        const sq = make(document);
        let msg = null;
        try { sq({ width: 0, height: 0 }, 64, true, 0.8); } catch (e) { msg = e.message; }
        ok('it throws', msg !== null);
        ok('drawImage was never called', drew === 0, 'drew ' + drew);
        ok('the message is the translated key text, not a canvas error', msg !== null && /no size/i.test(msg), String(msg));
        ok('a normal image still draws', (function () { try { return sq({ width: 10, height: 20 }, 64, true) === 'data:' && drew === 1; } catch (e) { return false; } })());
    }

    // ---- r2-36-01
    console.log('\n-- The X-Bonfire-Error map paragraph sits on refusalMessage (r2-36-01) --');
    {
        const before = n => {
            const at = script.indexOf('function ' + n + '(');
            // The comment block: back to the last blank line (either line ending).
            const head = script.slice(0, at);
            const gaps = head.match(/\r?\n[ \t]*\r?\n/g);
            return gaps ? head.slice(head.lastIndexOf(gaps[gaps.length - 1])) : head;
        };
        ok('not above refusalCode', !/sentence for each code/.test(before('refusalCode')));
        ok('above refusalMessage', /sentence for each code/.test(before('refusalMessage')));
    }

    // ---- r2-36-02
    console.log('\n-- simpleThemeEdit does not shadow the translator (r2-36-02) --');
    ok('no callback parameter is named t', !/function\s*\(\s*t\b/.test(fn('simpleThemeEdit')));

    // ---- r2-36-03
    console.log('\n-- loadThemes drops a response older than the latest request (r2-36-03) --');
    {
        const pending = [];
        const ApiClient = {
            getUrl: u => u,
            getJSON: u => (/admin\/themes/.test(u) ? new Promise(res => pending.push(res)) : Promise.resolve(null))
        };
        const rendered = [];
        const src = L.dashboardI18n(script) + '\n' + fn('loadThemes');
        const make = new Function('ApiClient', 'showThemeStatus', 'renderThemes', src + '; return loadThemes;');
        const load = make(ApiClient, () => {}, p => rendered.push(p._themes.data.tag));
        const page = { querySelector: () => element() };
        load(page); load(page);
        pending[1]({ tag: 'new' });
        await drain();
        pending[0]({ tag: 'old' });
        await drain();
        ok('the older answer did not draw after the newer', rendered.join() === 'new', rendered.join());
        ok('the page holds the newer data', page._themes.data.tag === 'new');
    }

    // ---- r2-34-03
    console.log('\n-- Nothing fills a data-i18n element before applyMarkupStrings (r2-34-03) --');
    ok('pageshow is the one entry point and applies strings first',
        (script.match(/addEventListener\('(pageshow|viewshow|pagebeforeshow|pageinit|viewbeforeshow)'/g) || []).length === 1
        && /try \{ applyMarkupStrings\(page\); \}[^\n]*\n\s*showPage\(page\);/.test(script));
    ok('no script-built markup carries data-i18n', !/(['"`])[^\n]*<[a-z]+[^>\n]*data-i18n=[^\n]*\1\s*(\+|;|,|\))/.test(
        script.split('\n').filter(l => !/^\s*(\/\/|\/\*|\*)/.test(l)).join('\n')));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
