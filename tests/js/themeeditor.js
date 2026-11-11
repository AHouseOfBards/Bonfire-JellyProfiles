#!/usr/bin/env node
/*
 * The dashboard's theme editor, driven in Chrome on the screens fixture, with Bonfire's
 * client script and stylesheet loaded as a real server loads them on the dashboard.
 *
 * Three things an administrator saw on the first screenshot of it (2026-11-11):
 *   1. the preview sat under every control, so a change near the top could not be seen
 *      without scrolling, and in Advanced it was about 1,000 px down;
 *   2. "Start from" read "—" on a new theme, which is in fact a copy of Bonfire;
 *   3. the Id box was shown in Simple, though it fills itself in from the name.
 *
 * Measured, not read from the source: where the preview is when the last control is on
 * screen, what the select shows, and whether the Id box is displayed.
 *
 * Needs Chrome, and says it was skipped, exiting 0, without it.
 *
 *     node tests/js/themeeditor.js [/old/profilesDashboard.html]
 */
'use strict';

const path = require('path');
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

// Is the preview card wholly inside the window, with the given control also on screen?
const PREVIEW_WITH = sel => `(function () {
  var last = document.querySelector(${JSON.stringify(sel)});
  if (!last) return { missing: true };
  last.scrollIntoView({ block: 'end' });
  var p = document.querySelector('#tePreview').getBoundingClientRect();
  var l = last.getBoundingClientRect();
  return { top: Math.round(p.top), bottom: Math.round(p.bottom), h: Math.round(p.height), vh: window.innerHeight,
           lastBottom: Math.round(l.bottom),
           inView: p.height > 40 && p.top >= 0 && p.bottom <= window.innerHeight && l.bottom <= window.innerHeight + 1 };
})()`;

const SHOWN = id => `(function () { var e = document.getElementById(${JSON.stringify(id)}); if (!e) return null;
  var box = e.closest('.inputContainer') || e; return box.offsetParent !== null && getComputedStyle(box).display !== 'none'; })()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n0 passed, 0 failed (skipped)');
        return;
    }
    const s = await fixture.start(0, { dashboard: process.argv[2] || undefined });
    const c = await chrome.launch();
    try {
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=desktop&theme=dark', 'desktop');
        await c.evaluate("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await c.evaluate("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await c.evaluate('window.__bonfire.injectStyles()');
        await c.evaluate("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: []}); return a.apply(this, arguments); };})()");
        ok('the client script gave the page its preview', await c.evaluate("typeof window.BonfireThemePreview === 'object'"));

        await c.evaluate("document.getElementById('themeNew').click()");
        await wait(600);
        await c.evaluate(frame);
        ok('New theme opens the editor', await c.evaluate("!document.getElementById('themeEditor').classList.contains('jpf-hidden')"));

        // 2. Start from names what the new theme is a copy of.
        const base = await c.evaluate("(function(){var s=document.getElementById('teBase'); var o=s.options[s.selectedIndex]; return { value: s.value, text: o ? o.textContent : '' };})()");
        ok('a new theme says it starts from Bonfire', base.value === 'b:bonfire' && /Bonfire/.test(base.text), JSON.stringify(base));

        // 3. The Id fills itself in; Simple does not show the box, Advanced does.
        ok('Simple does not show the Id box', (await c.evaluate(SHOWN('teId'))) === false);
        await c.evaluate("(function(){var n=document.getElementById('teName'); n.value='Movie Night'; n.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await wait(200);
        ok('the id still comes from the name', (await c.evaluate("document.getElementById('teId').value")) === 'movie-night');

        // 1. The preview is on screen while the last control is.
        const simple = await c.evaluate(PREVIEW_WITH('#tePattern'));
        ok('in Simple the preview is on screen beside the last control', simple.inView, JSON.stringify(simple));

        await c.evaluate("document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]').click()");
        await wait(500);
        ok('Advanced shows the Id box', (await c.evaluate(SHOWN('teId'))) === true);
        await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function(d){ d.open = true; })");
        await wait(300);
        const adv = await c.evaluate(PREVIEW_WITH('#teAdvancedFields > :last-child'));
        ok('in Advanced, with every section open, the preview is on screen at the last field', adv.inView, JSON.stringify(adv));

        // The same on a phone: the preview may sit above the fields, but it must stay on screen.
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=phone&theme=dark', 'phone');
        await c.evaluate("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await c.evaluate("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await c.evaluate('window.__bonfire.injectStyles()');
        await c.evaluate("document.getElementById('themeNew').click()");
        await wait(600);
        const phone = await c.evaluate(PREVIEW_WITH('#tePattern'));
        ok('on a phone the preview is on screen at the last control', phone.inView, JSON.stringify(phone));
    } finally {
        await c.close();
        await s.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exit(1);
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
