/*
 * The admin page's small honesty fixes: P2-305 (what is unsaved), P2-306 (a link that points
 * somewhere and statuses that can be acted on), P2-309 (status colours), P2-310 (one theme
 * order) and P2-311 (wording).
 *
 * Runs the real bindUnsaved, setUnsaved, paintOutcome and loadPanicStatus against stubs.
 * Red against the dashboard before this commit:
 *
 *     node tests/js/adminpolish.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);
const has = n => new RegExp('function ' + n + '\\s*\\(').test(script);
const strings = {};   // ADMIN_EN and the markup's own words, by key
(/const ADMIN_EN = \{([\s\S]*?)\n {12}\};/.exec(script) || [0, ''])[1].split('\n').forEach(line => {
    const m = /^\s*'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/.exec(line);
    if (m) strings[m[1]] = (m[2] !== undefined ? m[2] : m[3]);
});
(html.match(/data-i18n="([^"]+)"[^>]*>([^<]*)</g) || []).forEach(x => {
    const m = /data-i18n="([^"]+)"[^>]*>([^<]*)</.exec(x);
    if (m) strings[m[1]] = m[2];
});

// ── P2-309 ───────────────────────────────────────────────────────────────────
console.log('\n── Status colours (P2-309) ─────────────────────────────────────');
const styleEnd = html.indexOf('</style>');
const css = html.slice(0, styleEnd);
const body = html.slice(styleEnd);
const tokenBlock = /--jpf-good:\s*#51cf66;\s*--jpf-bad:\s*#ff6b6b;\s*--jpf-warn:\s*#ff9900;/.test(css);
ok('the three colours are named once, as tokens', tokenBlock);
const strip = css.replace(/--jpf-(good|bad|warn):\s*#[0-9a-f]{6};/g, '');
const literals = (strip.match(/#51cf66|#ff6b6b|#ff9900/gi) || []).length + (body.match(/#51cf66|#ff6b6b|#ff9900/gi) || []).length;
ok('and written nowhere else, in the stylesheet, the markup or the script', literals === 0, literals + ' left');
if (has('paintOutcome')) {
    const paint = new Function(L.extractFunction(script, 'paintOutcome') + '; return paintOutcome;')();
    const el = () => { const c = new Set(); return { style: {}, classList: { add: x => c.add(x), remove: x => c.delete(x), contains: x => c.has(x) } }; };
    const a = el(); paint(a, true);
    const b = el(); paint(b, false);
    const c2 = el(); paint(c2, false); paint(c2, true);
    ok('a good message is the good class and sets no colour of its own', a.classList.contains('jpf-text-good') && !a.classList.contains('jpf-text-bad') && !a.style.color);
    ok('a bad one is the bad class', b.classList.contains('jpf-text-bad') && !b.classList.contains('jpf-text-good') && !b.style.color);
    ok('and a message that changes its mind keeps one class only', c2.classList.contains('jpf-text-good') && !c2.classList.contains('jpf-text-bad'));
} else ok('the page paints outcomes by class', false, 'no paintOutcome');
ok('the classes exist', /\.jpf-text-good\s*\{\s*color:\s*var\(--jpf-good\)/.test(css) && /\.jpf-text-bad\s*\{\s*color:\s*var\(--jpf-bad\)/.test(css)
   && /\.jpf-text-warn\s*\{\s*color:\s*var\(--jpf-warn\)/.test(css));

// ── P2-305 ───────────────────────────────────────────────────────────────────
console.log('\n── Unsaved changes (P2-305) ────────────────────────────────────');
if (has('bindUnsaved') && has('setUnsaved') && has('isSavedField')) {
    const src = L.dashboardI18n(script)
        + script.slice(script.indexOf('const SAVED_FIELD_IDS'), script.indexOf('function isSavedField')) + '\n'
        + L.extractFunction(script, 'isSavedField') + '\n' + L.extractFunction(script, 'setUnsaved') + '\n' + L.extractFunction(script, 'bindUnsaved') + '\n';
    const mk = (tag, init) => {
        const cls = new Set((init && init.classes) || []);
        const e = Object.assign({ tag: tag, id: '', attrs: {}, children: [], parentNode: null, dataset: {}, textContent: '' }, init || {});
        e.classList = { add: c => cls.add(c), remove: c => cls.delete(c), contains: c => cls.has(c), toggle: (c, on) => { if (on) cls.add(c); else cls.delete(c); } };
        e.setAttribute = (k, v) => { e.attrs[k] = v; };
        Object.defineProperty(e, 'className', { get: () => Array.from(cls).join(' '), set: v => { cls.clear(); String(v).split(/\s+/).filter(Boolean).forEach(x => cls.add(x)); } });
        return e;
    };
    const holder = mk('div');
    const save1 = mk('button', { classes: ['jpf-save-settings'], parentNode: holder });
    const save2 = mk('button', { classes: ['jpf-save-settings'], parentNode: holder });
    holder.children = [save1, save2];
    holder.insertBefore = (n, ref) => { n.parentNode = holder; const i = holder.children.indexOf(ref); holder.children.splice(i < 0 ? holder.children.length : i, 0, n); };
    const handlers = {};
    const page = mk('div', { dataset: {} });
    page._configLoaded = true;
    page.addEventListener = (type, fn, capture) => { handlers[type] = { fn: fn, capture: capture }; };
    page.querySelectorAll = sel => {
        if (sel === '.jpf-save-settings') return [save1, save2];
        if (sel === '.jpf-unsaved') return holder.children.filter(c => c.classList.contains('jpf-unsaved'));
        return [];
    };
    const doc = { createElement: tag => mk(tag) };
    const api = new Function('document', src + '; return { bindUnsaved, setUnsaved };')(doc);
    api.bindUnsaved(page);
    const notes = () => holder.children.filter(c => c.classList.contains('jpf-unsaved'));
    const shown = () => notes().filter(n => !n.classList.contains('jpf-hidden')).length;
    ok('every Save gets a note beside it, hidden at first', notes().length === 2 && shown() === 0 && notes().every(n => n.textContent === 'Unsaved changes'));
    ok('bound once, in the capture phase, on both input and change', !!handlers.input && !!handlers.change && handlers.input.capture === true);
    const edit = (id, classes) => handlers.change.fn({ target: mk('input', { id: id || '', classes: classes || [] }) });
    edit('emailHost');
    edit('annMessage');
    ok('typing in a field Save does not send says nothing', shown() === 0);
    edit('requireMainPin');
    ok('changing one it does shows the note beside every Save', shown() === 2);
    api.setUnsaved(page, false);
    ok('a save or a reload clears it', shown() === 0);
    edit('', ['jpfProxyClient']);
    ok('the app boxes count, which have a class and no id', shown() === 2);
    api.setUnsaved(page, false);
    edit('', ['jpfObShow']);
    ok('and so do the first-time setup boxes', shown() === 2);
    api.setUnsaved(page, false);
    page._configLoaded = false;
    edit('requireMainPin');
    ok('before a read has worked nothing counts: the form is showing defaults', shown() === 0);
    api.bindUnsaved(page);
    ok('binding twice adds no second set', notes().length === 2);
} else ok('the page tracks unsaved changes', false, 'no bindUnsaved');
ok('a successful save and a load both clear it',
   /noteSavedSettings\(page, sent\);\s*setUnsaved\(page, false\);/.test(script) && /page\._configLoaded = true;\s*setUnsaved\(page, false\);/.test(script));

// ── P2-306 ───────────────────────────────────────────────────────────────────
console.log('\n── Pointers (P2-306) ───────────────────────────────────────────');
ok('the FAQ no longer points "below" into a closed fold', !/Check Client script below/.test(html));
ok('it has a button to the client script instead', /id="faqOpenClientScript"/.test(html)
   && /faqGo\.addEventListener\('click', function \(\) \{ goTo\(page, 'troubleshoot', 'jpfClientScript'\); \}\)/.test(script));
ok('and goTo opens the fold it lands in', /const advanced = target\.closest\('details'\);\s*if \(advanced\) advanced\.open = true;/.test(script));
ok('a stale script is a status with a way to fix it',
   /admin\.status\.scriptStale'\),\s*tab: 'troubleshoot', anchor: 'jpfClientScript', fix:/.test(script));
ok('so is Bonfire having been switched off', /admin\.status\.panicUsed'\),\s*tab: 'troubleshoot', anchor: 'jpfFaq', fix:/.test(script) && /id="jpfFaq"/.test(html));

// ── P2-310 ───────────────────────────────────────────────────────────────────
console.log('\n── Theme order (P2-310) ────────────────────────────────────────');
const root = path.resolve(__dirname, '..', '..');
const serverAll = /public static readonly string\[\] All =\s*\{([\s\S]*?)\};/.exec(fs.readFileSync(path.join(root, 'Configuration', 'PluginConfiguration.cs'), 'utf8'));
const constants = {};
(fs.readFileSync(path.join(root, 'Configuration', 'PluginConfiguration.cs'), 'utf8').match(/public const string \w+ = "[^"]+";/g) || [])
    .forEach(l => { const m = /string (\w+) = "([^"]+)"/.exec(l); constants[m[1]] = m[2]; });
const serverOrder = serverAll ? serverAll[1].split(',').map(x => x.trim()).filter(Boolean).map(n => constants[n]) : [];
const select = /<select[^>]*id="defaultTheme"[^>]*>([\s\S]*?)<\/select>/.exec(html);
const markupOrder = select ? (select[1].match(/value="([^"]+)"/g) || []).map(v => v.slice(7, -1)) : [];
const clientOrder = /const THEMES = \[([^\]]*)\]/.exec(fs.readFileSync(L.profilesPath() || path.join(root, 'Web', 'profiles.js'), 'utf8'));
const clientList = clientOrder ? clientOrder[1].split(',').map(x => x.trim().replace(/'/g, '')).filter(Boolean) : [];
ok('the Default theme list, the server and the client all name the built-ins in one order',
   serverOrder.length === 12 && JSON.stringify(markupOrder) === JSON.stringify(serverOrder) && JSON.stringify(clientList) === JSON.stringify(serverOrder),
   markupOrder.join() + ' | ' + serverOrder.join() + ' | ' + clientList.join());
if (has('themeOfferedIds') && has('builtInThemeRowsHtml')) {
    const fn = new Function('t', 'escapeHtml', 'themeDisplayName', L.extractFunction(script, 'themeOfferedIds') + L.extractFunction(script, 'builtInThemeRowsHtml')
        + '; return { themeOfferedIds, builtInThemeRowsHtml };')(k => k, s => s, (d, id) => id);
    const data = { 'default': 'bonfire', builtIn: serverOrder.map(id => ({ id: id, hidden: id === 'glass' })), custom: [{ id: 'mine' }] };
    const offered = fn.themeOfferedIds(data, 'bonfire');
    const rows = (fn.builtInThemeRowsHtml(data).match(/data-theme="([^"]+)"/g) || []).map(v => v.slice(12, -1));
    ok('the Hide list and the Default theme list run in the same order, the hidden one missing from the second only',
       JSON.stringify(rows) === JSON.stringify(serverOrder) && JSON.stringify(offered) === JSON.stringify(serverOrder.filter(x => x !== 'glass').concat('mine')),
       rows.join() + ' | ' + offered.join());
}

// ── P2-311 ───────────────────────────────────────────────────────────────────
console.log('\n── Wording (P2-311) ────────────────────────────────────────────');
ok('"Open." is a sentence', strings['admin.mnt.isOpen'] && strings['admin.mnt.isOpen'].split(/\s+/).length >= 3, strings['admin.mnt.isOpen']);
ok('"Not set up." says what is not set up', strings['admin.email.notSet'] && /email/i.test(strings['admin.email.notSet']), strings['admin.email.notSet']);
const lede = strings['admin.apps.lede'] || '';
ok('the Apps lede is two short sentences, not a list of five apps', lede.split(/[.!?]\s/).length <= 2 && lede.length < 170 && !/Findroid|Wholphin/.test(lede), lede.length + ': ' + lede);
ok('and still says a saved profile opens without its PIN unless the app asks every time', /without its PIN/.test(lede) && /every time/.test(lede));
ok('"Where to switch from" says what its two choices are', /data-i18n="admin\.switcher\.locationHint"/.test(html) && !!strings['admin.switcher.locationHint']);

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
