/*
 * Issue #31: the profile gate did not fit on the screen.
 *
 * The tiles were one fixed size per device class — 280px on a television — and the gate's
 * content was capped at 900px on every screen. On a 1920x1080 set that meant two tiles to a
 * row, so four profiles became two rows that ran 247px off the bottom, six ran 662px, and
 * the household had to scroll with a remote to find themselves. Measured in Chromium with
 * the real script and stylesheet before the fix:
 *
 *     1920x1080 tv  4 profiles  overflow 247px
 *     1280x720  tv  2 profiles  overflow 192px
 *     1366x768      6 profiles  overflow 182px
 *
 * The fix is fitGateToScreen: when the gate would scroll, it puts .jpf-fitted and a
 * --jpf-tile size on the gate's content and searches for the largest tile that fits.
 *
 * This harness has two halves, because either one alone is a restatement:
 *
 *   1. The routine, driven against a modelled overlay whose scroll height follows from the
 *      tile size the routine sets — tiles wrap to rows by the available width, as flex-wrap
 *      does — so "it fits" is answered by the layout, not by the routine's own say-so.
 *   2. The stylesheet: every property the fitted rules set must actually win the cascade
 *      over every other rule that sets it on the same element. Resolved pairwise —
 *      specificity, then order — not string-matched. A rule present and outranked is the
 *      way this sheet has shipped a dead override before.
 *
 * Point it at an older client to see it fail:
 *
 *     node tests/js/gatefit.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SRC_PATH = L.profilesPath();
const raw = fs.readFileSync(SRC_PATH, 'utf8');
let src = raw;
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

/* ── a gate whose height is a consequence of its tile size ─────────────────── */

function makeClassList() {
    const set = new Set();
    return {
        add: c => set.add(c), remove: c => set.delete(c),
        contains: c => set.has(c), toggle: c => (set.has(c) ? set.delete(c) : set.add(c)),
        _set: set
    };
}
function makeStyle() {
    const props = {};
    return {
        setProperty: (k, v) => { props[k] = String(v); },
        removeProperty: k => { delete props[k]; },
        getPropertyValue: k => props[k] || '',
        _props: props
    };
}

// screen: the overlay's visible height and the row width the grid gets.
// natural: the tile size the stylesheet gives without the fitted rules.
// chrome: everything that is not tiles — title, section header, footer, padding.
// When fitted, the stylesheet also tightens the title and footer margins; modelled
// as a fixed saving so the routine sees what the browser would.
function makeGate(opts) {
    const content = { classList: makeClassList(), style: makeStyle() };
    const overlay = { classList: makeClassList(), style: makeStyle() };
    const tile = () => {
        if (!content.classList.contains('jpf-fitted')) return opts.natural;
        const v = parseFloat(content.style.getPropertyValue('--jpf-tile'));
        return isFinite(v) ? v : opts.natural;
    };
    const height = () => {
        const t = tile();
        const fitted = content.classList.contains('jpf-fitted');
        const gap = fitted ? t / 5 : opts.naturalGap;
        const card = t + 20;
        const perRow = Math.max(1, Math.floor((opts.width + gap) / (card + gap)));
        const rows = Math.ceil(opts.profiles / perRow);
        const label = 60;
        return opts.chrome - (fitted ? opts.fittedSaving : 0) + rows * (t + label) + (rows - 1) * gap;
    };
    const sample = { getBoundingClientRect: () => ({ width: tile(), height: tile() }) };
    const grid = {
        querySelector: s => (/profile-avatar-container/.test(s) ? sample : null),
        closest: s => (s === '.profiles-modal-content' ? content : null)
    };
    overlay.querySelector = s => {
        if (s === '.profiles-grid') return grid;
        if (/profiles-grid .*profile-avatar-container/.test(s)) return sample;
        return null;
    };
    Object.defineProperty(overlay, 'scrollHeight', { get: () => Math.max(opts.screen, Math.ceil(height())) });
    Object.defineProperty(overlay, 'clientHeight', { get: () => opts.screen });
    return { overlay, content, tile, height };
}

function load(tv) {
    const htmlClasses = makeClassList();
    if (tv) htmlClasses.add('jpf-tv');
    const listeners = {};
    const sandbox = {
        console: { log() {}, warn() {}, error() {}, info() {}, debug() {} },
        setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
        document: {
            documentElement: { classList: htmlClasses },
            getElementById: () => null, querySelector: () => null, querySelectorAll: () => [],
            addEventListener() {}, createElement: () => ({ style: {}, classList: makeClassList(), setAttribute() {}, appendChild() {} }),
            head: { appendChild() {} }, body: { appendChild() {}, classList: makeClassList() }
        },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: '' }, location: { href: '', hash: '', pathname: '/' },
        fetch: () => new Promise(() => {}),
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; }
    };
    sandbox.window = sandbox;
    sandbox.addEventListener = (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); };
    sandbox.removeEventListener = () => {};
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox, { filename: SRC_PATH });
    return { P: sandbox.__PROFILES, listeners };
}

