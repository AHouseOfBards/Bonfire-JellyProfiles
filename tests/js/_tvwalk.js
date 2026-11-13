// Walk a drawn screen with a television remote, in real Chrome at 1920x1080 with layout-tv.
//
// Not a harness (no tests run here; the leading underscore is the convention for shared
// plumbing). tvinput.js walks a stub DOM with no layout; this presses real keys through
// CDP into the real page, so the real trap code runs on real geometry.
//
//   const tv = await require('./_tvwalk').start({ source });   // source: optional profiles.js text
//   const steps = await tv.walk(scene, ['ArrowDown', 'Enter', 'Back']);
//   const list  = await tv.controls(scene);
//   const reach = await tv.explore(scene);
//   await tv.close();
//
// `scene` is a scene name from tests/screens/scenes.js, or a query string such as
// 'picker&count=21'. device=tv is added. Keys: ArrowUp ArrowDown ArrowLeft ArrowRight
// Enter Escape Tab Back (webOS keyCode 461) Space.
//
// walk(scene, keys) -> [{ key, at, visible, onTop, ring, surface, gone }]
//   The first step is key null: where focus starts. `at` is a short selector (id, or
//   tag.class "text"); 'body' means focus is on nothing. visible: in the viewport, not
//   zero-sized, no visibility:hidden or display:none up the tree. onTop: the element is
//   what a viewer sees at its centre (not under another layer). ring: a focus outline or
//   box-shadow is drawn, on it or (cards) on a child that changes with focus. surface: the
//   screen the remote is confined to ('#profiles-gate-overlay' or a dialog's class), null
//   when none; gone: surface is null, i.e. the whole Bonfire stack left the page.
// controls(scene) -> [{ at, visible, onTop }]   every focusable control drawn.
// explore(scene) -> { start, controls, reached, unreached, edges }
//   Breadth-first: focus each reached control in turn (el.focus(), as a prior arrow press
//   would have) and press each arrow once, recording where focus goes. `unreached` is the
//   visible on-top controls no arrow chain from the start reached. `edges` lists
//   'from --Arrow--> to', and a trap shows as an arrow that stays or leaves the control set.

'use strict';
const path = require('path');
const fs = require('fs');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ROOT = path.resolve(__dirname, '..', '..');
const ICONS = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');

const KEYS = {
    ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', vk: 38 },
    ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', vk: 40 },
    ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft', vk: 37 },
    ArrowRight: { key: 'ArrowRight', code: 'ArrowRight', vk: 39 },
    Enter: { key: 'Enter', code: 'Enter', vk: 13, text: '\r' },
    Escape: { key: 'Escape', code: 'Escape', vk: 27 },
    Tab: { key: 'Tab', code: 'Tab', vk: 9 },
    Space: { key: ' ', code: 'Space', vk: 32, text: ' ' },
    Back: { key: 'GoBack', code: '', vk: 461 }
};

const CONTROLS = 'button, input:not([type=hidden]), select, textarea, a[href], summary, [tabindex]:not([tabindex^="-"]), [role=button]';

// Runs in the page. Helpers shared by every call.
const PAGE = `(function () {
  if (window.__tw) return;
  var n = 0;
  function describe(el) {
    if (!el || el === document.body || el === document.documentElement) return 'body';
    if (!el.__twid) el.__twid = ++n;
    if (el.id) return '#' + el.id;
    var c = typeof el.className === 'string' ? el.className.trim().split(/\\s+/)[0] : '';
    var t = (el.getAttribute('aria-label') || el.textContent || el.value || '').replace(/\\s+/g, ' ').trim().slice(0, 24);
    return el.tagName.toLowerCase() + (c ? '.' + c : '') + (t ? ' "' + t + '"' : '') + ' ~' + el.__twid;
  }
  function visible(el) {
    var r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return false;
    if (r.right <= 0 || r.bottom <= 0 || r.left >= innerWidth || r.top >= innerHeight) return false;
    for (var e = el; e && e.nodeType === 1; e = e.parentElement) {
      var s = getComputedStyle(e);
      if (s.display === 'none' || s.visibility === 'hidden') return false;
    }
    return true;
  }
  function onTop(el) {
    var r = el.getBoundingClientRect();
    var x = Math.min(Math.max(r.left + r.width / 2, 0), innerWidth - 1), y = Math.min(Math.max(r.top + r.height / 2, 0), innerHeight - 1);
    var top = document.elementFromPoint(x, y);
    return !!top && (top === el || el.contains(top) || top.contains(el));
  }
  function ring(el) {
    var s = getComputedStyle(el);
    var outline = s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0;
    if (outline || (s.boxShadow && s.boxShadow !== 'none')) return true;
    // Some controls (the profile cards) draw the ring on a child. Count that only when the
    // control and its children look different focused and blurred, so a standing shadow
    // does not pass for a ring. Transitions are off (see load), or the two would match.
    var before = sig(el);
    el.blur();
    var after = sig(el);
    el.focus();
    return before !== after;
  }
  function sig(el) {
    var all = [el].concat([].slice.call(el.querySelectorAll('*')).slice(0, 40));
    return all.map(function (e) {
      var s = getComputedStyle(e);
      return [s.outlineStyle, s.outlineWidth, s.boxShadow, s.borderColor, s.borderWidth, s.transform, s.filter, s.backgroundColor, s.opacity].join('/');
    }).join('|');
  }
  function state() {
    var el = document.activeElement;
    var body = !el || el === document.body || el === document.documentElement;
    return { at: describe(el), visible: body ? false : visible(el), onTop: body ? false : onTop(el), ring: body ? false : !!ring(el) };
  }
  function controls() {
    return [].slice.call(document.querySelectorAll(${JSON.stringify(CONTROLS)})).filter(function (e) { return !e.disabled; })
      .map(function (e) { return { at: describe(e), visible: visible(e), onTop: visible(e) && onTop(e) }; });
  }
  function find(at) {
    var all = document.querySelectorAll('*');
    for (var i = 0; i < all.length; i++) if (all[i].__twid && describe(all[i]) === at) return all[i];
    return at.charAt(0) === '#' ? document.getElementById(at.slice(1)) : null;
  }
  window.__tw = { describe: describe, state: state, controls: controls, find: find };
})()`;

