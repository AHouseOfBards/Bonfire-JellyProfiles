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
let sheetText = null;
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
        sheetText = css;
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
        // The length floor above cannot tell the stylesheet from the themes: the themes
        // alone are over 40,000 characters and carry the same sentinel selectors, so
        // dropping BONFIRE_STYLES from the sheet stayed green. Ask for the file itself.
        if (source.indexOf('let BONFIRE_STYLES = "') !== -1) {
            ok(css.indexOf(require('./_lib').stylesOnly()) !== -1,
                'the sheet contains Web/styles.css whole, not only the themes');
        }
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

    // Library artwork (issue #19) has never been seen working by anybody, and its two
    // startup calls were among the nine the 1.5.2 backtick killed — so for two releases
    // the feature could not have run at all, whatever its own harness said about rule
    // generation. tests/js/libartest.js calls those functions directly and so cannot
    // notice if init() stops reaching them; a count of steps would not notice either.
    // These name them.
    ['applyCachedLibraryArtwork', 'loadLibraryArtwork'].forEach(name => {
        ok(steps.includes(name),
            'init() still runs ' + name + '() — library artwork reaches the page',
            'init() steps are: ' + steps.join(', '));
    });

    steps.forEach(name => {
        if (name === 'loadLocale') return;          // a closure function, not a method
        if (name === 'watchJellyfinLanguage') return; // likewise; i18ntest.js runs it
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
    // The text above also matches a catch that rethrows, and a try that never calls fn.
    // Run it: it must call the step, and must swallow what the step throws.
    {
        let called = 0, stepErr = null;
        const realErr = console.error;
        console.error = function () {};
        try {
            P._step('probe', () => { called++; throw new Error('boom'); });
        } catch (e) { stepErr = e; }
        console.error = realErr;
        ok(called === 1, '_step() calls the step it is given', 'called ' + called + ' times');
        ok(!stepErr, '_step() swallows a throwing step, so one broken step cannot stop the rest',
            stepErr ? stepErr.message : '');
    }

    // 6. Run init() itself. Everything above calls the steps one by one, which cannot
    //    notice init() no longer reaching one, reaching it unwrapped, or stopping early:
    //    the step count only has to stay >= 10 and the names are read from init's own
    //    text, so deleting a step deleted its test with it. Spy on the methods, run
    //    init(), and require every step of this list to have been called by it. The list
    //    is pinned on purpose: removing a startup step has to be a decision made here.
    //    A step added to init() is still covered by item 3.
    const REQUIRED_STEPS = [
        'bindEvents', 'applyDeviceClasses', 'restoreCustomThemes', 'applyCachedTheme',
        'injectStyles', 'loadCustomThemes', '_adoptCover', 'applyCachedLibraryArtwork',
        'loadLibraryArtwork', 'loadSwitcherPrefs', 'bindPanicShortcut',
        '_bindOverlayFocusTrap', 'checkPersistedPanic',
        // reached through _startPageChecks, which init() calls last
        'checkMaintenance', 'validateSessionState'
    ];
    const order = [];
    // Item 3 already ran the steps by hand, and one of them started the once-per-load page
    // checks; init() would then skip them. Put the guard back as a fresh page has it.
    P._pageChecksStarted = false;
    // Likewise something in that loop left the plugin in its emergency-disabled state,
    // which makes init() return before the page checks.
    P._panicDisabled = false;
    localStorage.clear();
    REQUIRED_STEPS.forEach(name => {
        if (typeof P[name] !== 'function') return;   // reported by item 3 / below
        const real = P[name];
        P[name] = function () { order.push(name); return real.apply(this, arguments); };
    });
    let initErr = null;
    const realErr2 = console.error;
    const stepErrors = [];
    console.error = function () { stepErrors.push(Array.prototype.join.call(arguments, ' ')); };
    try { P.init(); } catch (e) { initErr = e; }
    console.error = realErr2;
    ok(!initErr, 'init() runs to the end without throwing',
        initErr ? initErr.name + ': ' + initErr.message : '');
    REQUIRED_STEPS.forEach(name => {
        ok(typeof P[name] === 'function' && order.indexOf(name) !== -1,
            'init() reaches ' + name + '()',
            'init() called, in order: ' + order.join(', '));
    });
    ok(stepErrors.length === 0, 'no startup step fails when init() runs it',
        stepErrors.join(' | '));
    // The three orders init()'s own comments say matter.
    [['applyDeviceClasses', 'injectStyles'],
     ['restoreCustomThemes', 'applyCachedTheme'],
     ['injectStyles', 'validateSessionState']].forEach(pair => {
        const a = order.indexOf(pair[0]), b = order.indexOf(pair[1]);
        ok(a !== -1 && b !== -1 && a < b, 'init() runs ' + pair[0] + '() before ' + pair[1] + '()',
            'order: ' + order.join(', '));
    });
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

// 5b. What a stray character in Web/styles.css can still break.
//
// Assertion 5 above is dead code since the stylesheet left the script: PublishStyles splices
// it in as a JSON string, so no character in the CSS can end a JavaScript literal, and the
// `injectStyles: function` / backtick search finds nothing to examine. The splice is total in
// JS terms. What is left is CSS's own grammar, and it fails silently in the browser: an
// unterminated /* comment, an unterminated string, or one unbalanced brace makes the parser
// swallow everything after it. The sheet injected is styles.css + '\n' + the themes' CSS, so
// that is the themes (every screen's colours) and every rule below the defect. Nothing throws,
// nothing logs, and injectStyles() reports success.
function cssDefects(css) {
    const out = [];
    const stack = [];
    let line = 1;
    for (let i = 0; i < css.length; i++) {
        const c = css[i];
        if (c === '\n') line++;
        if (c === '/' && css[i + 1] === '*') {
            const end = css.indexOf('*/', i + 2);
            if (end === -1) { out.push('unterminated comment opened on line ' + line); return out; }
            for (let k = i; k < end; k++) if (css[k] === '\n') line++;
            i = end + 1;
            continue;
        }
        if (c === '"' || c === "'") {
            let j = i + 1;
            while (j < css.length && css[j] !== c && css[j] !== '\n') j += css[j] === '\\' ? 2 : 1;
            if (css[j] !== c) { out.push('unterminated string opened on line ' + line); return out; }
            i = j;
            continue;
        }
        if (c === '{' || c === '(' || c === '[') stack.push({ c: c, line: line });
        else if (c === '}' || c === ')' || c === ']') {
            const want = c === '}' ? '{' : c === ')' ? '(' : '[';
            const top = stack.pop();
            if (!top || top.c !== want) { out.push('stray ' + c + ' on line ' + line); return out; }
        }
    }
    if (stack.length) out.push('unclosed ' + stack[stack.length - 1].c + ' opened on line ' + stack[stack.length - 1].line);
    return out;
}
{
    const L = require('./_lib');
    // The detector has to be able to go red, or the checks below are decoration.
    ok(cssDefects('a { b: c; /* open').length === 1, 'cssDefects sees an unterminated comment');
    ok(cssDefects('a { b: "c; }').length === 1, 'cssDefects sees an unterminated string');
    ok(cssDefects('a { b: c; }}').length === 1, 'cssDefects sees a stray close brace');
    ok(cssDefects('a { b: c; ').length === 1, 'cssDefects sees an unclosed brace');
    ok(cssDefects('a { content: "}{/*"; } /* } */ b[x="y"] { c: d(e) }').length === 0,
        'cssDefects leaves braces inside strings and comments alone');

    const own = cssDefects(L.stylesOnly());
    ok(own.length === 0, 'Web/styles.css has no unterminated comment or string and balanced braces',
        own.join('; ') + ' - everything after it, themes included, is swallowed by the browser');
    if (typeof sheetText === 'string') {
        const whole = cssDefects(sheetText);
        ok(whole.length === 0, 'the sheet injectStyles writes (styles.css + themes) is well formed',
            whole.join('; '));
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
