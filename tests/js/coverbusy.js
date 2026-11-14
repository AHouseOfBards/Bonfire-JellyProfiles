/*
 * Two things Bonfire's own stylesheet drew wrong, asked of a real browser.
 *
 * R-1273: the maintenance cover is a centred flex box with no overflow. A long message makes it
 * taller than the screen, and a centred flex item overflows at both ends, so the title and the
 * Sign out button were cut off and could not be scrolled to (a phone on its side, a 720p
 * television). Asked: with a long message, can the title be seen at the top and the last button
 * at the bottom of the scroll?
 *
 * R-1271: a busy button hides its label with color:transparent and drew its spinner's arc in
 * currentColor, so the arc was transparent too and only the faint ring showed. Asked, for each
 * kind of button: is the arc's colour opaque, and is it a colour other than the fill under it?
 *
 * Needs Chrome, like formfactors.js, and says it was skipped, exiting 0, without it.
 *
 *     node tests/js/coverbusy.js [path/to/styles.css]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const CSS = fs.readFileSync(process.argv[2] || path.join(L.ROOT, 'Web', 'styles.css'), 'utf8');

// Only what the rules under test read. The theme's real values are in profiles.js; these are
// distinct on purpose, so "the arc is the fill's colour" cannot pass by accident.
const TOKENS = ':root{--jpf-text-1:#eeeeee;--jpf-text-2:#cccccc;--jpf-warn:#ff9900;--jpf-gate-bg:#101010;'
    + '--jpf-accent:#ffaa00;--jpf-on-accent:#101010;--jpf-btn2-bg:#333333;--jpf-btn2-color:#f0f0f0;'
    + '--jpf-btn2-case:none;--jpf-btn2-spacing:0;--jpf-btn2-weight:600;--jpf-btn-radius:8px;'
    + '--jpf-danger:#ff6b6b;--jpf-danger-quiet:#ff8787;--jpf-border-3:rgba(255,255,255,.3);}';

const LONG = new Array(60).join('The server is being moved to new storage and will be back soon. ');

const PAGE = '<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width">'
    + '<style>html,body{margin:0;background:#101010;color:#eee;font-family:sans-serif}' + TOKENS + '</style>'
    + '<style>' + CSS + '</style><body>'
    + '<div id="profiles-maintenance" class="profiles-maintenance is-blocking" role="alertdialog">'
    + '<div class="profiles-maintenance-inner">'
    + '<div class="profiles-maintenance-title" id="t">Down for maintenance</div>'
    + '<div class="profiles-maintenance-text">' + LONG + '</div>'
    + '<button id="again" class="profiles-btn jpf-btn-primary">Check again</button>'
    + '<button id="out" class="profiles-btn jpf-btn-secondary">Sign out</button>'
    + '</div></div>'
    + '<div id="busy" style="padding:20px">'
    + ['jpf-btn-primary', 'jpf-btn-secondary', 'jpf-btn-danger', 'jpf-btn-danger-quiet'].map(k =>
        '<button class="profiles-btn ' + k + ' is-busy" data-kind="' + k + '">Working</button>').join(' ')
    + '</div><script>window.__ready=true</script></body>';

const MEASURE = `(function () {
  var box = document.getElementById('profiles-maintenance');
  var t = document.getElementById('t').getBoundingClientRect();
  var top = t.top;
  box.scrollTop = box.scrollHeight;
  var out = document.getElementById('out').getBoundingClientRect();
  return { top: top, outBottom: out.bottom, vh: window.innerHeight,
           scrolls: box.scrollHeight > box.clientHeight, canScroll: box.scrollTop > 0 };
})()`;

const ARCS = `(function () {
  var rgb = function (s) { var m = /rgba?\\(([^)]*)\\)/.exec(s); var p = m[1].split(',').map(Number); return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 }; };
  return [].slice.call(document.querySelectorAll('#busy .is-busy')).map(function (b) {
    var cs = getComputedStyle(b, '::after');
    var arc = rgb(cs.borderTopColor), fill = rgb(getComputedStyle(b).backgroundColor);
    return { kind: b.getAttribute('data-kind'), arc: cs.borderTopColor, a: arc.a, fill: getComputedStyle(b).backgroundColor,
             same: arc.r === fill.r && arc.g === fill.g && arc.b === fill.b && fill.a > 0.5 };
  });
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const c = await chrome.launch();
    try {
        const url = 'data:text/html;charset=utf-8,' + encodeURIComponent(PAGE);
        console.log('\n-- The maintenance cover with a long message -----------------');
        for (const dev of ['phone-land', 'tv720']) {
            await c.open(url, dev);
            const m = await c.evaluate(MEASURE);
            ok(dev + ': the message is taller than the screen, so this is the case under test', m.scrolls,
               'scrollHeight did not exceed clientHeight');
            ok(dev + ': the title is on screen at the top (' + Math.round(m.top) + 'px)', m.top >= 0);
            ok(dev + ': the cover scrolls', m.canScroll);
            ok(dev + ': Sign out can be scrolled into view (bottom ' + Math.round(m.outBottom) + ' of ' + m.vh + ')',
               m.outBottom <= m.vh + 1);
        }
        console.log('\n-- A busy button shows a spinner --------------------------------');
        await c.open(url, 'desktop');
        const arcs = await c.evaluate(ARCS);
        ok('all four kinds of button were measured', arcs.length === 4, arcs.length + ' found');
        arcs.forEach(a => {
            ok(a.kind + ': the arc is opaque (' + a.arc + ')', a.a > 0.5);
            ok(a.kind + ': the arc is not the colour of the fill under it', !a.same, 'fill ' + a.fill);
        });
    } finally {
        await c.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
