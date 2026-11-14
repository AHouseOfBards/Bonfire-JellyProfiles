// The sticky Save bar of the add and edit forms, by explicit geometry.
//
// The bar sticks to the bottom of the scroll area, so it overlaps the end of the form and can be
// NEARER to a field than the last field is. Spatial focus then picks the bar and Down skips
// that field (the add form's theme dropdown was skipped this way). tvsweep-b.js proves the
// rule in Chrome, but whether the skip happens there depends on scroll position, so it
// could not be made to go red for Down. This runs the real _moveOverlayFocus (and the
// _overlayFocusables and _focusVisibly it calls) on controls whose rects are written out, so
// no scroll or timing is involved.
//
// Layout (rect left/top/width/height, viewport 1080 high):
//   one column:  f1 top 100, f2 top 200 (x 0..100), f3 top 300 at x 40..140 (a little
//                right of the column, as a narrower field sits), bar button top 250,
//                height 160 (centre 330, below f3's centre 320). From f2 (centre 220) the
//                bar's centre is 110 away, f3's is 100 away plus 40 across x3 = 220:
//                the bar is NEARER, which is the case the old code got wrong.
//   two columns: left f1, f2 at x 0, right r1, r2, r3 at x 300 (r3 lowest, top 300), and
//                the markup order ends with f2, so the last control in markup order is not
//                the lowest on screen.
//
//   node tests/js/stickybar.js [path/to/profiles.js]
//
// Red first: point it at profiles.js from before the commit "TV: the sticky Save bar comes
// after every field in the form" (git show <that commit>^:Web/profiles.js).

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(L.readProfiles());
const { PP, sandbox } = env;
const doc = sandbox.document;
let active = null;
Object.defineProperty(doc, 'activeElement', { get: () => active, configurable: true });
sandbox.window.innerHeight = 1080;

/** A control with a written-out rect, as the trap's selector and the real functions read one. */
function control(name, left, top, width, height) {
    const n = {
        name, tagName: 'BUTTON', nodeType: 1, disabled: false, offsetParent: {}, parentElement: null,
        type: 'button', getAttribute: () => null,
        getBoundingClientRect: () => ({ left, top, width, height, bottom: top + height, right: left + width }),
        scrollIntoView() {}, closest: () => null,
        focus() { active = n; }
    };
    return n;
}

/** A root holding `fields` (markup order) and the bar's button, and answering the bar's selector. */
function formOf(fields, barButton) {
    const nodes = fields.concat([barButton]);
    const bar = { contains: (n) => n === barButton };
    return {
        nodeType: 1,
        contains: (n) => nodes.indexOf(n) !== -1,
        querySelectorAll: () => nodes,
        querySelector: (sel) => (sel === '.jpf-profile-dialog-actions' ? bar : null)
    };
}

function press(root, from, dir) {
    active = from;
    PP._moveOverlayFocus(root, dir);
    return active && active.name;
}

// ── One column ──────────────────────────────────────────────────────────────────────────
{
    const f1 = control('f1', 0, 100, 100, 40), f2 = control('f2', 0, 200, 100, 40), f3 = control('f3', 40, 300, 100, 40);
    const save = control('save', 0, 250, 100, 160);
    const root = formOf([f1, f2, f3], save);
    ok('one column: Down from the row above the last field goes to the last field, not the bar',
        press(root, f2, 'down') === 'f3', 'went to ' + press(root, f2, 'down'));
    ok('one column: Down from the last field goes to the bar', press(root, f3, 'down') === 'save');
    ok('one column: Up from the bar goes to the last field', press(root, save, 'up') === 'f3');
    ok('one column: Down from the first field goes to the next field', press(root, f1, 'down') === 'f2');
    ok('one column: Up from the last field goes to the one above', press(root, f3, 'up') === 'f2');
}

// ── Two columns, the last control in markup order not the lowest ─────────────────────────
{
    const f1 = control('f1', 0, 100, 100, 40), r1 = control('r1', 300, 100, 100, 40),
        r2 = control('r2', 300, 200, 100, 40), r3 = control('r3', 300, 300, 100, 40),
        f2 = control('f2', 0, 200, 100, 40);
    const save = control('save', 0, 250, 100, 160);
    const root = formOf([f1, r1, r2, r3, f2], save);
    ok('two columns: Up from the bar goes to the lowest control, not the last in markup order',
        press(root, save, 'up') === 'r3', 'went to ' + press(root, save, 'up'));
    ok('two columns: Down from the lowest control goes to the bar', press(root, r3, 'down') === 'save');
    ok('two columns: Down from the left column, with a control lower on screen, does not go to the bar',
        press(root, f2, 'down') === 'r3', 'went to ' + press(root, f2, 'down'));
}

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