console.log();
console.log('── the gate is fitted to the screen (issue #31) ────────────────');

const tv = load(true);
const P = tv.P;
ok('there is a routine that fits the gate', !!P && typeof P.fitGateToScreen === 'function');

if (P && typeof P.fitGateToScreen === 'function') {
    // The reported case: a 1080p television, four profiles, 280px tiles. 1640px of row
    // width is what the widened TV content gives on a 1920 screen.
    const TV1080 = { screen: 1080, width: 1640, natural: 280, naturalGap: 64, chrome: 560, fittedSaving: 120 };

    const two = makeGate(Object.assign({ profiles: 2 }, TV1080));
    P.fitGateToScreen(two.overlay);
    ok('two profiles that already fit are left at full size',
        !two.content.classList.contains('jpf-fitted') && two.tile() === 280);

    for (const n of [4, 6, 9]) {
        const g = makeGate(Object.assign({ profiles: n }, TV1080));
        const before = g.height();
        P.fitGateToScreen(g.overlay);
        const fitted = g.content.classList.contains('jpf-fitted');
        const t = g.tile();
        ok(n + ' profiles on a 1080p television: ' + Math.round(before) + 'px tall before, fits after (tile ' + t + 'px)',
            g.height() <= 1080 + 1);
        // Largest that fits, not merely one that fits: shrinking every gate to the floor
        // would also "fit", and would make every television look like a phone.
        if (fitted && t < 280) {
            g.content.style.setProperty('--jpf-tile', (t + 3) + 'px');
            ok('  and the tiles are only as small as they have to be', g.height() > 1080 + 1);
            g.content.style.setProperty('--jpf-tile', t + 'px');
        }
        ok('  and the class went on the gate\'s content, not the overlay',
            !g.overlay.classList.contains('jpf-fitted')
            && !('--jpf-tile' in g.overlay.style._props));
    }

    // Floor: a phone with a crowd. Tiles too small to read are worse than scrolling.
    const phone = load(false).P;
    const crowd = makeGate({ profiles: 12, screen: 844, width: 340, natural: 130, naturalGap: 48, chrome: 420, fittedSaving: 60 });
    phone.fitGateToScreen(crowd.overlay);
    ok('when even the smallest tiles cannot fit, it stops at a readable floor and scrolls',
        crowd.tile() >= 80 && crowd.height() > 844);

    // Refit: a gate that needed fitting, then a profile was deleted. The old size must not
    // stick, or the gate stays shrunk with room to spare.
    const shrink = makeGate(Object.assign({ profiles: 6 }, TV1080));
    P.fitGateToScreen(shrink.overlay);
    const wasFitted = shrink.content.classList.contains('jpf-fitted');
    const opts = Object.assign({ profiles: 2 }, TV1080);
    const refit = makeGate(opts);
    refit.content.classList.add('jpf-fitted');
    refit.content.style.setProperty('--jpf-tile', '150px');
    P.fitGateToScreen(refit.overlay);
    ok('a gate fitted once is restored to full size when it no longer needs it',
        wasFitted && !refit.content.classList.contains('jpf-fitted')
        && !('--jpf-tile' in refit.content.style._props));

    ok('a resize re-fits the gate, bound once however often the gate is drawn',
        (tv.listeners.resize || []).length === 1);
}

console.log();
console.log('── it runs when the gate is drawn, and never on the route poll ──');

// Read from the loaded object: these are methods, which extractFunction does not find.
const render = P && P.renderOverlayContent ? P.renderOverlayContent.toString() : '';
ok('renderOverlayContent fits the gate it has just drawn',
    /this\.fitGateToScreen\(overlay\)/.test(render));
const checkRoute = P && P.checkRoute ? P.checkRoute.toString() : '';
ok('checkRoute does not (it runs twice a second, forever)',
    checkRoute.length > 0 && !/fitGateToScreen/.test(checkRoute));

/* ── the fitted rules win the cascade ─────────────────────────────────────── */

console.log();
console.log('── the fitted sizes win the cascade, property by property ─────');

const css = L.extractCss(raw);
const bare = css.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));

