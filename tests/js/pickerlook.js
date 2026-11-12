/*
 * The picker as seen from a sofa: the focus frame, the line under a name, the lock badge.
 *
 * Three things were wrong with it on a television, and each is a number or a colour rather
 * than a missing rule, so each is asked as one:
 *
 *   - Focus. The focused tile's ring was the same width as the "signed in" ring, so the one
 *     tile the remote is on looked like the one already signed in. Focus now draws a thicker
 *     ring (the card has no outline: tvfocusring.js holds that).
 *   - "45 min left" / "Time's up" / "Signed in" were 0.75rem and 0.7rem: 20px at 1080p and
 *     15px at 720p, on a screen watched from three metres. And "Time's up" was the theme's
 *     warning colour, which on Jellyfin Light is mustard on grey at about 3:1.
 *   - The lock badge was 32px with a 14px padlock whatever the tile, which on a 280px
 *     television tile is a dot.
 *
 * The contrast is computed per theme, against each theme's own background, with the colour
 * the stylesheet actually resolves to; a rule that names a token the theme sets is read
 * through the theme file, not through a literal.
 *
 *   node tests/js/pickerlook.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const css = L.stylesOnly().replace(/\/\*[\s\S]*?\*\//g, '');
const rules = [];
{
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(css))) {
        m[1].split(',').map(s => s.trim()).filter(Boolean)
            .forEach(sel => rules.push({ sel, body: m[2], index: m.index }));
    }
}

/** The value of `prop` in the LAST rule whose selector is exactly `sel`, or null. */
function decl(sel, prop) {
    let found = null;
    for (const r of rules) {
        if (r.sel !== sel) continue;
        const re = new RegExp('(?:^|[;\\s])' + prop + '\\s*:\\s*([^;]+)', 'g');
        let m;
        while ((m = re.exec(r.body))) found = m[1].trim();
    }
    return found;
}
const px = v => { const m = /^(-?[\d.]+)px$/.exec(v || ''); return m ? parseFloat(m[1]) : null; };
const rem = v => { const m = /^(-?[\d.]+)rem$/.exec(v || ''); return m ? parseFloat(m[1]) : null; };

// ── Colour arithmetic, WCAG 2 (the same as themes.js) ───────────────────────────
function parseColour(v) {
    v = String(v || '').trim().toLowerCase();
    let m = /^#([0-9a-f]{6})([0-9a-f]{2})?$/.exec(v);
    if (!m) return null;
    return { r: parseInt(m[1].slice(0, 2), 16), g: parseInt(m[1].slice(2, 4), 16), b: parseInt(m[1].slice(4, 6), 16),
        a: m[2] ? parseInt(m[2], 16) / 255 : 1 };
}
const over = (f, b) => ({ r: f.r * f.a + b.r * (1 - f.a), g: f.g * f.a + b.g * (1 - f.a), b: f.b * f.a + b.b * (1 - f.a), a: 1 });
function lum(c) {
    const ch = x => { x /= 255; return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); };
    return 0.2126 * ch(c.r) + 0.7152 * ch(c.g) + 0.0722 * ch(c.b);
}
const contrast = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);

