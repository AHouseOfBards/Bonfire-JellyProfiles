/*
 * Second-review fixes to the client script and stylesheet (r2-31, r2-32, r2-39, r2-40).
 *
 *   node tests/js/r3a.js [old/profiles.js [old/styles.css]]
 *
 * Both arguments are optional so this can be pointed at the build carrying the bugs: it
 * must fail there before it may pass here.
 *
 * The CSS half resolves the cascade for a named element (specificity, then source order)
 * instead of looking for a string, because a rule that is present and outranked is the
 * defect class this repository has shipped three times.
 */
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();
const CSS = process.argv[3]
    ? fs.readFileSync(process.argv[3], 'utf8')
    : fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'styles.css'), 'utf8');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

// ── The stylesheet cascade ───────────────────────────────────────────────────────
const bare = CSS.replace(/\/\*[\s\S]*?\*\//g, '');
const rules = [];
{
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m;
    while ((m = re.exec(bare))) {
        m[1].split(',').map(s => s.trim()).filter(Boolean)
            .forEach(sel => rules.push({ sel, body: m[2], index: m.index }));
    }
}

function specificity(sel) {
    const ids = (sel.match(/#[\w-]+/g) || []).length;
    const cls = (sel.match(/\.[\w-]+|\[[^\]]*\]|:(?!not\b)[\w-]+/g) || []).length;
    return ids * 10000 + cls * 100;
}

/* A compound ("input#x.y:focus:not(.z)") against one element of a chain. */
function compoundMatches(compound, el) {
    const nots = [];
    let rest = compound.replace(/:not\(([^)]*)\)/g, (a, inner) => { nots.push(inner); return ''; });
    const tag = /^[a-z][\w-]*/i.exec(rest);
    if (tag && rest !== '*' && tag[0].toLowerCase() !== el.tag) return false;
    rest = rest.replace(/^[a-z][\w-]*/i, '').replace(/^\*/, '');
    const parts = rest.match(/#[\w-]+|\.[\w-]+|:[\w-]+|\[[^\]]*\]/g) || [];
    if (parts.join('') !== rest) return false;
    for (const p of parts) {
        if (p[0] === '#') { if (el.id !== p.slice(1)) return false; }
        else if (p[0] === '.') { if (el.classes.indexOf(p.slice(1)) === -1) return false; }
        // Keyboard focus is what the ring is for, so a focused element is also :focus-visible.
        else if (p === ':focus' || p === ':focus-visible') { if (!el.focus) return false; }
        else if (p[0] === '[') { /* an attribute test: assumed to match, the harsher reading */ }
        else return false;
    }
    for (const n of nots) {
        if (compoundMatches(n, el)) return false;
    }
    return true;
}

/* Descendant and child combinators both read as "an ancestor" here. */
function selectorMatches(sel, chain) {
    const steps = sel.replace(/\s*>\s*/g, ' ').split(/\s+/);
    let at = chain.length - 1;
    if (!compoundMatches(steps[steps.length - 1], chain[at])) return false;
    at--;
    for (let s = steps.length - 2; s >= 0; s--) {
        while (at >= 0 && !compoundMatches(steps[s], chain[at])) at--;
        if (at < 0) return false;
        at--;
    }
    return true;
}

function outlineOf(chain) {
    let best = null;
    for (const r of rules) {
        const m = /(^|[;\s])outline\s*:\s*([^;]+)/.exec(r.body);
        if (!m || !selectorMatches(r.sel, chain)) continue;
        const sp = specificity(r.sel);
        if (!best || sp > best.sp || (sp === best.sp && r.index > best.index)) best = { sp, index: r.index, value: m[2].trim(), sel: r.sel };
    }
    return best;
}

const el = (tag, classes, id, focus) => ({ tag, classes: classes ? classes.split(' ') : [], id: id || '', focus: !!focus });
function chainFor(tv, ...rest) {
    return [el('html', tv ? 'jpf-tv' : ''), el('body', ''), el('div', '', 'profiles-gate-overlay'),
        el('div', 'profiles-modal-content')].concat(rest);
}
const describe = o => o ? o.value + ' from ' + o.sel : 'nothing';

console.log('\n── r2-40-01: the onboarding fields on a television ─────────────────');
['onboarding-email', 'onboarding-email-code', 'onboarding-profile-name'].forEach(id => {
    const o = outlineOf(chainFor(true, el('div', 'jpf-create-profile-container jpf-onboarding'),
        el('div', 'bonfire-form-group'), el('input', '', id, true)));
    ok('#' + id + ' focused on a television gets the 4px ring (' + describe(o) + ')', !!o && /^4px solid/.test(o.value));
});

console.log('\n── r2-40-02: the tag chip remove button ────────────────────────────');
[['in a profile section', true], ['outside one', false]].forEach(([where, inSection]) => {
    [false, true].forEach(tv => {
        const middle = (inSection ? [el('div', 'jpf-profile-section')] : []).concat(
            [el('div', 'jpf-tag-editor'), el('span', 'jpf-tag-chip'), el('button', 'jpf-tag-chip-remove', '', true)]);
        const o = outlineOf(chainFor(tv, ...middle));
        const want = tv ? /^4px solid/ : /^2px solid/;
        ok('focused ' + where + (tv ? ' on a television' : '') + ' draws a ring, not none (' + describe(o) + ')',
            !!o && want.test(o.value));
    });
});

console.log('\n── r2-39-01: the gate hides jellyfin-web loaders, not its own ──────');
{
    const own = new Set();
    (SRC.match(/class="[^"]*"/g) || []).forEach(c => c.slice(7, -1).split(/\s+/).forEach(n => {
        if (/loader|spinner/.test(n)) own.add(n);
    }));
    ok('Bonfire has a loader/spinner class to protect', own.size > 0, [...own].join(','));
    const hiding = rules.filter(r => /^body\.profiles-no-scroll\s+\[(class|id)\*="(loader|spinner)"\]/.test(r.sel));
    ok('the wildcard hiding rules are still there', hiding.length === 4, String(hiding.length));
    own.forEach(name => {
        hiding.filter(r => /^body\.profiles-no-scroll\s+\[class\*=/.test(r.sel)).forEach(r => {
            const token = /\*="([^"]+)"/.exec(r.sel)[1];
            if (name.indexOf(token) === -1) return;
            const chain = chainFor(true, el('div', name));
            chain[0] = el('html', ''); chain[1] = el('body', 'profiles-no-scroll');
            ok('.' + name + ' is not caught by ' + r.sel, !selectorMatches(r.sel, chain));
        });
    });
}