// Every rule, with media blocks flattened. Specificity is decided per selector; position in
// the sheet breaks ties. A rule inside a media query competes on the same terms when the
// query matches, so it is included — the worst case is that it applies.
function rules() {
    const out = [];
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(bare)) !== null) {
        const sel = m[1].trim();
        if (!sel || sel.startsWith('@')) continue;
        sel.split(',').forEach(s => {
            s = s.trim().replace(/\s+/g, ' ');
            if (s) out.push({ selector: s, body: m[2], index: m.index });
        });
    }
    return out;
}
function specificity(sel) {
    const s = sel.replace(/::?[a-z-]+\([^)]*\)/g, m => (/^::/.test(m) ? ' x' : ' .x'));
    const ids = (s.match(/#[\w-]+/g) || []).length;
    const classes = (s.match(/\.[\w-]+|\[[^\]]*\]|:(?!:)[\w-]+/g) || []).length;
    const types = (s.replace(/#[\w-]+|\.[\w-]+|\[[^\]]*\]|::?[\w-]+/g, ' ')
        .match(/(^|[\s>+~])[a-z][\w-]*/gi) || []).length;
    return [ids, classes, types];
}
function beats(a, b) {
    const x = specificity(a.selector), y = specificity(b.selector);
    for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] > y[i];
    return a.index > b.index;
}
function setsProp(body, prop) {
    return new RegExp('(^|[;{\\s])' + prop + '\\s*:').test(body);
}
// The class the selector finally lands on, i.e. the element being styled.
function subject(sel) {
    const last = sel.split(/[\s>+~]+/).pop();
    const classes = last.match(/\.[\w-]+/g) || [];
    return classes.length ? classes[0] : (last === '*' ? '*' : null);
}

const all = rules();
// The one rule that only declares the default tile size is not a sizing rule.
const fitted = all.filter(r => /\.jpf-fitted/.test(r.selector) && !/^\s*--jpf-tile\s*:[^;]*;?\s*$/.test(r.body));
const fallback = all.find(r => /\.jpf-fitted$/.test(r.selector) && /--jpf-tile\s*:\s*\d+px/.test(r.body));
ok('the tile size has a declared default, so no fitted rule can resolve to nothing', !!fallback);
ok('the fitted rules are in the stylesheet', fitted.length >= 6);
ok('and are sized by the tile property the routine sets',
    fitted.length > 0 && fitted.every(r => /var\(--jpf-tile\)/.test(r.body)));

let pairs = 0;
const lost = [];
fitted.forEach(f => {
    const subj = subject(f.selector);
    const props = (f.body.match(/(^|[;\s])([a-z-]+)\s*:/g) || []).map(p => p.replace(/[;\s:]/g, ''));
    props.forEach(prop => {
        all.forEach(r => {
            if (r === f || /\.jpf-fitted/.test(r.selector)) return;
            if (subject(r.selector) !== subj || !setsProp(r.body, prop)) return;
            // :hover, :focus and friends are other states with their own reasons to differ.
            if (/:(hover|focus|focus-visible|active)\b/.test(r.selector)) return;
            // A rule for a different root mode cannot apply at the same time.
            if (/\.jpf-no-flex-gap/.test(r.selector) && !/\.jpf-no-flex-gap/.test(f.selector)) return;
            // Everything else that styles the same class is a competitor, including rules
            // written for it somewhere other than the grid: they apply inside it too.
            pairs++;
            if (!beats(f, r)) lost.push(prop + ': ' + f.selector + '  loses to  ' + r.selector);
        });
    });
});
ok('every fitted property outranks every rule that sets it on the same element ('
    + pairs + ' pairs resolved)', pairs > 0 && lost.length === 0);
lost.forEach(l => console.log('        ' + l));

// The television rules are the ones this has to beat, and the reason the issue was filed;
// name them so a selector rename cannot quietly drop them from the pairs above.
[['.jpf-tv .profile-avatar-container', 'width'],
 ['.jpf-tv .profile-card', 'width'],
 ['.jpf-tv .profiles-grid', 'gap'],
 ['.jpf-tv .profiles-title', 'margin-bottom']].forEach(([sel, prop]) => {
    const r = all.find(x => x.selector === sel && setsProp(x.body, prop));
    const f = fitted.find(x => subject(x.selector) === subject(sel) && setsProp(x.body, prop));
    ok(sel + ' { ' + prop + ' } is outranked when fitted', !!r && !!f && beats(f, r));
});

const wide = all.find(x => x.selector === '.jpf-tv .profiles-modal-content');
ok('a television gets rows wider than the 900px every screen used to share',
    !!wide && /max-width:\s*(1[0-9]{3}|none)/.test(wide.body) && beats(wide,
        all.find(x => x.selector === '.profiles-modal-content' && setsProp(x.body, 'max-width'))));

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
