/*
 * Does the client script actually start up?
 *
 * Written against the defect that shipped in 1.5.2 and 1.5.3-beta: two unescaped
 * backticks inside injectStyles' CSS template literal ended the literal early. The file
 * stayed valid JavaScript — `node --check` passed and the release went out — but at
 * runtime the expression became a tagged template on an undeclared identifier and threw
 * ReferenceError. injectStyles is the third call in init(), so the nine steps after it
 * never ran.
 *
 * The lesson this file exists to enforce: "does it parse" is not "does it run". Every
 * assertion below executes the shipped code rather than inspecting its text.
 *
 * init() now wraps each step in _step(), which catches. That is right for production and
 * wrong for a test, so this calls every step DIRECTLY and unwrapped — otherwise the guard
 * we just added would hide the very failure we are checking for.
 *
 *   node tests/js/inject.test.js                 # checks Web/profiles.js
 *   node tests/js/inject.test.js <path>          # or any other checkout, to prove it fails there
 */
'use strict';

const fs = require('fs');
const path = require('path');

const SRC = process.argv[2] || path.join(__dirname, '..', '..', 'Web', 'profiles.js');

// ── tiny assert harness ─────────────────────────────────────────────────────
let passed = 0;
const failures = [];
function ok(cond, label, detail) {
    if (cond) { passed++; return true; }
    failures.push(label + (detail ? '\n      ' + detail : ''));
    return false;
}

// ── a DOM stub just real enough to reach every startup step ─────────────────
function makeEl(tag) {
    const el = {
        tagName: (tag || 'div').toUpperCase(),
        id: '', className: '', textContent: '', innerHTML: '',
        style: { cssText: '', setProperty() {}, removeProperty() {} },
        dataset: {},
        children: [],
        classList: { add() {}, remove() {}, contains: () => false, toggle() {} },
        setAttribute() {}, removeAttribute() {}, getAttribute: () => null,
        addEventListener() {}, removeEventListener() {}, dispatchEvent() {},
        appendChild(c) { this.children.push(c); return c; },
        insertBefore(c) { this.children.push(c); return c; },
        removeChild() {}, remove() {}, focus() {}, click() {},
        contains: () => false, closest: () => null,
        querySelector: () => null, querySelectorAll: () => [],
        cloneNode() { return makeEl(tag); },
        get offsetParent() { return null; },
        getBoundingClientRect: () => ({ top: 0, left: 0, width: 0, height: 0, bottom: 0, right: 0 })
    };
    return el;
}

function makeStorage() {
    const m = Object.create(null);
    return {
        getItem: k => (k in m ? m[k] : null),
        setItem: (k, v) => { m[k] = String(v); },
        removeItem: k => { delete m[k]; },
        clear: () => { for (const k in m) delete m[k]; }
    };
}

function installDom() {
    const head = makeEl('head');
    const byId = Object.create(null);

    global.document = {
        head,
        body: makeEl('body'),
        documentElement: makeEl('html'),
        createElement: makeEl,
        getElementById: id => byId[id] || null,
        querySelector: () => null,
        querySelectorAll: () => [],
        addEventListener() {}, removeEventListener() {}, dispatchEvent() {},
        // so injectStyles' idempotency guard can see what it appended
        _register(el) { if (el.id) byId[el.id] = el; }
    };
    const realAppend = head.appendChild.bind(head);
    head.appendChild = function (c) { global.document._register(c); return realAppend(c); };

    global.window = {
        location: { hash: '', pathname: '/web/', search: '', href: 'http://x/web/',
                    reload() {}, replace() {} },
        addEventListener() {}, removeEventListener() {},
        setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
        matchMedia: () => ({ matches: false, addListener() {}, addEventListener() {} })
    };
    // Node 18+ defines a read-only `navigator`, so plain assignment throws.
    Object.defineProperty(global, 'navigator', {
        value: { languages: ['en-GB', 'en'], language: 'en-GB', userAgent: 'node' },
        configurable: true, writable: true
    });
    global.localStorage = makeStorage();
    global.sessionStorage = makeStorage();
    global.history = { pushState() {}, replaceState() {} };
    global.fetch = () => Promise.resolve({
        ok: true, status: 200,
        json: () => Promise.resolve({}),
        text: () => Promise.resolve('')
    });
    global.ApiClient = {
        getCurrentUserId: () => null,
        getUrl: p => '/' + p,
        serverAddress: () => 'http://x',
        accessToken: () => '',
        setAuthenticationInfo() {},
        getPluginConfiguration: () => Promise.resolve({})
    };
    // bindEvents() installs the 500ms route poll. Left on the real timers the process
    // never exits, so the run hangs rather than reporting. Record instead of schedule —
    // the counts are worth asserting on later (see P3, the tick work).
    global.__timers = { intervals: 0, timeouts: 0 };
    global.setInterval = () => { global.__timers.intervals++; return 0; };
    global.setTimeout = () => { global.__timers.timeouts++; return 0; };
    global.clearInterval = () => {};
    global.clearTimeout = () => {};

    global.MutationObserver = function () { return { observe() {}, disconnect() {} }; };
    global.KeyboardEvent = function () { return {}; };
    global.Image = function () { return makeEl('img'); };
    global.requestAnimationFrame = cb => { void cb; return 0; };
}

