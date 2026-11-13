/*
 * The admin page wears Bonfire's ember colours, and reads on both of Jellyfin's dashboard
 * themes.
 *
 * It used to borrow the theme's --accent (Jellyfin blue) for its buttons, tabs and
 * focus rings, with green/red/orange status colours chosen for a dark page. The owner
 * chose the warm Bonfire theme (Web/themes/bonfire.json) for it, fixed. Its accent,
 * #ffb347, is built for a dark page and is 1.9:1 on white, so the light dashboard theme
 * needs a darker ember from the same family. This checks that, one pair at a time:
 * every text or control against every ground it can sit on, in each theme. Nothing is
 * averaged or totalled; a failing pair is named.
 *
 * What the page sits on is read from jellyfin-web, release-10.11.z. The theme is the
 * `data-theme` attribute on <html> (src/scripts/themeManager.js setTheme), and:
 *
 *   dark   src/themes/themes.ts DEFAULT_COLOR_SCHEME  background.default #101010, paper #202020
 *   light  src/themes/themes.ts `light`               background.default #f2f2f2, paper #e8e8e8
 *   appletv src/themes/themes.ts `appletv`            background.default #d5e9f2, paper #fff
 *          (the other three schemes are dark; appletv is the second light one)
 *   page text: src/themes/dark/theme.scss  html { color: rgba(255,255,255,.8) }
 *              src/themes/light/theme.scss html { color: rgba(0,0,0,.87) }
 *
 * Also: every rule is scoped to #profilesConfigurationPage, no ember or status colour is
 * written as a literal outside the tokens at the top, and the header carries Bonfire's mark
 * with an alt text that every language has.
 *
 *   node tests/js/dashwarm.js [path/to/profilesDashboard.html]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; return; }
    fails.push(name + (detail ? '  - ' + detail : ''));
    console.log('  FAIL  ' + name + (detail ? '  - ' + detail : ''));
}
function finish() {
    console.log('');
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
}

const file = process.argv[2] || L.dashboardPath();
const html = fs.readFileSync(file, 'utf8');
const styleOpen = html.indexOf('<style>');
const styleClose = html.indexOf('</style>');
const css = html.slice(styleOpen + 7, styleClose).replace(/\/\*[\s\S]*?\*\//g, '');
const PAGE = '#profilesConfigurationPage';

// ── colour maths ────────────────────────────────────────────────────────────────────
function parse(v) {
    v = String(v).trim();
    let m = /^#([0-9a-f]{3})$/i.exec(v);
    if (m) return { r: parseInt(m[1][0] + m[1][0], 16), g: parseInt(m[1][1] + m[1][1], 16), b: parseInt(m[1][2] + m[1][2], 16), a: 1 };
    m = /^#([0-9a-f]{6})$/i.exec(v);
    if (m) return { r: parseInt(m[1].slice(0, 2), 16), g: parseInt(m[1].slice(2, 4), 16), b: parseInt(m[1].slice(4, 6), 16), a: 1 };
    m = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)$/i.exec(v);
    if (m) return { r: +m[1], g: +m[2], b: +m[3], a: m[4] === undefined ? 1 : +m[4] };
    return null;
}
function over(top, under) {
    const a = top.a;
    return { r: top.r * a + under.r * (1 - a), g: top.g * a + under.g * (1 - a), b: top.b * a + under.b * (1 - a), a: 1 };
}
function lum(c) {
    const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
}
function ratio(a, b) {
    const x = lum(a), y = lum(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}
function lab(c) {
    const f1 = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    const r = f1(c.r), g = f1(c.g), b = f1(c.b);
    const X = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047, Y = r * 0.2126 + g * 0.7152 + b * 0.0722, Z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883;
    const f = t => t > 0.008856 ? Math.cbrt(t) : 7.787 * t + 16 / 116;
    return [116 * f(Y) - 16, 500 * (f(X) - f(Y)), 200 * (f(Y) - f(Z))];
}
function deltaE(a, b) {
    const p = lab(a), q = lab(b);
    return Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]);
}

// ── a small stylesheet reader: rules, nested at-rules, comma lists ───────────────────
// Returns [{ selector, decls: {prop: value}, order }]. @keyframes bodies are skipped.
function readRules(text) {
    const rules = [];
    let order = 0;
    function block(src) {
        let i = 0;
        while (i < src.length) {
            const open = src.indexOf('{', i);
            if (open < 0) break;
            const head = src.slice(i, open).trim();
            let depth = 1, j = open + 1;
            while (j < src.length && depth) { if (src[j] === '{') depth++; else if (src[j] === '}') depth--; j++; }
            const body = src.slice(open + 1, j - 1);
            if (head.charAt(0) === '@') {
                if (!/^@keyframes|^@font-face/i.test(head)) block(body);
            } else {
                const decls = {};
                body.split(';').forEach(d => {
                    const k = d.indexOf(':');
                    if (k > 0) decls[d.slice(0, k).trim()] = d.slice(k + 1).trim();
                });
                head.split(',').forEach(s => rules.push({ selector: s.trim().replace(/\s+/g, ' '), decls: decls, order: order++ }));
            }
            i = j;
        }
    }
    block(text);
    return rules;
}
const rules = readRules(css);

// ── scope: every selector starts at the page ─────────────────────────────────────────
const SCOPE = /^(?:html\[data-theme="[a-z]+"\] )?#profilesConfigurationPage(?![\w-])/;
const unscoped = rules.filter(r => !SCOPE.test(r.selector));
ok('every rule is scoped to #profilesConfigurationPage', unscoped.length === 0,
    unscoped.length + ' not: ' + unscoped.slice(0, 4).map(r => r.selector).join(' | '));

// ── tokens, per Jellyfin theme ───────────────────────────────────────────────────────
function declsFor(selector) {
    const out = {};
    rules.filter(r => r.selector === selector).forEach(r => Object.keys(r.decls).forEach(k => { out[k] = r.decls[k]; }));
    return out;
}
function tokensFor(theme) {
    const t = Object.assign({}, declsFor(PAGE));
    if (theme !== 'dark') Object.assign(t, declsFor('html[data-theme="' + theme + '"] ' + PAGE));
    const res = {};
    Object.keys(t).forEach(k => { if (k.indexOf('--jpf-') === 0) res[k] = t[k]; });
    return res;
}
function resolve(tokens, name, seen) {
    let v = tokens[name];
    if (v === undefined) return null;
    const m = /^var\((--[\w-]+)(?:\s*,\s*(.+))?\)$/.exec(v);
    if (!m) return v;
    seen = seen || [];
    if (seen.indexOf(name) >= 0) return null;
    seen.push(name);
    const inner = resolve(tokens, m[1], seen);
    return inner === null ? (m[2] || null) : inner;
}

// What each Jellyfin theme sits on. Sources in the header.
const THEMES = {
    dark:    { grounds: { page: '#101010', paper: '#202020' }, text: 'rgba(255, 255, 255, 0.8)' },
    light:   { grounds: { page: '#f2f2f2', paper: '#e8e8e8' }, text: 'rgba(0, 0, 0, 0.87)' },
    appletv: { grounds: { page: '#d5e9f2', paper: '#ffffff' }, text: 'rgba(0, 0, 0, 0.87)' }
};

const NEED = ['--jpf-accent', '--jpf-accent-hover', '--jpf-accent-ink', '--jpf-ring', '--jpf-good', '--jpf-bad', '--jpf-bad-fill', '--jpf-warn',
    '--jpf-accent-tint', '--jpf-accent-line', '--jpf-good-tint', '--jpf-good-line', '--jpf-bad-tint', '--jpf-bad-line', '--jpf-warn-tint', '--jpf-warn-line'];

const measured = [];
Object.keys(THEMES).forEach(theme => {
    const tokens = tokensFor(theme);
    const col = {};
    NEED.forEach(n => {
        const raw = resolve(tokens, n);
        const c = raw === null ? null : parse(raw);
        ok(theme + ': ' + n + ' is defined as a plain colour', !!c, String(raw));
        col[n] = c;
    });
    if (NEED.some(n => !col[n])) return;
    const textColor = parse(THEMES[theme].text);

    Object.keys(THEMES[theme].grounds).forEach(gname => {
        const ground = parse(THEMES[theme].grounds[gname]);
        const at = theme + '/' + gname;
        // Text and controls: 4.5. Focus rings and a control's own boundary: 3.
        ['--jpf-accent', '--jpf-good', '--jpf-bad', '--jpf-warn'].forEach(n => {
            const r = ratio(col[n], ground);
            measured.push([theme, gname, n + ' text', r]);
            ok(at + ': ' + n + ' as text >= 4.5', r >= 4.5, r.toFixed(2));
        });
        ['--jpf-ring', '--jpf-accent', '--jpf-accent-line', '--jpf-good-line', '--jpf-bad-line', '--jpf-warn-line'].forEach(n => {
            // A line is laid over the ground at its own alpha; a ring is solid.
            const eff = over(col[n], ground);
            const need = /-line$/.test(n) ? 1 : 3; // the notices' outline is decoration, their words carry the meaning
            const r = ratio(eff, ground);
            if (need === 3) {
                measured.push([theme, gname, n + ' ring/edge', r]);
                ok(at + ': ' + n + ' as ring or edge >= 3', r >= 3, r.toFixed(2));
            }
        });
        // A button's fill must stand apart from the ground, its label from its fill.
        const fillR = ratio(col['--jpf-accent'], ground);
        ok(at + ': button fill against ground >= 3', fillR >= 3, fillR.toFixed(2));
        ['--jpf-accent', '--jpf-accent-hover'].forEach(n => {
            const r = ratio(col['--jpf-accent-ink'], col[n]);
            if (gname === 'page') measured.push([theme, '-', 'ink on ' + n, r]);
            ok(at + ': button label on ' + n + ' >= 4.5', r >= 4.5, r.toFixed(2));
        });
        // The filled badge (.jpf-badge-bad): white label on the fill, the fill against the ground.
        const white = parse('#ffffff');
        const rb = ratio(white, col['--jpf-bad-fill']);
        ok(at + ': white label on --jpf-bad-fill >= 4.5', rb >= 4.5, rb.toFixed(2));
        const fb = ratio(col['--jpf-bad-fill'], ground);
        ok(at + ': --jpf-bad-fill against ground >= 3', fb >= 3, fb.toFixed(2));
        // The notices: a status colour's words on its own tint, laid over the ground.
        [['--jpf-good', '--jpf-good-tint'], ['--jpf-bad', '--jpf-bad-tint'], ['--jpf-warn', '--jpf-warn-tint'], ['--jpf-accent', '--jpf-accent-tint']].forEach(p => {
            const bg = over(col[p[1]], ground);
            const r = ratio(col[p[0]], bg);
            ok(at + ': ' + p[0] + ' on ' + p[1] + ' >= 4.5', r >= 4.5, r.toFixed(2));
            const body = ratio(over(textColor, bg), bg);
            ok(at + ': page text on ' + p[1] + ' >= 4.5', body >= 4.5, body.toFixed(2));
        });
        // The inactive tab is the page's text at 0.55 opacity (the rule's own number).
        const op = tabOpacity();
        if (op !== null) {
            const tab = over({ r: textColor.r, g: textColor.g, b: textColor.b, a: textColor.a * op }, ground);
            const r = ratio(tab, ground);
            ok(at + ': inactive tab text >= 4.5', r >= 4.5, r.toFixed(2) + ' at opacity ' + op);
        }
    });

    // Apart from each other: accent and the three statuses, every pair.
    const fam = ['--jpf-accent', '--jpf-good', '--jpf-bad', '--jpf-warn'];
    for (let i = 0; i < fam.length; i++) for (let j = i + 1; j < fam.length; j++) {
        const d = deltaE(col[fam[i]], col[fam[j]]);
        ok(theme + ': ' + fam[i] + ' and ' + fam[j] + ' differ (CIE76 dE >= 20)', d >= 20, d.toFixed(1));
    }
});

function tabOpacity() {
    let op = null;
    rules.filter(r => r.selector === PAGE + ' .jpf-tab').forEach(r => { if (r.decls.opacity !== undefined) op = parseFloat(r.decls.opacity); });
    return op;
}

// ── the rules use the tokens ─────────────────────────────────────────────────────────
// Last rule at a selector wins, as it does when specificity ties. Only the page's own
// declarations; the theme-scoped blocks hold tokens and nothing else (checked below).
function decl(selector, prop) {
    let v = null;
    rules.filter(r => r.selector === PAGE + ' ' + selector).forEach(r => { if (r.decls[prop] !== undefined) v = r.decls[prop]; });
    return v;
}
function uses(selector, prop, token) {
    const v = decl(selector, prop);
    ok(selector + ' { ' + prop + ' } is ' + token, v === 'var(' + token + ')' || (v !== null && v.indexOf('var(' + token + ')') >= 0), String(v));
}
uses('.jpf-tab.is-active', 'border-bottom-color', '--jpf-accent');
uses('.jpf-tab.is-active', 'color', '--jpf-accent');
uses('.jpf-tab:focus-visible', 'outline', '--jpf-ring');
uses('.jpf-te-mode:focus-visible', 'outline', '--jpf-ring');
uses('.sectionTitle', 'color', '--jpf-accent');
uses('.button-submit', 'background', '--jpf-accent');
uses('.button-submit', 'color', '--jpf-accent-ink');
uses('.button-submit:hover', 'background', '--jpf-accent-hover');
uses('.button-submit:focus', 'background', '--jpf-accent-hover');
uses('.button-submit:focus-visible', 'outline', '--jpf-ring');
uses('a', 'color', '--jpf-accent');
uses('.button-link', 'color', '--jpf-accent');
uses('.inputLabelFocused', 'color', '--jpf-accent');
uses('.jpf-note-warn', 'background', '--jpf-warn-tint');
uses('.jpf-note-warn', 'color', '--jpf-warn');
uses('.jpf-note-error', 'background', '--jpf-bad-tint');
uses('.jpf-note-ok', 'background', '--jpf-good-tint');

// Secondary buttons: every is="emby-button" raised button that is not a primary
// (.button-submit), a link, or the red badge. They wore Jellyfin's grey .raised box,
// which the owner saw clash with the ember (1.9.0.4 eye test). One rule styles them all,
// so no button can be left grey by being missed.
const SEC = '.raised:not(.button-submit):not(.button-link):not(.jpf-badge-bad)';
uses(SEC, 'background', '--jpf-accent-tint');
uses(SEC, 'color', '--jpf-accent');
uses(SEC, 'border', '--jpf-accent-line');
uses(SEC + ':hover', 'background', '--jpf-accent-tint-hover');
uses(SEC + ':focus', 'background', '--jpf-accent-tint-hover');
uses(SEC + ':hover', 'color', '--jpf-accent-hover');
uses(SEC + ':focus', 'color', '--jpf-accent-hover');
uses(SEC + ':focus-visible', 'outline', '--jpf-ring');
// Its label, at rest and under the pointer, on every ground of every theme.
Object.keys(THEMES).forEach(theme => {
    const tokens = tokensFor(theme);
    // [label, fill]: at rest, and under the pointer or focus.
    [['--jpf-accent', '--jpf-accent-tint'], ['--jpf-accent-hover', '--jpf-accent-tint-hover']].forEach(p => {
        const raw = resolve(tokens, p[1]);
        const c = raw === null ? null : parse(raw);
        const lab = parse(resolve(tokens, p[0]) || '');
        ok(theme + ': ' + p[1] + ' is defined as a plain colour', !!c, String(raw));
        if (!c || !lab) return;
        Object.keys(THEMES[theme].grounds).forEach(g => {
            const bg = over(c, parse(THEMES[theme].grounds[g]));
            const r = ratio(lab, bg);
            ok(theme + '/' + g + ': secondary button ' + p[0] + ' on ' + p[1] + ' >= 4.5', r >= 4.5, r.toFixed(2));
        });
    });
});

// The theme-scoped blocks hold tokens and nothing else: a light-only rule would be a
// second place a colour is decided.
['light', 'appletv'].forEach(theme => {
    const d = declsFor('html[data-theme="' + theme + '"] ' + PAGE);
    ok(theme + ': its block exists', Object.keys(d).length > 0);
    const stray = Object.keys(d).filter(k => k.indexOf('--jpf-') !== 0);
    ok(theme + ': its block sets tokens only', stray.length === 0, stray.join(', '));
});

// ── no ember or status colour written outside the tokens ────────────────────────────
// A colour is chromatic if its channels are not within 12 of each other: white and black
// overlays, and greys, are neutral and stay literal.
function chromatic(c) { return Math.max(c.r, c.g, c.b) - Math.min(c.r, c.g, c.b) > 12; }
const literalRe = /#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b|rgba?\([^)]*\)/g;
const strays = [];
rules.forEach(r => {
    const isToken = r.selector === PAGE || /^html\[data-theme="[a-z]+"\] #profilesConfigurationPage$/.test(r.selector);
    Object.keys(r.decls).forEach(k => {
        if (isToken && k.indexOf('--jpf-') === 0) return;
        (r.decls[k].match(literalRe) || []).forEach(lit => {
            const c = parse(lit);
            if (c && chromatic(c)) strays.push(r.selector.replace(PAGE + ' ', '') + ' { ' + k + ': ' + lit + ' }');
        });
    });
});
ok('no ember or status colour is a literal outside the tokens', strays.length === 0, strays.length + ': ' + strays.slice(0, 5).join(' | '));

// Each token is named once at the top of the page's block, not in a second rule.
const named = {};
rules.forEach(r => {
    if (r.selector !== PAGE) return;
    Object.keys(r.decls).forEach(k => { if (k.indexOf('--jpf-') === 0) named[k] = (named[k] || 0) + 1; });
});
const twice = Object.keys(named).filter(k => named[k] > 1);
ok('each token is named once in the base block', twice.length === 0, twice.join(', '));
ok('no token falls back to Jellyfin\'s own --accent', css.indexOf('var(--accent') < 0);

// Old literals in the script half, written into markup the script builds.
const script = html.slice(styleClose);
['#ffc078', '#f59f00', '#e64980', '#00a4dc'].forEach(lit => {
    ok('script has no ' + lit, script.toLowerCase().indexOf(lit) < 0);
});

// ── the header and the mark ──────────────────────────────────────────────────────────
const top = html.slice(styleClose, html.indexOf('role="tablist"', styleClose));
const brand = /<[a-z]+[^>]*class="[^"]*\bjpf-brand\b[^"]*"[^>]*>([\s\S]*?)<\/div>/.exec(top);
ok('a header sits above the tabs', !!brand);
const mark = brand ? /<([a-z]+)[^>]*role="img"[^>]*>/.exec(brand[1]) : null;
ok('the header carries the mark as an image role', !!mark);
const altKey = mark ? /data-i18n-aria-label="(admin\.[\w.]+)"/.exec(mark[0]) : null;
ok('the mark\'s alt text is a data-i18n-aria-label admin key', !!altKey);
ok('the mark has an English aria-label', !!mark && /aria-label="[^"]+"/.test(mark[0]));
ok('the mark is the svg flame the client uses, in the current color', !!brand && /<svg[^>]*class="jpf-brand-mark"[^>]*fill="currentColor"/.test(brand[1]) && /viewBox="14 2 92 120"/.test(brand[1]));
// The product's name is a name, not a word: it stays as it is, marked so.
ok('the header\'s name is marked translate="no"', !!brand && /translate="no"[^>]*>Bonfire</.test(brand[1]));
const nameKey = null;
const i18nDir = L.i18nDir();
const langs = fs.readdirSync(i18nDir).filter(f => /\.json$/.test(f));
ok('there are language files to check', langs.length > 0);
[altKey, nameKey].forEach(k => {
    if (!k) return;
    langs.forEach(f => {
        const j = JSON.parse(fs.readFileSync(path.join(i18nDir, f), 'utf8'));
        ok(k[1] + ' is translated in ' + f, typeof j[k[1]] === 'string' && j[k[1]].length > 0);
    });
});
const brandCss = rules.filter(r => /\.jpf-brand/.test(r.selector));
ok('the header is styled, and small', brandCss.length > 0);
const sizes = [];
brandCss.forEach(r => { const fs2 = r.decls['font-size']; if (fs2) sizes.push(parseFloat(fs2)); });
ok('no header text is bigger than 1.5rem', sizes.every(s => s <= 1.5));

// ── report ───────────────────────────────────────────────────────────────────────────
if (process.env.DASHWARM_REPORT) {
    measured.forEach(m => console.log(m[0] + '/' + m[1] + '  ' + m[2] + '  ' + m[3].toFixed(2)));
}
finish();
