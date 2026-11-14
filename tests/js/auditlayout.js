/*
 * Dashboard, Switches tab: each entry was a column with its text floating in the middle of a
 * full-width row, and the list scrolled sideways (owner, 1.9.0.4 eye test, 2900px). Two causes:
 * the row was width: 100% plus 15px of padding on each side with content-box sizing, so it was
 * 30px wider than its list; and Jellyfin's .listItem centers what is inside it.
 *
 * Wide enough, the entries are aligned columns (time, who switched to whom, device, address);
 * on a phone they stack. Asked in a real browser at 2900, 1366 and 360px. The fixture does not
 * carry jellyfin-web's .listItem, so its centring is added here: the page's own rule has to
 * beat it, or this would pass for the wrong reason.
 *
 *     node tests/js/auditlayout.js [path/to/profilesDashboard.html]
 *
 * The optional path points the fixture at an older copy of the page (red-first).
 */
'use strict';
const fs = require('fs');
const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ICONS = path.join(__dirname, '..', 'screens', '.cache', 'material-icons.woff2');
const SIZES = [['wide', 2900, 1200, false, 'desktop'], ['laptop', 1366, 768, false, 'desktop'], ['phone', 360, 740, true, 'phone']];
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const MEASURE = "(function(){"
    + "var st=document.createElement('style');st.textContent='.content-primary{max-width:none!important}"
    + " .listItem{display:flex;align-items:center;justify-content:center;text-align:center}';document.head.appendChild(st);"
    + "var list=document.getElementById('auditLogsList');var lb=list.getBoundingClientRect();"
    + "function r(e){var b=e.getBoundingClientRect();return {l:b.left,r:b.right,t:b.top,b:b.bottom,w:b.width};}"
    + "var rows=[].map.call(list.querySelectorAll('.jpf-audit-row'),function(row){"
    + "var cells=['.jpf-audit-meta > span:first-child','.jpf-audit-what','.jpf-audit-where','.jpf-audit-meta > span:last-child'].map(function(q){return row.querySelector(q);});"
    + "return {row:r(row),cells:cells.map(r)};});"
    + "function vis(id){var e=document.getElementById(id);if(!e)return null;var b=e.getBoundingClientRect();return {w:b.width,r:b.right};}"
    + "return {list:{l:lb.left,r:lb.right,sw:list.scrollWidth,cw:list.clientWidth},rows:rows,win:window.innerWidth,"
    + "controls:['auditFilter','auditDownload','auditDelete'].map(vis)};})()";

function overlap(a, b) { return a.l < b.r - 1 && b.l < a.r - 1 && a.t < b.b - 1 && b.t < a.b - 1; }

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
            const r = await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=activity&device=' + z[4], z[4]);
            if (r.error) { ok(z[0] + ': the page draws', false, r.error); continue; }
            await c.page('Emulation.setDeviceMetricsOverride', { width: z[1], height: z[2], deviceScaleFactor: 1, mobile: z[3] });
            await new Promise(r2 => setTimeout(r2, 400));
            const m = await c.evaluate(MEASURE);
            ok(z[0] + ': the log has entries', m.rows.length >= 2, String(m.rows.length));
            ok(z[0] + ': the list does not scroll sideways', m.list.sw <= m.list.cw, m.list.sw + ' in ' + m.list.cw);
            ok(z[0] + ': no entry sticks out of the list', m.rows.every(x => x.row.l >= m.list.l - 1 && x.row.r <= m.list.r + 1));
            ok(z[0] + ': no cell sticks out of its entry', m.rows.every(x => x.cells.every(k => k.l >= x.row.l - 1 && k.r <= x.row.r + 1)));
            ok(z[0] + ': nothing in an entry overlaps', m.rows.every(x => x.cells.every((a, i) => x.cells.every((b, j) => i >= j || !overlap(a, b)))));
            const left = m.rows.length ? m.rows[0].cells[0].l - m.rows[0].row.l : 99;
            ok(z[0] + ': the text starts at the entry\'s left padding, not the middle (' + Math.round(left) + 'px)', left >= 0 && left <= 16);
            if (z[0] === 'phone') {
                ok(z[0] + ': the parts are stacked, one under the other', m.rows.every(x => x.cells[2].t > x.cells[1].t && x.cells[1].t > x.cells[0].t - 1));
                ok(z[0] + ': each line starts at the same left edge', m.rows.every(x => Math.abs(x.cells[1].l - x.cells[0].l) < 2 && Math.abs(x.cells[2].l - x.cells[0].l) < 2));
            } else {
                ok(z[0] + ': time, who, device and address share one line', m.rows.every(x => x.cells.every(k => Math.abs(k.t - x.cells[0].t) < 12)));
                ok(z[0] + ': the columns run left to right in that order', m.rows.every(x => x.cells[0].l < x.cells[1].l && x.cells[1].l < x.cells[2].l && x.cells[2].l < x.cells[3].l));
                ok(z[0] + ': the columns line up from one entry to the next',
                    m.rows.every(x => x.cells.every((k, i) => Math.abs(k.l - m.rows[0].cells[i].l) < 2)));
                ok(z[0] + ': the columns use the width (the address column starts past 60% of it)',
                    m.rows.every(x => x.cells[3].l - x.row.l > 0.6 * x.row.w));
            }
            ok(z[0] + ': the filter, Download and Delete are on screen', m.controls.every(k => k && k.w > 0 && k.r <= m.win + 1), JSON.stringify(m.controls));
        }
    } finally { await c.close(); await s.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
