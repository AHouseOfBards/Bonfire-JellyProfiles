/*
 * The flat look: the same features on every television, with the blur and the moving
 * decoration swapped for flat equivalents where the browser cannot afford them.
 *
 * "Cannot afford" is found by capability, not by a user-agent list: a television browser
 * that does not know backdrop-filter is Chromium 75 or older (webOS 4.5 is 53, webOS 5 is
 * 68), and its software compositor chokes on a blurred drop-shadow across a screen of
 * profile tiles. A television that parses it, and anything that is not a television, is
 * unchanged. html gets `jpf-flat` and the stylesheet's `.jpf-flat` block does the rest.
 * `.jpf-flat` block does the rest.
 *
 * Two halves, because either alone is a restatement:
 *
 *   1. Detection, run in a sandbox: which browsers get the class, and that it is worked
 *      out once and not on the route tick.
 *   2. The stylesheet, ENUMERATED. Every declaration in styles.css that costs a blur or a
 *      loop (a blurred box-shadow / text-shadow / drop-shadow, a backdrop-filter, an
 *      animation that is not the spinner) is found by parsing and paired with the
 *      `.jpf-flat` rule that replaces it, resolved through the cascade: same element, flat
 *      value, outranks it. A count of flat rules would pass with one of twenty-six missing,
 *      and a present rule that is outranked is how this sheet has shipped dead overrides.
 *
 *     node tests/js/flatlook.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const raw = fs.readFileSync(L.profilesPath(), 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (raw.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = raw.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

/* ── 1. detection ─────────────────────────────────────────────────────────── */

function makeClassList(initial) {
    const set = new Set(initial || []);
    return {
        add: c => set.add(c), remove: c => set.delete(c), contains: c => set.has(c),
        toggle: (c, force) => { const on = force === undefined ? !set.has(c) : !!force; if (on) set.add(c); else set.delete(c); return on; }
    };
}

// css: what CSS.supports answers for backdrop-filter — true, false, or 'absent' (no CSS object).
function build(opts) {
    const store = Object.assign({}, opts.storage || {});
    const counts = { supports: 0 };
    const body = {
        children: [], classList: makeClassList(),
        appendChild(el) { el.parentNode = body; body.children.push(el); return el; },
        removeChild(el) { body.children = body.children.filter(c => c !== el); el.parentNode = null; return el; }
    };
    const root = { style: {}, classList: makeClassList(opts.rootClasses) };
    const observers = [];
    const doc = {
        body, documentElement: root, head: { appendChild() {} },
        addEventListener() {}, removeEventListener() {},
        querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
        createElement() {
            const el = { style: { cssText: '' }, children: [], parentNode: null, classList: makeClassList(), appendChild(c) { el.children.push(c); return c; } };
            Object.defineProperty(el, 'scrollHeight', { get() { return el.parentNode === body && el.children.length > 1 ? el.children.length - 1 : 0; } });
            return el;
        }
    };
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: {
            getItem: k => (Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); }, removeItem: k => { delete store[k]; }
        },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: opts.ua || 'Mozilla/5.0' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
        document: doc,
        MutationObserver: function () { this.observe = (target, init) => observers.push({ target, init }); this.disconnect = () => {}; },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Uint8ClampedArray
    };
    if (opts.css !== 'absent') {
        sandbox.CSS = { supports: (prop) => { counts.supports++; return /backdrop-filter/.test(prop) ? opts.css === true : true; } };
    }
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    return { P: sandbox.__PROFILES, root, store, counts, observers };
}

const UA_TV = 'Mozilla/5.0 (Web0S; Linux/SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/53.0.2785.34 Safari/537.36 WebAppManager';
const UA_DESKTOP = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
function start(opts) { const e = build(opts); e.P.applyDeviceClasses(); return e; }
const flat = e => e.root.classList.contains('jpf-flat');

console.log();
console.log('── which browsers get the flat look ───────────────────────────');
ok('a television that cannot blur (webOS 4.5 / 5 class) does',
    flat(start({ ua: UA_TV, css: false })));
ok('a television with no CSS.supports at all does (the safe answer to "cannot tell")',
    flat(start({ ua: UA_TV, css: 'absent' })));
ok('a television that CAN blur (a 2020 set) keeps the full look',
    !flat(start({ ua: UA_TV, css: true })));
