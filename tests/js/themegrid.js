/*
 * Dashboard, Theme tab: the built-in and custom theme lists were one narrow column of rows,
 * which left most of a wide monitor empty (owner, 1.9.0.4 eye test). They are a grid of
 * cards now: several to a row on a wide screen, one on a phone, nothing clipped.
 *
 * Asked in a real browser, at the owner's monitor (2900px), a laptop (1366px) and a compact
 * phone (360px). The fixture caps .content-primary at 54em; jellyfin-web's own does not
 * (src/styles/dashboard.scss has padding only), so the cap is lifted here, or the wide
 * case would measure the fixture and not the page.
 *
 *     node tests/js/themegrid.js [path/to/profilesDashboard.html]
 *
 * The optional path points the fixture at an older copy of the page (red-first).
 */
'use strict';
const fs = require('fs');
const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ICONS = path.join(__dirname, '..', 'screens', '.cache', 'material-icons.woff2');
const SIZES = [['wide', 2900, 1200, false, 'desktop', 3], ['laptop', 1366, 768, false, 'desktop', 2], ['phone', 360, 740, true, 'phone', 1]];
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const MEASURE = "(function(){"
    + "var st=document.createElement('style');st.textContent='.content-primary{max-width:none!important}';document.head.appendChild(st);"
    + "function box(sel){var l=[].slice.call(document.querySelectorAll(sel));return l.map(function(e){var b=e.getBoundingClientRect();"
    + "return {l:b.left,r:b.right,t:b.top,w:b.width,over:e.scrollWidth-e.clientWidth};});}"
    + "var host=document.getElementById('themesBuiltIn');var hb=host.getBoundingClientRect();"
    + "return {built:box('#themesBuiltIn > *'),custom:box('#themesCustom > .jpf-te-item'),host:{l:hb.left,r:hb.right},"
    + "doc:document.documentElement.scrollWidth,win:window.innerWidth,"
    + "hide:[].map.call(document.querySelectorAll('#themesBuiltIn .jpf-ob-box'),function(e){var b=e.getBoundingClientRect();return {r:b.right};})};})()";

function perRow(list) {
    if (!list.length) return 0;
    return list.filter(c => Math.abs(c.t - list[0].t) < 2).length;
}

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    const fopts = fs.existsSync(ICONS) ? { materialIcons: ICONS } : {};
    if (process.argv[2]) fopts.dashboard = process.argv[2];
    const s = await fixture.start(0, fopts);
    const c = await chrome.launch();
    try {
        for (const z of SIZES) {
            const r = await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=' + z[4], z[4]);
            if (r.error) { ok(z[0] + ': the page draws', false, r.error); continue; }
            await c.page('Emulation.setDeviceMetricsOverride', { width: z[1], height: z[2], deviceScaleFactor: 1, mobile: z[3] });
            await new Promise(r2 => setTimeout(r2, 400));
            const m = await c.evaluate(MEASURE);
            ok(z[0] + ': the built-in themes are listed', m.built.length > 2, String(m.built.length));
            const n = perRow(m.built);
            ok(z[0] + ': ' + (z[5] === 1 ? 'one card' : 'at least ' + z[5] + ' cards') + ' per row (' + n + ')', z[5] === 1 ? n === 1 : n >= z[5]);
            ok(z[0] + ': no card sticks out of the list', m.built.every(b => b.l >= m.host.l - 1 && b.r <= m.host.r + 1), JSON.stringify(m.host));
            ok(z[0] + ': nothing inside a card is clipped', m.built.every(b => b.over <= 0), JSON.stringify(m.built.map(b => b.over)));
            ok(z[0] + ': every Hide box sits inside the list', m.hide.every(h => h.r <= m.host.r + 1));
            ok(z[0] + ': the page does not scroll sideways', m.doc <= m.win, m.doc + ' in ' + m.win);
            if (m.custom.length) {
                ok(z[0] + ': your own themes are cards too (' + perRow(m.custom) + ' per row)', z[5] === 1 ? perRow(m.custom) === 1 : perRow(m.custom) >= Math.min(z[5], m.custom.length));
                ok(z[0] + ': none of them is clipped', m.custom.every(b => b.over <= 0));
            }
        }
    } finally { await c.close(); await s.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
