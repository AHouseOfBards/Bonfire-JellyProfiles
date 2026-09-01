// Guards the merged library list in the edit form.
//
// The risk here is not that it looks wrong — it is that the save path and the
// artwork editor query the two halves by class (.library-checkbox and
// .libart-row). Merging them into one row is only safe while both classes stay
// exactly where those queries expect them, and nothing in the build would fail
// if they stopped: the form would simply save no libraries.

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

const SRC = fs.readFileSync(L.profilesPath(), 'utf8');

let pass = 0, fail = 0;
function check(name, actual, expected) {
    const ok = Object.is(actual, expected);
    if (ok) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + '\n          expected: ' + expected + '\n          actual:   ' + actual); }
}

console.log('── There is one list, not two ────────────────────────────────');
check('the artwork list still exists', SRC.includes('id="edit-library-artwork"'), true);
// The CREATE form keeps its own checklist: a profile that does not exist yet has
// no id, so there is no artwork to merge into. Exactly one must remain.
check('only the create form still has a separate checklist',
    (SRC.match(/<div class="library-checklist">/g) || []).length, 1);
check('and its heading with it', /<label>Library Artwork<\/label>/.test(SRC), false);
// Translated since 1.5.3, so the literal is gone from the markup — but the wording it
// resolves to is the thing that was decided, and still has to hold. The create form keeps
// "Enabled Libraries"; the edit section, which already sits under a Libraries heading,
// must not.
check('the edit section label is just "Libraries"',
    /<label style="margin: 0;">\$\{t\('profileForm\.libraries'\)\}<\/label>/.test(SRC)
    && /'profileForm\.libraries':\s*'Libraries'/.test(SRC), true);
check('and the create form still says "Enabled Libraries"',
    /<label style="margin: 0;">\$\{t\('profileForm\.enabledLibraries'\)\}<\/label>/.test(SRC)
    && /'profileForm\.enabledLibraries':\s*'Enabled Libraries'/.test(SRC), true);

console.log();
console.log('── The row still answers both queries ────────────────────────');
// Pull one rendered row template out of the source and check its shape.
const rowStart = SRC.indexOf('<div class="libart-row" data-lib=');
check('a row template exists', rowStart > -1, true);
const chooseAt = SRC.indexOf('libart-choose', rowStart);
const row = SRC.slice(rowStart, SRC.indexOf('</div>', chooseAt) + 6);

check('row carries the checkbox the save path looks for',
    /class="library-checkbox"/.test(row), true);
check('row keeps data-lib for the artwork editor', /data-lib=/.test(row), true);
check('row keeps the mode select', /class="libart-mode"/.test(row), true);
check('row keeps the Choose button', /libart-choose/.test(row), true);
check('row keeps the thumbnail', /class="libart-thumb"/.test(row), true);
check('the checkbox and the select name the same library',
    /class="library-checkbox" value="\$\{lib\.id\}"/.test(row)
    && /data-lib="\$\{lib\.id\}"/.test(row), true);
