#!/usr/bin/env node
/*
 * The theme editor in three columns from 1280 px (Advanced only), and the preview that opens
 * its fields when clicked (2026-11-12), driven in Chrome on the screens fixture with Bonfire's
 * client script loaded.
 *
 * Before: at every width Advanced was two columns (fields, with the preview beside or above
 * them) and every group was a fold-out; the preview could not be clicked.
 *
 * The page content is held to 1040 px at 1280 (jellyfin-web's own dashboard has a 240 px drawer
 * beside it), so the layout is asked at the width it really has, not at the fixture's.
 *
 *     node tests/js/themecolumns.js [/old/profilesDashboard.html]
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
const CLICK = sel => `(function(){ var e = ${sel}; if (!e) return false; e.click(); return true; })()`;
const SET = (sel, v, ev) => `(function(){ var e = ${sel}; if (!e) return false; e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event('${ev || 'input'}', { bubbles: true })); return true; })()`;
const KEY = (target, code) => `(function(){ var e = new KeyboardEvent('keydown', { keyCode: ${code}, which: ${code}, bubbles: true, cancelable: true });
    ${target}.dispatchEvent(e); return e.defaultPrevented; })()`;
// Which groups are on screen, which is selected, and where the three columns are.
const SHOWN = `[].slice.call(document.querySelectorAll('#teAdvancedFields details.jpf-te-group')).filter(function (d) { return getComputedStyle(d).display !== 'none'; }).map(function (d) { return d.getAttribute('data-group'); })`;
const PRESSED = `[].slice.call(document.querySelectorAll('#teOutline .jpf-te-opt')).filter(function (b) { return b.getAttribute('aria-pressed') === 'true'; }).map(function (b) { return b.getAttribute('data-outline'); })`;
const BOXES = `(function () { function r(s) { var e = document.querySelector(s); if (!e || getComputedStyle(e).display === 'none') return null; var b = e.getBoundingClientRect(); return { l: Math.round(b.left), r: Math.round(b.right), t: Math.round(b.top), h: Math.round(b.height), w: Math.round(b.width) }; }
  return { outline: r('#teOutline'), preview: r('#tePreview .jpf-theme-preview'), fields: r('#teAdvancedFields'), card: r('#themeEditor .jpf-te-side') }; })()`;
const THREE = `document.getElementById('themeEditor').classList.contains('jpf-te-3col')`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n0 passed, 0 failed (skipped)');
        return;
    }
    const s = await fixture.start(0, { dashboard: process.argv[2] || undefined });
    const c = await chrome.launch();
    const ev = x => c.evaluate(x).catch(() => null);
    const size = async (w, h) => { await c.page('Emulation.setDeviceMetricsOverride', { width: w, height: h || 900, deviceScaleFactor: 1, mobile: false }); await wait(400); await ev(frame); };
    try {
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=desktop&theme=dark', 'desktop');
        await ev("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await ev("(function(){ var st = document.createElement('style'); st.textContent = '.content-primary{max-width:1040px!important}'; document.head.appendChild(st); })()");
        await ev("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await ev('window.__bonfire.injectStyles()');
        await ev("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: window.__problems || []}); return a.apply(this, arguments); };})()");
        await size(1280);
        await ev("document.getElementById('themeNew').click()");
        await wait(600);

        // 1. Where the layout switches.
        ok('in Simple at 1280 px there are no columns', (await ev(THREE)) === false);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev(frame);
        ok('Advanced at 1280 px is three columns', (await ev(THREE)) === true);
        await size(1279);
        ok('at 1279 px it is not', (await ev(THREE)) === false);
        await size(1000);
        ok('at 1000 px it is not', (await ev(THREE)) === false);
        const flat = (await ev(`({ outline: getComputedStyle(${Q('#teOutline')} || document.body).display, shown: ${SHOWN}.length, groups: document.querySelectorAll('#teAdvancedFields details.jpf-te-group').length, body: getComputedStyle(${Q('#themeEditor .jpf-te-body')}).display })`)) || {};
        ok('between 900 and 1279 the outline is not drawn and every group is there to fold open', flat.outline === 'none' && flat.groups >= 8 && flat.shown === flat.groups && flat.body === 'flex', JSON.stringify(flat));
        const narrow = await ev(`getComputedStyle(${Q('#teAdvanced')}).display`);
        const one = await ev(BOXES);
        await size(1280);
        ok('crossing back up to 1280 px with Advanced open brings the columns back', (await ev(THREE)) === true);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="simple"]')));
        ok('Simple at 1280 px is not columns', (await ev(THREE)) === false && (await ev(`getComputedStyle(${Q('#teOutline') || 'document.body'}).display`)) !== 'flex');
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev(frame);
        ok('narrow Advanced is still shown', narrow !== 'none');

        // 2. The outline selects one group at a time.
        const keys = await ev("[].slice.call(document.querySelectorAll('#teAdvancedFields details.jpf-te-group')).map(function (d) { return d.getAttribute('data-group'); })");
        const opts = await ev("[].slice.call(document.querySelectorAll('#teOutline .jpf-te-opt')).map(function (b) { return b.getAttribute('data-outline'); })");
        ok('the outline has a button for every group, as buttons', keys.length >= 8 && JSON.stringify(opts) === JSON.stringify(keys) && (await ev("[].every.call(document.querySelectorAll('#teOutline .jpf-te-opt'), function (b) { return b.tagName === 'BUTTON'; })")) === true, JSON.stringify(opts));
        ok('one is selected, and it is the first', JSON.stringify(await ev(PRESSED)) === JSON.stringify([keys[0]]), JSON.stringify(await ev(PRESSED)));
        ok('only that group is shown on the right', JSON.stringify(await ev(SHOWN)) === JSON.stringify([keys[0]]), JSON.stringify(await ev(SHOWN)));
        await ev(CLICK(Q(`#teOutline .jpf-te-opt[data-outline="${keys[2]}"]`)));
        ok('choosing another group selects it and shows only it', JSON.stringify(await ev(PRESSED)) === JSON.stringify([keys[2]]) && JSON.stringify(await ev(SHOWN)) === JSON.stringify([keys[2]]), JSON.stringify(await ev(PRESSED)) + JSON.stringify(await ev(SHOWN)));

        // 3. Three columns, side by side, the preview larger than in two.
        const b = await ev(BOXES);
        ok('the outline, the preview and the fields are three columns, left to right', !!(b.outline && b.preview && b.fields) && b.outline.r <= b.preview.l + 1 && b.preview.r <= b.fields.l + 1, JSON.stringify(b));
        ok('the preview is larger than it is in two columns', !!(b.preview && one.preview) && b.preview.h > one.preview.h, JSON.stringify([b.preview, one.preview]));
        const pos = await ev(`({ o: getComputedStyle(${Q('#teOutline')} || document.body).position, p: getComputedStyle(${Q('#themeEditor .jpf-te-side')}).position })`);
        ok('the outline and the preview are sticky', !!pos && /sticky/.test(pos.o) && /sticky/.test(pos.p), JSON.stringify(pos));

        // 4. Colours two across.
        await ev(CLICK(Q(`#teOutline .jpf-te-opt[data-outline="${keys[0]}"]`)));
        await ev(frame);
        const cols = (await ev(`(function(){ var rows = [].slice.call(document.querySelectorAll('#teAdvancedFields details.is-selected .jpf-te-adv-color')).filter(function (r) { return r.offsetWidth > 0; }).map(function (r) { var b = r.getBoundingClientRect(); return { t: Math.round(b.top), l: Math.round(b.left) }; });
            return { n: rows.length, across: rows.length ? rows.filter(function (r) { return Math.abs(r.t - rows[0].t) < 3; }).length : 0 }; })()`)) || { n: 0, across: 0 };
        ok('the colours sit two across or better', cols.n >= 4 && cols.across >= 2, JSON.stringify(cols));

        // 5. Search: a match in another group selects that group.
        await ev(SET(Q('#teSearch'), 'decorations'));
        await ev(frame);
        const last = keys[keys.length - 1];
        ok('a match only in another group selects it', JSON.stringify(await ev(PRESSED)) === JSON.stringify([last]) && JSON.stringify(await ev(SHOWN)) === JSON.stringify([last]), JSON.stringify(await ev(PRESSED)) + JSON.stringify(await ev(SHOWN)));
        ok('and the groups with no match leave the outline', (await ev("[].filter.call(document.querySelectorAll('#teOutline .jpf-te-opt'), function (b) { return getComputedStyle(b).display !== 'none'; }).length")) === 1);
        await ev(SET(Q('#teSearch'), ''));
        await ev(frame);
        ok('clearing the search brings the outline back, the choice kept', (await ev("[].filter.call(document.querySelectorAll('#teOutline .jpf-te-opt'), function (b) { return getComputedStyle(b).display !== 'none'; }).length")) === keys.length && JSON.stringify(await ev(SHOWN)) === JSON.stringify([last]));

        // 6. Keyboard.
        await ev(CLICK(Q(`#teOutline .jpf-te-opt[data-outline="${keys[0]}"]`)));
        await ev(`${Q(`#teOutline .jpf-te-opt[data-outline="${keys[0]}"]`)}.focus(); 0`);
        await ev(KEY('document.activeElement', 40));
        ok('the down arrow moves focus to the next button', (await ev("document.activeElement && document.activeElement.getAttribute('data-outline')")) === keys[1]);
        ok('moving does not select', JSON.stringify(await ev(PRESSED)) === JSON.stringify([keys[0]]));
        await ev(KEY('document.activeElement', 38));
        ok('the up arrow moves back', (await ev("document.activeElement && document.activeElement.getAttribute('data-outline')")) === keys[0]);
        await ev(KEY('document.activeElement', 35));
        ok('End goes to the last', (await ev("document.activeElement && document.activeElement.getAttribute('data-outline')")) === last);
        const ring = (await ev("(function(){ var s = getComputedStyle(document.activeElement); return { w: parseFloat(s.outlineWidth), st: s.outlineStyle }; })()")) || { w: 0, st: 'none' };
        ok('the focused button has a visible ring', ring.w >= 2 && ring.st !== 'none', JSON.stringify(ring));

        // 7. Changed dots, Reset group and problems keep working, and show on the outline.
        await ev(CLICK(Q(`#teOutline .jpf-te-opt[data-outline="${keys[0]}"]`)));
        await ev(frame);
        const target = await ev("(function(){ var i = document.querySelector('#teAdvancedFields details.is-selected .jpf-te-adv-color .jpf-te-input'); return i ? i.id : ''; })()");
        ok('the test found a colour box to change', !!target);
        await ev(SET(Q('#' + target), '#123456', 'input'));
        await wait(300);
        ok('a changed field shows its dot, and its group a count on the outline', (await ev("document.querySelectorAll('#teAdvancedFields details.is-selected .jpf-te-adv.is-changed').length")) === 1
            && (await ev(`${Q(`#teOutline .jpf-te-opt[data-outline="${keys[0]}"] .jpf-te-ocount`)}.textContent`)) === '1');
        await ev(CLICK(Q('#teAdvancedFields details.is-selected [data-reset-group]')));
        await wait(300);
        ok('Reset group clears it, count and all', (await ev("document.querySelectorAll('#teAdvancedFields .jpf-te-adv.is-changed').length")) === 0
            && (await ev(`${Q(`#teOutline .jpf-te-opt[data-outline="${keys[0]}"] .jpf-te-ocount`)}.textContent`)) === '');
        const lastField = await ev(`(function(){ var d = document.querySelector('#teAdvancedFields details[data-group="${last}"] .jpf-te-field[data-paths]'); return d ? d.getAttribute('data-paths').split(' ')[0] : ''; })()`);
        await ev(`window.__problems = [{ path: ${JSON.stringify(lastField)}, code: 'range', detail: '' }]; ${ED}.problems = window.__problems; 0`);
        await ev(SET(Q('#teName'), 'Marked', 'input'));
        await wait(700);
        ok('a problem in a group that is not showing marks it on the outline', (await ev(`${Q(`#teOutline .jpf-te-opt[data-outline="${last}"]`)}.classList.contains('jpf-te-has-problem')`)) === true);
        await ev(CLICK(Q('#teCount')));
        await wait(500);
        ok('the bar count selects that group and shows the field with the problem', JSON.stringify(await ev(PRESSED)) === JSON.stringify([last]) && (await ev("(function(){ var b = document.querySelector('#themeEditor .jpf-te-bad'); return !!b && b.getBoundingClientRect().height > 0; })()")) === true);
        await ev(`window.__problems = []; ${ED}.problems = []; 0`);
        await ev(SET(Q('#teName'), 'Clean', 'input'));
        await wait(500);
        await ev(CLICK(Q('#teCancel')));
        await ev("document.getElementById('themeNew').click(); 0");
        await wait(500);
        ok('Undo is still in the bar', (await ev("!!document.getElementById('teUndo') && !!document.getElementById('teRedo')")) === true);

        // 8. A saved choice of open groups is not overwritten by the columns.
        await ev("try { localStorage.setItem('bonfire-theme-groups', JSON.stringify([])); } catch (e) {} 0");
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev(CLICK(Q(`#teOutline .jpf-te-opt[data-outline="${keys[3]}"]`)));
        await wait(300);
        ok('the open-groups memory is not rewritten while the columns choose', (await ev("localStorage.getItem('bonfire-theme-groups')")) === '[]', await ev("localStorage.getItem('bonfire-theme-groups')"));
        await size(1000);
        ok('narrowing puts the folds back as the viewer left them', JSON.stringify(await ev("[].filter.call(document.querySelectorAll('#teAdvancedFields details.jpf-te-group'), function (d) { return d.open; }).map(function (d) { return d.getAttribute('data-group'); })")) === '[]');
    } finally { await c.close(); await s.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
