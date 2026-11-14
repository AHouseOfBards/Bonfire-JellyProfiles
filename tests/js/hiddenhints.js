/*
 * A hint that is hidden stays hidden. `.jpf-form-hint` carries `display: block !important`
 * (added in 1.0.35 so a theme's absolute or flex rules cannot pull a hint out of the flow),
 * and that beat the inline `display: none` two hints used to hide themselves: "No days
 * checked, so no time is allowed." showed on every profile form, and the library artwork
 * explainer showed with its switch off.
 *
 * Judged from the cascade in a real Chrome (getComputedStyle), not from how the rule or
 * the script is spelled:
 *
 *   - Every hint the form marks hidden (hidden attribute or inline display:none) computes
 *     to display:none, enumerated over every hint on the Create and Edit forms.
 *   - The no-days hint follows the form: off with "Only at these times" unticked, off with
 *     Mon-Fri ticked, on with the box ticked and every day cleared, off when one day is ticked.
 *   - The artwork explainer follows its switch.
 *   - A shown hint is still display:block, position:relative.
 *
 * Needs Chrome; skips (exit 0) without it.
 *
 *     node tests/js/hiddenhints.js [/path/to/old/profiles.js]
 */
'use strict';

const fs = require('fs');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// Every hint on the form that the page itself marks hidden, with what the cascade made of it.
const MARKED = `(function () {
  return Array.prototype.map.call(document.querySelectorAll('.jpf-form-hint'), function (el) {
    return { id: el.id || el.className, marked: el.hidden || el.style.display === 'none',
      shown: getComputedStyle(el).display };
  });
})()`;
const SHOWN = id => `(function () {
  var el = document.getElementById(${JSON.stringify(id)});
  if (!el) return null;
  var s = getComputedStyle(el);
  return { display: s.display, position: s.position };
})()`;
// Drives the form's own checkboxes and fires the change a person's click would.
const SET = (id, on) => `(function () {
  var el = document.getElementById(${JSON.stringify(id)});
  if (!el) return false;
  if (el.checked !== ${on}) { el.checked = ${on}; el.dispatchEvent(new Event('change', { bubbles: true })); }
  return true;
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    const url = scene => 'http://127.0.0.1:' + server.port + '/?theme=bonfire&scene=' + scene + '&device=desktop';
    const nodays = async (where, expectShown) => {
        const s = await c.evaluate(SHOWN('viewing-nodays'));
        ok(where + ': no-days hint ' + (expectShown ? 'shows' : 'is hidden'), !!s && (s.display !== 'none') === expectShown,
            s ? 'display ' + s.display : 'not on the page');
    };
    try {
        for (const scene of ['add', 'edit']) {
            const r = await c.open(url(scene), 'desktop');
            if (r.error) { ok(scene + ': the screen draws', false, r.error); continue; }
            const all = await c.evaluate(MARKED);
            ok(scene + ': the form has hints marked hidden', all.some(h => h.marked), 'none found, so nothing was checked');
            all.filter(h => h.marked).forEach(h => ok(scene + ': hidden hint "' + h.id + '" is not displayed', h.shown === 'none', 'display ' + h.shown));

            // The time-of-day rules.
            if (scene === 'add') {
                await nodays(scene + ', "Only at these times" off', false);
            }
            ok(scene + ': hours box found', await c.evaluate(SET('viewing-hours', true)));
            for (const d of [1, 2, 3, 4, 5, 6, 0]) await c.evaluate(SET('viewing-day-' + d, false));
            await nodays(scene + ', hours on, no day ticked', true);
            const shown = await c.evaluate(SHOWN('viewing-nodays'));
            ok(scene + ': a shown hint is still in the flow (block, relative)', !!shown && shown.display === 'block' && shown.position === 'relative',
                shown ? shown.display + ' / ' + shown.position : 'missing');
            for (const d of [1, 2, 3, 4, 5]) await c.evaluate(SET('viewing-day-' + d, true));
            await nodays(scene + ', hours on, Mon-Fri ticked', false);
            await c.evaluate(SET('viewing-hours', false));
            await nodays(scene + ', hours off again', false);

            // The artwork explainer follows its switch (Edit only).
            if (scene === 'edit') {
                const before = await c.evaluate(SHOWN('edit-libart-hint'));
                ok('edit: artwork explainer is hidden with its switch off', !!before && before.display === 'none', before ? 'display ' + before.display : 'missing');
                await c.evaluate(SET('edit-libart-toggle', true));
                const on = await c.evaluate(SHOWN('edit-libart-hint'));
                ok('edit: artwork explainer shows with its switch on', !!on && on.display === 'block' && on.position === 'relative', on ? on.display + ' / ' + on.position : 'missing');
                await c.evaluate(SET('edit-libart-toggle', false));
                const off = await c.evaluate(SHOWN('edit-libart-hint'));
                ok('edit: artwork explainer hides again', !!off && off.display === 'none', off ? 'display ' + off.display : 'missing');
            }
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
