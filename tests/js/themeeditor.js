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
        await pickerChecks(c, s);
        await expandChecks(c, s);
    } finally {
        await c.close();
        await s.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exit(1);
}

// ── The editor's own colour picker (2.0) ──────────────────────────────────────────
// <input type="color"> opens a different native dialog in every browser; the owner's rule is
// that nothing behaves differently by platform when it can be avoided. So every colour
// control is a swatch button that opens one popup Bonfire draws itself.

const KEYS = {
    Enter: { key: 'Enter', code: 'Enter', vk: 13, text: '\r' },
    ' ': { key: ' ', code: 'Space', vk: 32, text: ' ' },
    Escape: { key: 'Escape', code: 'Escape', vk: 27 },
    ArrowRight: { key: 'ArrowRight', code: 'ArrowRight', vk: 39 },
    ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft', vk: 37 },
    ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', vk: 38 },
    ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', vk: 40 }
};
async function press(c, name) {
    const k = KEYS[name];
    const base = { key: k.key, code: k.code, windowsVirtualKeyCode: k.vk, nativeVirtualKeyCode: k.vk };
    await c.page('Input.dispatchKeyEvent', Object.assign({ type: k.text ? 'keyDown' : 'rawKeyDown' }, base, k.text ? { text: k.text } : {}));
    await c.page('Input.dispatchKeyEvent', Object.assign({ type: 'keyUp' }, base));
    await wait(60);
}

const POPUP = '(function () { var p = document.querySelector("#themeEditor .jpf-te-pop"); return p && p.offsetParent !== null ? p : null; })()';
const RECT = expr => '(function () { var e = ' + expr + '; if (!e) return null; var r = e.getBoundingClientRect(); return { l: r.left, t: r.top, r: r.right, b: r.bottom }; })()';
const overlaps = (a, b) => a && b && a.l < b.r && a.r > b.l && a.t < b.b && a.b > b.t;
const CARD = "document.querySelector('#tePreview .jpf-theme-preview')";
const POP_Q = "document.querySelector('#themeEditor .jpf-te-pop";

