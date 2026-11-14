/*
 * Dropdowns, button heights and the Advanced theme editor's size, in a real browser.
 *
 *   1. Every <select> on the admin page, static and script-built (households forms, theme
 *      editor), gets the arrow jellyfin-web draws. The page lacks jellyfin-web, so the
 *      upgrade is modelled step by step from src/elements/emby-select/emby-select.js
 *      (release-10.11.z): `is="emby-select"` adds emby-select-withcolor, and attachedCallback
 *      appends .selectArrowContainer to the select's parent only when the element is upgraded
 *      at all (needs is=) and does not already carry the class emby-select (it returns early
 *      if it does).
 *   2. The arrow points up while the list is open and down when closed: opened by a
 *      primary-button mousedown, Alt+Down, F4, Space or Enter; closed by change, blur, Escape,
 *      Tab and a second mousedown.
 *   3. A button on the same row as a select or text box is exactly as tall as it, checked per
 *      pair, never as a total. Every script-built form is forced open first.
 *   4. The Advanced theme editor fills the window at 2560x1300: preview and field panel
 *      together take the width, height follows the window, the panel scrolls inside its own
 *      column and the page does not need to. At phone width the editor stays inside the window.
 *
 * Red against the page before the fix:
 *
 *     node tests/js/admindropdowns.js '' /path/to/old/profilesDashboard.html
 *
 * (argv[2] is the client script, which this harness does not read; argv[3] is the page.)
 */
'use strict';

const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ROOT = path.join(__dirname, '..', '..');
const ICONS = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// Runs in the page. Models what emby-select does when it upgrades a select, and opens every form.
const PREPARE = `(function () {
  var page = document.getElementById('profilesConfigurationPage');
  // The sizes jellyfin-web's own stylesheets give an upgraded control (emby-select.scss,
  // emby-input.scss, release-10.11.z), which the test page does not load: without them a
  // select and a button come out the same height here and the row looks right.
  if (!document.getElementById('jpfModelStyle')) {
    var st = document.createElement('style');
    st.id = 'jpfModelStyle';
    st.textContent = '.content-primary { max-width: none !important; }' +
      '.selectContainer { position: relative; margin-bottom: 1.8em; }' +
      'select.emby-select { display: block; margin: 0; font-size: 110%; box-sizing: border-box; width: 100%; padding: 0.5em 1.9em 0.5em 0.5em; border: 0.07em solid #292929; }' +
      'input.emby-input { display: block; margin: 0; font-size: 110%; box-sizing: border-box; width: 100%; padding: 0.4em 0.25em; border: 0.16em solid #292929; }' +
      '.selectArrowContainer { position: absolute; top: 0.2em; right: 0.3em; pointer-events: none; }' +
      '.selectArrow { margin-top: 1.2em; font-size: 1.7em; }';
    document.head.appendChild(st);
  }
  [].forEach.call(page.querySelectorAll('input[is="emby-input"]'), function (i) { i.classList.add('emby-input'); });
  [].forEach.call(page.querySelectorAll('.jpf-extras-form, .jpf-account-extras, .jpf-hidden'), function (f) {
    if (f.id === 'themeEditor') return;
    if (/jpf-extras-form|jpf-account-extras/.test(f.className)) f.classList.remove('jpf-hidden');
  });
  [].forEach.call(page.querySelectorAll('details'), function (d) { d.open = true; });
  [].forEach.call(page.querySelectorAll('select[is="emby-select"]'), function (s) {
    if (s.__up) return;
    s.__up = true;
    s.classList.add('emby-select-withcolor');
    if (s.classList.contains('emby-select')) return;
    s.classList.add('emby-select');
    var label = document.createElement('label');
    label.className = 'selectLabel';
    s.parentNode.insertBefore(label, s);
    if (s.classList.contains('emby-select-withcolor'))
      s.parentNode.insertAdjacentHTML('beforeend', '<div class="selectArrowContainer"><div style="visibility:hidden;display:none;">0</div><span class="selectArrow material-icons keyboard_arrow_down" aria-hidden="true"></span></div>');
  });
  return true;
})()`;

const SELECTS = `(function () {
  function name(s) { return (s.id || s.className || s.tagName).toString().slice(0, 40); }
  var out = [];
  [].forEach.call(document.querySelectorAll('#profilesConfigurationPage select'), function (s) {
    var arrow = s.parentNode && s.parentNode.querySelector(':scope > .selectArrowContainer .selectArrow');
    out.push({ name: name(s), upgraded: s.getAttribute('is') === 'emby-select', inBox: !!(s.parentNode && s.parentNode.classList.contains('selectContainer')), arrow: !!arrow });
  });
  return out;
})()`;

