// r2-24-03 / r2-24-02: showAlert and showConfirmDialog escape their title and message
// themselves, markup only on an explicit opt-in, and the alert's OK answers once.
// Drives the real dialogs against a recording element. Against the build before the fix
// the raw text lands in the page as markup.
//
//   node tests/js/dialogescape.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const env = D.loadPlugin(L.readProfiles());
const { PP, sandbox } = env;

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const EVIL = '<img src=x onerror=alert(1)>';

function capture(run) {
    const handlers = {};
    const inner = () => ({ addEventListener(ev, fn) { handlers[ev] = fn; }, focus() {}, select() {}, setAttribute() {}, getAttribute: () => null, style: {}, click() { if (handlers.click) handlers.click(); } });
    let written = '';
    const buttons = {};
    const el = { style: {}, classList: { add() {}, remove() {}, contains: () => false },
        set innerHTML(v) { written = v; }, get innerHTML() { return written; },
        addEventListener() {}, setAttribute() {}, getAttribute: () => null, appendChild() {}, remove() {}, focus() {},
        querySelector: sel => buttons[sel] || (buttons[sel] = inner()),
        querySelectorAll: () => [] };
    const was = sandbox.document.createElement;
    sandbox.document.createElement = () => el;
    try { run(); } finally { sandbox.document.createElement = was; }
    return { html: () => written, buttons };
}

(() => {
    const a = capture(() => PP.showAlert(EVIL, EVIL));
    ok('an alert writes its title and message as text', a.html().indexOf('<img') === -1 && a.html().indexOf('&lt;img') !== -1, a.html().slice(0, 200));

    const c = capture(() => PP.showConfirmDialog(EVIL, EVIL, () => {}));
    ok('a confirm writes its title and message as text', c.html().indexOf('<img') === -1 && c.html().indexOf('&lt;img') !== -1, c.html().slice(0, 200));

    const h = capture(() => PP.showConfirmDialog('T', 'a<br><strong>b</strong>', () => {}, null, true));
    ok('a caller that passes markup says so, and gets it', h.html().indexOf('a<br><strong>b</strong>') !== -1);

    const h2 = capture(() => PP.showAlert('T', 'a<b>b</b>', null, true));
    ok('an alert has the same opt-in', h2.html().indexOf('a<b>b</b>') !== -1);

    let closed = 0;
    const o = capture(() => PP.showAlert('T', 'M', () => { closed++; }));
    const btn = o.buttons['#dialog-close-btn'];
    btn.click(); btn.click();
    ok('OK runs onClose once however often it is pressed', closed === 1, 'ran ' + closed);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