// ── 1. The line under a name is readable on every theme ─────────────────────────
console.log('\n── "Time\'s up" and "45 min left" can be read on every theme ─────');
{
    // Which theme colour each token the sheet might name stands for.
    const TOKENS = { '--jpf-warn': 'warning', '--jpf-text-1': 'text1', '--jpf-text-2': 'text2',
        '--jpf-name-color': 'name', '--jpf-accent': 'accent' };
    const dir = L.themesDir();
    const themes = fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')));
    for (const sel of ['.jpf-profile-viewing.is-out', '.jpf-profile-viewing']) {
        const value = decl(sel, 'color');
        const op = decl(sel, 'opacity');
        // .jpf-profile-viewing sets no colour: it inherits the name's, which is a theme token.
        const token = value ? (/^var\((--[\w-]+)\)$/.exec(value) || [])[1] : '--jpf-name-color';
        const key = TOKENS[token];
        ok(sel + ' takes a colour the theme sets (' + (value || 'inherited') + ')', !!key);
        if (!key) continue;
        const opacity = op === null ? 1 : parseFloat(op);
        for (const t of themes) {
            const bg = parseColour(t.background.fill);
            const fg = parseColour(t.colors[key]);
            if (!bg || !fg) { ok(t.id + ': ' + sel + ' has colours to compare', false); continue; }
            const seen = over({ r: fg.r, g: fg.g, b: fg.b, a: fg.a * opacity }, bg);
            const ratio = contrast(seen, bg);
            ok(t.id + ': ' + sel + ' on its background, ' + ratio.toFixed(2) + ':1 (4.5 needed)', ratio >= 4.5);
        }
    }
}

// ── 2. Television sizes ─────────────────────────────────────────────────────────
console.log('\n── On a television those lines are as big as they need to be ────');
for (const sel of ['.jpf-tv .jpf-profile-viewing', '.jpf-tv .jpf-profile-current-badge']) {
    const size = rem(decl(sel, 'font-size'));
    ok(sel + ' is at least 1rem (20px at 720p, 27px at 1080p) — got ' + size + 'rem', size !== null && size >= 1);
}
{
    const w = px(decl('.jpf-tv .jpf-profile-lock-indicator', 'width'));
    const h = px(decl('.jpf-tv .jpf-profile-lock-indicator', 'height'));
    ok('the lock badge is at least 48px on a television — got ' + w + 'x' + h, w !== null && h !== null && w >= 48 && h >= 48);
    const icon = px(decl('.jpf-tv .jpf-profile-lock-indicator svg', 'width'));
    ok('and its padlock grows with it (at least 22px) — got ' + icon, icon !== null && icon >= 22);
}

// ── 3. The focus frame ──────────────────────────────────────────────────────────
console.log('\n── Focus reads as focus, not as "signed in" ─────────────────────');
{
    // The signed-in ring is the base border. Focus on a television must be thicker.
    const base = decl('.jpf-profile-avatar', 'border');
    const focus = decl('.jpf-tv .jpf-profile-card:focus .jpf-profile-avatar', 'border-width');
    ok('the signed-in ring is the theme\'s ring width (' + base + ')', /var\(--jpf-ring-w\)/.test(base || ''));
    ok('a focused tile on a television has a thicker border-width (' + focus + ')',
        !!focus && /calc\(var\(--jpf-ring-w\)\s*\*\s*([2-9])\)/.test(focus));
}

// ── 5. No crown ─────────────────────────────────────────────────────────────────
console.log('\n── The main account has no crown ────────────────────────────────');
{
    const js = fs.readFileSync(process.argv[2] || L.profilesPath(), 'utf8');
    ok('the script draws no crown', !/profile-crown/.test(js));
    ok('the stylesheet has no crown rule or keyframes', !/profile-crown|crownFloat/.test(L.stylesOnly()));
}

// ── 4. The markup ───────────────────────────────────────────────────────────────
console.log('\n── A profile out of time is dimmed ──────────────────────────────');
{
    const dim = decl('.jpf-profile-card.is-out:not(:focus) .jpf-profile-avatar', 'opacity');
    ok('an out-of-time tile\'s avatar is dimmed (' + dim + ')', dim !== null && parseFloat(dim) < 1 && parseFloat(dim) >= 0.4);
    const js = fs.readFileSync(process.argv[2] || L.profilesPath(), 'utf8');
    const from = js.indexOf('const renderCard = (p) =>');
    const card = js.slice(from, js.indexOf('jpf-profile-avatar-container', from));
    ok('the card itself is given the class, from the flag the line uses', /is-out/.test(card) && /viewing/.test(card));
}

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
if (fails.length) process.exit(1);
