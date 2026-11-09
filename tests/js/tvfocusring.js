/*
 * R-0004: on a television the focused profile card drew a 4px rectangular outline
 * round a round avatar. Chromium 53 (webOS 4.5) draws an outline as a rectangle and
 * ignores border-radius, so the card's box, name and all, was boxed in the accent
 * colour while the avatar underneath already carried its own ring.
 *
 * Asks what the cascade settles on for a focused .profile-card under .jpf-tv, rather
 * than whether some string is present: a rule that is there and outranked, or one that
 * is gone while a broader rule still matches, are both a different answer from the
 * one the page shows.
 *
 *   node tests/js/tvfocusring.js                 # the working tree
 *   node tests/js/tvfocusring.js path/to/old.js  # (the stylesheet is read from the tree)
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
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

/* (ids, classes + pseudo-classes + attributes, elements) as one comparable number. */
function specificity(sel) {
    const ids = (sel.match(/#[\w-]+/g) || []).length;
    const cls = (sel.match(/\.[\w-]+|:[\w-]+(?!\()|\[[^\]]*\]/g) || []).length;
    return ids * 10000 + cls * 100;
}

/*
 * The elements this is asked about are matched by their compound steps. A selector
 * applies to "a focused card under .jpf-tv" if every step of it is satisfied by one of
 * the ancestors .jpf-tv / .profiles-grid-ish wrappers or by the card itself, and its
 * last step is the card. Selectors naming a descendant of the card (".profile-card
 * :focus .x") end on something else and are excluded by the last-step test.
 */
const CARD_STEPS = new Set(['.jpf-tv', '.profile-card', '.profile-card:focus', ':focus', '.focusable']);

function appliesToFocusedCardOnTv(sel) {
    const steps = sel.split(/\s+/);
    const last = steps[steps.length - 1];
    if (!CARD_STEPS.has(last) && !/^\.profile-card(:focus)?$/.test(last)) return false;
    // `:focus`/`:focus-visible` on a bare last step only matches a card when it follows
    // an ancestor that can contain one; the cards are never inside .profile-section.
    if (/\.profile-section/.test(sel)) return false;
    return steps.every(s => CARD_STEPS.has(s) || s === '.jpf-tv');
}

function resolved(prop) {
    let best = null;
    for (const r of rules) {
        if (!appliesToFocusedCardOnTv(r.sel)) continue;
        const m = /(^|[;\s])outline\s*:\s*([^;]+)/.exec(r.body);
        if (!m) continue;
        const sp = specificity(r.sel);
        if (!best || sp > best.sp || (sp === best.sp && r.index > best.index)) {
            best = { sp, index: r.index, value: m[2].trim(), sel: r.sel };
        }
    }
    return best;
}

const outline = resolved('outline');
ok('the cascade settles an outline for the focused card at all', !!outline);
ok('a focused card on a television draws no outline (got "'
    + (outline ? outline.value + '" from ' + outline.sel : 'nothing') + ')',
    !!outline && /^(none|0)\b/.test(outline.value));

/* ...and focus must still be findable some other way: the avatar ring. */
function setsOn(sel, prop) {
    return rules.some(r => r.sel === sel && new RegExp('(^|[;\\s])' + prop + '\\s*:').test(r.body));
}
ok('focus is still drawn on the avatar: border-color',
    setsOn('.profile-card:focus .profile-avatar', 'border-color'));
ok('focus is still drawn on the avatar: scale',
    setsOn('.profile-card:focus .profile-avatar', 'transform'));
ok('focus-within (Chromium 53 cannot share a list with it) draws it too',
    setsOn('.profile-card:focus-within .profile-avatar', 'border-color'));

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
if (fails.length) process.exit(1);
