/*
 * The admin page on a phone: one column at 480px and under.
 *
 * Auto-fit / auto-fill grids collapse by themselves. A grid with fixed tracks (server and
 * port side by side, an announcement beside its buttons, a label beside its field) does not,
 * and at 360px the two halves are each too narrow to read. Each such rule has to be matched,
 * by selector, by an @media (max-width: 480px) rule that sets it to one column. Enumerated
 * pairwise: "the page has a phone rule" would pass with one of four missing.
 * Sideways scroll was measured separately in a browser (every tab at 360px: none).
 *
 *     node tests/js/adminphone.js /path/to/old/profilesDashboard.html
 */
'use strict';
const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const html = process.argv[2] ? fs.readFileSync(process.argv[2], 'utf8') : fs.readFileSync(L.dashboardPath(), 'utf8');
const styles = (html.match(/<style[^>]*>[\s\S]*?<\/style>/g) || []).join('\n')
    .replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));

// [{ selector, body, phone, wide }]. A rule inside @media (min-width: N) with N above 480px
// never reaches a phone, so it needs no phone rule: the Switches log's columns exist only
// from 900px, and below that the row is a stacked flex box.
const rules = [];
let i = 0, head = '', depth = 0, inPhone = false, phoneDepth = -1, inWide = false, wideDepth = -1;
while (i < styles.length) {
    const ch = styles[i];
    if (ch === '{') {
        const h = head.trim();
        if (h.charAt(0) === '@') {
            if (/max-width:\s*480px/.test(h)) { inPhone = true; phoneDepth = depth; }
            const minW = /min-width:\s*(\d+)px/.exec(h);
            if (minW && Number(minW[1]) > 480 && !inWide) { inWide = true; wideDepth = depth; }
            depth++; head = ''; i++; continue;
        }
        const end = styles.indexOf('}', i);
        rules.push({ selector: h.replace(/\s+/g, ' '), body: styles.slice(i + 1, end), phone: inPhone, wide: inWide });
        head = ''; i = end + 1; continue;
    }
    if (ch === '}') {
        depth--;
        if (inPhone && depth === phoneDepth) inPhone = false;
        if (inWide && depth === wideDepth) inWide = false;
        head = ''; i++; continue;
    }
    head += ch; i++;
}

const tracks = v => v.trim().split(/\s+(?![^(]*\))/).filter(Boolean);
const fixed = rules.filter(r => !r.phone && !r.wide).map(r => {
    const m = /grid-template-columns:\s*([^;]+)/.exec(r.body);
    return m && !/auto-fit|auto-fill/.test(m[1]) && tracks(m[1]).length > 1 ? { sel: r.selector, cols: m[1].trim() } : null;
}).filter(Boolean);
ok('the page has fixed-track grids to police (' + fixed.length + ')', fixed.length >= 3);

const missing = fixed.filter(f => !rules.some(r => r.phone && r.selector === f.sel
    && /grid-template-columns:\s*(1fr|minmax\(0,\s*1fr\))\s*(;|$)/.test(r.body.trim())));
ok('each is one column at 480px and under (' + (fixed.length - missing.length) + ' of ' + fixed.length + ')',
    missing.length === 0, missing.map(m => m.sel + ' { ' + m.cols + ' }').join('\n          '));

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
