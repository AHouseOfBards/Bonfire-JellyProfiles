#!/usr/bin/env node
/*
 * The theme editor's top bar, undo and redo, and problems on the field (2026-11-12), driven
 * in Chrome on the screens fixture with Bonfire's client script loaded.
 *
 * Before: Save, Cancel and the Problems list were at the very bottom of the editor, about
 * 1,000 px down in Advanced; there was no undo; a problem coloured the label of its row.
 *
 *     node tests/js/themebar.js [/old/profilesDashboard.html]
 *
 * Needs Chrome; says it was skipped, exiting 0, without it.
 */
'use strict';

const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}
const wait = ms => new Promise(r => setTimeout(r, ms));
const frame = 'new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})';
const Q = s => `document.querySelector(${JSON.stringify(s)})`;
const ED = `document.getElementById('profilesConfigurationPage')._themeEditor`;
const SET = (sel, v, ev) => `(function(){ var e = ${sel}; if (!e) return false; e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event('${ev || 'input'}', { bubbles: true })); return true; })()`;
const CLICK = sel => `(function(){ var e = ${sel}; if (!e) return false; e.click(); return true; })()`;
// Where the bar is, and whether Save is wholly on screen.
const BAR = `(function () { var b = document.getElementById('teBar'), s = document.getElementById('teSave');
  if (!b || !s) return { missing: true }; var r = b.getBoundingClientRect(), v = s.getBoundingClientRect();
  return { top: Math.round(r.top), h: Math.round(r.height), pos: getComputedStyle(b).position, saveIn: v.top >= 0 && v.bottom <= window.innerHeight && v.width > 0 }; })()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n0 passed, 0 failed (skipped)');
        return;
    }
    const s = await fixture.start(0, { dashboard: process.argv[2] || undefined });
    const c = await chrome.launch();
    const ev = x => c.evaluate(x);
    try {
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=desktop&theme=dark', 'desktop');
        await ev("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await ev("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await ev('window.__bonfire.injectStyles()');
        // The server's check answers whatever the test last put in window.__problems.
        await ev("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: window.__problems || []}); return a.apply(this, arguments); };})()");
        await ev("document.getElementById('themeNew').click()");
        await wait(600);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function (d) { d.open = true; })");
        await ev(frame);

        // 1. The bar.
        const at = await ev(BAR);
        ok('the editor has a bar with Save in it', !at.missing && (await ev("document.getElementById('teBar').contains(document.getElementById('teSave')) && document.getElementById('teBar').contains(document.getElementById('teCancel'))")) === true, JSON.stringify(at));
        ok('the bar is sticky', at.pos === 'sticky' || at.pos === '-webkit-sticky', JSON.stringify(at));
        ok('it carries the theme name', (await ev("(document.getElementById('teBar') || { textContent: '' }).textContent.indexOf(document.getElementById('teName').value) >= 0")) === true);
        ok('it leaves a slot for a File menu', (await ev("!!document.getElementById('teFileSlot') && document.getElementById('teBar').contains(document.getElementById('teFileSlot'))")) === true);
        await ev("var last = [].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-field')).pop(); last.scrollIntoView({ block: 'end' }); 0");
        await ev(frame);
        const down = await ev(BAR);
        ok('scrolled to the last field, the bar and Save are still in view', !down.missing && down.top >= 0 && down.top < 120 && down.saveIn, JSON.stringify(down));
        ok('and Save is not left at the foot of the form', (await ev("!document.querySelector('#themeEditor .jpf-te-end .button-submit')")) === true);

        // 2. The problem count, and a problem on its field and group.
        const path = await ev("(function(){ var f = document.querySelector('#teAdvancedFields .jpf-te-field[data-paths]'); return f ? f.getAttribute('data-paths').split(' ')[0] : ''; })()");
        ok('the test found a field to mark', !!path, 'no Advanced field');
        ok('with no problems the bar says so', /no problem/i.test(await ev("(document.getElementById('teCount') || { textContent: '' }).textContent")));
        await ev(`window.__problems = [{ path: ${JSON.stringify(path)}, code: 'range', detail: '' }]; ${ED}.problems = window.__problems; document.getElementById('profilesConfigurationPage')._themeEditor.view; 0`);
        await ev(SET(Q('#teName'), 'Marked', 'input'));
        await wait(700);
        ok('a problem is counted in the bar', /^1 problem$/.test(await ev("(document.getElementById('teCount') || { textContent: '' }).textContent")), await ev("(document.getElementById('teCount') || { textContent: '' }).textContent"));
        const marked = await ev(`(function(){ var f = [].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-field')).filter(function (x) { return (x.getAttribute('data-paths') || '').split(' ').indexOf(${JSON.stringify(path)}) >= 0; })[0];
            if (!f) return { none: true }; var m = f.querySelector('.jpf-te-msg'); var st = getComputedStyle(f); var d = f.closest('details');
            return { bad: f.classList.contains('jpf-te-bad'), msg: !!m && m.textContent.length > 0, edge: st.borderLeftWidth, edgeColor: st.borderLeftColor, group: d ? d.classList.contains('jpf-te-has-problem') : null }; })()`);
        ok('the row the check names gets a red edge and its message', marked.bad && marked.msg && parseFloat(marked.edge) >= 2 && marked.edgeColor !== 'rgba(0, 0, 0, 0)', JSON.stringify(marked));
        ok('and its group shows a mark', marked.group === true, JSON.stringify(marked));
        ok('the bottom list is still a summary', (await ev("document.querySelectorAll('#teProblems li').length")) === 1);
        await ev("window.scrollTo(0, 0); 0");
        await ev(CLICK(Q('#teCount')));
        await wait(700);
        ok('clicking the count brings the first problem into view', (await ev("(function(){ var b = document.querySelector('#themeEditor .jpf-te-bad'); if (!b) return false; var r = b.getBoundingClientRect(); return r.bottom > 0 && r.top < window.innerHeight; })()")) === true);
        await ev(`window.__problems = []; ${ED}.problems = []; 0`);
        await ev(SET(Q('#teName'), 'Clean', 'input'));
        await wait(700);
        ok('with the problem gone the mark and the count clear', (await ev("document.querySelectorAll('#themeEditor .jpf-te-bad, #themeEditor .jpf-te-has-problem').length")) === 0
            && /no problem/i.test(await ev("(document.getElementById('teCount') || { textContent: '' }).textContent")));

        // 4. Undo and redo.
        const DEPTH = `(((${ED}) || {}).undo || []).length`;
        const REDOS = `(((${ED}) || {}).redo || []).length`;
        const DRAFTJ = `JSON.stringify((${ED}).draft)`;
        const CSS = "(document.getElementById('jpfThemeEditorCss') || { textContent: '' }).textContent";
        const KEY = (target, code, shift) => `(function(){ var e = new KeyboardEvent('keydown', { keyCode: ${code}, which: ${code}, ctrlKey: true, shiftKey: ${!!shift}, bubbles: true, cancelable: true });
            ${target}.dispatchEvent(e); return e.defaultPrevented; })()`;
        await ev(CLICK(Q('#teCancel')));
        await ev("document.getElementById('themeNew').click(); 0");
        await wait(500);
        await ev("window.scrollTo(0, 0); 0");
        const d0 = await ev(DEPTH);
        ok('a new editor starts with nothing to undo', d0 === 0 && (await ev("!!document.getElementById('teUndo') && document.getElementById('teUndo').disabled && document.getElementById('teRedo').disabled")) === true);
        for (const v of ['A', 'Al', 'Alp', 'Alph', 'Alpha']) await ev(SET(Q('#teName'), v, 'input'));
        await wait(900);
        ok('a typing burst is one step', (await ev(DEPTH)) === d0 + 1, String(await ev(DEPTH)));
        const afterName = await ev(DRAFTJ);
        const range = '#teAdvancedFields input[type="range"][data-path]';
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        const cssBefore = await ev(CSS);
        for (const v of ['1', '2', '3', '4', '5', '6']) await ev(`(function(){ var e = ${Q(range)}; e.value = ${JSON.stringify(String(Number(v) * 3))}; e.dispatchEvent(new Event('input', { bubbles: true })); })()`);
        await ev(`${Q(range)}.dispatchEvent(new Event('change', { bubbles: true }))`);
        const afterSlide = await ev(DRAFTJ);
        const cssAfter = await ev(CSS);
        ok('a slider drag is one step', afterSlide !== afterName && (await ev(DEPTH)) === d0 + 2, String(await ev(DEPTH)));
        ok('Undo is on and Redo is not', (await ev("!document.getElementById('teUndo').disabled && document.getElementById('teRedo').disabled")) === true);
        await ev(CLICK(Q('#teUndo')));
        ok('Undo puts the draft back as it was before the drag', (await ev(DRAFTJ)) === afterName);
        ok('and the control and the preview follow', (await ev(`${Q(range)}.value`)) !== String(18) && (await ev(CSS)) === cssBefore, 'css changed: ' + ((await ev(CSS)) === cssBefore));
        await ev(CLICK(Q('#teRedo')));
        ok('Redo puts the drag back', (await ev(DRAFTJ)) === afterSlide && (await ev(CSS)) === cssAfter && (await ev(`${Q(range)}.value`)) === '18');
        // Ctrl+Z.
        await ev(`${Q('#teName')}.focus(); 0`);
        const inBox = await ev(KEY(Q('#teName'), 90));
        ok('Ctrl+Z in a text box is left to the box', inBox === false && (await ev(DRAFTJ)) === afterSlide, 'defaultPrevented ' + inBox);
        const inJson = await ev(`(function(){ var t = document.createElement('textarea'); document.getElementById('teAdvancedFields').appendChild(t); var e = new KeyboardEvent('keydown', { keyCode: 90, ctrlKey: true, bubbles: true, cancelable: true }); t.dispatchEvent(e); t.remove(); return e.defaultPrevented; })()`);
        ok('and in a JSON box', inJson === false);
        await ev("document.activeElement && document.activeElement.blur(); 0");
        ok('Ctrl+Z elsewhere undoes', (await ev(KEY('document.body', 90))) === true && (await ev(DRAFTJ)) === afterName);
        ok('Ctrl+Shift+Z redoes', (await ev(KEY('document.body', 90, true))) === true && (await ev(DRAFTJ)) === afterSlide);
        await ev(KEY('document.body', 90));
        ok('Ctrl+Y redoes', (await ev(KEY('document.body', 89))) === true && (await ev(DRAFTJ)) === afterSlide);
        // A change after an undo drops what could have been redone.
        await ev(KEY('document.body', 90));
        await ev(SET(Q('#teName'), 'Beta', 'change'));
        ok('a new change after Undo clears Redo', (await ev(REDOS)) === 0 && (await ev("document.getElementById('teRedo').disabled")) === true);
        // The stack is capped at 50.
        for (let i = 0; i < 55; i++) await ev(SET(Q('#teName'), 'N' + i, 'change'));
        ok('the stack is capped at 50 steps', (await ev(DEPTH)) === 50, String(await ev(DEPTH)));
        // A colour-picker session is one step.
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="simple"]')));
        const before = await ev(DEPTH);
        await ev(CLICK(Q('#teAccent')));
        for (let i = 0; i < 3; i++) await ev(`(function(){ var p = document.querySelectorAll('.jpf-te-pop .jpf-te-pre'); p[${i + 2}].click(); })()`);
        await wait(900);
        await ev(CLICK(Q('#teAccent')));
        ok('a colour-picker session is one step', (await ev(DEPTH)) === Math.min(50, before + 1) && (await ev("!document.querySelector('.jpf-te-pop')")) === true, before + ' -> ' + (await ev(DEPTH)));
        // Ctrl+S saves; Cancel and a new editor start with an empty stack.
        await ev("window.__put = []; var a0 = ApiClient.ajax; ApiClient.ajax = function (o) { if (o && o.type === 'PUT') { window.__put.push(o.url); return Promise.resolve({ created: true }); } return a0.apply(this, arguments); }; 0");
        ok('Ctrl+S saves', (await ev(KEY('document.body', 83))) === true && await (async () => { await wait(300); return (await ev('window.__put.length')) === 1; })());
        await ev("document.getElementById('themeNew').click(); 0");
        await wait(500);
        ok('opening another theme clears the stack', (await ev(DEPTH)) === 0 && (await ev("document.getElementById('teUndo').disabled")) === true);
        await ev(SET(Q('#teName'), 'Gamma', 'change'));
        await ev(CLICK(Q('#teCancel')));
        await ev("document.getElementById('themeNew').click(); 0");
        await wait(500);
        ok('Cancel clears it too', (await ev(DEPTH)) === 0);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function (d) { d.open = true; })");
        await ev(frame);

        // 5. The File menu (2026-11-12, round 2): Download saves the draft, Load replaces it.
        const HEX = 'abcdef0123456789abcdef0123456789';
        const LOAD = text => `(function(){ var f = new File([${JSON.stringify(text)}], 'x.json', { type: 'application/json' }); var dt = new DataTransfer(); dt.items.add(f);
            var i = document.getElementById('teFileInput'); i.files = dt.files; i.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
        const NOTE = "(document.getElementById('teFileNote') || { textContent: '', classList: { contains: function () { return true; } } })";
        await ev(`window.__put = []; window.__blob = null; window.__dlName = '';
            var o0 = URL.createObjectURL; URL.createObjectURL = function (b) { window.__blob = b; return o0.call(URL, b); };
            var k0 = HTMLAnchorElement.prototype.click; HTMLAnchorElement.prototype.click = function () { if (this.download) { window.__dlName = this.download; return; } return k0.apply(this, arguments); };
            var f0 = window.fetch; window.fetch = function (u) { if (/themes\\/(backgrounds|pattern-files)\\//.test(String(u))) return Promise.resolve({ ok: !!window.__present }); return f0.apply(this, arguments); };
            var a1 = ApiClient.ajax; ApiClient.ajax = function (o) { if (o && o.type === 'PUT') { window.__put.push(o.url); return Promise.resolve({ created: true }); } return a1.apply(this, arguments); }; 0`);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev(SET(Q('#teName'), 'Unsaved Mine', 'change'));
        await ev(`(function(){ var d = ${ED}.draft; d.background = d.background || {}; d.background.image = { file: '${HEX}.png', dim: 0.6 }; })()`);
        ok('the bar has a File menu that is closed at first', (await ev("!!document.getElementById('teFile') && document.getElementById('teFileSlot').contains(document.getElementById('teFile')) && document.getElementById('teFileMenu').classList.contains('jpf-hidden')")) === true);
        await ev(CLICK(Q('#teFile')));
        ok('it opens with Download and Load', (await ev("(function(){ var m = document.getElementById('teFileMenu'); return !m.classList.contains('jpf-hidden') && m.getBoundingClientRect().width > 0 && !!m.querySelector('#teFileDownload') && !!m.querySelector('#teFileLoad') && document.getElementById('teFile').getAttribute('aria-expanded') === 'true'; })()")) === true);
        await ev(CLICK(Q('#teFileDownload')));
        ok('Download closes the menu', (await ev("document.getElementById('teFileMenu').classList.contains('jpf-hidden')")) === true);
        const dl = JSON.parse(await ev("window.__blob ? window.__blob.text() : '{}'"));
        ok('Download saves the draft, unsaved edits included, as <id>.json', (await ev('window.__dlName')) === 'unsaved-mine.json' && dl.name && dl.name.text === 'Unsaved Mine', JSON.stringify(dl).slice(0, 200));
        ok('and leaves the picture out, keeping its dim', dl.background && dl.background.image && Object.keys(dl.background.image).join() === 'dim' && dl.background.image.dim === 0.6, JSON.stringify(dl.background));
        await ev(`delete ${ED}.draft.background.image; 0`);

        const was = await ev(DRAFTJ);
        const depth0 = await ev(DEPTH);
        const id0 = await ev(`${ED}.draft.id`);
        await ev(`window.__problems = [{ path: 'zzz', code: 'unknown', detail: '' }]; 0`);
        await ev(LOAD('{"id":"other","name":{"text":"Loaded"},"zzz":1,"colors":{"accent":"#112233"}}'));
        await wait(700);
        const loaded = JSON.parse(await ev(DRAFTJ));
        ok('Load replaces the draft and keeps the open theme\'s id', loaded.name.text === 'Loaded' && loaded.colors.accent === '#112233' && loaded.id === id0 && id0 === 'unsaved-mine', JSON.stringify(loaded));
        ok('it saves nothing', (await ev('window.__put.length')) === 0);
        ok('it is one undo step', (await ev(DEPTH)) === depth0 + 1, depth0 + ' -> ' + (await ev(DEPTH)));
        ok('a field the editor does not know is kept, and the server\'s check counts it', loaded.zzz === 1 && /^1 problem$/.test(await ev("document.getElementById('teCount').textContent")) && (await ev("document.querySelectorAll('#teProblems li').length")) === 1);
        ok('the name box and the bar follow the file', (await ev("document.getElementById('teName').value + '|' + document.getElementById('teBarName').textContent")) === 'Loaded|Loaded');
        await ev(CLICK(Q('#teUndo')));
        ok('Undo puts back the draft from before the file', (await ev(DRAFTJ)) === was);
        await ev(`window.__problems = []; 0`);

        const depth1 = await ev(DEPTH);
        for (const [what, text] of [['text that is not JSON', 'not json'], ['a JSON array', '[1,2]'], ['a bare value', '5']]) {
            await ev(LOAD(text));
            await wait(250);
            ok('a bad file (' + what + ') gives a one-line error and changes nothing', (await ev(DRAFTJ)) === was && (await ev(DEPTH)) === depth1
                && (await ev(NOTE + '.textContent')).length > 0 && (await ev(NOTE + '.classList.contains("jpf-hidden")')) === false, await ev(NOTE + '.textContent'));
        }
        await ev(`(function(){ var f = new File(['{"id":"x",' + ' '.repeat(262144) + '}'], 'big.json'); var dt = new DataTransfer(); dt.items.add(f); var i = document.getElementById('teFileInput'); i.files = dt.files; i.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        await wait(250);
        ok('a file over 256 KB is refused without being read', (await ev(DRAFTJ)) === was && /256/.test(await ev(NOTE + '.textContent')), await ev(NOTE + '.textContent'));

        // A background picture the server lacks is dropped, with the note under the Photo control.
        await ev(`window.__present = false; 0`);
        await ev(LOAD(`{"id":"q","name":{"text":"Pic"},"background":{"fill":"#101010","image":{"file":"${HEX}.png","dim":0.7}}}`));
        await wait(700);
        ok('a background picture the server lacks is dropped', (await ev(`${ED}.draft.background.image === undefined && ${ED}.draft.background.fill === '#101010'`)) === true);
        ok('and the Photo control says to upload it', /imported/.test(await ev("document.querySelector('#teAdvancedFields .jpf-te-photo .jpf-te-photo-note').textContent")), await ev("document.querySelector('#teAdvancedFields .jpf-te-photo .jpf-te-photo-note').textContent"));
        // One it has is kept.
        await ev(`window.__present = true; 0`);
        await ev(LOAD(`{"id":"q","name":{"text":"Pic"},"background":{"fill":"#101010","image":{"file":"${HEX}.png","dim":0.7}}}`));
        await wait(700);
        ok('one the server has is kept', (await ev(`${ED}.draft.background.image && ${ED}.draft.background.image.file`)) === HEX + '.png');
        await ev(`window.__present = false; 0`);

        // A pattern picture the server lacks: the card says so, and Save is held.
        await ev("document.getElementById('profilesConfigurationPage')._themes.catalog.patterns = [{ name: 'leaves' }]; 0");
        await ev(LOAD(`{"id":"q","name":{"text":"Pat"},"decorations":[{"name":"pattern","image":"${HEX}.png","size":240,"drift":"none"}]}`));
        await wait(700);
        await ev("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function (d) { d.open = true; })");
        const card = await ev(`(function(){ var m = document.querySelector('#teAdvancedFields .jpf-te-missing'); var c = m && m.closest('.jpf-te-card'); return { text: m ? m.textContent : '', upload: !!(c && c.querySelector('[data-pattern-act]')) }; })()`);
        ok('a pattern picture the server lacks shows "Missing picture" with its Upload button', card.text === 'Missing picture' && card.upload === true, JSON.stringify(card));
        ok('and it is counted as a problem', /^1 problem$/.test(await ev("document.getElementById('teCount').textContent")));
        await ev(CLICK(Q('#teSave')));
        await wait(300);
        ok('Save is held until it is replaced', (await ev('window.__put.length')) === 0);
        const builtIn = await ev(`(function(){ var s = document.querySelector('#teAdvancedFields select[data-path="decorations.0.image"]'); var o = [].slice.call(s.options).filter(function (x) { return x.value && x.value !== '${HEX}.png'; })[0]; return o ? o.value : ''; })()`);
        ok('the test found a built-in picture to choose', !!builtIn);
        await ev(SET(Q('#teAdvancedFields select[data-path="decorations.0.image"]'), builtIn, 'change'));
        await wait(700);
        ok('choosing a built-in clears the mark and the count', (await ev("document.querySelectorAll('#teAdvancedFields .jpf-te-missing').length")) === 0 && /no problem/i.test(await ev("document.getElementById('teCount').textContent")));
        await ev(CLICK(Q('#teSave')));
        await wait(300);
        ok('and Save goes through', (await ev('window.__put.length')) === 1);
        await ev("document.getElementById('themeNew').click(); 0");
        await wait(500);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function (d) { d.open = true; })");
        await ev(frame);

        // 3. At 480 px the bar still fits, with the rare buttons behind the "..." menu.
        await c.page('Emulation.setDeviceMetricsOverride', { width: 480, height: 800, deviceScaleFactor: 1, mobile: false });
        await wait(300);
        await ev("var l = [].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-field')).pop(); l.scrollIntoView({ block: 'end' }); 0");
        await ev(frame);
        const narrow = await ev(`(function () { var b = document.getElementById('teBar'); if (!b) return { missing: true }; var r = b.getBoundingClientRect();
            var kids = [].slice.call(b.querySelectorAll('button, #teBarName')).filter(function (e) { return e.getBoundingClientRect().width > 0; });
            var out = kids.filter(function (e) { var q = e.getBoundingClientRect(); return q.right > window.innerWidth + 0.5 || q.left < -0.5; }).map(function (e) { return e.id || e.className; });
            return { top: Math.round(r.top), h: Math.round(r.height), scrollW: document.documentElement.scrollWidth, w: window.innerWidth, out: out,
                expand: getComputedStyle(document.getElementById('teBarExpand')).display, more: getComputedStyle(document.getElementById('teMore').parentNode).display }; })()`);
        ok('at 480 px every control in the bar is inside the window and the bar is one line', !narrow.missing && narrow.out.length === 0 && narrow.h < 70 && narrow.top >= 0 && narrow.top < 120, JSON.stringify(narrow));
        ok('and Expand and Cancel have moved into the menu', narrow.expand === 'none' && narrow.more !== 'none', JSON.stringify(narrow));
        await ev(CLICK(Q('#teMore')));
        ok('the menu opens and holds Expand and Cancel', (await ev("(function(){ var m = document.getElementById('teMenu'); return !m.classList.contains('jpf-hidden') && !!m.querySelector('[data-more=teBarExpand]') && !!m.querySelector('[data-more=teCancel]') && m.getBoundingClientRect().width > 0; })()")) === true);
        await ev(CLICK(Q('#teMenu [data-more="teBarExpand"]')));
        await wait(300);
        ok('Expand from the menu opens the expanded view', (await ev("!!document.getElementById('teExpanded')")) === true);
        await ev("document.querySelector('#teExpanded .jpf-te-xclose').click(); 0");
        ok('Cancel from the menu closes the editor', await (async () => {
            await ev(CLICK(Q('#teMore')));
            await ev(CLICK(Q('#teMenu [data-more="teCancel"]')));
            return (await ev("document.getElementById('themeEditor').classList.contains('jpf-hidden')")) === true;
        })());
        const consoleErrors = await ev('1').then(() => 0);
        void consoleErrors;
    } finally {
        await c.close();
        await s.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
}

main().catch(e => { console.error(e); process.exit(1); });
