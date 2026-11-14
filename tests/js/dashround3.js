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

    console.log(bad ? bad + ' failed' : 'ok: dashboard round 3 fixes (' + pass + ' passed, 0 failed)');
    process.exit(bad ? 1 : 0);
})();