function loadPlugin(source) {
    // Swap the single bootstrap call for an export so the shipped methods are reachable.
    const marker = 'ProfilesPlugin.init();';
    if (source.indexOf(marker) === -1) {
        throw new Error('bootstrap call "' + marker + '" not found — has init() been renamed?');
    }
    const exposed = source.replace(marker, 'module.exports = ProfilesPlugin;');
    const mod = { exports: {} };
    // eslint-disable-next-line no-new-func
    new Function('module', exposed)(mod);
    return mod.exports;
}

// ── run ─────────────────────────────────────────────────────────────────────
//
// The bundle, exactly as the server sends it: profiles.js with Web/styles.css spliced
// into BONFIRE_STYLES. This harness EXECUTES the startup path, so it has to run what a
// browser runs — the file on its own now carries an empty stylesheet by design, and
// asserting against that would test a script nobody is served.
const source = require('./_lib').readClientBundle(fs.readFileSync(SRC, 'utf8'));
installDom();

let P = null;
try {
    P = loadPlugin(source);
} catch (e) {
    ok(false, 'the module evaluates', e.name + ': ' + e.message);
}

if (P) {
    ok(typeof P === 'object' && P !== null, 'the plugin object is exported');

    // 1. injectStyles runs, and produces a real stylesheet.
    let sheet = null;
    let threw = null;
    try {
        P.injectStyles();
        sheet = document.head.children[document.head.children.length - 1] || null;
    } catch (e) {
        threw = e;
    }
    ok(!threw, 'injectStyles() does not throw',
        threw ? threw.name + ': ' + threw.message : '');

    if (sheet) {
        const css = sheet.textContent || sheet.innerHTML || '';
        // A floor, and the reason for it has changed. It used to guard against a literal
        // cut off at the first stray backtick — the defect that shipped 1.5.2 dead — and
        // 60,000 sat just under the inline sheet's ~68,500. The stylesheet is a real .css
        // file now and cannot be truncated that way, and de-indenting it out of the
        // JavaScript took ~25,000 characters of leading whitespace off, so the true size
        // is ~58,700. What the floor still catches is the splice failing: the marker
        // edited away, styles.css missing from the build, an empty resource. All of those
        // leave a sheet of a few hundred characters at most.
        ok(css.length > 40000, 'the stylesheet is a full sheet, not an empty splice',
            'got ' + css.length + ' characters, expected > 40000');
        ok(sheet.id === 'jpf-styles', 'the style element carries id="jpf-styles"',
            'got id="' + sheet.id + '"');

        // Sentinel selectors: a sheet that is long enough but missing the gate would
        // still be broken.
        ['.profile-card', '.profile-avatar', '.profiles-grid', '#profiles-gate-overlay']
            .forEach(sel => ok(css.indexOf(sel) !== -1,
                'the stylesheet defines ' + sel));
    } else {
        ok(false, 'injectStyles() appended a <style> element');
    }

    // 2. It is idempotent.
    const before = document.head.children.length;
    try { P.injectStyles(); } catch (e) { /* already reported above */ }
    ok(document.head.children.length === before,
        'a second injectStyles() call appends nothing',
        'head went from ' + before + ' to ' + document.head.children.length + ' children');

    // 3. Every step init() runs must survive being called on its own.
    //    The list is read out of init() itself, so a step added later is covered
    //    automatically rather than being silently untested.
    const initBody = source.slice(source.indexOf('init: function'),
                                 source.indexOf('bindEvents: function'));
    const steps = [];
    const re = /this\._step\(\s*'([^']+)'/g;
    let m;
    while ((m = re.exec(initBody)) !== null) steps.push(m[1]);

    ok(steps.length >= 10, 'init() routes its startup calls through _step()',
        'found ' + steps.length + ' _step() calls, expected at least 10');

    steps.forEach(name => {
        if (name === 'loadLocale') return;          // a closure function, not a method
        if (name === 'injectStyles') return;        // covered above, and already appended
        if (typeof P[name] !== 'function') {
            ok(false, 'init() step "' + name + '" exists on the plugin object');
            return;
        }
        let err = null;
        try { P[name](); } catch (e) { err = e; }
        ok(!err, 'startup step ' + name + '() does not throw',
            err ? err.name + ': ' + err.message : '');
    });

    // 4. _step must actually contain the guard, or item 3 proves nothing about production.
    ok(/_step:\s*function[\s\S]{0,400}?try\s*\{[\s\S]{0,200}?catch/.test(source),
        '_step() wraps its call in try/catch');
}

// 5. No stray backtick can hide in the stylesheet literal again.
const at = source.indexOf('injectStyles: function');
if (at !== -1) {
    const open = source.indexOf('`', at);
    const close = source.indexOf('`;', open + 1);
    if (open !== -1 && close !== -1) {
        const literal = source.slice(open + 1, close);
        const stray = (literal.match(/`/g) || []).length;
        ok(stray === 0, 'the stylesheet template literal contains no stray backticks',
            'found ' + stray + ' — each one ends the literal early');
        ok(literal.indexOf('${') === -1,
            'the stylesheet template literal contains no ${} interpolation');
    }
}

// ── report ──────────────────────────────────────────────────────────────────
const total = passed + failures.length;
console.log('inject.test.js  ' + path.relative(process.cwd(), SRC));
console.log('  ' + passed + '/' + total + ' assertions passed');
if (failures.length) {
    console.log('');
    failures.forEach(f => console.log('  FAIL  ' + f));
    console.log('');
    process.exit(1);
}
console.log('  OK');