ok('an old browser that is not a television is left alone',
    !flat(start({ ua: UA_DESKTOP, css: false })));
ok('Standard layout on an old television is the ordinary look, not flat',
    !flat(start({ ua: UA_TV, css: false, storage: { jellyfin_profiles_layout: 'standard' } })));
ok('TV layout forced on a modern desktop is not flat: that browser can afford it',
    !flat(start({ ua: UA_DESKTOP, css: true, storage: { jellyfin_profiles_layout: 'tv' } })));
ok('TV layout forced on a desktop that cannot blur is flat',
    flat(start({ ua: UA_DESKTOP, css: false, storage: { jellyfin_profiles_layout: 'tv' } })));

console.log();
console.log('── worked out once, kept in step with the layout ──────────────');
const e = start({ ua: UA_TV, css: false });
const asked = e.counts.supports;
for (let i = 0; i < 20; i++) e.observers[0] && e.P.applyDeviceClasses();
ok('the capability is asked once, not on every pass', e.counts.supports === asked && asked >= 1);
e.P.setLayoutChoice('standard');
ok('choosing Standard layout drops the flat class with the TV one', !flat(e) && !e.root.classList.contains('jpf-tv'));
e.P.setLayoutChoice('tv');
ok('and choosing TV layout brings it back', flat(e));
const tick = e.P.checkRoute ? e.P.checkRoute.toString() : '';
ok('checkRoute does not mention it', tick.length > 0 && !/jpf-flat|supports\(/.test(tick));

/* ── 2. the stylesheet ────────────────────────────────────────────────────── */

// The sheet alone. L.extractCss(raw) is the sheet followed by what the theme generator
// writes, which is read separately below, so that what the generator emits is held to the
// generator's own rules and not to the sheet's.
const css = fs.readFileSync(L.stylesPath(), 'utf8');
const bare = css.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));

// Every declaration, with the at-rules it sits in. A block is "@name prelude { … }" or
// "selector { … }"; the scanner keeps a stack so a rule inside @media knows it.
function declarations(text) {
    const out = [];
    const stack = [];
    let i = 0;
    let head = '';
    let headStart = 0;
    while (i < text.length) {
        const ch = text[i];
        if (ch === '{') {
            const prelude = head.trim();
            // The body of a plain rule runs to its closing brace; at-rules nest.
            if (prelude.charAt(0) === '@' && !/^@(font-face|page)/.test(prelude)) {
                stack.push(prelude);
                head = ''; i++; headStart = i; continue;
            }
            const end = text.indexOf('}', i);
            const body = text.slice(i + 1, end);
            out.push({ selector: prelude, body, index: headStart, context: stack.slice() });
            head = ''; i = end + 1; headStart = i; continue;
        }
        if (ch === '}') { stack.pop(); head = ''; i++; headStart = i; continue; }
        head += ch; i++;
    }
    return out;
}
function props(body) {
    const out = [];
    body.split(';').forEach(d => {
        const m = /^\s*([a-z-]+)\s*:\s*([\s\S]+?)\s*$/.exec(d);
        if (m) out.push({ prop: m[1], value: m[2].replace(/\s*!important\s*$/, '').replace(/\s+/g, ' ') });
    });
    return out;
}
function splitTop(value) {
    const out = []; let depth = 0; let cur = '';
    for (const ch of value) {
        if (ch === '(') depth++; if (ch === ')') depth--;
        if (ch === ',' && depth === 0) { out.push(cur); cur = ''; } else cur += ch;
    }
    out.push(cur);
    return out.map(s => s.trim()).filter(Boolean);
}
// A shadow costs a blur when its blur radius is not a literal zero. A var() is unknown, and
// a theme fills it with a blur, so it counts.
function shadowCosts(value) {
    if (/^none$/i.test(value)) return false;
    return splitTop(value).some(s => {
        // A shadow that is only a variable is unknown. A variable after literal offsets is a
        // colour ("0 0 0 2px var(--jpf-accent)"), and the numbers decide.
        if (/^var\([^)]*\)$/.test(s)) return true;
        s = s.replace(/var\([^)]*\)/g, ' ');
        const nums = (s.replace(/rgba?\([^)]*\)|hsla?\([^)]*\)|#[0-9a-f]+/gi, ' ').match(/-?[\d.]+(?:px|rem|em)?/g) || []);
        return nums.length >= 3 && parseFloat(nums[2]) !== 0;
    });
}
function filterCosts(value) {
    return /blur\(/.test(value) || splitTop(value.replace(/\)\s+(?=[a-z-]+\()/g, '),')).some(f => {
        const m = /drop-shadow\(([^)]*(?:\([^)]*\)[^)]*)*)\)/.exec(f);
        return !!m && shadowCosts(m[1]);
    });
}
function costOf(p, context) {
    if (context.some(c => /prefers-reduced-motion:\s*reduce/.test(c))) return null;
    const gated = context.some(c => /^@supports/.test(c) && /backdrop-filter/.test(c));
    if (p.prop === 'box-shadow' || p.prop === 'text-shadow') return shadowCosts(p.value) ? 'blurred ' + p.prop : null;
    if (p.prop === 'filter' || p.prop === '-webkit-filter') return filterCosts(p.value) ? 'blurred filter' : null;
    if (/^(-webkit-)?backdrop-filter$/.test(p.prop)) return /^none$/.test(p.value) || gated ? null : 'backdrop-filter';
    if (/^(-webkit-)?animation(-name)?$/.test(p.prop)) {
        if (/^none$/.test(p.value)) return null;
        // The spinner is the only sign that a tap registered, on a device where nothing else
        // moves; it stays on purpose (see the reduced-motion block's own comment).
        if (/jpfSpin/.test(p.value)) return null;
        return 'animation';
    }
    return null;
}
function flatValue(p) {
    if (p.prop === 'box-shadow' || p.prop === 'text-shadow') return !shadowCosts(p.value);
    if (p.prop === 'filter' || p.prop === '-webkit-filter') return !filterCosts(p.value);
    if (/backdrop-filter/.test(p.prop)) return /^none$/.test(p.value);
    if (/animation/.test(p.prop)) return /^none$/.test(p.value);
    return false;
}

