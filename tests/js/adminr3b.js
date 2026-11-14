/*
 * The theme editor's second-review fixes (r2-37-01 .. r2-37-04).
 *
 *   01  The shade square's mousemove/mouseup listeners sat on document until a mouseup,
 *       so closing the popup mid-drag (Escape, a click outside) left them behind.
 *   02  Exports revoked their blob URL at 0 ms, before some browsers had read it.
 *   03  The expanded preview refitted on every scroll event.
 *   04  Field paths and option values went into innerHTML unescaped.
 *
 * Runs the real closeColorPopup, fitThemeExpanded and teControl. Red against the old
 * dashboard:
 *
 *     node tests/js/adminr3b.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);
const source = ['escapeHtml', 'teControl', 'fitThemeExpanded', 'closeColorPopup']
    .map(n => L.extractFunction(script, n)).join('\n');

console.log('\n-- r2-37-04: paths and option values are escaped --');
{
    const make = new Function('t', source + '; return { teControl: teControl };');
    const api = make(k => k);
    const page = {};
    const evil = 'a"><img src=x onerror=1>';
    const color = api.teControl(page, 'color', evil, null, false);
    ok('a colour control does not carry the raw path', color.indexOf(evil) === -1 && color.indexOf('"><img') === -1);
    const choice = api.teControl(page, 'choice', 'x.y', [evil, ['v"q', 'Label']], false);
    ok('a select does not carry a raw option value', choice.indexOf('"><img') === -1 && choice.indexOf('value="v"q"') === -1);
    ok('a plain path still reads back as itself', /data-path="x\.y"/.test(api.teControl(page, 'number', 'x.y', [0, 1, 1], false)));
}
ok('the field list escapes its paths, option values and group names',
    // (?<!\[) leaves out a selector such as [data-path="..."], which is not markup.
    !/(?<!\[)data-(path|paths|pick|list|group)="' \+ (path|p|group\[0\])\b/.test(script) && !/<option value="' \+ v \+ '">/.test(script),
    'an attribute is still built from a bare path');

console.log('\n-- r2-37-03: a scroll does not refit the expanded preview --');
{
    const make = new Function('window', source + '; return fitThemeExpanded;');
    let fits = 0;
    let rect = { width: 800, height: 600 };
    const gate = { getBoundingClientRect: () => rect };
    const win = { innerWidth: 1000, innerHeight: 700, BonfireThemePreview: { fit() { fits++; } } };
    const fit = make(win);
    const page = { _themeExpand: { wrap: { querySelector: () => gate } } };
    fit(page);
    ok('a content change fits', fits === 1, String(fits));
    fit(page, true); fit(page, true); fit(page, true);
    ok('three scroll bursts at the same size do not refit', fits === 1, String(fits));
    rect = { width: 400, height: 600 };
    fit(page, true);
    ok('a changed size does', fits === 2, String(fits));
    win.innerHeight = 500;
    fit(page, true);
    ok('so does a resized window', fits === 3, String(fits));
    fit(page);
    ok('and a plain call always fits', fits === 4, String(fits));
}

console.log('\n-- r2-37-02: exports are not revoked at 0 ms --');
{
    const zero = script.match(/revokeObjectURL\(url\);\s*\}\s*,\s*0\s*\)/g) || [];
    const all = script.match(/revokeObjectURL\(url\);\s*\}\s*,\s*(\d+)\s*\)/g) || [];
    ok('no export revokes its URL at 0 ms', zero.length === 0, zero.length + ' left');
    ok('there are exports to check', all.length >= 3, String(all.length));
    ok('every delay is at least a second', all.every(m => +/,\s*(\d+)\s*\)$/.exec(m)[1] >= 1000));
}

console.log('\n-- r2-37-01: closing the picker ends a drag in progress --');
{
    const make = new Function('document', 'window', 'themeHistoryClose', source + '; return closeColorPopup;');
    let ended = 0;
    const removed = [];
    const doc = { removeEventListener: (e) => removed.push(e), activeElement: null };
    const win = { removeEventListener: () => {} };
    const close = make(doc, win, () => {});
    const st = {
        btn: { focus() {} }, pop: { contains: () => false, parentNode: null },
        onOutside: () => {}, onKey: () => {}, onMove: () => {}, dragOff: () => { ended++; }
    };
    close({ _picker: st }, false);
    ok('the drag listeners are removed on close', ended === 1, String(ended));
}
ok('a mousedown on the square records how to end its drag',
    /st\.dragOff\s*=\s*up/.test(script) && /document\.addEventListener\('mousemove', move\)/.test(script));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
