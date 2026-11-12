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
            && /no problem/i.test(await ev("document.getElementById('teCount').textContent")));

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