function specificity(sel) {
    const s = sel.replace(/::?[a-z-]+\([^)]*\)/g, m => (/^::/.test(m) ? ' x' : ' .x'));
    const ids = (s.match(/#[\w-]+/g) || []).length;
    const classes = (s.match(/\.[\w-]+|\[[^\]]*\]|:(?!:)[\w-]+/g) || []).length;
    const types = (s.replace(/#[\w-]+|\.[\w-]+|\[[^\]]*\]|::?[\w-]+/g, ' ').match(/(^|[\s>+~])[a-z][\w-]*/gi) || []).length;
    return [ids, classes, types];
}
function beats(a, b) {
    const x = specificity(a.selector), y = specificity(b.selector);
    for (let k = 0; k < 3; k++) if (x[k] !== y[k]) return x[k] > y[k];
    return a.index > b.index;
}
const norm = s => s.replace(/\s+/g, ' ').trim();
function selectors(rule) { return rule.selector.split(',').map(norm).filter(Boolean); }

console.log();
console.log('── every blur and loop in the stylesheet has a flat replacement ─');

const all = declarations(bare).filter(r => !/^@keyframes|^@-webkit-keyframes/.test(r.context.join(' ')) && !/^[\d.%\s,]+$|^(from|to)$/.test(r.selector));
const costly = [];
all.forEach(r => {
    if (/\.jpf-flat/.test(r.selector)) return;
    props(r.body).forEach(p => {
        const why = costOf(p, r.context);
        if (why) selectors(r).forEach(sel => costly.push({ sel, prop: p.prop, why, rule: { selector: sel, index: r.index } }));
    });
});
ok('the survey finds them (' + costly.length + ' declarations)', costly.length >= 20);

const flatRules = [];
all.forEach(r => {
    if (!/\.jpf-flat/.test(r.selector)) return;
    selectors(r).forEach(sel => flatRules.push({ selector: sel, body: r.body, index: r.index, context: r.context }));
});
ok('and the stylesheet has a .jpf-flat block', flatRules.length > 0);

const missing = [];
const outranked = [];
const notFlat = [];
costly.forEach(c => {
    // The replacement is the same selector with .jpf-flat in front: the same element in the
    // same state, one class heavier, later in the sheet.
    const want = '.jpf-flat ' + c.sel;
    const hit = flatRules.filter(f => f.selector === want
        && props(f.body).some(p => p.prop === c.prop));
    if (!hit.length) { missing.push(c.sel + ' { ' + c.prop + ' }  (' + c.why + ')'); return; }
    const f = hit[hit.length - 1];
    const p = props(f.body).filter(q => q.prop === c.prop).pop();
    if (!flatValue(p)) notFlat.push(want + ' { ' + c.prop + ': ' + p.value + ' }');
    if (!beats({ selector: f.selector, index: f.index }, c.rule)) outranked.push(want + '  loses to  ' + c.sel);
});
ok('each one has a .jpf-flat rule for the same element and property (' + (costly.length - missing.length) + ' of ' + costly.length + ')',
    missing.length === 0, missing.join('\n          '));
ok('each replacement is actually flat (no blur, no loop)', notFlat.length === 0, notFlat.join('\n          '));
ok('and outranks the rule it replaces, by specificity and order', outranked.length === 0, outranked.join('\n          '));

// A flat rule for something that costs nothing is a stray; it would hide a rename.
const strays = [];
flatRules.forEach(f => {
    const base = f.selector.replace(/^\.jpf-flat\s+/, '');
    props(f.body).forEach(p => {
        if (!costly.some(c => c.sel === base && c.prop === p.prop)) {
            // transition: none is the one flat rule that has no blur to answer to.
            if (p.prop === 'transition') return;
            // A rule that puts back what a broader flat rule took (a hard ring that would
            // otherwise lose a tie to it) answers to an original that is not costly.
            if (flatValue(p) && all.some(r => !/\.jpf-flat/.test(r.selector) && selectors(r).indexOf(base) !== -1
                && props(r.body).some(q => q.prop === p.prop))) return;
            strays.push(f.selector + ' { ' + p.prop + ' }');
        }
    });
});
ok('and no .jpf-flat rule answers to nothing (a renamed selector would leave one behind)',
    strays.length === 0, strays.join('\n          '));

// What is kept: the cue that says which tile is focused must survive the flattening.
const keptFocus = flatRules.filter(f => /\.jpf-profile-avatar\.is-transparent img/.test(f.selector) && /hover|focus/.test(f.selector));
ok('a cut-out picture keeps its sharp focus outline when its blur is dropped',
    keptFocus.length >= 2 && keptFocus.every(f => /drop-shadow\([^)]* 0 [^)]*\)/.test(f.body) || /drop-shadow\(3px 0 0/.test(f.body)));

console.log();
console.log('── what the theme generator writes ────────────────────────────');
const themeCss = L.themesCss(raw);
const themeBare = themeCss.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));
const themeCostly = [];
declarations(themeBare).forEach(r => {
    props(r.body).forEach(p => {
        const why = costOf(p, r.context);
        if (why) themeCostly.push({ sel: r.selector, why, context: r.context });
    });
});
const blur = themeCostly.filter(c => /backdrop-filter/.test(c.why) || c.why === 'blurred filter');
ok('the generator writes some effects, so the next two checks have something to read (' + themeCostly.length + ')',
    themeCostly.length > 0);
// Frost: costOf already passes a backdrop-filter inside @supports for it, which is the
// capability test itself (a browser that cannot blur never sees it). What is left here
// must be none.
ok('every backdrop-filter is behind @supports backdrop-filter, so a browser that cannot blur never gets one',
    blur.filter(c => /backdrop-filter/.test(c.why)).length === 0);
const drift = themeCostly.filter(c => c.why === 'animation');
ok('every animation a theme writes is held off a flat television (:not(.jpf-flat)) and off reduced motion',
    drift.every(c => /:not\(\.jpf-flat\)/.test(c.sel) && c.context.some(x => /prefers-reduced-motion:\s*no-preference/.test(x))),
    drift.filter(c => !/:not\(\.jpf-flat\)/.test(c.sel)).map(c => c.sel).join('\n          '));
const themeBlurShadow = themeCostly.filter(c => /blurred (box|text)-shadow/.test(c.why));
ok('and no theme writes a blurred shadow itself, only variables the sheet\'s own rules read',
    themeBlurShadow.length === 0, themeBlurShadow.map(c => c.sel).join('\n          '));

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
