/*
 * Choosing another theme on Settings -> Theme is a short cross-fade, not a hard cut.
 *
 * Reported on 1.9.0.4-beta: "choosing a theme switches the whole screen at once." The
 * theme moves gradients, patterns, pictures and fonts, which a transition on the
 * variables cannot blend, so the old look is held on a layer above the new background and
 * faded out (fadeThemeFrom in profiles.js).
 *
 * Asked in a real browser, because blending is what the compositor does and nothing in a
 * stub DOM can show it. Against a build with no fade there is no layer and the first
 * frame after the switch is already the new theme: every assertion about the middle of
 * the fade fails.
 *
 *   node tests/js/themefade.js [path/to/profiles.js]
 *
 * Needs Chrome; with none it says so and exits 0, as coverframe.js does.
 */
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const chrome = require('../screens/chrome');
const fixture = require('../screens/fixture');

const JS_PATH = process.argv[2] || L.profilesPath();
const SOURCE = fs.readFileSync(JS_PATH, 'utf8');
const SHOT_DIR = path.join(L.ROOT, '.scratch');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// What the page reports about the gate and the layer, as Chrome computes them.
const SNAP = '(function(){var g=document.getElementById("profiles-gate-overlay"),v=g.querySelector(".jpf-theme-veil"),'
    + 'gs=getComputedStyle(g),d=v&&v.querySelector(".jpf-theme-veil-deco");'
    + 'return {theme:document.documentElement.getAttribute("data-jpf-theme"),gateImage:gs.backgroundImage,gateColor:gs.backgroundColor,'
    + 'veil:!!v,veilOpacity:v?Number(getComputedStyle(v).opacity):null,veilImage:v?getComputedStyle(v).backgroundImage:null,'
    + 'veilColor:v?getComputedStyle(v).backgroundColor:null,veilZ:v?getComputedStyle(v).zIndex:null,'
    + 'focus:document.activeElement?document.activeElement.className:""};})()';

const pause = ms => 'new Promise(function(r){setTimeout(r,' + ms + ');})';

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    const icons = path.join(L.ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');
    const server = await fixture.start(0, { source: SOURCE, materialIcons: icons });
    const base = 'http://127.0.0.1:' + server.port;
    const c = await chrome.launch();
    const scene = (theme, opts) => c.open(base + '/?scene=themes&device=desktop&theme=' + theme, 'desktop', opts);
    try {
        // Where the gate ends up after a direct switch: the page loaded in that theme.
        const directOf = async id => { await scene(id); return c.evaluate(SNAP); };
        const direct = await directOf('harvest');
        const from = await directOf('bonfire');
        ok('the two themes have different backgrounds (the test can tell them apart)',
            direct.gateImage !== from.gateImage || direct.gateColor !== from.gateColor,
            from.gateImage + ' / ' + direct.gateImage);

        console.log('\n── 1. A switch while the gate is up ──────────────────────────');
        await scene('bonfire');
        const mid = await c.evaluate('(function(){var P=window.__bonfire;P.applyTheme("harvest");return ' + pause(120)
            + '.then(function(){return ' + SNAP + ';});})()');
        ok('the page is on the new theme at once', mid.theme === 'harvest', mid.theme);
        ok('mid-fade there is a layer of the old look above the new background',
            mid.veil && mid.veilImage === from.gateImage && mid.veilColor === from.gateColor,
            JSON.stringify(mid));
        ok('mid-fade the gate itself is already the new look', mid.gateImage === direct.gateImage && mid.gateColor === direct.gateColor);
        ok('mid-fade the layer is part way: neither fully there nor gone', mid.veilOpacity > 0 && mid.veilOpacity < 1, String(mid.veilOpacity));
        ok('the layer is under the content (z-index -1, as the pattern layer)', mid.veilZ === '-1', String(mid.veilZ));
        fs.mkdirSync(SHOT_DIR, { recursive: true });
        // A frame taken part way through, for a person to look at.
        await scene('bonfire');
        await c.evaluate('window.__bonfire.applyTheme("harvest");' + pause(100));
        const png = await c.shot('png');
        fs.writeFileSync(path.join(SHOT_DIR, 'themefade-mid.png'), png);

        const end = await c.evaluate(pause(500) + '.then(function(){return ' + SNAP + ';})');
        ok('at the end the layer is gone', end.veil === false, JSON.stringify(end));
        ok('and the gate equals a direct switch', end.theme === 'harvest' && end.gateImage === direct.gateImage && end.gateColor === direct.gateColor);

        console.log('\n── 2. A quick second choice ──────────────────────────────────');
        await scene('bonfire');
        const second = await c.evaluate('(function(){var P=window.__bonfire;P.applyTheme("harvest");return ' + pause(80)
            + '.then(function(){P.applyTheme("bonfire");return ' + pause(600) + ';}).then(function(){return ' + SNAP + ';});})()');
        ok('it ends on the second theme, with nothing left over',
            second.theme === 'bonfire' && second.veil === false && second.gateImage === from.gateImage && second.gateColor === from.gateColor,
            JSON.stringify(second));

        console.log('\n── 3. Reduced motion ─────────────────────────────────────────');
        await scene('bonfire', { reducedMotion: true });
        const still = await c.evaluate('(function(){var P=window.__bonfire;P.applyTheme("harvest");return ' + pause(60)
            + '.then(function(){return ' + SNAP + ';});})()');
        ok('an instant switch: no layer, already the new look',
            still.theme === 'harvest' && still.veil === false && still.gateImage === direct.gateImage && still.gateColor === direct.gateColor,
            JSON.stringify(still));

        console.log('\n── 4. Where it is written ────────────────────────────────────');
        ok('the fade is opacity only: no filter or blur on the layer',
            !/\.jpf-theme-veil[^{]*\{[^}]*(filter|blur)/.test(L.extractCss(SOURCE)));
    } finally { await c.close(); await server.close(); }

    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
