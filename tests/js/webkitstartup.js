/*
 * The client script, started in WebKit (Safari, the iPhone and iPad app), at a phone and a
 * tablet size: it loads with no uncaught error and no console error, injects its
 * stylesheet, and draws the profile screen.
 *
 * The same signals inject.test.js uses, in the engine that has never run them. The defect
 * this is red against is 1.5.2: valid JavaScript that threw on the first call, so there was
 * no gate and no message. Point argv[2] at a copy with such a call to see it fail.
 *
 *     node tests/js/webkitstartup.js [path/to/profiles.js]
 *
 * Says SKIP and exits 0 when Playwright's WebKit is not installed (see _webkit.js).
 */
'use strict';

const W = require('./_webkit');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

(async () => {
    const browser = await W.launch();
    const src = W.sources();
    const srv = await W.serve(src.js, src.cssPath);
    try {
        for (const device of ['phone', 'tablet']) {
            const s = await W.openScene(browser, srv, 'picker', device);
            const tag = device + ': ';
            const state = await s.page.evaluate(() => {
                const sheet = document.getElementById('jpf-styles');
                const overlay = document.getElementById('profiles-gate-overlay');
                const r = overlay && overlay.getBoundingClientRect();
                return {
                    error: window.__error, loaded: !!window.__bonfire,
                    sheet: !!sheet, rules: sheet && sheet.sheet ? sheet.sheet.cssRules.length : 0,
                    overlay: !!overlay, overlayW: r ? r.width : 0, overlayH: r ? r.height : 0,
                    shown: overlay ? getComputedStyle(overlay).visibility + '/' + getComputedStyle(overlay).display : '',
                    cards: document.querySelectorAll('.jpf-profile-card').length,
                    misses: window.__misses
                };
            });
            ok(tag + 'the script loaded', state.loaded);
            ok(tag + 'startup threw nothing', !state.error, state.error);
            ok(tag + 'no uncaught error or console error', s.errors.length === 0, s.errors.join(' | '));
            ok(tag + 'the stylesheet was injected and parsed', state.sheet && state.rules > 100, 'rules: ' + state.rules);
            ok(tag + 'the profile screen is drawn and fills the viewport',
                state.overlay && /^visible\//.test(state.shown)
                    && Math.round(state.overlayW) === W.DEVICES[device].width && Math.round(state.overlayH) === W.DEVICES[device].height,
                JSON.stringify(state));
            ok(tag + 'the four fixture profiles are on it', state.cards >= 4, 'cards: ' + state.cards);
            await s.close();
        }
    } finally {
        await browser.close();
        await srv.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('FAIL  harness error: ' + (e && e.stack || e)); process.exit(1); });