async function start(opts) {
    const o = opts || {};
    if (!fs.existsSync(ICONS)) throw new Error('Material Icons font missing; run node tests/screens/shoot.js once, or see pinscreens.js');
    const server = await fixture.start(0, { materialIcons: ICONS, source: o.source });
    const c = await chrome.launch();

    async function load(scene) {
        const q = /[=&]/.test(scene) ? scene : 'scene=' + scene;
        const url = 'http://127.0.0.1:' + server.port + '/?' + (/(^|&)scene=/.test(q) ? q : 'scene=' + q) + '&device=tv';
        const r = await c.open(url, 'tv');
        if (r.error) throw new Error('scene failed: ' + r.error);
        // init() never runs in the fixture, and it is what binds the remote's key handler
        // (once, for the page's life). Bind it as init does, or no arrow does anything.
        await c.evaluate('window.__bonfire._bindOverlayFocusTrap()');
        await c.evaluate(PAGE);
        await c.evaluate("(function(){var s=document.createElement('style');s.textContent='*,*::before,*::after{transition:none!important;animation:none!important}';document.head.appendChild(s)})()");
    }

    async function press(name) {
        const k = KEYS[name];
        if (!k) throw new Error('unknown key ' + name);
        const base = { key: k.key, code: k.code, windowsVirtualKeyCode: k.vk, nativeVirtualKeyCode: k.vk };
        await c.page('Input.dispatchKeyEvent', Object.assign({ type: k.text ? 'keyDown' : 'rawKeyDown' }, base, k.text ? { text: k.text } : {}));
        await c.page('Input.dispatchKeyEvent', Object.assign({ type: 'keyUp' }, base));
        await c.evaluate('new Promise(function(r){setTimeout(r,450)})');
    }

    const stateNow = () => c.evaluate('window.__tw.state()');
    const gate = () => c.evaluate("!!document.getElementById('profiles-gate-overlay')");

    async function walk(scene, keys) {
        await load(scene);
        const steps = [];
        const s0 = await stateNow();
        const surface0 = await c.evaluate("(function(){var s=window.__bonfire._activeTrapSurface();return s?(s.id?'#'+s.id:'.'+String(s.className).split(' ')[0]):null})()");
        steps.push(Object.assign({ key: null }, s0, { surface: surface0, gone: surface0 === null }));
        for (const key of keys) {
            await press(key);
            const s = await stateNow();
            // The gate (the picker's overlay) is what the dialogs stack on; "gone" is the
            // whole stack: no overlay or dialog element left.
            const surface = await c.evaluate("(function(){var s=window.__bonfire._activeTrapSurface();return s?(s.id?'#'+s.id:'.'+String(s.className).split(' ')[0]):null})()");
            steps.push(Object.assign({ key }, s, { surface, gone: surface === null }));
        }
        return steps;
    }

    async function controls(scene) {
        await load(scene);
        return c.evaluate('window.__tw.controls()');
    }

    async function explore(scene) {
        await load(scene);
        const all = await c.evaluate('window.__tw.controls()');
        const set = {};
        all.forEach(x => { set[x.at] = x; });
        const start = (await stateNow()).at;
        const reached = {};
        const edges = [];
        const queue = [start];
        reached[start] = true;
        while (queue.length) {
            const from = queue.shift();
            for (const arrow of ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']) {
                const ok = await c.evaluate('(function(){var e=window.__tw.find(' + JSON.stringify(from) + ');if(!e)return false;e.focus();return document.activeElement===e})()');
                if (!ok) break;
                // Up and down on a dropdown change its value and leave focus where it is
                // (the trap hands them to it). Pressing them here changed the form under
                // the walk, which moved other controls and made reach depend on the order.
                if ((arrow === 'ArrowUp' || arrow === 'ArrowDown') && await c.evaluate('document.activeElement.tagName==="SELECT"')) {
                    edges.push(from + ' --' + arrow + '--> ' + from);
                    continue;
                }
                await press(arrow);
                const to = (await stateNow()).at;
                edges.push(from + ' --' + arrow + '--> ' + to);
                if (!reached[to]) { reached[to] = true; if (set[to]) queue.push(to); }
            }
        }
        const unreached = all.filter(x => x.visible && x.onTop && !reached[x.at]).map(x => x.at);
        return { start, controls: all, reached: Object.keys(reached), unreached, edges };
    }

    async function close() { await c.close(); await server.close(); }
    return { walk, controls, explore, close, evaluate: c.evaluate, load, press, state: stateNow };
}

module.exports = { start, KEYS };
