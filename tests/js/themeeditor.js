#!/usr/bin/env node
/*
 * The dashboard's theme editor, driven in Chrome on the screens fixture, with Bonfire's
 * client script and stylesheet loaded as a real server loads them on the dashboard.
 *
 * Three things an administrator saw on the first screenshot of it (2026-11-11):
 *   1. the preview sat under every control, so a change near the top could not be seen
 *      without scrolling, and in Advanced it was about 1,000 px down;
 *   2. "Start from" read "—" on a new theme, which is in fact a copy of Bonfire;
 *   3. the Id box was shown in Simple, though it fills itself in from the name. Since
 *      1.9.0.4-beta there is no Id box at all: the id is always made from the name.
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

        // 3. No Id box anywhere (owner, 1.9.0.4-beta): the id is made from the name, unique
        // among themes, and fixed once saved. It was shown in Advanced to be typed over.
        const DRAFT_ID = "document.getElementById('profilesConfigurationPage')._themeEditor.draft.id";
        ok('the editor has no Id box', (await c.evaluate("!document.getElementById('teId') && !document.querySelector('#themeEditor [data-paths=id], #themeEditor [data-path=id]')")) === true);
        await c.evaluate("(function(){var n=document.getElementById('teName'); n.value='Movie Night'; n.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await wait(200);
        ok('the id still comes from the name', (await c.evaluate(DRAFT_ID)) === 'movie-night');
        await c.evaluate("(function(){var n=document.getElementById('teName'); n.value='Bonfire'; n.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await wait(200);
        ok('and is kept unique among themes', (await c.evaluate(DRAFT_ID)) === 'bonfire-2');
        await c.evaluate("(function(){var n=document.getElementById('teName'); n.value='Movie Night'; n.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await wait(200);

        // 1. The preview is on screen while the last control is.
        const simple = await c.evaluate(PREVIEW_WITH('#tePattern'));
        ok('in Simple the preview is on screen beside the last control', simple.inView, JSON.stringify(simple));

        await c.evaluate("document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]').click()");
        await wait(500);
        ok('Advanced has no Id box either', (await c.evaluate("!document.querySelector('#teAdvancedFields [data-path=id]') && !document.getElementById('teId')")) === true);
        // Eight groups named for what they style (owner, 2026-11-12); only Colors starts open,
        // and the groups a viewer opens are remembered (localStorage, per viewer).
        const GROUPS = "[].slice.call(document.querySelectorAll('#teAdvancedFields > details')).map(function(d){ return (d.getAttribute('data-group') || '').replace('admin.themes.group.', '') + '=' + d.querySelector('summary').textContent + (d.open ? '*' : ''); }).join('|')";
        ok('Advanced is eight groups named for what they style, Colors open',
            (await c.evaluate(GROUPS)) === 'colours=Colors*|background=Background|tiles=Tiles and layout|names=Names|type=Type|buttons=Buttons|cards=Cards and text boxes|decorations=Decorations', await c.evaluate(GROUPS));
        await c.evaluate("document.querySelector('#teAdvancedFields details[data-group$=\"names\"] summary').click()");
        await wait(300);
        const savedGroups = await c.evaluate("localStorage.getItem('bonfire-theme-groups')");
        ok('opening a group saves which are open', savedGroups === '["admin.themes.group.colours","admin.themes.group.names"]');
        // The scene clears storage on every load (tests/screens/dashboard.js), so the saved
        // value is put back, as the same browser would still hold it.
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=desktop&theme=dark', 'desktop');
        await c.evaluate("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await c.evaluate("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await c.evaluate('window.__bonfire.injectStyles()');
        await c.evaluate("localStorage.setItem('bonfire-theme-groups', " + JSON.stringify(savedGroups) + ")");
        await c.evaluate("document.getElementById('themeNew').click()");
        await wait(600);
        await c.evaluate("document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]').click()");
        await wait(500);
        ok('and a new visit opens those groups again', (await c.evaluate(GROUPS)).replace(/=[^|*]*/g, '') === 'colours*|background|tiles|names*|type|buttons|cards|decorations', await c.evaluate(GROUPS));

        // Search (owner, 2026-11-12): one box above the groups, by the translated name and the
        // path. A row is "shown" by its computed display, because a row inside a closed group
        // is not on screen but is not hidden by the search either.
        const SHOWN = "(function(){ var shown = function (e) { return !!e && getComputedStyle(e).display !== 'none'; };" +
            " return JSON.stringify({ rows: [].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-adv')).filter(shown).map(function (r) { return r.getAttribute('data-paths'); })," +
            " groups: [].slice.call(document.querySelectorAll('#teAdvancedFields > details')).filter(shown).map(function (d) { return d.getAttribute('data-group').replace('admin.themes.group.', '') + (d.open ? '*' : ''); })," +
            " noMatch: shown(document.getElementById('teNoMatch')) }); })()";
        const TYPE = v => "(function(){ var s = document.getElementById('teSearch'); if (!s) return false; s.value = " + JSON.stringify(v) + "; s.dispatchEvent(new Event('input', { bubbles: true })); return true; })()";
        const shown = async () => JSON.parse(await c.evaluate(SHOWN));
        const total = (await shown()).rows.length;
        ok('there is one search box above the groups', (await c.evaluate("(function(){ var s = document.getElementById('teSearch'); return !!s && s.nextElementSibling.id === 'teNoMatch' && s.nextElementSibling.nextElementSibling.id === 'teAdvancedFields'; })()")) === true);
        await c.evaluate(TYPE('focused button'));
        let st = await shown();
        ok('searching a name keeps only the rows with it, and only their group, open',
            st.rows.length === 6 && st.rows.every(p => /^parts\.button2\.focus\./.test(p)) && JSON.stringify(st.groups) === '["buttons*"]' && st.noMatch === false, JSON.stringify(st));
        await c.evaluate(TYPE('button2.focus.glow'));
        st = await shown();
        ok('and a path finds its row alone', JSON.stringify(st.rows) === '["parts.button2.focus.glow"]', JSON.stringify(st));
        await c.evaluate(TYPE('Avatar RING'));
        st = await shown();
        ok('every word must match, in any case', st.rows.indexOf('colors.ring') !== -1 && st.rows.indexOf('shape.ringWidth') !== -1 && st.rows.every(p => /ring/i.test(p)), JSON.stringify(st));
        await c.evaluate(TYPE('zzzz'));
        st = await shown();
        ok('with nothing found it says No match and shows no group', st.rows.length === 0 && st.groups.length === 0 && st.noMatch === true
            && (await c.evaluate("document.getElementById('teNoMatch').textContent")) === 'No match', JSON.stringify(st));
        ok('searching never changes which groups are remembered', (await c.evaluate("localStorage.getItem('bonfire-theme-groups')")) === savedGroups);
        await c.evaluate(TYPE(''));
        await wait(300);
        st = await shown();
        ok('clearing restores every row, the open groups and hides No match',
            st.rows.length === total && st.noMatch === false && st.groups.join('|') === 'colours*|background|tiles|names*|type|buttons|cards|decorations', JSON.stringify(st.groups) + ' ' + st.rows.length + '/' + total);
        ok('and still did not touch the saved groups', (await c.evaluate("localStorage.getItem('bonfire-theme-groups')")) === savedGroups);

        await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function(d){ d.open = true; })");
        await wait(300);
        const adv =await c.evaluate(PREVIEW_WITH('#teAdvancedFields > :last-child'));
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
        await advancedControlChecks(c, s);
        await previewSizeChecks(c, s);
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
    Tab: { key: 'Tab', code: 'Tab', vk: 9 },
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

