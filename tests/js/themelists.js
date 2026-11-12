#!/usr/bin/env node
/*
 * The theme editor's Decorations and Background layers as controls (2.0), driven in Chrome.
 * Both were JSON boxes. Each control must write into the draft what the box would have
 * held, and the cards must follow the draft when a list is changed by the other route.
 *
 *     node tests/js/themelists.js [/old/profilesDashboard.html]
 *
 * Needs Chrome; says it was skipped, exiting 0, without it.
 */
'use strict';

const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}
const wait = ms => new Promise(r => setTimeout(r, ms));
const frame = 'new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})';
const Q = s => `document.querySelector(${JSON.stringify(s)})`;
const DRAFT = p => `(function(){ var d = document.getElementById('profilesConfigurationPage')._themeEditor.draft; return JSON.stringify(${JSON.stringify(p)}.split('.').reduce(function (o, k) { return o && typeof o === 'object' ? o[k] : undefined; }, d)); })()`;
const SET = (sel, v, ev) => `(function(){ var e = ${sel}; if (!e) return false; if (e.type === 'checkbox') e.checked = ${JSON.stringify(v)}; else e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event('${ev || 'input'}', { bubbles: true })); return true; })()`;
const CLICK = sel => `(function(){ var e = ${sel}; if (!e) return false; e.click(); return true; })()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n0 passed, 0 failed (skipped)');
        return;
    }
    const s = await fixture.start(0, { dashboard: process.argv[2] || undefined });
    const c = await chrome.launch();
    const ev = x => c.evaluate(x);
    const draft = async p => JSON.parse((await ev(DRAFT(p))) || 'null');
    const on = name => Q(`#teAdvancedFields [data-deco-on="${name}"]`);
    const path = p => Q(`#teAdvancedFields [data-path="${p}"]`);
    const act = (a, at) => Q(`#teAdvancedFields [data-list-act="${a}"]${at === undefined ? '' : `[data-at="${at}"]`}`);
    try {
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=desktop&theme=dark', 'desktop');
        await ev("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await ev("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await ev('window.__bonfire.injectStyles()');
        await ev("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: []}); return a.apply(this, arguments); };})()");
        await ev("document.getElementById('themeNew').click()");
        await wait(600);
        await ev(CLICK(Q('#themeEditor .jpf-te-mode[data-view="advanced"]')));
        await ev("[].slice.call(document.querySelectorAll('#teAdvancedFields details')).forEach(function (d) { d.open = true; })");
        await ev(frame);

        // 1. Cards, not boxes.
        ok('the decorations are five cards, one per kind', (await ev(`document.querySelectorAll('#teAdvancedFields [data-list="decorations"] .jpf-te-card').length`)) === 5);
        ok('the JSON boxes are behind them, hidden', await ev(`[].slice.call(document.querySelectorAll('#teAdvancedFields textarea')).every(function (t) { return t.classList.contains('jpf-hidden'); })`));
        await ev(CLICK(Q('#teAdvancedFields .jpf-te-jsonbtn[data-list="decorations"]')));
        ok('Edit as JSON shows the box and hides the cards', await ev(`!${path('decorations')}.classList.contains('jpf-hidden') && ${Q('#teAdvancedFields .jpf-te-cards[data-list="decorations"]')}.classList.contains('jpf-hidden')`));
        await ev(SET(path('decorations'), '[{"name": "frost", "blur": 10}]'));
        ok('a pasted list is the draft', JSON.stringify(await draft('decorations')) === '[{"name":"frost","blur":10}]');
        await ev(CLICK(Q('#teAdvancedFields .jpf-te-jsonbtn[data-list="decorations"]')));
        ok('and back to cards, with frost on at 10', (await ev(`${on('frost')}.checked`)) === true && (await ev(`${path('decorations.0.blur')}.value`)) === '10');
        await ev(CLICK(on('frost'))); // off
        ok('frost off takes it out of the list', (await draft('decorations')).length === 0);

        // 2. Pattern, its controls and the cascade.
        ok('twinkle and the second pattern wait for a pattern, with a reason', (await ev(`${on('twinkle')}.disabled && ${on('pattern2')}.disabled`)) === true
            && (await ev(`document.querySelectorAll('#teAdvancedFields .jpf-te-reason').length`)) === 2);
        await ev(CLICK(on('pattern')));
        const p0 = await draft('decorations.0');
        ok('pattern on writes a whole pattern', p0 && p0.name === 'pattern' && typeof p0.size === 'number' && p0.drift === 'none', JSON.stringify(p0));
        ok('and the two that needed it are free', (await ev(`!${on('twinkle')}.disabled && !${on('pattern2')}.disabled`)) === true);
        ok('seconds is off while there is no drift', (await ev(`${path('decorations.0.seconds')}.disabled`)) === true);
        await ev(SET(path('decorations.0.size'), '100'));
        await ev(SET(path('decorations.0.opacity'), '0.5'));
        ok('size and opacity sliders write numbers', (await draft('decorations.0.size')) === 100 && (await draft('decorations.0.opacity')) === 0.5);
        await ev(SET(path('decorations.0.drift'), 'down', 'change'));
        ok('drift down gives it seconds, and the slider wakes', (await draft('decorations.0.seconds')) === 32 && (await ev(`!${path('decorations.0.seconds')}.disabled`)) === true);
        await ev(SET(path('decorations.0.seconds'), '90'));
        ok('seconds per cycle writes seconds', (await draft('decorations.0.seconds')) === 90);
        await ev(CLICK(on('pattern2')));
        ok('a second pattern, as the second', (await draft('decorations.1.name')) === 'pattern2');
        await ev(CLICK(on('twinkle')));
        ok('twinkle on', (await draft('decorations.2.name')) === 'twinkle' && (await draft('decorations.2.seconds')) === 8);
        // (The fixture serves no pictures, so what the preview draws of them is decolayers.js's.)
        await ev(CLICK(on('pattern')));
        ok('pattern off takes the second pattern and the twinkle with it', (await draft('decorations')).length === 0);
        await ev(CLICK(on('pattern')));
        ok('and back on, it is what it was', (await draft('decorations.0.size')) === 100);

        // 3. Rings.
        await ev(CLICK(on('rings')));
        const ri = (await draft('decorations')).length - 1;
        ok('rings on starts with one pair', (await draft('decorations.' + ri + '.gradients')).length === 1);
        ok('the last ring cannot be removed', (await ev(`${act('rm-ring')}.disabled`)) === true);
        for (let n = 0; n < 11; n++) await ev(CLICK(act('add-ring', ri)));
        ok('twelve is the most', (await draft('decorations.' + ri + '.gradients')).length === 12 && (await ev(`${act('add-ring', ri)}.disabled`)) === true);
        await ev(CLICK(act('rm-ring', ri + '-0')));
        ok('a ring can be removed', (await draft('decorations.' + ri + '.gradients')).length === 11);
        await ev(SET(path('decorations.' + ri + '.gradients.1.0'), '#112233'));
        ok('a ring colour is written into its pair', (await draft('decorations.' + ri + '.gradients.1.0')) === '#112233');
        await ev(CLICK(Q(`#teAdvancedFields [data-pick="decorations.${ri}.gap"]`)));
        await wait(150);
        ok('the gap colour opens Bonfire\'s own picker', await ev(`!!document.querySelector('#themeEditor .jpf-te-pop')`));
        await ev(CLICK(Q('#teAdvancedFields summary')));
        await ev(SET(Q(`#teAdvancedFields [data-path="shape.ringWidth"]`).replace('querySelector', 'querySelectorAll').replace(/\)$/, ')[1]'), '7'));
        const widths = await ev(`[].slice.call(document.querySelectorAll('#teAdvancedFields [data-path="shape.ringWidth"]')).map(function (e) { return e.value; })`);
        ok('ring thickness is reachable here and under Shape, one value', widths.length === 2 && widths[0] === '7' && widths[1] === '7' && (await draft('shape.ringWidth')) === 7, JSON.stringify(widths));

        // 4. Background layers.
        const layersBefore = (await draft('background.layers') || []).length;
        await ev(CLICK(act('add-layer')));
        ok('Add layer adds a linear layer', (await draft('background.layers')).length === layersBefore + 1 && (await draft('background.layers.' + layersBefore + '.type')) === 'linear');
        await ev(SET(path('background.layers.' + layersBefore + '.type'), 'radial', 'change'));
        const lay = await draft('background.layers.' + layersBefore);
        ok('radial swaps the angle for a shape and a centre', lay.type === 'radial' && lay.angle === undefined && lay.shape && typeof lay.x === 'number', JSON.stringify(lay));
        await ev(CLICK(act('add-stop', layersBefore)));
        ok('a stop is added', (await draft('background.layers.' + layersBefore + '.stops')).length === 3);
        await ev(CLICK(act('rm-stop', layersBefore + '-2')));
        ok('and removed, but never below two', (await draft('background.layers.' + layersBefore + '.stops')).length === 2 && (await ev(`${act('rm-stop', layersBefore + '-1')}.disabled`)) === true);
        await ev(SET(path('background.layers.' + layersBefore + '.stops.0.color'), '#abcdef'));
        ok('a stop colour is written', (await draft('background.layers.' + layersBefore + '.stops.0.color')) === '#abcdef');
        if (layersBefore > 0) {
            await ev(CLICK(act('up', layersBefore)));
            ok('Move up moves it above the one before', (await draft('background.layers.' + (layersBefore - 1) + '.type')) === 'radial');
            await ev(CLICK(act('down', layersBefore - 1)));
        }
        await ev(CLICK(act('rm-layer', layersBefore)));
        ok('Remove takes the layer away', (await draft('background.layers')).length === layersBefore);
        ok('the preview still draws', await ev(`!!${Q('#tePreview .jpf-theme-preview')}`));
    } finally {
        await c.close();
        await s.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exit(1);
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