console.log('\n── r2-40-04: the no-flex-gap fallback for a wrapping row ───────────');
{
    const wraps = /flex-wrap:\s*wrap/.test((rules.find(r => r.sel === '.jpf-actions') || { body: '' }).body);
    ok('.jpf-actions wraps (the premise)', wraps);
    const leading = rules.filter(r => /^\.jpf-no-flex-gap\s+\.jpf-actions\s*>\s*\*\s*\+\s*\*$/.test(r.sel) && /margin-left/.test(r.body));
    ok('no leading margin-left on the second and later items of a wrapping row', leading.length === 0);
    const all = rules.filter(r => /^\.jpf-no-flex-gap\s+\.jpf-actions\s*>\s*\*$/.test(r.sel));
    ok('every item has a bottom margin standing in for the row gap',
        all.some(r => /margin\s*:\s*0\s+\d+px\s+\d+px/.test(r.body)));
}

// ── The script ───────────────────────────────────────────────────────────────────
function before(marker, take) {
    const at = SRC.indexOf(marker);
    return at < 0 ? '' : SRC.slice(Math.max(0, at - take), at);
}

// r2-31-02 was wrong: both handlers strip spaces before the six-digit check, and the
// server takes "123 456" (tests/cs/email). maxlength 6 cut a pasted "123 456" to "123 45".
console.log('\n── r2-31-02: a code pasted as "123 456" fits both code fields ──────');
['onboarding-email-code', 'account-email-code'].forEach(id => {
    const m = new RegExp('id="' + id + '" maxlength="(\\d+)" ').exec(SRC);
    ok(id + ' has maxlength 7, room for one space (got ' + (m && m[1]) + ')', !!m && m[1] === '7');
});
ok('the onboarding handler strips spaces before the six-digit check',
    /const code = input\.value\.replace\(\/\\s\/g, ''\);\s*if \(!\/\^\\d\{6\}\$\/\.test\(code\)\)/.test(SRC));
ok('the account handler strips spaces before the six-digit check',
    /const typed = codeInput\.value\.replace\(\/\\s\/g, ''\);\s*if \(!\/\^\\d\{6\}\$\/\.test\(typed\)\)/.test(SRC));