function flipScript(index) {
    return `new Promise(function (done) {
  var s = document.querySelectorAll('#profilesConfigurationPage select')[${index}];
  var arrow = s.parentNode.querySelector(':scope > .selectArrowContainer .selectArrow');
  if (!arrow) return done({ noArrow: true });
  // A select in a hidden tab or form has no box, and a box-less element reports no transform:
  // show its ancestors while it is judged.
  var shown = [];
  var tmp = document.createElement('style');
  tmp.textContent = '[data-jpf-show] { display: block !important; }';
  document.head.appendChild(tmp);
  for (var a = s.parentNode; a && a !== document.body; a = a.parentNode) { if (a.offsetParent === null && getComputedStyle(a).display === 'none') { a.setAttribute('data-jpf-show', ''); shown.push(a); } }
  var finish = done;
  done = function (v) { shown.forEach(function (n) { n.removeAttribute('data-jpf-show'); }); tmp.remove(); finish(v); };
  function up() { var t = getComputedStyle(arrow).transform; return t !== 'none' && /^matrix\\(-1/.test(t); }
  function fire(type, init) {
    var e = type === 'mousedown' ? new MouseEvent(type, Object.assign({ bubbles: true, cancelable: true, button: 0 }, init || {}))
      : type === 'keydown' ? new KeyboardEvent(type, Object.assign({ bubbles: true, cancelable: true }, init || {}))
      : new Event(type, { bubbles: type !== 'blur' });
    s.dispatchEvent(e);
  }
  var steps = [
    ['closed to start', function () {}, false],
    ['mousedown opens', function () { fire('mousedown'); }, true],
    ['change closes', function () { fire('change'); }, false],
    ['mousedown opens again', function () { fire('mousedown'); }, true],
    ['blur closes', function () { fire('blur'); s.dispatchEvent(new Event('focusout', { bubbles: true })); }, false],
    ['Alt+Down opens', function () { fire('keydown', { key: 'ArrowDown', keyCode: 40, altKey: true }); }, true],
    ['Escape closes', function () { fire('keydown', { key: 'Escape', keyCode: 27 }); }, false],
    ['Space opens', function () { fire('keydown', { key: ' ', keyCode: 32 }); }, true],
    ['a mousedown elsewhere closes', function () { document.querySelector('#profilesConfigurationPage .jpf-brand').dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0 })); }, false]
  ];
  var res = [], i = 0;
  (function next() {
    if (i >= steps.length) return done({ res: res });
    var st = steps[i++];
    st[1]();
    setTimeout(function () { res.push({ step: st[0], want: st[2], got: up() }); next(); }, 260);
  })();
})`;
}

// Pairs: a button and the select or text box beside it on one row.
const PAIRS = `(function () {
  var page = document.getElementById('profilesConfigurationPage');
  function vis(el) { var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; }
  function nm(el) { return el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + '.' + String(el.className).split(/\\s+/).filter(function (c) { return c && c !== 'raised' && c !== 'emby-button'; }).slice(0, 2).join('.'); }
  var CTL = 'select, input[type=text], input[type=number], input[type=email], input[type=password], input[type=url], input[type=search], input:not([type])';
  var out = [];
  [].forEach.call(page.querySelectorAll('button'), function (b) {
    if (!vis(b)) return;
    var br = b.getBoundingClientRect();
    for (var p = b.parentNode, d = 0; p && p !== page && d < 3; p = p.parentNode, d++) {
      var found = [].filter.call(p.querySelectorAll(CTL), function (c) {
        if (c.closest('button') || !vis(c)) return false;
        var cr = c.getBoundingClientRect();
        var apart = br.left >= cr.right - 1 ? br.left - cr.right : cr.left >= br.right - 1 ? cr.left - br.right : -1;
        return apart >= 0 && apart < 48 && br.top < cr.bottom && br.bottom > cr.top;
      });
      if (found.length) {
        found.forEach(function (c) {
          out.push({ button: nm(b), control: nm(c), bh: Math.round(br.height * 10) / 10, ch: Math.round(c.getBoundingClientRect().height * 10) / 10 });
        });
        break;
      }
    }
  });
  return out;
})()`;

// The page equalises button heights on the next frame after a change; wait for it.
const PAIRS_WAITED = `new Promise(function (r) { setTimeout(function () { r(${PAIRS}); }, 400); })`;

