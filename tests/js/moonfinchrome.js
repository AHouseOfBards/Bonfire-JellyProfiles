#!/usr/bin/env node
/*
 * Moonfin theme files, driven in Chrome (2026-11-12). tests/js/moonfinthemes.js runs the
 * conversion as plain functions; this loads a hand-written Moonfin file through the editor's
 * "Load theme file" and the theme list's Import, captures what "Download for Moonfin"
 * writes, and measures a glass file in the preview.
 *
 *     node tests/js/moonfinchrome.js [/old/profilesDashboard.html]
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
const ED = `document.getElementById('profilesConfigurationPage')._themeEditor`;
const AT = p => `(function(){ var o = ${ED}.draft; ${JSON.stringify(p)}.split('.').forEach(function(k){ o = o == null ? o : o[k]; }); return o; })()`;

// A Moonfin file as Moonfin writes it: colors alpha first. The title glow is blue at alpha
// 0x80, which Bonfire's base theme does not use anywhere.
function handFile(extra) {
    const f = {
        schemaVersion: 1, id: 'hand-made', displayName: 'Hand made',
        colors: {
            background: '#101820', onBackground: '#E0E8F0', surface: '#202830', onSurface: '#E0E8F0', surfaceVariant: '#283038', scrim: '#99000000',
            accent: '#10A0F0', onAccent: '#001020', buttonNormal: '#33AABBCC', buttonFocused: '#778899', buttonDisabled: '#445566', buttonActive: '#10A0F0',
            onButtonNormal: '#112233', onButtonFocused: '#001122', onButtonDisabled: '#889900', inputBackground: '#303840', inputFocused: '#384048',
            inputBorder: '#80A0B0C0', inputBorderFocused: '#80D0FF', rangeTrack: '#445566', rangeProgress: '#10A0F0', rangeThumb: '#10A0F0',
            seekbarBuffered: '#667788', badgeBackground: '#112233', onBadge: '#FFFFFF', badgeUnplayed: '#10A0F0', badgeWatched: '#33AA55',
            recordingActive: '#E04050', recordingScheduled: '#E0A000', error: '#E04050'
        },
        semantic: { statusAvailable: '#33AA55', statusRequested: '#E0A000', statusPending: '#E0A000', statusDownloading: '#10A0F0', mediaTypeBadgeMovie: '#10A0F0', mediaTypeBadgeShow: '#33AA55' },
        book: { background: '#101820', accent: '#10A0F0', mutedText: '#8899AA', primaryText: '#E0E8F0', sectionTitle: '#E0E8F0', divider: '#334455', placeholder: '#445566', shadow: '#000000', gradientTop: '#101820', gradientBottom: '#202830', inactiveChip: '#334455' },
        borders: {
            cardBorder: { color: '#5599AABB', width: 1 }, chipBorder: { color: '#AA445566', width: 2 }, focusBorder: { color: '#80D0FF', width: 4 },
            cardRadius: 14, chipRadius: 999, chipBackground: '#33AABBCC',
            focusGlow: [{ color: '#4010A0F0', blurRadius: 20, spreadRadius: 2, offsetX: 2, offsetY: 6 }]
        },
        textGlow: [{ color: '#8010A0F0', blurRadius: 9, spreadRadius: 0, offsetX: 1, offsetY: 3 }]
    };
    return Object.assign(f, extra || {});
}

// Hands a file to an <input type=file> the way a chooser does.
const CHOOSE = (sel, obj, name) => `(function(){ var i = document.querySelector(${JSON.stringify(sel)});
  var dt = new DataTransfer(); dt.items.add(new File([${JSON.stringify(JSON.stringify(obj))}], ${JSON.stringify(name)}, { type: 'application/json' }));
  i.files = dt.files; i.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n0 passed, 0 failed (skipped)');
        return;
    }
    const s = await fixture.start(0, { dashboard: process.argv[2] || undefined });
    const c = await chrome.launch();
    const ev = x => c.evaluate(x);
    try {
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=look&device=desktop&theme=dark', 'desktop');
        await ev("[].slice.call(document.querySelectorAll('details')).forEach(function(d){ if (d.querySelector('#themeEditor')) d.open = true; })");
        await ev("new Promise(function(r){var s=document.createElement('script'); s.src='/bonfire.js'; s.onload=r; s.onerror=r; document.head.appendChild(s);})");
        await ev('window.__bonfire.injectStyles()');
        await ev("(function(){var a=ApiClient.ajax; ApiClient.ajax=function(o){ if(o && /admin\\/themes\\/validate/.test(o.url)) return Promise.resolve({problems: []}); return a.apply(this, arguments); };})()");
        // The blobs the page hands to a download, in order.
        await ev("window.__blobs = []; (function(){ var u = URL.createObjectURL; URL.createObjectURL = function (b) { window.__blobs.push(b); return u.call(URL, b); }; })(); 0");
        await ev("document.getElementById('themeNew').click()");
        await wait(600);

        // 1. Load theme file, in the editor.
        await ev(CHOOSE('#teFileInput', handFile(), 'hand.theme.json'));
        await wait(600);
        ok('Load theme file takes the accent and the text colour', (await ev(AT('colors.accent'))) === '#10a0f0' && (await ev(AT('colors.text1'))) === '#e0e8f0', await ev('JSON.stringify(' + ED + '.draft.colors)'));
        ok('the button colours come across, alpha moved to the end', (await ev(AT('parts.button2.fill'))) === '#aabbcc33' && (await ev(AT('parts.button2.border.color'))) === '#445566aa');
        ok('the focus glow comes across', (await ev(AT('focus.glow.color'))) === '#10a0f040' && (await ev(AT('focus.glow.blur'))) === 20);
        ok('the title glow comes across with its own colour', (await ev(AT('depth.title.color'))) === '#10a0f080' && (await ev(AT('depth.title.blur'))) === 9, await ev('JSON.stringify(' + AT('depth.title') + ')'));
        const shadow = await ev("getComputedStyle(document.querySelector('#tePreview .jpf-theme-preview-name') || document.body).textShadow");
        ok('the preview draws the title glow in that colour', /rgba\(16, 160, 240, 0\.50?\d*\)/.test(shadow), shadow);

        // The editor's Colour box under Title shadow holds it, and emptying the box goes back
        // to the shadow colour at the shadow's opacity.
        const box = "document.querySelector('#teAdvancedFields input[data-path=\"depth.title.color\"]')";
        await ev(`document.querySelector('#themeEditor .jpf-te-mode[data-view="advanced"]').click()`);
        await wait(300);
        ok('the Title shadow colour box shows the glow colour', (await ev(`(${box} || { value: null }).value`)) === '#10a0f080', await ev(`${box} ? ${box}.value : 'no box'`));
        await ev(`(function(){ var e = ${box}; e.value = ''; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        await wait(200);
        const back = await ev(AT('depth.title.color') + ' === undefined');
        ok('emptying it takes the colour out of the draft', back === true, await ev('JSON.stringify(' + AT('depth.title') + ')'));
        const plainGlow = await ev("getComputedStyle(document.querySelector('#tePreview .jpf-theme-preview-name')).textShadow");
        ok('and the preview falls back to the shadow colour', plainGlow !== 'none' && plainGlow !== shadow, plainGlow);
        await ev(CHOOSE('#teFileInput', handFile(), 'hand.theme.json'));
        await wait(600);

        // 2. The same file through Import on the theme list.
        await ev("document.getElementById('teCancel').click()");
        await wait(300);
        await ev(CHOOSE('#themeImportFile', handFile({ id: 'second-one', displayName: 'Second one' }), 'second.theme.json'));
        await wait(800);
        ok('Import opens the editor on a new theme from the file', (await ev(AT('name.text'))) === 'Second one' && (await ev(AT('colors.accent'))) === '#10a0f0', await ev('JSON.stringify(' + ED + ' && ' + ED + '.draft.name)'));
        ok('and carries the title glow colour', (await ev(AT('depth.title.color'))) === '#10a0f080');

        // 3. Download for Moonfin.
        await ev("window.__blobs.length = 0; document.getElementById('teFile').click(); document.getElementById('teFileMoonfin').click(); 0");
        await wait(300);
        const written = JSON.parse(await ev("window.__blobs.length ? window.__blobs[window.__blobs.length - 1].text() : 'null'"));
        ok('Download for Moonfin wrote a file', !!written && written.colors && written.borders, JSON.stringify(written).slice(0, 200));
        ok('it has the colours alpha first', !!written && written.colors.accent === '#10A0F0' && written.colors.buttonNormal === '#33AABBCC', written && JSON.stringify([written.colors.accent, written.colors.buttonNormal]));
        ok('it writes the title glow with the colour it was given', !!written && written.textGlow && written.textGlow.length === 1 &&
            written.textGlow[0].color === '#8010A0F0' && written.textGlow[0].blurRadius === 9, written && JSON.stringify(written.textGlow));
        ok('it writes the focus glow', !!written && written.borders.focusGlow.length === 1 && written.borders.focusGlow[0].color === '#4010A0F0');

        // 4. A glass file lands on the glass base and the preview is blurred.
        await ev("document.getElementById('teCancel').click()");
        await wait(300);
        await ev("document.getElementById('themeNew').click()");
        await wait(600);
        await ev(CHOOSE('#teFileInput', handFile({ id: 'glassy', displayName: 'Glassy', isGlass: true }), 'glass.theme.json'));
        await wait(600);
        const frost = await ev(AT('decorations') + ' && JSON.stringify(' + AT('decorations') + ')');
        ok('a glass file brings the glass base\'s frost', /frost/.test(String(frost)), frost);
        ok('and the glass file\'s own colours are on top of it', (await ev(AT('colors.accent'))) === '#10a0f0');
        // The preview card has no panel for a blur to sit behind, so what shows of glass there
        // is the background it keeps: a glass file keeps the base's layers, a plain one has none.
        const PV = "(function(){ var c = document.querySelector('#tePreview .jpf-theme-preview'); var s = getComputedStyle(c); return { bg: s.backgroundImage, color: s.backgroundColor }; })()";
        const glassLook = await ev(PV);
        ok('the glass preview draws the base\'s background layers', /gradient|url\(/.test(glassLook.bg), JSON.stringify(glassLook));
        const baseLayers = await ev(AT('background.layers') + ' ? ' + AT('background.layers') + '.length : 0');
        ok('the draft keeps those layers', baseLayers > 0, String(baseLayers));
        await ev("document.getElementById('teCancel').click()");
        await wait(300);
        await ev("document.getElementById('themeNew').click()");
        await wait(600);
        await ev(CHOOSE('#teFileInput', handFile({ id: 'plain', displayName: 'Plain' }), 'plain.theme.json'));
        await wait(600);
        const plainLook = await ev(PV);
        ok('a file that is not glass draws one flat colour and no frost', plainLook.bg === 'none' && !/frost/.test(String(await ev('JSON.stringify(' + AT('decorations') + ')'))), JSON.stringify(plainLook));
        ok('and the glass preview differs from it', glassLook.bg !== plainLook.bg);
    } finally {
        await c.close();
        await s.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