console.log('\n── r2-32: header code comments and arms ────────────────────────────');
{
    const lead = before('        _findHeaderContainer: function', 900);
    ok('the container doc block sits on _findHeaderContainer', /The container the floating switcher button is placed in/.test(lead));
    const lead2 = before('        _isLaidOut: function', 900);
    ok('and no longer on _isLaidOut', !/The container the floating switcher button is placed in/.test(lead2));
    const b = SRC.indexOf('// Strategy B:'), k = SRC.indexOf('const knownBtns');
    ok('the Strategy B comment runs straight into its knownBtns query',
        b > 0 && k > b && !/document\.querySelector|Strategy A2/.test(SRC.slice(b, k)));
    ok('the geometric search has one condition, not two identical arms',
        !/else if \(this\._fallbackPosDirty\) \{\s*this\._geoSearchedAt/.test(SRC));
}

// ── Behaviour: the onboarding steps and withMainPin ──────────────────────────────
(async () => {
    console.log('\n── r2-31-01: busy is set inside the main PIN callback ──────────────');
    const env = D.loadPlugin(SRC);
    const { PP, content, settle } = env;
    PP.beginNavigation = () => 1;
    PP.navIsCurrent = () => true;
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    PP._focusFirstOnTv = () => {};
    PP.currentProfiles = [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true }];
    PP._onboarding = { steps: [{ id: 'email' }], index: 0 };
    PP.showOnboardingStep = () => {};
    const id = sel => content.querySelector(sel);

    let asked = 0;
    // The prompt is shown and dismissed: the callback never runs.
    PP.withMainPin = function () { asked++; };

    const addr = { next: () => {}, skip: () => {} };
    PP._onboardingEmail(addr.next, addr.skip);
    if (!id('#onboarding-email')) {
        ok('the email step draws', false);
    } else {
        id('#onboarding-email').value = 'a@b.co';
        id('#onboarding-next').click();
        id('#onboarding-next').click();
        ok('email address: a dismissed PIN prompt leaves Next working (asked ' + asked + ' times)', asked === 2);
    }

    asked = 0;
    PP._onboardingEmail(addr.next, addr.skip, 'a@b.co');
    id('#onboarding-email-code').value = '123456';
    id('#onboarding-next').click();
    id('#onboarding-next').click();
    ok('email code: a dismissed PIN prompt leaves Next working (asked ' + asked + ' times)', asked === 2);

    asked = 0;
    PP._onboardingProfile(addr.next, addr.skip);
    id('#onboarding-profile-name').value = 'Kids';
    id('#onboarding-next').click();
    id('#onboarding-next').click();
    ok('first profile: a dismissed PIN prompt leaves Create working (asked ' + asked + ' times)', asked === 2);

    // And the guard still stops a double click once the callback has run.
    let ran = 0, runCb = null;
    PP.withMainPin = function (p, cb) { runCb = cb; };
    PP._emailCall = function () { ran++; return new Promise(() => {}); };
    PP._onboardingEmail(addr.next, addr.skip, 'a@b.co');
    id('#onboarding-email-code').value = '123456';
    id('#onboarding-next').click();
    runCb(); runCb();
    ok('once past the PIN, a second run of the callback does not send twice (sent ' + ran + ')', ran === 1);

    console.log('\n── r2-31-01: withMainPin when serverInfo rejects ───────────────────');
    const env2 = D.loadPlugin(SRC);
    const P2 = env2.PP;
    P2.currentProfiles = [{ profileUserId: 'main', isMaster: true, isBonfire: false, hasPin: false }];
    P2.serverInfo = () => Promise.reject(new Error('down'));
    let called = 0;
    let unhandled = 0;
    const onUnhandled = () => { unhandled++; };
    process.on('unhandledRejection', onUnhandled);
    P2.withMainPin(P2.currentProfiles, () => { called++; });
    await env2.settle();
    await new Promise(r => setImmediate(r));
    process.removeListener('unhandledRejection', onUnhandled);
    ok('the action still runs (the server enforces the PIN) and nothing is left unhandled',
        called === 1 && unhandled === 0, 'called ' + called + ', unhandled ' + unhandled);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('  FAIL  threw: ' + (e && e.stack || e)); process.exit(1); });