async function openEditor(c, s, device, before) {
    await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=' + device + '&theme=dark', device);
    await c.evaluate("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
    await c.evaluate("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
    await c.evaluate('window.__bonfire.injectStyles()');
    await c.evaluate("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: []}); return a.apply(this, arguments); };})()");
    if (before) await c.evaluate(before);
    await c.evaluate("document.getElementById('themeNew').click()");
    await wait(600);
    await c.evaluate(frame);
}

// ── Advanced's controls say how they are filled (owner, 1.9.0.4-beta) ─────────────
// Shadows and bounded numbers were raw JSON and number boxes. Each control must write the
// value the old box wrote for the same input: a slider the same number, a shadow's small
// boxes the same object JSON.parse gave, None the null the box took.
const ADV_FIELD = p => `document.querySelector('#teAdvancedFields [data-path="${p}"]')`;
const DRAFT = p => `(function(){ var d = document.getElementById('profilesConfigurationPage')._themeEditor.draft; return JSON.stringify(${JSON.stringify(p)}.split('.').reduce(function (o, k) { return o && typeof o === 'object' ? o[k] : undefined; }, d)); })()`;
const SET = (sel, v, ev) => `(function(){ var e = ${sel}; if (!e) return false; if (e.type === 'checkbox') e.checked = ${JSON.stringify(v)}; else e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event('${ev || 'input'}', { bubbles: true })); return true; })()`;

async function advancedControlChecks(c, s) {
    console.log('\n  Advanced controls');
    await openEditor(c, s, 'desktop');
    await c.evaluate("document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]').click()");
    await wait(300);
    // A bounded number is a slider with its value shown, and writes the number.
    const r = await c.evaluate(`(function(){ var e = ${ADV_FIELD('shape.radius.small')}; return e ? { type: e.type, min: e.min, max: e.max } : null; })()`);
    ok('a bounded number is a slider from its schema range', !!r && r.type === 'range' && r.min === '0' && r.max === '64', JSON.stringify(r));
    await c.evaluate(SET(ADV_FIELD('shape.radius.small'), '14'));
    ok('the slider writes the number the box wrote', (await c.evaluate(DRAFT('shape.radius.small'))) === '14');
    ok('and shows its value beside it', (await c.evaluate(`(function(){ var e = ${ADV_FIELD('shape.radius.small')}; var o = e && e.parentNode.querySelector('.jpf-te-val'); return o ? o.textContent : null; })()`)) === '14');
    await c.evaluate(SET(ADV_FIELD('focus.scale'), '1.12'));
    ok('a fractional slider writes the fraction', (await c.evaluate(DRAFT('focus.scale'))) === '1.12');

    // A shadow: small labelled boxes, each writing its key; together the object the box wrote.
    const text = '{"x":1,"y":-2,"blur":30,"spread":0,"opacity":0.5,"color":"#11223380"}';
    const want = JSON.parse(text);
    for (const k of ['x', 'y', 'blur', 'spread', 'opacity', 'color']) {
        const done = await c.evaluate(SET(ADV_FIELD('depth.avatar.' + k), String(want[k])));
        ok('depth.avatar has a box for ' + k, done === true);
    }
    const got = JSON.parse((await c.evaluate(DRAFT('depth.avatar'))) || 'null');
    ok('the shadow boxes write the object the JSON box wrote', !!got && Object.keys(want).every(k => got[k] === want[k]) && Object.keys(got).length === Object.keys(want).length,
        JSON.stringify(got));
    await c.evaluate(SET(ADV_FIELD('depth.avatar'), true, 'change'));
    ok('None writes null, as the box did', (await c.evaluate(DRAFT('depth.avatar'))) === 'null');
    ok('and greys out the boxes', await c.evaluate(`(function(){ var e = ${ADV_FIELD('depth.avatar.x')}; return !!e && e.disabled === true; })()`));
    await c.evaluate(SET(ADV_FIELD('depth.avatar'), false, 'change'));
    ok('unticking None brings the boxes\' values back', (await c.evaluate(DRAFT('depth.avatar.blur'))) === '30');
    for (const p of ['depth.title', 'depth.sheet', 'focus.glow', 'parts.button2.focus.glow']) {
        ok(p + ' is a shadow, not a JSON box', await c.evaluate(`(function(){ var n = ${ADV_FIELD(p)}; return !!${ADV_FIELD(p + '.blur')} && !!n && n.type === 'checkbox'; })()`));
    }

    // Only the free-form lists stay JSON, and every field says how it is filled.
    const json = await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields textarea')).map(function (e) { return e.getAttribute('data-path'); })");
    ok('only the free-form lists are JSON', JSON.stringify(json) === JSON.stringify(['background.layers', 'decorations']), JSON.stringify(json));
    const hints = await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-adv:not(.jpf-te-photo)')).map(function (f) { var h = f.querySelector('.jpf-te-hint'); var c = h ? h.cloneNode(true) : null; if (c) [].slice.call(c.querySelectorAll('.jpf-te-example')).forEach(function (x) { x.parentNode.removeChild(x); }); return { p: f.getAttribute('data-paths'), h: c ? c.textContent.replace(/\\s+/g, ' ').trim() : '' }; })");
    // The JSON example is code, not words, and is checked on its own below.
    // A hint that says nothing ("A hex color", "Choose from the list") was dropped on purpose
    // (owner, 2026-11-12): a field has one only where it tells a range or an effect.
    const bad = hints.filter(x => x.h.split(' ').length >= 10);
    ok('a hint, where a field has one, is under ten words', hints.length > 50 && bad.length === 0, bad.slice(0, 5).map(x => x.p + ': "' + x.h + '"').join('; ') + ' of ' + hints.length);
    const empty = hints.filter(x => !x.h);
    ok('and most fields have none: the hints left are the ranges, shadows, lists and font',
        empty.length > 30 && hints.length - empty.length < 40, empty.length + ' without of ' + hints.length);
    // Plain names (owner, 2026-11-12): each row's label is a short name, with its path
    // small beneath it.
    const names = await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-adv:not(.jpf-te-photo)')).map(function (f) { var n = f.querySelector('.jpf-te-adv-name'), p = f.querySelector('.jpf-te-adv-label code'); return { path: f.getAttribute('data-paths'), name: n ? n.textContent : '', code: p ? p.textContent : '', size: p ? parseFloat(getComputedStyle(p).fontSize) : 0, nameSize: n ? parseFloat(getComputedStyle(n).fontSize) : 0 }; })");
    const unnamed = names.filter(n => !n.name || n.name === n.path || n.name.indexOf('.') !== -1 || n.name.split(' ').length > 6);
    ok('every Advanced row has a plain name, not its path', names.length > 50 && unnamed.length === 0, unnamed.slice(0, 5).map(n => n.path + ': "' + n.name + '"').join('; '));
    ok('and the path is still there beneath it, smaller', names.every(n => n.code === n.path && n.size < n.nameSize), names.filter(n => n.code !== n.path || n.size >= n.nameSize).slice(0, 3).map(n => n.path).join());
    ok('no two rows share a name', new Set(names.map(n => n.name)).size === names.length);
    ok('the focused button\'s border width reads as in the brief',
        (names.filter(n => n.path === 'parts.button2.focus.border.width')[0] || {}).name === 'Focused button, border width',
        JSON.stringify(names.filter(n => n.path === 'parts.button2.focus.border.width')));
    const ex = await c.evaluate("[].slice.call(document.querySelectorAll('#teAdvancedFields .jpf-te-example')).map(function (e) { try { return Array.isArray(JSON.parse(e.textContent)); } catch (x) { return false; } })");
    ok('the JSON lists show an example that parses', ex.length === 2 && ex.every(Boolean), JSON.stringify(ex));
}

// ── The preview's size and place (owner, 1.9.0.4-beta) ───────────────────────────
// It was a ~240 px card at the top right. On a wide page it is now a column of about 45% of
// the editor, kept in view below Jellyfin's header; on a phone, where nothing fits beside
// the form, a short strip that stays at the top, its Expand button still opening it full size.
// Chromium 53 has no position: sticky, so the same is checked with sticky taken away.
const HEADER = "(function(){ var h = document.createElement('div'); h.className = 'skinHeader'; h.style.cssText = 'position:fixed;top:0;left:0;right:0;height:56px;z-index:50;background:#333'; document.body.appendChild(h); })()";
const NO_STICKY = "(function(){ var real = CSS.supports; CSS.supports = function (p, v) { if (/sticky/.test(String(v) + String(p))) return false; return real.apply(CSS, arguments); };" +
    " var st = document.createElement('style'); st.textContent = '#profilesConfigurationPage .jpf-te-side { position: relative !important; }'; document.head.appendChild(st); })()";
const AT_LAST_FIELD = `(async function () {
  document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]').click();
  await new Promise(function (r) { setTimeout(r, 300); });
  [].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function (d) { d.open = true; });
  await new Promise(function (r) { setTimeout(r, 200); });
  document.querySelector('#teAdvancedFields > :last-child').scrollIntoView({ block: 'end' });
  await new Promise(function (r) { setTimeout(r, 250); });
  var side = document.querySelector('#themeEditor .jpf-te-side').getBoundingClientRect();
  var card = document.querySelector('#tePreview .jpf-theme-preview').getBoundingClientRect();
  var body = document.querySelector('#themeEditor .jpf-te-body').getBoundingClientRect();
  var tile = document.querySelector('#tePreview .jpf-theme-preview-tile').getBoundingClientRect();
  var hb = document.querySelector('.skinHeader').getBoundingClientRect().bottom;
  var ex = document.getElementById('teExpand').getBoundingClientRect();
  var mid = document.elementFromPoint(card.left + 8, card.top + card.height / 2);
  return { cardW: Math.round(card.width), bodyW: Math.round(body.width), tile: Math.round(tile.width),
    cardTop: Math.round(card.top), cardBottom: Math.round(card.bottom), sideTop: Math.round(side.top), sideBottom: Math.round(side.bottom), sideH: Math.round(side.height),
    hb: Math.round(hb), vh: window.innerHeight, expandIn: ex.top >= side.top - 1 && ex.bottom <= side.bottom + 1 && ex.height > 0,
    seen: !!(mid && mid.closest('#tePreview')) };
})()`;

async function previewSizeChecks(c, s) {
    console.log('\n  preview size and place');
    for (const variant of ['', ' without position: sticky']) {
        await openEditor(c, s, 'desktop', variant ? NO_STICKY : null);
        await c.evaluate(HEADER);
        const d = await c.evaluate(AT_LAST_FIELD);
        const tag = ' (desktop' + variant + ')';
        ok('the preview is a column of at least 42% of the editor' + tag, d.cardW >= 0.42 * d.bodyW, JSON.stringify(d));
        ok('and its tiles are drawn large enough to judge' + tag, d.tile >= 44, JSON.stringify(d));
        ok('at the last Advanced field it is on screen, below Jellyfin\'s header' + tag,
            d.cardTop >= d.hb - 1 && d.cardBottom <= d.vh && d.seen, JSON.stringify(d));

        await openEditor(c, s, 'phone', variant ? NO_STICKY : null);
        await c.evaluate(HEADER);
        const p = await c.evaluate(AT_LAST_FIELD);
        const ptag = ' (phone' + variant + ')';
        ok('on a phone it is a strip at the top at the last Advanced field' + ptag,
            p.sideTop >= p.hb - 1 && p.sideTop <= p.hb + 16 && p.sideH <= 0.15 * p.vh && p.seen, JSON.stringify(p));
        ok('with Expand in the strip' + ptag, p.expandIn, JSON.stringify(p));
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(250);
        ok('and Expand opens it full size from there' + ptag, await c.evaluate("!!document.getElementById('teExpanded')"));
        await press(c, 'Escape');
    }
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
        // Pressed with the page scrolled well past the editor's top, the way an administrator
        // reaches it in Advanced. It drew at the top of the page with the dashboard showing
        // under it (owner's screenshot, 1.9.0.4-beta). It is now one fixed layer over the
        // whole window, Jellyfin's header included, and the page stays where it was.
        await c.evaluate("document.getElementById('teCancel').scrollIntoView({block:'end'})");
        await wait(100);
        const before = await c.evaluate('window.pageYOffset');
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(300);
        const x = await c.evaluate("(function () { var g = " + X + "; if (!g) return null; var r = g.getBoundingClientRect(), w = window.innerWidth, h = window.innerHeight;" +
            "var pts = [[2, 2], [w - 3, 2], [2, h - 3], [w - 3, h - 3], [w / 2, h / 2], [w / 2, 20]];" +
            "var mine = pts.filter(function (p) { var e = document.elementFromPoint(p[0], p[1]); return !!(e && g.contains(e)); }).length;" +
            "return { l: Math.round(r.left), t: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height), vw: w, vh: h, mine: mine, of: pts.length }; })()");
        ok('Expand shows the expanded view' + tag, !!x, 'no #teExpanded');
        if (!x) continue;
        ok('it is the size of the window, wherever the page is scrolled' + tag,
            Math.abs(x.l) <= 1 && Math.abs(x.t) <= 1 && Math.abs(x.w - x.vw) <= 1 && Math.abs(x.h - x.vh) <= 1, JSON.stringify(x));
        ok('and nothing of the page shows at its corners, its middle or under the header' + tag, x.mine === x.of, JSON.stringify(x));
        await c.evaluate("document.querySelector('#teExpanded .jpf-te-xclose').click()");
        await wait(100);
        const after = await c.evaluate('window.pageYOffset');
        ok('closing it leaves the page scrolled where it was' + tag, before > 0 && Math.abs(after - before) <= 1, before + ' -> ' + after);
        ok('with focus on Expand' + tag, await c.evaluate("document.activeElement === document.getElementById('teExpand')"));
        await c.evaluate("document.getElementById('teExpand').scrollIntoView({block:'center'})");
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(300);
        const parts = await c.evaluate("({ cards: document.querySelectorAll('#teExpanded .profile-card').length, add: document.querySelectorAll('#teExpanded .action-add-profile').length," +
            "title: !!document.querySelector('#teExpanded .profiles-title'), buttons: document.querySelectorAll('#teExpanded .profiles-btn').length })");
        ok('it shows sample profiles, the add tile, the title and the buttons' + tag, parts.cards >= 4 && parts.add === 1 && parts.title && parts.buttons >= 1, JSON.stringify(parts));
        // Laid out as the real picker is: its rules in styles.css are keyed to the real
        // overlay's id, and its rows are balanced by script. Without either, a phone showed
        // one huge tile a row (rows 1/1/1/1) where the picker shows two, and the desktop page
        // left one tile alone on a second row (3/1).
        const rows = await c.evaluate("(function () { var t = {}; [].slice.call(document.querySelectorAll('#teExpanded .profiles-grid .profile-card')).forEach(function (e) { var k = Math.round(e.getBoundingClientRect().top); t[k] = (t[k] || 0) + 1; }); return Object.keys(t).sort(function (a, b) { return a - b; }).map(function (k) { return t[k]; }); })()");
        ok('its tiles sit at least two to a row, with no row of one' + tag, rows.length > 0 && rows.every(n => n >= 2), rows.join('/'));
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
        // Leaving the page with it open. Its document-level Tab trap and its style element
        // outlived the page, so Tab stopped working across the rest of jellyfin-web.
        // viewManager fires viewbeforehide on the page element when it is navigated away.
        await openEditor(c, s, device);
        await c.evaluate("document.getElementById('teExpand').click()");
        await wait(200);
        await c.evaluate("document.getElementById('profilesConfigurationPage').dispatchEvent(new CustomEvent('viewbeforehide', { bubbles: true, cancelable: true }))");
        await c.evaluate("(function(){ window.__tabBlocked = null; window.addEventListener('keydown', function (e) { if (e.keyCode === 9) window.__tabBlocked = e.defaultPrevented; }); document.body.focus(); })()");
        await press(c, 'Tab');
        const left = await c.evaluate("({ view: !!" + X + ", css: !!document.getElementById('jpfThemeExpandCss'), blocked: window.__tabBlocked })");
        ok('leaving the page closes the expanded view, frees Tab and removes its style' + tag,
            !left.view && !left.css && left.blocked === false, JSON.stringify(left));
        // The colour popup listens on the document in the same way.
        await c.evaluate("document.getElementById('teAccent').scrollIntoView({block:'center'})");
        await c.evaluate("document.getElementById('teAccent').click()");
        await wait(150);
        const opened = !!(await c.evaluate('!!' + POPUP));
        await c.evaluate("document.getElementById('profilesConfigurationPage').dispatchEvent(new CustomEvent('viewbeforehide', { bubbles: true, cancelable: true }))");
        ok('leaving the page closes the colour popup' + tag, opened && !(await c.evaluate("!!document.querySelector('#themeEditor .jpf-te-pop')")), 'opened: ' + opened);
    }
}
function rgbOf(h) { const m = /^#([0-9a-f]{6})/i.exec(h || ''); return m ? [0, 2, 4].map(i => parseInt(m[1].slice(i, i + 2), 16)) : null; }

main().catch(e => { console.error(e.stack || e); process.exit(1); });