async function openEditor(c, s, device) {
    await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=' + device + '&theme=dark', device);
    await c.evaluate("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
    await c.evaluate("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
    await c.evaluate('window.__bonfire.injectStyles()');
    await c.evaluate("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: []}); return a.apply(this, arguments); };})()");
    await c.evaluate("document.getElementById('themeNew').click()");
    await wait(600);
    await c.evaluate(frame);
}

async function pickerChecks(c, s) {
    console.log('\n  colour picker');
    const SHADOW = "document.querySelector('#teAdvancedFields [data-path=\"colors.shadow\"]')";
    for (const device of ['desktop', 'tablet', 'phone']) {
        await openEditor(c, s, device);
        const tag = ' (' + device + ')';
        if (device === 'desktop') {
            ok('no input type=color is left in the editor', (await c.evaluate("document.querySelectorAll('#themeEditor input[type=color]').length")) === 0);
            const n = await c.evaluate("[document.querySelectorAll('#teSimple .jpf-te-sw').length, document.querySelectorAll('#teAdvancedFields .jpf-te-sw').length, document.querySelectorAll('#teAdvancedFields [data-kind=color]').length]");
            ok('Simple has four swatches and Advanced one for every colour field', n[0] === 4 && n[1] === n[2] && n[1] > 10, JSON.stringify(n));
        }
        // Opens on click, Enter and Space.
        await c.evaluate("document.getElementById('teAccent').scrollIntoView({block:'center'})");
        await c.evaluate("document.getElementById('teAccent').click()");
        await wait(150);
        ok('a click opens the popup' + tag, !!(await c.evaluate('!!' + POPUP)));
        const rows = await c.evaluate("(function () { var ps = [].slice.call(document.querySelectorAll('#themeEditor .jpf-te-pop .jpf-te-pre'));" +
            "var tops = {}; ps.forEach(function (p) { var k = Math.round(p.getBoundingClientRect().top); tops[k] = (tops[k] || 0) + 1; });" +
            "return { n: ps.length, rows: Object.keys(tops).map(function (k) { return tops[k]; }), named: ps.every(function (p) { return /^#[0-9a-f]{6}$/i.test(p.getAttribute('aria-label') || ''); }) }; })()");
        ok('24 presets in three rows of eight, each named by its hex' + tag, rows.n === 24 && rows.rows.join() === '8,8,8' && rows.named, JSON.stringify(rows));
        const card = await c.evaluate(RECT(CARD));
        const pop = await c.evaluate(RECT(POPUP));
        ok('the popup does not cover the preview card' + tag, pop && card && !overlaps(pop, card), JSON.stringify({ pop, card }));
        ok('the popup is anchored to its square (within 60 px of it)' + tag, await c.evaluate("(function () { var a = document.getElementById('teAccent').getBoundingClientRect(), p = " + POPUP + ".getBoundingClientRect();" +
            "return Math.min(Math.abs(p.top - a.bottom), Math.abs(a.top - p.bottom), Math.abs(p.left - a.right), Math.abs(a.left - p.right)) < 60; })()"));

        // A preset changes the draft and the preview.
        const before = await c.evaluate("document.getElementById('teAccent').value");
        const preset = await c.evaluate("(function(){ var p = document.querySelectorAll('#themeEditor .jpf-te-pop .jpf-te-pre')[2]; var h = p.getAttribute('aria-label').toLowerCase(); p.click(); return h; })()");
        await wait(150);
        const after = await c.evaluate("({ v: document.getElementById('teAccent').value, css: (document.getElementById('jpfThemeEditorCss') || {}).textContent || ''," +
            "btn: getComputedStyle(document.querySelector('#tePreview .jpf-te-preview-primary')).backgroundColor })");
        const rgb = 'rgb(' + [1, 3, 5].map(i => parseInt(preset.slice(i, i + 2), 16)).join(', ') + ')';
        ok('a preset becomes the swatch value' + tag, after.v === preset && before !== preset, JSON.stringify({ before, preset, v: after.v }));
        ok('and the preview card wears it' + tag, after.btn === rgb && after.css.toLowerCase().indexOf(preset) !== -1, JSON.stringify({ btn: after.btn, rgb }));

        // Escape closes and focus goes back to the square.
        await press(c, 'Escape');
        ok('Escape closes the popup' + tag, !(await c.evaluate('!!' + POPUP)));
        ok('and focus is back on the square' + tag, await c.evaluate("document.activeElement === document.getElementById('teAccent')"));
        await press(c, 'Enter');
        ok('Enter opens it' + tag, !!(await c.evaluate('!!' + POPUP)));
        await press(c, 'Escape');
        await press(c, ' ');
        ok('Space opens it' + tag, !!(await c.evaluate('!!' + POPUP)));
        await c.evaluate("document.getElementById('themeEditorTitle').dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))");
        await wait(100);
        ok('a click outside closes it' + tag, !(await c.evaluate('!!' + POPUP)));
        ok('and focus is back on the square after it' + tag, await c.evaluate("document.activeElement === document.getElementById('teAccent')"));

        // Advanced: the same popup beside the text box, and alpha the field already has stays.
        await c.evaluate("document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]').click()");
        await wait(300);
        await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function(d){ d.open = true; })");
        await c.evaluate("(function(){ var t = " + SHADOW + "; t.value = '#11223344'; t.dispatchEvent(new Event('input', { bubbles: true })); t.parentNode.querySelector('.jpf-te-sw').scrollIntoView({ block: 'center' }); })()");
        await wait(100);
        await c.evaluate(SHADOW + ".parentNode.querySelector('.jpf-te-sw').click()");
        await wait(150);
        const advPop = await c.evaluate(RECT(POPUP));
        ok('the popup opens from an Advanced square without covering the card' + tag, advPop && !overlaps(advPop, await c.evaluate(RECT(CARD))), JSON.stringify(advPop));
        await c.evaluate("document.querySelectorAll('#themeEditor .jpf-te-pop .jpf-te-pre')[5].click()");
        await wait(100);
        const adv = await c.evaluate(SHADOW + '.value');
        ok('a preset in Advanced keeps the alpha the field had' + tag, /^#[0-9a-f]{6}44$/i.test(adv), adv);
        await press(c, 'Escape');
    }

    // The shade square, the hue strip and the hex box, on the desktop.
    await openEditor(c, s, 'desktop');
    await c.evaluate("document.getElementById('teAccent').click()");
    await wait(150);
    const sw = () => c.evaluate("document.getElementById('teAccent').value");
    const hexBox = () => c.evaluate(POP_Q + " .jpf-te-hex').value.toLowerCase()");
    await c.evaluate("(function(){ var h = " + POP_Q + " .jpf-te-hex'); h.value = '#336699'; h.dispatchEvent(new Event('input', { bubbles: true })); })()");
    await wait(100);
    ok('a hex typed in the box is the colour', (await sw()) === '#336699', await sw());
    const hue = await c.evaluate("+" + POP_Q + " .jpf-te-hue').getAttribute('aria-valuenow')");
    ok('and the hue strip follows it', Math.abs(hue - 210) <= 1, String(hue));
    await c.evaluate(POP_Q + " .jpf-te-sv').focus()");
    ok('the shade square shows where the keyboard is', await c.evaluate("getComputedStyle(" + POP_Q + " .jpf-te-sv')).outlineStyle !== 'none'"));
    const start = await sw();
    await press(c, 'ArrowRight');
    const right = await sw();
    ok('Right on the shade square adds saturation', right !== start && (await hexBox()) === right, start + ' -> ' + right);
    await press(c, 'ArrowUp');
    const up = await sw();
    ok('Up adds brightness', up !== right && (await hexBox()) === up, right + ' -> ' + up);
    await press(c, 'ArrowLeft'); await press(c, 'ArrowDown');
    ok('Left and Down take them back', (await sw()) === start, start + ' vs ' + (await sw()));
    await c.evaluate(POP_Q + " .jpf-te-hue').focus()");
    ok('the hue strip shows where the keyboard is', await c.evaluate("getComputedStyle(" + POP_Q + " .jpf-te-hue')).outlineStyle !== 'none'"));
    await press(c, 'ArrowRight');
    const hue2 = await c.evaluate("+" + POP_Q + " .jpf-te-hue').getAttribute('aria-valuenow')");
    ok('Right on the hue strip moves the hue and the colour', hue2 !== hue && (await sw()) !== start && (await hexBox()) === (await sw()), hue + ' -> ' + hue2);
    // The mouse: the top right corner of the square is full saturation and brightness.
    const box = await c.evaluate(RECT(POP_Q + " .jpf-te-sv')"));
    await c.page('Input.dispatchMouseEvent', { type: 'mousePressed', x: box.r - 1, y: box.t + 1, button: 'left', clickCount: 1 });
    await c.page('Input.dispatchMouseEvent', { type: 'mouseReleased', x: box.r - 1, y: box.t + 1, button: 'left', clickCount: 1 });
    await wait(100);
    const top = rgbOf(await sw());
    ok('the mouse sets the shade: top right is the pure hue', top && Math.max.apply(null, top) >= 250 && Math.min.apply(null, top) <= 6, JSON.stringify(top));
}
// ── The expanded preview (2.0) ───────────────────────────────────────────────────
// An Expand button on the preview card shows the profile picker itself in the theme being
// edited, over the Bonfire settings page and nothing outside it (not Jellyfin's header),
// and it keeps up with the draft while it is open.
const X = "document.getElementById('teExpanded')";
async function expandChecks(c, s) {
    console.log('\n  expanded preview');
    for (const device of ['desktop', 'phone']) {
        const tag = ' (' + device + ')';
        await openEditor(c, s, device);
        // Jellyfin's own header: fixed along the top, outside the plugin's page.
        await c.evaluate("(function(){ var h = document.createElement('div'); h.className = 'skinHeader'; h.style.cssText = 'position:fixed;top:0;left:0;right:0;height:56px;z-index:50;background:#333'; document.body.appendChild(h); })()");
        ok('the preview card has an Expand button' + tag, await c.evaluate("!!document.getElementById('teExpand') && !" + X));
        await c.evaluate("document.getElementById('teExpand').scrollIntoView({block:'center'})");
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(300);
        const x = await c.evaluate("(function () { var g = " + X + "; if (!g) return null; var r = g.getBoundingClientRect(), p = document.getElementById('profilesConfigurationPage').getBoundingClientRect(), h = document.querySelector('.skinHeader').getBoundingClientRect();" +
            "var mid = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2), top = document.elementFromPoint(window.innerWidth / 2, 20);" +
            "return { l: r.left, t: r.top, r: r.right, b: r.bottom, pl: p.left, pr: p.right, hb: h.bottom, vh: window.innerHeight, midIn: !!(mid && g.contains(mid)), headerIsHeader: !!(top && top.closest('.skinHeader')) }; })()");
        ok('Expand shows the expanded view' + tag, !!x, 'no #teExpanded');
        if (!x) continue;
        ok('it covers the settings page and nothing outside it' + tag,
            x.l >= x.pl - 1 && x.r <= x.pr + 1 && x.r - x.l >= x.pr - x.pl - 2 && x.t >= x.hb - 1 && x.b >= Math.min(x.vh, 100000) - 1 && x.midIn, JSON.stringify(x));
        ok('and leaves Jellyfin\'s header uncovered' + tag, x.headerIsHeader, JSON.stringify(x));
        const parts = await c.evaluate("({ cards: document.querySelectorAll('#teExpanded .profile-card').length, add: document.querySelectorAll('#teExpanded .action-add-profile').length," +
            "title: !!document.querySelector('#teExpanded .profiles-title'), buttons: document.querySelectorAll('#teExpanded .profiles-btn').length })");
        ok('it shows sample profiles, the add tile, the title and the buttons' + tag, parts.cards >= 4 && parts.add === 1 && parts.title && parts.buttons >= 1, JSON.stringify(parts));
        const look = await c.evaluate("({ bg: getComputedStyle(document.getElementById('teExpandGate')).backgroundImage, color: getComputedStyle(document.querySelector('#teExpanded .profiles-title')).color })");
        ok('it wears the draft: the theme\'s background layers' + tag, /gradient/.test(look.bg), look.bg);
        // It follows a change made while it is open.
        await c.evaluate("(function(){ var b = document.getElementById('teText'); b.value = '#00ff00'; b.dispatchEvent(new Event('input', { bubbles: true })); })()");
        await wait(200);
        const green = await c.evaluate("getComputedStyle(document.querySelector('#teExpanded .profiles-title')).color");
        ok('and follows a change made while it is open' + tag, green === 'rgb(0, 255, 0)' && look.color !== green, look.color + ' -> ' + green);
        // Escape and Close both go back, with focus on Expand.
        await press(c, 'Escape');
        ok('Escape closes it' + tag, !(await c.evaluate('!!' + X)));
        ok('and focus is on Expand' + tag, await c.evaluate("document.activeElement === document.getElementById('teExpand')"));
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(200);
        await c.evaluate("document.querySelector('#teExpanded .jpf-te-xclose').click()");
        ok('Close closes it' + tag, !(await c.evaluate('!!' + X)));
        ok('and focus is on Expand after Close' + tag, await c.evaluate("document.activeElement === document.getElementById('teExpand')"));
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(200);
        await c.evaluate("document.getElementById('teCancel').click()");
        ok('closing the editor takes it down' + tag, !(await c.evaluate('!!' + X)));
    }
}
function rgbOf(h) { const m = /^#([0-9a-f]{6})/i.exec(h || ''); return m ? [0, 2, 4].map(i => parseInt(m[1].slice(i, i + 2), 16)) : null; }

main().catch(e => { console.error(e.stack || e); process.exit(1); });