// The <label> wrapper supplies the accessible name now, so an aria-label would
// make a screen reader announce the library twice.
check('the label names the checkbox, not a duplicate aria-label',
    /aria-label="Show /.test(row), false);

console.log();
console.log('── The row is still reachable from a remote ──────────────────');
// initTVCheckboxes binds ONLY to .library-check-label. A native checkbox does not
// toggle on Enter, and a TV remote's OK arrives as Enter, so losing this wrapper
// silently makes libraries unsettable on webOS and Tizen — with no error anywhere.
check('the tick sits inside a library-check-label',
    /<label class="library-check-label libart-check">[\s\S]{0,200}class="library-checkbox"/.test(row), true);
check('the name is inside that label too, so it is a click target',
    /<label class="library-check-label[\s\S]{0,400}libart-name[\s\S]{0,120}<\/label>/.test(row), true);
check('initTVCheckboxes still binds that class',
    SRC.includes("querySelectorAll('.library-check-label')"), true);
check('the edit form still calls initTVCheckboxes',
    (SRC.match(/this\.initTVCheckboxes\(content\)/g) || []).length >= 2, true);

console.log();
console.log('── Nothing scrolls inside the dialog any more ────────────────');
const CSS = L.extractCss(SRC);
const listRule = CSS.slice(CSS.indexOf('.libart-list {'));
const listBody = listRule.slice(0, listRule.indexOf('}'));
check('.libart-list has no max-height', /max-height/.test(listBody), false);
check('.libart-list has no overflow', /overflow/.test(listBody), false);
// .library-checklist is still used by the CREATE form, which has no artwork rows
// and therefore no merge — it may keep its own scroller.
check('the create form still has its checklist rule', CSS.includes('.library-checklist {'), true);

console.log();
console.log('── Artwork controls follow the tick ──────────────────────────');
// Exercise the real helpers against a minimal fake row.
const src = SRC.replace('ProfilesPlugin.init();', 'globalThis.__PP = ProfilesPlugin;');
function makeStorage() {
    const m = new Map();
    return { getItem: k => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)),
             removeItem: k => m.delete(k), clear: () => m.clear() };
}
const noopEl = { style: {}, classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
                 appendChild() {}, addEventListener() {}, removeEventListener() {}, remove() {} };
const sandbox = {
    console, localStorage: makeStorage(), sessionStorage: makeStorage(),
    setTimeout: () => 0, clearTimeout: () => {}, setInterval: () => 0, clearInterval: () => {},
    requestAnimationFrame: () => 0, fetch: () => Promise.reject(new Error('no network')),
    MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
    document: { head: noopEl, body: noopEl, documentElement: noopEl,
        createElement: () => Object.assign({}, noopEl, { innerHTML: '', querySelector: () => null, querySelectorAll: () => [] }),
        querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
        addEventListener() {}, removeEventListener() {} },
    window: { location: { hash: '' }, addEventListener() {}, navigator: { userAgent: 'harness' } },
    navigator: { userAgent: 'harness' },
    ApiClient: { getUrl: p => '/' + p, serverAddress: () => 'http://localhost:8096' }
};
sandbox.window.localStorage = sandbox.localStorage;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
vm.runInContext(src, sandbox);
const PP = sandbox.__PP;

function fakeRow(checked) {
    const select = { disabled: false, className: 'libart-mode' };
    const button = { disabled: false, className: 'libart-choose' };
    const box = { checked: checked, className: 'library-checkbox' };
    return {
        style: {},
        _controls: [select, button],
        select, button, box,
        querySelector: sel => (sel === '.library-checkbox' ? box : null),
        querySelectorAll: () => [select, button]
    };
}

let r = fakeRow(true);
PP.syncLibraryRowState(r);
check('ticked: the mode select is usable', r.select.disabled, false);
check('ticked: Choose is usable', r.button.disabled, false);
check('ticked: the row is at full strength', r.style.opacity, '');

r = fakeRow(false);
PP.syncLibraryRowState(r);
check('unticked: the mode select is disabled', r.select.disabled, true);
check('unticked: Choose is disabled', r.button.disabled, true);
check('unticked: the row is dimmed', r.style.opacity, '0.5');

// Toggling back must restore, not leave it stuck.
r.box.checked = true;
PP.syncLibraryRowState(r);
check('re-ticking restores the controls', r.select.disabled, false);
check('re-ticking restores full strength', r.style.opacity, '');

check('a missing row is survivable', (() => {
    try { PP.syncLibraryRowState(null); return true; } catch (e) { return false; }
})(), true);

console.log();
console.log('── The wiring is present ─────────────────────────────────────');
check('select-all syncs every row', SRC.includes('this.syncAllLibraryRows(content);'), true);
check('an individual tick syncs its own row',
    SRC.includes("this.syncLibraryRowState(cb.closest('.libart-row'));"), true);
check('rows start in the right state, not on first change',
    (SRC.match(/this\.syncAllLibraryRows\(content\);/g) || []).length >= 2, true);

console.log();
console.log(pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
