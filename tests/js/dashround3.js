/*
 * Three dashboard fixes from the round 3 review, each run as code:
 *
 *  - themePictureExists: only a 404 means a theme's picture is missing. A 429 or a 5xx read
 *    as missing, and loading a theme file then dropped its background (r3-09-03).
 *  - bindControlHeights: a change of text alone does not re-measure every button. The theme
 *    editor rewrites the preview's name on each keystroke and slider frame, and each one
 *    paid for a pass over every button on the page (r3-09-01).
 *  - The editor's drag step ends on touchcancel and on the window losing focus, not only on
 *    touchend and mouseup, or later edits joined that step in the undo history (r3-09-04).
 *
 *   node tests/js/dashround3.js [path-to-profilesDashboard.html]
 */
'use strict';
const fs = require('fs');
const path = require('path');
const file = process.argv[2] || path.join(__dirname, '..', '..', 'Web', 'profilesDashboard.html');
const src = fs.readFileSync(file, 'utf8');

function fn(name) {
    const at = src.indexOf('function ' + name + '(');
    if (at < 0) throw new Error('no function ' + name);
    const open = src.indexOf('{', src.indexOf(')', at));
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(at, i + 1);
    }
    throw new Error('unterminated ' + name);
}

let pass = 0, bad = 0;
function check(ok, msg) { if (ok) pass++; else { bad++; console.log('FAIL ' + msg); } }

(async function () {
    // ── themePictureExists ──────────────────────────────────────────────
    {
        const ApiClient = { getUrl: p => p }; // eslint-disable-line no-unused-vars
        let status = 200;
        const fetch = () => Promise.resolve({ ok: status >= 200 && status < 300, status }); // eslint-disable-line no-unused-vars
        const exists = eval('(' + fn('themePictureExists') + ')');
        for (const [s, want] of [[200, true], [404, false], [429, true], [500, true], [503, true]]) {
            status = s;
            check(await exists('backgrounds', 'a.png') === want, 'HTTP ' + s + ' reads as ' + (want ? 'there' : 'missing'));
        }
    }

    // ── bindControlHeights ──────────────────────────────────────────────
    {
        let observerCb = null, frames = 0, passes = 0;
        const window = { // eslint-disable-line no-unused-vars
            MutationObserver: function (cb) { observerCb = cb; this.observe = () => {}; },
            requestAnimationFrame: f => { frames++; f(); },
            addEventListener: () => {}
        };
        const MutationObserver = window.MutationObserver; // eslint-disable-line no-unused-vars
        const matchControlHeights = () => { passes++; }; // eslint-disable-line no-unused-vars
        const bind = eval('(' + fn('bindControlHeights') + ')');
        const page = {};
        bind(page);
        const start = passes;
        const text = { nodeType: 3 }, el = { nodeType: 1 };
        observerCb([{ type: 'childList', addedNodes: [text], removedNodes: [text] }]);
        check(passes === start, 'a text change alone does not re-measure (' + (passes - start) + ' passes)');
        observerCb([{ type: 'childList', addedNodes: [el], removedNodes: [] }]);
        check(passes === start + 1, 'an element added does (' + (passes - start) + ')');
        observerCb([{ type: 'attributes', attributeName: 'hidden', addedNodes: [], removedNodes: [] }]);
        check(passes === start + 2, 'and so does one shown or hidden (' + (passes - start) + ')');
        observerCb([{ type: 'childList', addedNodes: [text], removedNodes: [] }, { type: 'childList', addedNodes: [], removedNodes: [el] }]);
        check(passes === start + 3, 'a batch with one element change in it does (' + (passes - start) + ')');
    }

    // ── the drag step ───────────────────────────────────────────────────
    // Where the editor binds its listeners: every way a drag can end must close the step.
    {
        const at = src.indexOf("editor.addEventListener('mousedown', down);");
        const block = at < 0 ? '' : src.slice(at, src.indexOf('#teUndo', at));
        for (const [target, ev] of [['document', 'mouseup'], ['document', 'touchend'], ['document', 'touchcancel'], ['window', 'blur']]) {
            check(new RegExp(target + "\\.addEventListener\\('" + ev + "', up\\)").test(block), 'a drag ends on ' + target + ' ' + ev);
        }
    }

    // ── matchControlHeights (r3-09-02) ──────────────────────────────────
    // A fixed height clipped a translated label that wrapped, and a button that stopped being
    // beside a control kept the height it was given. It is a minimum now, and taken back.
    {
        const ROW_CONTROLS = 'select'; // eslint-disable-line no-unused-vars
        const match = eval('(' + fn('matchControlHeights') + ')');
        let besideNow = true;
        const button = { style: {}, classList: { contains: () => false }, parentNode: null,
            getBoundingClientRect: () => ({ left: 210, right: 300, top: 0, bottom: 30, width: 90, height: 30 }) };
        const control = { closest: () => null,
            getBoundingClientRect: () => ({ left: 0, right: 200, top: 0, bottom: 40, width: 200, height: 40 }) };
        const row = { querySelectorAll: () => (besideNow ? [control] : []), parentNode: null };
        button.parentNode = row;
        const page = { querySelectorAll: () => [button] };
        match(page);
        check(button.style.minHeight === '40px', 'a button beside a control gets its height as a minimum (' + button.style.minHeight + ')');
        check(!button.style.height, 'and not as a fixed height, which clips a label that wraps (' + button.style.height + ')');
        besideNow = false;
        match(page);
        check(!button.style.minHeight, 'a button no longer beside a control loses it (' + button.style.minHeight + ')');
    }

    // ── the bug report's emergency-code line (r3-09-05) ─────────────────
    // Read before the status had loaded, it said "not set" for a code that is set.
    {
        const line = (/'Emergency code: ' \+ ([^\n]*)/.exec(src) || [, ''])[1];
        check(/_panicConfigured\s*===?\s*undefined|typeof page\._panicConfigured/.test(line) && /unknown/.test(line),
              'the report says unknown when the emergency code status has not loaded (' + line.trim() + ')');
    }

    console.log(bad ? bad + ' failed' : 'ok: dashboard round 3 fixes (' + pass + ' passed, 0 failed)');
    process.exit(bad ? 1 : 0);
})();