const EDITOR = `(function () {
  function r(el) { var b = el.getBoundingClientRect(); return { top: b.top + pageYOffset, left: b.left, width: b.width, height: b.height, bottom: b.bottom + pageYOffset }; }
  var q = function (s) { return document.querySelector(s); };
  var ed = q('#themeEditor'), body = q('#themeEditor .jpf-te-body'), side = q('#themeEditor .jpf-te-side'),
      fields = q('#themeEditor .jpf-te-fields'), outline = q('#teOutline');
  if (!ed || ed.classList.contains('jpf-hidden')) return { error: 'the editor is not open' };
  return { vw: innerWidth, vh: innerHeight, docW: document.documentElement.scrollWidth, ed: r(ed), body: r(body), side: r(side), fields: r(fields),
    outline: r(outline), three: ed.classList.contains('jpf-te-3col'),
    fieldsScroll: fields.scrollHeight, fieldsClient: fields.clientHeight, fieldsOverflowY: getComputedStyle(fields).overflowY, docH: document.documentElement.scrollHeight };
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[3];
    const server = await fixture.start(0, { materialIcons: ICONS, dashboard: given || undefined });
    const c = await chrome.launch();
    const url = (tab, device) => 'http://127.0.0.1:' + server.port + '/dashboard?tab=' + tab + '&device=' + device;
    const TABS = ['status', 'households', 'devices', 'security', 'look', 'setup', 'clients', 'messages', 'activity', 'troubleshoot'];
    try {
        // ── 1 and 2: every select ─────────────────────────────────────────
        const seenSelects = new Set();
        let flips = 0;
        // Every tab's markup is in the page at once, so one load sees every static select.
        for (const tab of ['households']) {
            const r = await c.open(url(tab, 'desktop'), 'desktop');
            if (r.error) { ok(tab + ': the tab draws', false, r.error); continue; }
            await c.evaluate(PREPARE);
            const list = await c.evaluate(SELECTS);
            for (let i = 0; i < list.length; i++) {
                const s = list[i];
                const key = tab + ' ' + s.name;
                seenSelects.add(key);
                ok(key + ': upgraded by emby-select and inside a selectContainer', s.upgraded && s.inBox, 'is=' + s.upgraded + ', in a selectContainer=' + s.inBox);
                ok(key + ': draws an arrow', s.arrow);
                if (!s.arrow) continue;
                const f = await c.evaluate(flipScript(i));
                for (const st of f.res) {
                    flips++;
                    ok(key + ': arrow ' + (st.want ? 'up' : 'down') + ' after "' + st.step + '"', st.got === st.want);
                }
            }
        }
        ok('the harness reached the households forms and the dashboard selects', [...seenSelects].some(k => /link-user/.test(k)) && [...seenSelects].some(k => /defaultTheme/.test(k)), [...seenSelects].length + ' selects');

        // ── 3: a button beside a control ─────────────────────────────────
        let pairs = 0;
        for (const tab of TABS) {
            await c.open(url(tab, 'desktop'), 'desktop');
            await c.evaluate(PREPARE);
            const list = await c.evaluate(PAIRS_WAITED);
            list.forEach(p => { pairs++; ok(tab + ': ' + p.button + ' is as tall as ' + p.control, Math.abs(p.bh - p.ch) <= 0.5, 'button ' + p.bh + 'px, control ' + p.ch + 'px'); });
        }
        ok('the harness found rows with a button beside a control, the households one among them', pairs >= 3, pairs + ' pairs');

        // ── 4: the Advanced editor ───────────────────────────────────────
        const openEditor = async device => {
            await c.open(url('look', device), device);
            await c.evaluate(`(function(){ var b = document.querySelector('#profilesConfigurationPage button.jpf-te-act[data-act=edit][data-id=shop]'); b.click(); return true; })()`);
            await c.evaluate(`new Promise(function(r){ setTimeout(r, 400); })`);
            await c.evaluate(`(function(){ var b = document.querySelector('#themeEditor .jpf-te-mode[data-view=advanced]'); if (b) b.click(); return true; })()`);
            await c.evaluate(`new Promise(function(r){ setTimeout(r, 500); })`);
            return c.evaluate(PREPARE).then(() => c.evaluate(`(function(){ document.querySelector('#themeEditor').scrollIntoView(); return true; })()`)).then(() => c.evaluate(EDITOR));
        };
        // The Advanced selects are built when the editor opens: check them too.
        {
            const m = await openEditor('desktop');
            if (m.error) ok('the Advanced editor opens', false, m.error);
            else {
                const list = await c.evaluate(SELECTS);
                const adv = list.filter(s => /^teField|^te[A-Z]/.test(s.name) || /jpf-te-input/.test(s.name));
                ok('the Advanced editor builds dropdowns', adv.length >= 2, adv.length + ' found');
                adv.forEach(s => ok('editor ' + s.name + ': upgraded, in a selectContainer, draws an arrow', s.upgraded && s.inBox && s.arrow, JSON.stringify(s)));
                const pl = await c.evaluate(PAIRS_WAITED);
                pl.forEach(p => ok('editor: ' + p.button + ' is as tall as ' + p.control, Math.abs(p.bh - p.ch) <= 0.5, 'button ' + p.bh + 'px, control ' + p.ch + 'px'));
            }
        }
        {
            const d = chrome.DEVICES;
            d.wide = { width: 2560, height: 1300, mobile: false, scale: 1 };
            const m = await openEditor('wide');
            if (m.error) ok('2560: the editor opens', false, m.error);
            else {
                console.log('  measured 2560x1300: ' + JSON.stringify({ ed: m.ed, side: m.side, fields: m.fields, outline: m.outline, fieldsScroll: m.fieldsScroll, fieldsClient: m.fieldsClient, docH: m.docH }));
                ok('2560: three columns', m.three);
                ok('2560: the editor takes most of the window width', m.ed.width >= m.vw * 0.8, m.ed.width + ' of ' + m.vw);
                const used = m.side.width + m.fields.width + m.outline.width;
                ok('2560: outline, preview and fields together fill the editor width', used >= m.ed.width * 0.85, Math.round(used) + ' of ' + Math.round(m.ed.width));
                ok('2560: the field panel is not a narrow column', m.fields.width >= m.ed.width * 0.22, Math.round(m.fields.width) + 'px of ' + Math.round(m.ed.width));
                ok('2560: the preview is more than half the editor tall in the window', m.side.height >= m.vh * 0.6, Math.round(m.side.height) + 'px in ' + m.vh);
                ok('2560: the editor body ends inside the window', m.body.bottom - 0 <= m.ed.top + m.vh + 1 && m.body.height <= m.vh, 'body ' + Math.round(m.body.height) + 'px, window ' + m.vh);
                ok('2560: the preview and the panel are as tall as each other', Math.abs(m.side.height - m.fields.height) <= 1, Math.round(m.side.height) + ' vs ' + Math.round(m.fields.height));
                ok('2560: the panel is never taller than the window and scrolls inside itself', /auto|scroll/.test(m.fieldsOverflowY) && m.fields.height <= m.vh, m.fieldsOverflowY + ', ' + Math.round(m.fields.height) + 'px in ' + m.vh);
            }
        }
        {
            // A short window: the group is longer than the column, so the column must scroll and the page must not grow with it.
            chrome.DEVICES.short = { width: 1440, height: 600, mobile: false, scale: 1 };
            const m = await openEditor('short');
            if (m.error) ok('1440x600: the editor opens', false, m.error);
            else {
                console.log('  measured 1440x600: ' + JSON.stringify({ body: m.body, fields: m.fields, fieldsScroll: m.fieldsScroll, fieldsClient: m.fieldsClient }));
                ok('1440x600: the panel scrolls inside its own column', m.fieldsScroll > m.fieldsClient + 1, m.fieldsScroll + ' in ' + m.fieldsClient);
                ok('1440x600: the body is no taller than the window', m.body.height <= m.vh, Math.round(m.body.height) + ' in ' + m.vh);
            }
        }
        {
            const m = await openEditorPhone();
            async function openEditorPhone() {
                await c.open(url('look', 'phone'), 'phone');
                await c.evaluate(`(function(){ document.querySelector('#profilesConfigurationPage button.jpf-te-act[data-act=edit][data-id=shop]').click(); return true; })()`);
                await c.evaluate(`new Promise(function(r){ setTimeout(r, 400); })`);
                return c.evaluate(EDITOR);
            }
            if (m.error) ok('phone: the editor opens', false, m.error);
            else {
                console.log('  measured phone: ' + JSON.stringify({ vw: m.vw, ed: m.ed, side: m.side, fields: m.fields, docW: m.docW }));
                ok('phone: no sideways scroll', m.docW <= m.vw, m.docW + ' in ' + m.vw);
                ok('phone: the editor is inside the window', m.ed.left >= 0 && m.ed.left + m.ed.width <= m.vw + 1);
                ok('phone: the body is its natural height, not a window-sized box', m.body.height > 0 && m.fieldsOverflowY === 'visible', m.fieldsOverflowY);
            }
        }
        ok('arrows were checked through ' + flips + ' open/close steps', flips > 50, String(flips));
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
