/*
 * The inline <head> script, executed.
 *
 * It is the only part of the plugin that runs before anything else on a page load, and
 * it is the part that hides the whole document. Two things were wrong with it:
 *
 *  - it painted #101010 with color-scheme:dark unconditionally. Jellyfin ships light
 *    themes, so on any of them a profile switch flashed a full-screen black rectangle —
 *    the exact flash this script exists to prevent, in the other direction — and forced
 *    dark scrollbars and form controls while it did.
 *  - if the reveal never came, it held the page hidden for four seconds. On a television
 *    four seconds of nothing is indistinguishable from a crash.
 *
 * The script lives in a C# const, so it is reassembled from the string literals in
 * WebInjection.cs and run here. That is worth the trouble: this script is never covered
 * by node --check, never linted, and a mistake in it blanks the entire web interface.
 *
 *     node tests/js/headscript.js "" "" /path/to/old/WebInjection.cs
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SRC = process.argv[4] || path.join(L.ROOT, 'WebInjection.cs');
const cs = fs.readFileSync(SRC, 'utf8');

// ── reassemble the C# constant ───────────────────────────────────────────────
function headScript() {
    const at = cs.indexOf('internal const string HeadScript =');
    if (at === -1) throw new Error('HeadScript not found in ' + SRC);
    const end = cs.indexOf('";', at);
    if (end === -1) throw new Error('HeadScript is not terminated');

    const region = cs.slice(at, end + 2)
        .split('\n')
        // Drop // comments. The script itself contains none; the region does.
        .map(line => {
            const c = line.indexOf('//');
            return c === -1 ? line : line.slice(0, c);
        })
        .join('\n');

    const parts = region.match(/"(?:[^"\\]|\\.)*"/g) || [];
    return parts
        .map(p => p.slice(1, -1).replace(/\\(["'\\])/g, '$1'))
        .join('');
}

const tag = headScript();

console.log('\n── The constant reassembles into a real script tag ─────────────');

// If the reassembly is wrong every assertion below is about the wrong text, so this is
// checked first and loudly rather than left to produce quiet nonsense.
ok('it opens with the tag the injector looks for', tag.indexOf('<script id="jpf-eh">') === 0);
ok('and closes', /<\/script>$/.test(tag));
ok('with a body of a plausible size (' + tag.length + ' chars)', tag.length > 300);

const body = tag.replace(/^<script id="jpf-eh">/, '').replace(/<\/script>$/, '');

let syntaxError = null;
try { new vm.Script(body, { filename: 'headscript' }); } catch (e) { syntaxError = e; }
ok('the script parses' + (syntaxError ? ': ' + syntaxError.message : ''), syntaxError === null);

// ── run it ───────────────────────────────────────────────────────────────────
function run(store, opts) {
    opts = opts || {};
    const removed = [];
    const listeners = {};
    const timeouts = [];

    const style = {};
    const sandbox = {
        localStorage: {
            getItem: k => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = v; },
            removeItem: k => { removed.push(k); delete store[k]; }
        },
        document: {
            documentElement: { style: style },
            addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); }
        },
        setTimeout: (fn, ms) => { timeouts.push({ fn: fn, ms: ms }); return timeouts.length; },
        clearTimeout: id => { if (timeouts[id - 1]) timeouts[id - 1].cancelled = true; },
        RegExp, JSON, String, Number
    };
    sandbox.window = sandbox;
    if (opts.jpLoaded) sandbox.__jpLoaded = 1;

    vm.createContext(sandbox);
    new vm.Script(body, { filename: 'headscript' }).runInContext(sandbox);

    return {
        style: style,
        sandbox: sandbox,
        removed: removed,
        timeouts: timeouts,
        fire: type => (listeners[type] || []).forEach(fn => fn({})),
        listens: type => (listeners[type] || []).length > 0
    };
}

console.log('\n── An ordinary page load is left completely alone ──────────────');

const idle = run({});
ok('nothing is hidden when no switch is in progress', !idle.style.opacity);
ok('no colour is painted', !idle.style.background);
ok('no failsafe is armed', idle.timeouts.length === 0);
ok('and no listener is left behind', !idle.listens('DOMContentLoaded'));

console.log('\n── A switch holds the colour the last page was painted in ──────');

const light = run({ 'jpf-sw': '1', 'jpf-bg': 'rgb(255, 255, 255)|light' });
ok('the page is hidden', light.style.opacity === '0');
ok('painted in the colour it was left in, not a hardcoded dark one ('
   + light.style.background + ')', light.style.background === 'rgb(255, 255, 255)');
ok('with a matching colour-scheme, so scrollbars and form controls do not go dark '
   + 'over a light page (' + light.style.colorScheme + ')', light.style.colorScheme === 'light');
ok('the switching flag is consumed', light.removed.indexOf('jpf-sw') !== -1);
ok('and profiles.js is told a switch is in progress', light.sandbox.__jpSwitching === 1);

const dark = run({ 'jpf-sw': '1', 'jpf-bg': '#101010|dark' });
ok('a dark theme is held dark', dark.style.background === '#101010'
   && dark.style.colorScheme === 'dark');

console.log('\n── A missing or nonsense colour falls back rather than failing ─');

const noBg = run({ 'jpf-sw': '1' });
ok('no stored colour falls back to the old default', noBg.style.background === '#101010');
ok('and to dark', noBg.style.colorScheme === 'dark');

// A value that is not a colour would paint nothing, leaving the page hidden until a
// failsafe fired — worse than the flash this whole script exists to avoid.
[
    ['url(javascript:0)', 'a url()'],
    ['red;position:fixed', 'a second declaration'],
    ['', 'an empty string'],
    ['not-a-colour', 'a bare word']
].forEach(function (pair) {
    const r = run({ 'jpf-sw': '1', 'jpf-bg': pair[0] + '|dark' });
    ok(pair[1] + ' is refused and the default used instead (' + r.style.background + ')',
       r.style.background === '#101010');
});

const oddScheme = run({ 'jpf-sw': '1', 'jpf-bg': '#ffffff|sideways' });
ok('an unrecognised colour-scheme becomes dark rather than being passed through',
   oddScheme.style.colorScheme === 'dark');

console.log('\n── The page is never held hidden for long ──────────────────────');

const armed = run({ 'jpf-sw': '1', 'jpf-bg': '#101010|dark' });
ok('a failsafe is armed', armed.timeouts.length === 1);
ok('at 1.5 seconds, not four — four seconds of nothing on a TV reads as a crash '
   + '(' + (armed.timeouts[0] || {}).ms + 'ms)',
   armed.timeouts.length === 1 && armed.timeouts[0].ms === 1500);

armed.timeouts[0].fn();
ok('and it puts the page back', armed.style.opacity === ''
   && armed.style.background === '' && armed.style.colorScheme === '');

console.log('\n── If the script never loaded, do not wait for it at all ───────');

// profiles.js is a deferred script, so it has finished evaluating before
// DOMContentLoaded. The flag missing at that point is proof it never loaded.
const dead = run({ 'jpf-sw': '1', 'jpf-bg': '#101010|dark' }, { jpLoaded: false });
ok('a second trigger is registered', dead.listens('DOMContentLoaded'));
dead.fire('DOMContentLoaded');
ok('with no profiles.js, the page comes back immediately rather than after the timeout',
   dead.style.opacity === '');
ok('and the failsafe is stood down', dead.timeouts[0].cancelled === true);

const alive = run({ 'jpf-sw': '1', 'jpf-bg': '#101010|dark' }, { jpLoaded: true });
alive.fire('DOMContentLoaded');
ok('but when profiles.js did load, the reveal is left to it — showing the shell here '
   + 'would be a blank page, which is what it is gated on viewshow to avoid',
   alive.style.opacity === '0');
ok('and the failsafe stays armed as the backstop', alive.timeouts[0].cancelled !== true);

console.log('\n── profiles.js holds up its end ────────────────────────────────');

const js = L.readProfiles();
ok('it sets the flag the head script tests for', /window\.__jpLoaded\s*=\s*1/.test(js));
ok('it records the colour it is leaving', /_captureLeavingBackground/.test(js));
ok('and stores it under the key the head script reads',
   /backgroundKey:\s*'jpf-bg'/.test(js));

// The point of all of the above: no remaining path paints a fixed dark colour.
// Matched at the assignment rather than anywhere in the file — the comment explaining
// why this changed says "#101010" too, and counting that reported a bug in the prose.
const hardcoded = js.match(/style\.(?:cssText|background)\s*=\s*'[^']*#101010/g) || [];
ok('no switch path still hardcodes the dark background ('
   + (hardcoded.length ? hardcoded.join(' / ') : 'none') + ')',
   hardcoded.length === 0);

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
