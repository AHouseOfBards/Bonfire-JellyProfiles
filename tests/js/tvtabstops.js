/*
 * R-1180: initTVCheckboxes gives every .jpf-library-check-label tabindex 0, and a label
 * that wraps a real checkbox then offers two stops for one control (the viewing days,
 * the hours switch, most toggles). Tab visited the label and then the box; the remote's
 * arrows did the same.
 *
 * Runs the real initTVCheckboxes and the real focus-trap lookup on fake nodes and asks
 * what a person tabbing through would land on: one stop per checkbox, and Enter on the
 * label still ticks it.
 *
 *   node tests/js/tvtabstops.js [path/to/profiles.js]
 */
'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const env = D.loadPlugin(L.readProfiles());
const PP = env.PP;

function node(tag, type) {
    const attrs = {};
    const listeners = {};
    return {
        tagName: tag, type: type, attrs: attrs, checked: false, disabled: false, offsetParent: {},
        children: [],
        setAttribute: (k, v) => { attrs[k] = String(v); },
        getAttribute: k => (k in attrs ? attrs[k] : null),
        hasAttribute: k => k in attrs,
        addEventListener: (n, fn) => { listeners[n] = fn; },
        dispatchEvent: () => true,
        fire: (n, e) => listeners[n] && listeners[n](e),
        querySelector: function (sel) { return /checkbox/.test(sel) ? this.children[0] || null : null; }
    };
}

// A label wrapping a checkbox, and a label with nothing to tick in it.
const box = node('input', 'checkbox');
const label = node('label');
label.children.push(box);
const bare = node('label');

const container = { querySelectorAll: () => [label, bare] };
PP.initTVCheckboxes(container);

ok('the label is the stop', label.getAttribute('tabindex') === '0');
ok('a label with no checkbox is still a stop', bare.getAttribute('tabindex') === '0');
ok('the checkbox inside is not a second Tab stop', box.getAttribute('tabindex') === '-1');

// The trap's own lookup: it lists every input, so it has to leave this one out too.
const root = {
    querySelectorAll: () => [label, box, bare]
};
const stops = PP._overlayFocusables(root);
ok('the focus trap lists the label once and not the box inside it',
    stops.indexOf(label) >= 0 && stops.indexOf(box) < 0);

// Enter on the label still ticks the box.
let evt = { key: 'Enter', preventDefault: () => {} };
label.fire('keydown', evt);
ok('Enter on the label ticks the checkbox', box.checked === true);

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
